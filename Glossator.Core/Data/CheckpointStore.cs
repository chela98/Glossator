using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Glossator.Core.Models.Checkpoint;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glossator.Core.Data;

/// <summary>
/// Çoklu worker'ların eşzamanlı çeviri segmentlerini SQLite'a kilitlenme olmadan (single-writer micro-batching)
/// yazmasını sağlayan ve okuma/silme/flush operasyonlarını tek çatı altında toplayan kalıcılık deposu.
/// </summary>
public sealed class CheckpointStore : IAsyncDisposable, IDisposable
{
    private sealed record FlushCommand(TaskCompletionSource Tcs);

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<CheckpointStore> _logger;
    private readonly Channel<object> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private int _isDisposed;
    private volatile Exception? _lastPersistError;

    public CheckpointStore(
        ISqliteConnectionFactory connectionFactory,
        ILogger<CheckpointStore>? logger = null,
        int capacity = 5000)
    {
        _connectionFactory = connectionFactory;
        _logger = logger ?? NullLogger<CheckpointStore>.Instance;

        var options = new BoundedChannelOptions(capacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateBounded<object>(options);
        _consumerTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Çevrilen bir segment checkpoint kaydını arka planda SQLite'a yazılmak üzere kuyruğa ekler.
    /// </summary>
    public ValueTask EnqueueAsync(SegmentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        return _channel.Writer.WriteAsync(checkpoint, cancellationToken);
    }

    /// <summary>
    /// O ana kadar kuyruğa eklenmiş tüm checkpoint'lerin SQLite'a fiziksel olarak yazıldığını garanti eder (Flush).
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new FlushCommand(tcs), cancellationToken).ConfigureAwait(false);

        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Belirli bir kitap ve bölüm için tamamlanmış ('Completed') tüm segment checkpoint'lerini döner.
    /// </summary>
    public async Task<Dictionary<int, SegmentCheckpoint>> GetCompletedCheckpointsAsync(
        string bookId,
        string chapterFile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT BookId, ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt
            FROM SegmentCheckpoints
            WHERE BookId = @BookId AND ChapterFile = @ChapterFile AND StageStatus = 'Completed';
            """;

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@BookId", string.IsNullOrWhiteSpace(bookId) ? "default" : bookId);
        command.Parameters.AddWithValue("@ChapterFile", chapterFile);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<int, SegmentCheckpoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var cp = new SegmentCheckpoint
            {
                BookId = reader.GetString(0),
                ChapterFile = reader.GetString(1),
                SegmentIndex = reader.GetInt32(2),
                OriginalHash = reader.GetString(3),
                TagName = reader.GetString(4),
                OriginalText = reader.GetString(5),
                TranslatedText = reader.GetString(6),
                StageStatus = reader.GetString(7),
                UpdatedAt = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
            result[cp.SegmentIndex] = cp;
        }

        return result;
    }

    /// <summary>
    /// Belirli bir bölümün checkpoint kayıtlarını siler (--fresh modunda kullanılır).
    /// </summary>
    public async Task ClearCheckpointsAsync(
        string bookId,
        string chapterFile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        const string sql = "DELETE FROM SegmentCheckpoints WHERE BookId = @BookId AND ChapterFile = @ChapterFile;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@BookId", string.IsNullOrWhiteSpace(bookId) ? "default" : bookId);
        command.Parameters.AddWithValue("@ChapterFile", chapterFile);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<SegmentCheckpoint>(64);
        var reader = _channel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                batch.Clear();
                var stopwatch = Stopwatch.StartNew();

                while (batch.Count < 50 && stopwatch.ElapsedMilliseconds < 100)
                {
                    if (reader.TryRead(out var item))
                    {
                        if (item is SegmentCheckpoint cp)
                        {
                            batch.Add(cp);
                        }
                        else if (item is FlushCommand flush)
                        {
                            await HandleFlushCommandAsync(flush, batch);
                        }
                    }
                    else
                    {
                        var remaining = 100 - (int)stopwatch.ElapsedMilliseconds;
                        if (remaining <= 0) break;

                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        waitCts.CancelAfter(remaining);
                        try
                        {
                            if (!await reader.WaitToReadAsync(waitCts.Token)) break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await PersistBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _lastPersistError = ex;
                    }
                    finally
                    {
                        batch.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // İptal durumunda kalan öğeleri tüket
        }

        // Kalan son öğeleri boşalt (Drain remaining)
        batch.Clear();
        while (reader.TryRead(out var remainingItem))
        {
            if (remainingItem is SegmentCheckpoint cp)
            {
                batch.Add(cp);
                if (batch.Count >= 50)
                {
                    try
                    {
                        await PersistBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _lastPersistError = ex;
                    }
                    finally
                    {
                        batch.Clear();
                    }
                }
            }
            else if (remainingItem is FlushCommand flush)
            {
                await HandleFlushCommandAsync(flush, batch).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            try
            {
                await PersistBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _lastPersistError = ex;
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    private async Task HandleFlushCommandAsync(FlushCommand flush, List<SegmentCheckpoint> batch)
    {
        try
        {
            if (batch.Count > 0)
            {
                await PersistBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }

            if (_lastPersistError != null)
            {
                var err = _lastPersistError;
                _lastPersistError = null;
                flush.Tcs.TrySetException(err);
            }
            else
            {
                flush.Tcs.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            _lastPersistError = null;
            flush.Tcs.TrySetException(ex);
        }
        finally
        {
            batch.Clear();
        }
    }

    private async Task PersistBatchAsync(List<SegmentCheckpoint> checkpoints, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                const string sql = """
                    INSERT INTO SegmentCheckpoints (BookId, ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt)
                    VALUES (@BookId, @ChapterFile, @SegmentIndex, @OriginalHash, @TagName, @OriginalText, @TranslatedText, @StageStatus, @UpdatedAt)
                    ON CONFLICT(BookId, ChapterFile, SegmentIndex) DO UPDATE SET
                        OriginalHash = excluded.OriginalHash,
                        TagName = excluded.TagName,
                        OriginalText = excluded.OriginalText,
                        TranslatedText = excluded.TranslatedText,
                        StageStatus = excluded.StageStatus,
                        UpdatedAt = excluded.UpdatedAt;
                    """;

                await using var cmd = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
                var pBookId = cmd.Parameters.Add("@BookId", SqliteType.Text);
                var pChapterFile = cmd.Parameters.Add("@ChapterFile", SqliteType.Text);
                var pSegmentIndex = cmd.Parameters.Add("@SegmentIndex", SqliteType.Integer);
                var pOriginalHash = cmd.Parameters.Add("@OriginalHash", SqliteType.Text);
                var pTagName = cmd.Parameters.Add("@TagName", SqliteType.Text);
                var pOriginalText = cmd.Parameters.Add("@OriginalText", SqliteType.Text);
                var pTranslatedText = cmd.Parameters.Add("@TranslatedText", SqliteType.Text);
                var pStageStatus = cmd.Parameters.Add("@StageStatus", SqliteType.Text);
                var pUpdatedAt = cmd.Parameters.Add("@UpdatedAt", SqliteType.Text);
                await cmd.PrepareAsync(ct).ConfigureAwait(false);

                foreach (var cp in checkpoints)
                {
                    pBookId.Value = string.IsNullOrWhiteSpace(cp.BookId) ? "default" : cp.BookId;
                    pChapterFile.Value = cp.ChapterFile;
                    pSegmentIndex.Value = cp.SegmentIndex;
                    pOriginalHash.Value = cp.OriginalHash;
                    pTagName.Value = cp.TagName;
                    pOriginalText.Value = cp.OriginalText;
                    pTranslatedText.Value = cp.TranslatedText;
                    pStageStatus.Value = cp.StageStatus;
                    pUpdatedAt.Value = cp.UpdatedAt.ToString("o");
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqliteException ex) when (attempt < maxRetries && (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6))
            {
                _logger.LogWarning(ex, "SQLite veritabanı geçici olarak meşgul/kilitli ({ErrorCode}), {Attempt}. deneme sonrası {Delay}ms bekleniyor...",
                    ex.SqliteErrorCode, attempt, attempt * 100);
                await Task.Delay(attempt * 100, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Checkpoint batch yazımı başarısız oldu ({Count} checkpoint atlandı): {Message}",
                    checkpoints.Count, ex.Message);
                _lastPersistError = ex;
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Consumer task tamamlandı
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public void Dispose()
    {
        // UI SynchronizationContext kilitlenmesini önlemek için arka plan iş parçacığında güvenli boşaltma yap
        Task.Run(async () => await DisposeAsync().ConfigureAwait(false))
            .Wait(TimeSpan.FromSeconds(5));
    }
}
