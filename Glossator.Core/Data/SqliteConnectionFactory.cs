namespace Glossator.Core.Data;

using Microsoft.Data.Sqlite;

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

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
                    ChapterFile     TEXT NOT NULL,
                    SegmentIndex    INTEGER NOT NULL,
                    OriginalHash    TEXT NOT NULL,
                    TagName         TEXT NOT NULL,
                    OriginalText    TEXT NOT NULL,
                    TranslatedText  TEXT NOT NULL,
                    StageStatus     TEXT NOT NULL,
                    UpdatedAt       TEXT NOT NULL,
                    PRIMARY KEY (ChapterFile, SegmentIndex)
                );
                CREATE INDEX IF NOT EXISTS idx_checkpoints_chapter ON SegmentCheckpoints(ChapterFile);
                """;

            await using var schemaCmd = connection.CreateCommand();
            schemaCmd.CommandText = ddl;
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
