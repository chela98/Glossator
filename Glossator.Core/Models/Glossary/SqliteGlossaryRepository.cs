using Microsoft.Data.Sqlite;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Glossary;

public class SqliteGlossaryRepository
{
    private readonly string _connectionString;

    public SqliteGlossaryRepository(string dbPath = "glossator.db")
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    /// <summary>
    /// Tablo yoksa otomatik oluşturur.
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTableSql = """
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
            """;

        using var command = new SqliteCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Kitaptan çıkarılan terimleri veritabanına ekler. 
    /// DİKKAT: Eğer terim daha önce eklenmiş ve onaylanmışsa (IsReviewed=1) senin ayarlarını ASLA ezmez!
    /// </summary>
    public int ImportTerms(IEnumerable<GlossaryEntry> terms, string sourceBook)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        var insertedCount = 0;

        var insertSql = """
            INSERT INTO Terms (EnglishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook, CreatedAt, UpdatedAt)
            VALUES (@EnglishTerm, @EnglishDefinition, @Policy, 0, @SourceBook, @Now, @Now)
            ON CONFLICT(EnglishTerm) DO UPDATE SET
                EnglishDefinition = COALESCE(Terms.EnglishDefinition, excluded.EnglishDefinition),
                SourceBook = COALESCE(Terms.SourceBook, excluded.SourceBook)
            WHERE Terms.IsReviewed = 0;
            """;

        foreach (var term in terms)
        {
            using var command = new SqliteCommand(insertSql, connection, transaction);
            command.Parameters.AddWithValue("@EnglishTerm", term.EnglishTerm);
            command.Parameters.AddWithValue("@EnglishDefinition", (object?)term.EnglishDefinition ?? DBNull.Value);
            command.Parameters.AddWithValue("@Policy", (int)term.Policy);
            command.Parameters.AddWithValue("@SourceBook", (object?)sourceBook ?? DBNull.Value);
            command.Parameters.AddWithValue("@Now", now);

            var rowsAffected = command.ExecuteNonQuery();
            if (rowsAffected > 0) insertedCount++;
        }

        transaction.Commit();
        return insertedCount;
    }

    /// <summary>
    /// Henüz senin onayından geçmemiş (IsReviewed=0) terimleri getirir.
    /// </summary>
    public List<GlossaryEntry> GetUnreviewedTerms()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook FROM Terms WHERE IsReviewed = 0 ORDER BY EnglishTerm;";
        using var command = new SqliteCommand(query, connection);
        using var reader = command.ExecuteReader();

        var list = new List<GlossaryEntry>();
        while (reader.Read())
        {
            list.Add(new GlossaryEntry
            {
                Id = reader.GetInt32(0),
                EnglishTerm = reader.GetString(1),
                TurkishTerm = reader.IsDBNull(2) ? null : reader.GetString(2),
                EnglishDefinition = reader.IsDBNull(3) ? null : reader.GetString(3),
                Policy = (GlossingPolicy)reader.GetInt32(4),
                IsReviewed = reader.GetInt32(5) == 1,
                SourceBook = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return list;
    }

    /// <summary>
    /// Kullanıcının onayladığı terim politikasını kaydeder.
    /// </summary>
    public void UpdateTermReview(int id, GlossingPolicy policy, string? turkishTerm = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var updateSql = """
            UPDATE Terms 
            SET Policy = @Policy, 
                TurkishTerm = @TurkishTerm, 
                IsReviewed = 1, 
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id;
            """;

        using var command = new SqliteCommand(updateSql, connection);
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Policy", (int)policy);
        command.Parameters.AddWithValue("@TurkishTerm", (object?)turkishTerm ?? DBNull.Value);
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o"));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Çeviri sırasında kullanılacak TÜM onaylı veya güncel terimleri çeker.
    /// </summary>
    public List<GlossaryEntry> GetAllTerms()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var query = "SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook FROM Terms ORDER BY EnglishTerm;";
        using var command = new SqliteCommand(query, connection);
        using var reader = command.ExecuteReader();

        var list = new List<GlossaryEntry>();
        while (reader.Read())
        {
            list.Add(new GlossaryEntry
            {
                Id = reader.GetInt32(0),
                EnglishTerm = reader.GetString(1),
                TurkishTerm = reader.IsDBNull(2) ? null : reader.GetString(2),
                EnglishDefinition = reader.IsDBNull(3) ? null : reader.GetString(3),
                Policy = (GlossingPolicy)reader.GetInt32(4),
                IsReviewed = reader.GetInt32(5) == 1,
                SourceBook = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return list;
    }
}