namespace Glossator.Tests.Data;

using Glossator.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task CreateOpenConnectionAsync_SetsPragmasAndCreatesTables()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_db_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(tempDb);

        try
        {
            await using var connection = await factory.CreateOpenConnectionAsync();

            // 1. PRAGMA busy_timeout kontrolü (30000 ms olmalı)
            await using var cmdTimeout = connection.CreateCommand();
            cmdTimeout.CommandText = "PRAGMA busy_timeout;";
            var timeoutResult = Convert.ToInt32(await cmdTimeout.ExecuteScalarAsync());
            Assert.Equal(30000, timeoutResult);

            // 2. PRAGMA foreign_keys kontrolü (1 olmalı)
            await using var cmdFk = connection.CreateCommand();
            cmdFk.CommandText = "PRAGMA foreign_keys;";
            var fkResult = Convert.ToInt32(await cmdFk.ExecuteScalarAsync());
            Assert.Equal(1, fkResult);

            // 3. PRAGMA journal_mode kontrolü (WAL olmalı)
            await using var cmdWal = connection.CreateCommand();
            cmdWal.CommandText = "PRAGMA journal_mode;";
            var walResult = Convert.ToString(await cmdWal.ExecuteScalarAsync());
            Assert.Equal("wal", walResult?.ToLowerInvariant());

            // 4. Tabloların varlığı kontrolü
            await using var cmdTable = connection.CreateCommand();
            cmdTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Terms', 'SegmentCheckpoints');";
            var tableCount = Convert.ToInt32(await cmdTable.ExecuteScalarAsync());
            Assert.Equal(2, tableCount);
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public void CreateOpenConnection_Synchronous_OpensValidConnection()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_sync_db_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(tempDb);

        try
        {
            using var connection = factory.CreateOpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Terms', 'SegmentCheckpoints');";
            var tableCount = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(2, tableCount);
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task InitializeDatabaseAsync_UnderHighConcurrency_ExecutesIdempotently()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_concurrent_db_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(tempDb);

        try
        {
            // 20 eşzamanlı istek aynı anda fabrikadan bağlantı ister
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                await using var conn = await factory.CreateOpenConnectionAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }));

            var results = await Task.WhenAll(tasks);
            Assert.All(results, val => Assert.Equal(1, val));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task InitializeDatabaseAsync_MigratesLegacySchemaWithoutBookId_PreservingExistingData()
    {
        var legacyDb = Path.Combine(Path.GetTempPath(), $"legacy_db_{Guid.NewGuid():N}.db");
        try
        {
            // 1. BookId kolonu OLMAYAN eski şemayı manuel oluştur
            var csb = new SqliteConnectionStringBuilder { DataSource = legacyDb };
            await using (var conn = new SqliteConnection(csb.ToString()))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE SegmentCheckpoints (
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
                    INSERT INTO SegmentCheckpoints VALUES ('old_ch1.xhtml', 1, 'h1', 'p', 'Old Source', 'Eski Çeviri', 'Completed', '2026-01-01');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Yeni fabrikayı eski DB üzerinde başlat
            var factory = new SqliteConnectionFactory(legacyDb);
            await using var migratedConn = await factory.CreateOpenConnectionAsync();

            // 3. Verinin korunduğunu ve varsayılan BookId='default' atandığını doğrula
            await using var queryCmd = migratedConn.CreateCommand();
            queryCmd.CommandText = "SELECT BookId, TranslatedText FROM SegmentCheckpoints WHERE ChapterFile = 'old_ch1.xhtml';";
            await using var reader = await queryCmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("default", reader.GetString(0));
            Assert.Equal("Eski Çeviri", reader.GetString(1));
        }
        finally
        {
            CleanupDb(legacyDb);
        }
    }

    private static void CleanupDb(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var wal = $"{path}-wal";
        try { if (File.Exists(wal)) File.Delete(wal); } catch { }
        var shm = $"{path}-shm";
        try { if (File.Exists(shm)) File.Delete(shm); } catch { }
    }
}
