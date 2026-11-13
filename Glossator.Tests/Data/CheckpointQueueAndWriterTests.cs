namespace Glossator.Tests.Data;

using Glossator.Core.Data;
using Glossator.Core.Models.Checkpoint;
using Microsoft.Data.Sqlite;
using Xunit;

public class CheckpointStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;

    public CheckpointStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_checkpoint_store_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _connectionFactory.InitializeDatabaseAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CheckpointStore_EnqueueAndFlush_PersistsImmediatelyWithoutWaitingTimer()
    {
        await using var store = new CheckpointStore(_connectionFactory, capacity: 50);

        var cp = new SegmentCheckpoint
        {
            BookId = "book_flush",
            ChapterFile = "ch01.html",
            SegmentIndex = 1,
            OriginalHash = SegmentCheckpoint.ComputeHash("Hello"),
            TagName = "p",
            OriginalText = "Hello",
            TranslatedText = "Merhaba",
            StageStatus = "Completed",
            UpdatedAt = DateTime.UtcNow
        };

        await store.EnqueueAsync(cp);

        // Timer'ın (100ms) dolmasını beklemeden Flush çağırıyoruz
        await store.FlushAsync();

        // Veritabanında kaydın anında var olduğunu doğrula
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TranslatedText FROM SegmentCheckpoints WHERE BookId = 'book_flush' AND ChapterFile = 'ch01.html' AND SegmentIndex = 1;";
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal("Merhaba", result);
    }

    [Fact]
    public async Task CheckpointStore_GetCompletedCheckpoints_ReturnsAccurateData()
    {
        await using var store = new CheckpointStore(_connectionFactory);

        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_read",
            ChapterFile = "ch02.html",
            SegmentIndex = 0,
            OriginalHash = "hash0",
            TagName = "h1",
            OriginalText = "Title",
            TranslatedText = "Başlık",
            StageStatus = "Completed"
        });

        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_read",
            ChapterFile = "ch02.html",
            SegmentIndex = 1,
            OriginalHash = "hash1",
            TagName = "p",
            OriginalText = "Paragraph",
            TranslatedText = "Paragraf",
            StageStatus = "Completed"
        });

        // Henüz tamamlanmamış (ör. InProgress) bir kayıt ekle
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_read",
            ChapterFile = "ch02.html",
            SegmentIndex = 2,
            OriginalHash = "hash2",
            TagName = "p",
            OriginalText = "Pending",
            TranslatedText = "Bekliyor",
            StageStatus = "InProgress"
        });

        await store.FlushAsync();

        var completed = await store.GetCompletedCheckpointsAsync("book_read", "ch02.html");

        Assert.Equal(2, completed.Count);
        Assert.True(completed.ContainsKey(0));
        Assert.True(completed.ContainsKey(1));
        Assert.False(completed.ContainsKey(2)); // InProgress dahil edilmemeli
        Assert.Equal("Başlık", completed[0].TranslatedText);
        Assert.Equal("Paragraf", completed[1].TranslatedText);
    }

    [Fact]
    public async Task CheckpointStore_ClearCheckpoints_RemovesTargetChapterOnly()
    {
        await using var store = new CheckpointStore(_connectionFactory);

        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_clear",
            ChapterFile = "ch01.html",
            SegmentIndex = 0,
            OriginalHash = "h1",
            TagName = "p",
            OriginalText = "Ch1",
            TranslatedText = "Bölüm 1",
            StageStatus = "Completed"
        });

        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_clear",
            ChapterFile = "ch02.html",
            SegmentIndex = 0,
            OriginalHash = "h2",
            TagName = "p",
            OriginalText = "Ch2",
            TranslatedText = "Bölüm 2",
            StageStatus = "Completed"
        });

        await store.FlushAsync();

        // ch01'i temizle
        await store.ClearCheckpointsAsync("book_clear", "ch01.html");

        var ch01Checkpoints = await store.GetCompletedCheckpointsAsync("book_clear", "ch01.html");
        var ch02Checkpoints = await store.GetCompletedCheckpointsAsync("book_clear", "ch02.html");

        Assert.Empty(ch01Checkpoints);
        Assert.Single(ch02Checkpoints);
        Assert.Equal("Bölüm 2", ch02Checkpoints[0].TranslatedText);
    }

    [Fact]
    public async Task CheckpointStore_UnderConcurrentProducers_PersistsAllItemsWithoutLoss()
    {
        const int producerCount = 8;
        const int itemsPerProducer = 50; // Toplam 400 checkpoint
        const int channelCapacity = 25;  // Dar kanal: Sırt-basıncı (Backpressure) üretir

        await using var store = new CheckpointStore(_connectionFactory, capacity: channelCapacity);

        var barrier = new Barrier(producerCount);

        var tasks = Enumerable.Range(0, producerCount).Select(producerId => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < itemsPerProducer; i++)
            {
                await store.EnqueueAsync(new SegmentCheckpoint
                {
                    BookId = "concurrent_book",
                    ChapterFile = $"ch_{producerId:D2}.xhtml",
                    SegmentIndex = i,
                    OriginalHash = $"hash_{producerId}_{i}",
                    TagName = "p",
                    OriginalText = $"Source text {producerId}_{i}",
                    TranslatedText = $"Çeviri metin {producerId}_{i}",
                    StageStatus = "Completed",
                    UpdatedAt = DateTime.UtcNow
                });
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        await store.FlushAsync();

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SegmentCheckpoints WHERE BookId = 'concurrent_book';";
        var persistedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        Assert.Equal(producerCount * itemsPerProducer, persistedCount);
    }

    [Fact]
    public async Task CheckpointStore_UpdatesExistingRecord_OnConflict()
    {
        await using var store = new CheckpointStore(_connectionFactory, capacity: 50);

        // İlk kayıt
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "test_book",
            ChapterFile = "ch03.html",
            SegmentIndex = 42,
            OriginalHash = "hash_old",
            TagName = "p",
            OriginalText = "Old Text",
            TranslatedText = "Eski Çeviri",
            StageStatus = "InProgress",
            UpdatedAt = DateTime.UtcNow
        });

        // Güncelleme
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "test_book",
            ChapterFile = "ch03.html",
            SegmentIndex = 42,
            OriginalHash = "hash_new",
            TagName = "p",
            OriginalText = "New Text",
            TranslatedText = "Yeni Çeviri",
            StageStatus = "Completed",
            UpdatedAt = DateTime.UtcNow
        });

        await store.FlushAsync();

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TranslatedText, StageStatus FROM SegmentCheckpoints WHERE BookId = 'test_book' AND ChapterFile = 'ch03.html' AND SegmentIndex = 42;";
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Yeni Çeviri", reader.GetString(0));
        Assert.Equal("Completed", reader.GetString(1));
    }

    [Fact]
    public void SegmentCheckpoint_ComputeHash_MatchesExpectedSha256()
    {
        // Arrange
        const string text = "Hello World";
        const string expectedSha256 = "A591A6D40BF420404A011733CFB7B190D62C65BF0BCDA32B57B277D9AD9F146E";

        // Act
        var hash = SegmentCheckpoint.ComputeHash(text);

        // Assert
        Assert.Equal(expectedSha256, hash);
        Assert.Equal(hash, SegmentCheckpoint.ComputeHash(text));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
    private sealed class FaultyConnectionFactory : ISqliteConnectionFactory
    {
        public string ConnectionString => "Data Source=:memory:";
        public SqliteConnection CreateOpenConnection() => throw new InvalidOperationException("Simulated SQLite failure");
        public Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated SQLite failure");
        public Task InitializeDatabaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task CheckpointStore_FlushAsync_WhenPersistenceFails_ThrowsInvalidOperationException()
    {
        // Arrange
        var faultyFactory = new FaultyConnectionFactory();
        await using var store = new CheckpointStore(faultyFactory, capacity: 50);

        var cp = new SegmentCheckpoint
        {
            BookId = "book_fail",
            ChapterFile = "ch01.html",
            SegmentIndex = 1,
            OriginalHash = "hash",
            TagName = "p",
            OriginalText = "Hello",
            TranslatedText = "Merhaba",
            StageStatus = "Completed"
        };

        await store.EnqueueAsync(cp);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.FlushAsync());
        Assert.Equal("Simulated SQLite failure", ex.Message);
    }

    [Fact]
    public async Task CheckpointStore_AfterDisposeAsync_EnqueueAndFlushThrowObjectDisposedException()
    {
        // Arrange
        var store = new CheckpointStore(_connectionFactory, capacity: 10);
        await store.DisposeAsync();

        var cp = new SegmentCheckpoint
        {
            BookId = "b",
            ChapterFile = "c",
            SegmentIndex = 1,
            OriginalHash = "h",
            TagName = "p",
            OriginalText = "x",
            TranslatedText = "y"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.EnqueueAsync(cp));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.FlushAsync());
    }

    [Fact]
    public async Task CheckpointStore_WhenFactoryFailsOnceThenRecovers_SubsequentFlushSucceedsWithoutPoisonedBatch()
    {
        // Arrange
        var flakyFactory = new FlakyConnectionFactory(_connectionFactory, failNextOpen: true);
        await using var store = new CheckpointStore(flakyFactory, capacity: 50);

        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_recover",
            ChapterFile = "ch01.html",
            SegmentIndex = 1,
            OriginalHash = "h1",
            TagName = "p",
            OriginalText = "Bad Batch",
            TranslatedText = "Kötü"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FlushAsync());

        // Act: Hata giderildikten sonra yeni bir öğe kuyruğa al ve flush et (önceki zehirli batch kalıntısı olmamalı)
        flakyFactory.FailNextOpen = false;
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "book_recover",
            ChapterFile = "ch01.html",
            SegmentIndex = 2,
            OriginalHash = "h2",
            TagName = "p",
            OriginalText = "Good Batch",
            TranslatedText = "İyi"
        });

        await store.FlushAsync();

        // Assert
        var completed = await store.GetCompletedCheckpointsAsync("book_recover", "ch01.html");
        Assert.False(completed.ContainsKey(1));
        Assert.True(completed.ContainsKey(2));
        Assert.Equal("İyi", completed[2].TranslatedText);
    }

    private sealed class FlakyConnectionFactory(ISqliteConnectionFactory inner, bool failNextOpen) : ISqliteConnectionFactory
    {
        public bool FailNextOpen { get; set; } = failNextOpen;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateOpenConnection() => FailNextOpen ? throw new InvalidOperationException("Transient failure") : inner.CreateOpenConnection();
        public Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default)
            => FailNextOpen ? throw new InvalidOperationException("Transient failure") : inner.CreateOpenConnectionAsync(cancellationToken);
        public Task InitializeDatabaseAsync(CancellationToken cancellationToken = default) => inner.InitializeDatabaseAsync(cancellationToken);
    }
}
