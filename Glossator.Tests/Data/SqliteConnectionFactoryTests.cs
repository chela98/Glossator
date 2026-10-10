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
            // SQLite bağlantı havuzunu temizleyip dosyayı sil
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { /* Ignore */ }
            }
            var walFile = $"{tempDb}-wal";
            if (File.Exists(walFile))
            {
                try { File.Delete(walFile); } catch { /* Ignore */ }
            }
            var shmFile = $"{tempDb}-shm";
            if (File.Exists(shmFile))
            {
                try { File.Delete(shmFile); } catch { /* Ignore */ }
            }
        }
    }
}
