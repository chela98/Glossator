namespace Glossator.Core.Data;

using Microsoft.Data.Sqlite;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public string ConnectionString => _connectionString;

    public SqliteConnectionFactory(string dbPath = "glossator.db")
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            await InitializeDatabaseAsync(cancellationToken);
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Her bağlantıda zorunlu connection-level interceptor pragmaları
        await using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA busy_timeout = 30000;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            """;
        await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    public SqliteConnection CreateOpenConnection()
    {
        if (!_initialized)
        {
            InitializeDatabaseAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA busy_timeout = 30000;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            """;
        pragmaCmd.ExecuteNonQuery();

        return connection;
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Veritabanı dosyası düzeyinde bir defalık WAL modu
            await using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            await walCmd.ExecuteNonQueryAsync(cancellationToken);

            const string ddl = """
                CREATE TABLE IF NOT EXISTS Terms (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EnglishTerm TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    TurkishTerm TEXT,
                    EnglishDefinition TEXT,
                    Policy INTEGER NOT NULL DEFAULT 0,
                    IsReviewed INTEGER NOT NULL DEFAULT 0,
                    SourceBook TEXT,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_terms_english ON Terms(EnglishTerm);
                CREATE INDEX IF NOT EXISTS idx_terms_reviewed ON Terms(IsReviewed);

                CREATE TABLE IF NOT EXISTS SegmentCheckpoints (
                    BookId          TEXT NOT NULL DEFAULT 'default',
                    ChapterFile     TEXT NOT NULL,
                    SegmentIndex    INTEGER NOT NULL,
                    OriginalHash    TEXT NOT NULL,
                    TagName         TEXT NOT NULL,
                    OriginalText    TEXT NOT NULL,
                    TranslatedText  TEXT NOT NULL,
                    StageStatus     TEXT NOT NULL,
                    UpdatedAt       TEXT NOT NULL,
                    PRIMARY KEY (BookId, ChapterFile, SegmentIndex)
                );
                """;

            await using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = ddl;
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken);

            // Geriye dönük uyumluluk: Eğer SegmentCheckpoints tablosunda BookId kolonu yoksa otomatik taşı
            await using var checkColCmd = connection.CreateCommand();
            checkColCmd.CommandText = "PRAGMA table_info(SegmentCheckpoints);";
            await using var reader = await checkColCmd.ExecuteReaderAsync(cancellationToken);
            var hasBookId = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                var colName = reader.GetString(1);
                if (colName.Equals("BookId", StringComparison.OrdinalIgnoreCase))
                {
                    hasBookId = true;
                    break;
                }
            }
            await reader.CloseAsync();

            if (!hasBookId)
            {
                const string migrateDdl = """
                    CREATE TABLE IF NOT EXISTS SegmentCheckpoints_Migrated (
                        BookId          TEXT NOT NULL DEFAULT 'default',
                        ChapterFile     TEXT NOT NULL,
                        SegmentIndex    INTEGER NOT NULL,
                        OriginalHash    TEXT NOT NULL,
                        TagName         TEXT NOT NULL,
                        OriginalText    TEXT NOT NULL,
                        TranslatedText  TEXT NOT NULL,
                        StageStatus     TEXT NOT NULL,
                        UpdatedAt       TEXT NOT NULL,
                        PRIMARY KEY (BookId, ChapterFile, SegmentIndex)
                    );
                    INSERT OR IGNORE INTO SegmentCheckpoints_Migrated (BookId, ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt)
                    SELECT 'default', ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt
                    FROM SegmentCheckpoints;
                    DROP TABLE SegmentCheckpoints;
                    ALTER TABLE SegmentCheckpoints_Migrated RENAME TO SegmentCheckpoints;
                    """;
                await using var migrateCmd = connection.CreateCommand();
                migrateCmd.CommandText = migrateDdl;
                await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // BookId kolonu garanti edildikten sonra index oluşturulur
            await using var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_checkpoints_book_chapter ON SegmentCheckpoints(BookId, ChapterFile);";
            await indexCmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
