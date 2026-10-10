using System.Diagnostics;
using Glossator.Core.Data;
using Glossator.Core.Models.Checkpoint;
using Microsoft.Data.Sqlite;

namespace Glossator.Core.Glossary;

/// <summary>
/// Çoklu iş parçacıklarının (worker pool) SQLite veritabanında kilitlenme (database is locked)
/// oluşturmasını önleyen, Channel tabanlı tekil arka plan yazıcı (Single-Writer Micro-Batching Consumer).
/// </summary>
public sealed class CheckpointBackgroundWriter : IAsyncDisposable
{
    private readonly ICheckpointQueue _queue;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;

    public CheckpointBackgroundWriter(ICheckpointQueue queue, ISqliteConnectionFactory connectionFactory)
    {
        _queue = queue;
        _connectionFactory = connectionFactory;
        _consumerTask = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<SegmentCheckpoint>(64);
        var reader = _queue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                batch.Clear();
                var stopwatch = Stopwatch.StartNew();

                // 50 öğe topla veya en fazla 100 ms bekle
                while (batch.Count < 50 && stopwatch.ElapsedMilliseconds < 100 && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    await PersistBatchAsync(batch, CancellationToken.None);
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
            batch.Add(remainingItem);
            if (batch.Count >= 50)
            {
                await PersistBatchAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await PersistBatchAsync(batch, CancellationToken.None);
        }
    }

    private async Task PersistBatchAsync(List<SegmentCheckpoint> checkpoints, CancellationToken ct)
    {
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            const string sql = """
                INSERT INTO SegmentCheckpoints (ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt)
                VALUES (@ChapterFile, @SegmentIndex, @OriginalHash, @TagName, @OriginalText, @TranslatedText, @StageStatus, @UpdatedAt)
                ON CONFLICT(ChapterFile, SegmentIndex) DO UPDATE SET
                    OriginalHash = excluded.OriginalHash,
                    TagName = excluded.TagName,
                    OriginalText = excluded.OriginalText,
                    TranslatedText = excluded.TranslatedText,
                    StageStatus = excluded.StageStatus,
                    UpdatedAt = excluded.UpdatedAt;
                """;

            foreach (var cp in checkpoints)
            {
                await using var cmd = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
                cmd.Parameters.AddWithValue("@ChapterFile", cp.ChapterFile);
                cmd.Parameters.AddWithValue("@SegmentIndex", cp.SegmentIndex);
                cmd.Parameters.AddWithValue("@OriginalHash", cp.OriginalHash);
                cmd.Parameters.AddWithValue("@TagName", cp.TagName);
                cmd.Parameters.AddWithValue("@OriginalText", cp.OriginalText);
                cmd.Parameters.AddWithValue("@TranslatedText", cp.TranslatedText);
                cmd.Parameters.AddWithValue("@StageStatus", cp.StageStatus);
                cmd.Parameters.AddWithValue("@UpdatedAt", cp.UpdatedAt.ToString("o"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // Log or fallback
            Debug.WriteLine($"[CheckpointBackgroundWriter Error] Batch yazımı sırasında hata: {ex.Message}");
        }
    }

    private int _isDisposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _queue.Complete();
        try
        {
            await _consumerTask;
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
}
