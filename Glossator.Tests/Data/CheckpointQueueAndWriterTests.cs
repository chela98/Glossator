using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossator.Tests.Data;

public class CheckpointQueueAndWriterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;

    public CheckpointQueueAndWriterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_queue_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);

        // Şemayı oluştur
        using var connection = _connectionFactory.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SegmentCheckpoints (
                ChapterFile TEXT NOT NULL,
                SegmentIndex INTEGER NOT NULL,
                OriginalHash TEXT NOT NULL,
                TagName TEXT NOT NULL,
                OriginalText TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                StageStatus TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ChapterFile, SegmentIndex)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task CheckpointQueue_EnqueueAndRead_WorksCorrectly()
    {
        var queue = new CheckpointQueue(capacity: 100);

        var cp = new SegmentCheckpoint
        {
            ChapterFile = "ch01.html",
            SegmentIndex = 1,
            OriginalHash = "hash1",
            TagName = "p",
            OriginalText = "Hello",
            TranslatedText = "Merhaba",
            StageStatus = "Completed",
            UpdatedAt = DateTime.UtcNow
        };

        await queue.EnqueueAsync(cp);
        queue.Complete();

        var items = new List<SegmentCheckpoint>();
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal("ch01.html", items[0].ChapterFile);
        Assert.Equal("Merhaba", items[0].TranslatedText);
    }

    [Fact]
    public async Task CheckpointBackgroundWriter_MicroBatchesAndPersistsToSqlite()
    {
        var queue = new CheckpointQueue();
        await using var writer = new CheckpointBackgroundWriter(queue, _connectionFactory);

        for (int i = 0; i < 15; i++)
        {
            await queue.EnqueueAsync(new SegmentCheckpoint
            {
                ChapterFile = "ch02.html",
                SegmentIndex = i,
                OriginalHash = $"hash_{i}",
                TagName = "p",
                OriginalText = $"Text {i}",
                TranslatedText = $"Metin {i}",
                StageStatus = "Completed",
                UpdatedAt = DateTime.UtcNow
            });
        }

        // DisposeAsync ile kalan tüm öğelerin SQLite'a aktarılmasını (drain) bekle
        await writer.DisposeAsync();

        // SQLite'tan doğrula
        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SegmentCheckpoints WHERE ChapterFile = 'ch02.html';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        Assert.Equal(15, count);
    }

    [Fact]
    public async Task CheckpointBackgroundWriter_HandlesOnConflictUpdate()
    {
        var queue = new CheckpointQueue();
        await using var writer = new CheckpointBackgroundWriter(queue, _connectionFactory);

        // İlk kayıt
        await queue.EnqueueAsync(new SegmentCheckpoint
        {
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
        await queue.EnqueueAsync(new SegmentCheckpoint
        {
            ChapterFile = "ch03.html",
            SegmentIndex = 42,
            OriginalHash = "hash_new",
            TagName = "p",
            OriginalText = "New Text",
            TranslatedText = "Yeni Çeviri",
            StageStatus = "Completed",
            UpdatedAt = DateTime.UtcNow
        });

        await writer.DisposeAsync();

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TranslatedText, StageStatus FROM SegmentCheckpoints WHERE ChapterFile = 'ch03.html' AND SegmentIndex = 42;";
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Yeni Çeviri", reader.GetString(0));
        Assert.Equal("Completed", reader.GetString(1));
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
}
