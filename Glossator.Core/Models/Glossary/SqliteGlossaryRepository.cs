using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Glossator.Core.Serialization;
using System.Text;
using Microsoft.Data.Sqlite;
using Glossator.Core.Data;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Glossary;

/// <summary>
/// Terimler (Terms tablosu) için SQLite veri erişim katmanı.
/// </summary>
public class SqliteGlossaryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteGlossaryRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public SqliteGlossaryRepository(string dbPath = "glossator.db")
        : this(new SqliteConnectionFactory(dbPath))
    {
    }

    /// <summary>
    /// Kitaptan çıkarılan terimleri asenkron olarak veritabanına ekler.
    /// DİKKAT: Eğer terim daha önce eklenmiş ve onaylanmışsa (IsReviewed=1) ayarları ASLA ezmez!
    /// </summary>
    public async Task<int> ImportTermsAsync(IEnumerable<GlossaryEntry> terms, string sourceBook, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

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
            await using var command = new SqliteCommand(insertSql, connection, (SqliteTransaction)transaction);
            command.Parameters.AddWithValue("@EnglishTerm", term.EnglishTerm);
            command.Parameters.AddWithValue("@EnglishDefinition", (object?)term.EnglishDefinition ?? DBNull.Value);
            command.Parameters.AddWithValue("@Policy", (int)term.Policy);
            command.Parameters.AddWithValue("@SourceBook", (object?)sourceBook ?? DBNull.Value);
            command.Parameters.AddWithValue("@Now", now);

            var rowsAffected = await command.ExecuteNonQueryAsync(ct);
            if (rowsAffected > 0) insertedCount++;
        }

        await transaction.CommitAsync(ct);
        return insertedCount;
    }

    private static List<(string English, string Turkish, GlossingPolicy Policy)>? _cachedSeedTerms;

    /// <summary>
    /// Gömülü 'seed_glossary.yaml' kaynağından temel yazılım ve teknik terimleri yükler.
    /// Kodun içine veri gömme anti-pattern'ını ortadan kaldırır.
    /// </summary>
    public static List<(string English, string Turkish, GlossingPolicy Policy)> LoadDefaultCoreTerms()
    {
        if (_cachedSeedTerms != null)
        {
            return _cachedSeedTerms;
        }

        try
        {
            var assembly = typeof(SqliteGlossaryRepository).Assembly;
            const string resourceName = "Glossator.Core.Resources.seed_glossary.yaml";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var fileModel = deserializer.Deserialize<GlossaryFileModel>(reader);
                if (fileModel?.Terms != null && fileModel.Terms.Count > 0)
                {
                    _cachedSeedTerms = fileModel.Terms
                        .Where(t => !string.IsNullOrWhiteSpace(t.English))
                        .Select(t => (t.English, t.Turkish ?? t.English, t.Policy))
                        .ToList();
                    return _cachedSeedTerms;
                }
            }
        }
        catch
        {
            // Resource okuma hatasında emniyet kemeri fallback'e devam et
        }

        // Minimal emniyet kemeri fallback
        _cachedSeedTerms =
        [
            ("LLM", "LLM", GlossingPolicy.AlwaysEnglish),
            ("API", "API", GlossingPolicy.AlwaysEnglish),
            ("SDK", "SDK", GlossingPolicy.AlwaysEnglish),
            ("REST", "REST", GlossingPolicy.AlwaysEnglish),
            ("JSON", "JSON", GlossingPolicy.AlwaysEnglish),
            ("pipeline", "boru hattı", GlossingPolicy.AlwaysTurkish),
            ("workflow", "iş akışı", GlossingPolicy.AlwaysTurkish),
            ("boilerplate", "hazır kod", GlossingPolicy.ProgressiveGloss)
        ];

        return _cachedSeedTerms;
    }

    /// <summary>
    /// Sektör standardı temel terimlerin Türkçe karşılıklarını ve politikalarını asenkron olarak veritabanına işler.
    /// Kullanıcı tarafından elle düzenlenmiş (IsReviewed=1) terimleri ASLA ezmez.
    /// </summary>
    public async Task SeedDefaultTurkishTranslationsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow.ToString("o");
        var insertSql = """
            INSERT INTO Terms (EnglishTerm, TurkishTerm, Policy, IsReviewed, CreatedAt, UpdatedAt)
            VALUES (@EnglishTerm, @TurkishTerm, @Policy, 1, @Now, @Now)
            ON CONFLICT(EnglishTerm) DO UPDATE SET
                TurkishTerm = excluded.TurkishTerm,
                Policy = excluded.Policy
            WHERE Terms.IsReviewed = 0;
            """;

        var seedTerms = LoadDefaultCoreTerms();
        foreach (var (en, tr, policy) in seedTerms)
        {
            await using var cmd = new SqliteCommand(insertSql, connection, (SqliteTransaction)transaction);
            cmd.Parameters.AddWithValue("@EnglishTerm", en);
            cmd.Parameters.AddWithValue("@TurkishTerm", tr);
            cmd.Parameters.AddWithValue("@Policy", (int)policy);
            cmd.Parameters.AddWithValue("@Now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Kullanıcının onayladığı terim politikasını kaydeder.
    /// </summary>
    public void UpdateTermReview(int id, GlossingPolicy policy, string? turkishTerm = null)
    {
        using var connection = _connectionFactory.CreateOpenConnection();

        var updateSql = """
            UPDATE Terms 
            SET Policy = @Policy, 
                TurkishTerm = COALESCE(@TurkishTerm, TurkishTerm), 
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
        using var connection = _connectionFactory.CreateOpenConnection();

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

    /// <summary>
    /// Çeviri sırasında kullanılacak TÜM onaylı veya güncel terimleri asenkron çeker.
    /// </summary>
    public async Task<List<GlossaryEntry>> GetAllTermsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        var query = "SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook FROM Terms ORDER BY EnglishTerm;";
        await using var command = new SqliteCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var list = new List<GlossaryEntry>();
        while (await reader.ReadAsync(cancellationToken))
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
    /// Tekil bir terimi veritabanına ekler veya mevcutsa günceller (CLI desteği).
    /// </summary>
    public async Task<int> AddOrUpdateTermAsync(GlossaryEntry entry, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var now = DateTime.UtcNow.ToString("o");
        var sql = """
            INSERT INTO Terms (EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook, CreatedAt, UpdatedAt)
            VALUES (@EnglishTerm, @TurkishTerm, @EnglishDefinition, @Policy, @IsReviewed, @SourceBook, @Now, @Now)
            ON CONFLICT(EnglishTerm) DO UPDATE SET
                TurkishTerm = COALESCE(excluded.TurkishTerm, Terms.TurkishTerm),
                EnglishDefinition = COALESCE(excluded.EnglishDefinition, Terms.EnglishDefinition),
                Policy = CASE WHEN Terms.IsReviewed = 1 AND excluded.IsReviewed = 0 THEN Terms.Policy ELSE excluded.Policy END,
                IsReviewed = CASE WHEN Terms.IsReviewed = 1 AND excluded.IsReviewed = 0 THEN 1 ELSE excluded.IsReviewed END,
                SourceBook = COALESCE(excluded.SourceBook, Terms.SourceBook),
                UpdatedAt = excluded.UpdatedAt;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@EnglishTerm", entry.EnglishTerm.Trim());
        cmd.Parameters.AddWithValue("@TurkishTerm", (object?)entry.TurkishTerm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@EnglishDefinition", (object?)entry.EnglishDefinition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Policy", (int)entry.Policy);
        cmd.Parameters.AddWithValue("@IsReviewed", entry.IsReviewed ? 1 : 0);
        cmd.Parameters.AddWithValue("@SourceBook", (object?)entry.SourceBook ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", now);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Birden fazla terimi tek bir işlem (transaction) içinde ekler veya günceller (Yüksek performanslı toplu içe aktarma).
    /// </summary>
    public async Task<int> AddOrUpdateTermsBatchAsync(IEnumerable<GlossaryEntry> entries, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow.ToString("o");
        var sql = """
            INSERT INTO Terms (EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook, CreatedAt, UpdatedAt)
            VALUES (@EnglishTerm, @TurkishTerm, @EnglishDefinition, @Policy, @IsReviewed, @SourceBook, @Now, @Now)
            ON CONFLICT(EnglishTerm) DO UPDATE SET
                TurkishTerm = COALESCE(excluded.TurkishTerm, Terms.TurkishTerm),
                EnglishDefinition = COALESCE(excluded.EnglishDefinition, Terms.EnglishDefinition),
                Policy = CASE WHEN Terms.IsReviewed = 1 AND excluded.IsReviewed = 0 THEN Terms.Policy ELSE excluded.Policy END,
                IsReviewed = CASE WHEN Terms.IsReviewed = 1 AND excluded.IsReviewed = 0 THEN 1 ELSE excluded.IsReviewed END,
                SourceBook = COALESCE(excluded.SourceBook, Terms.SourceBook),
                UpdatedAt = excluded.UpdatedAt;
            """;

        var count = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.EnglishTerm)) continue;

            await using var cmd = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
            cmd.Parameters.AddWithValue("@EnglishTerm", entry.EnglishTerm.Trim());
            cmd.Parameters.AddWithValue("@TurkishTerm", (object?)entry.TurkishTerm ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@EnglishDefinition", (object?)entry.EnglishDefinition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Policy", (int)entry.Policy);
            cmd.Parameters.AddWithValue("@IsReviewed", entry.IsReviewed ? 1 : 0);
            cmd.Parameters.AddWithValue("@SourceBook", (object?)entry.SourceBook ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Now", now);

            await cmd.ExecuteNonQueryAsync(ct);
            count++;
        }

        await transaction.CommitAsync(ct);
        return count;
    }

    /// <summary>
    /// Terimlerde İngilizce veya Türkçe metne göre arama yapar (CLI desteği).
    /// </summary>
    public async Task<List<GlossaryEntry>> SearchTermsAsync(string query, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var sql = """
            SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook
            FROM Terms
            WHERE EnglishTerm LIKE @q OR TurkishTerm LIKE @q OR EnglishDefinition LIKE @q
            ORDER BY EnglishTerm;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@q", $"%{query.Trim()}%");

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<GlossaryEntry>();
        while (await reader.ReadAsync(ct))
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
    /// ID'ye göre terim arar.
    /// </summary>
    public async Task<GlossaryEntry?> GetTermByIdAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var sql = "SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook FROM Terms WHERE Id = @Id;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new GlossaryEntry
            {
                Id = reader.GetInt32(0),
                EnglishTerm = reader.GetString(1),
                TurkishTerm = reader.IsDBNull(2) ? null : reader.GetString(2),
                EnglishDefinition = reader.IsDBNull(3) ? null : reader.GetString(3),
                Policy = (GlossingPolicy)reader.GetInt32(4),
                IsReviewed = reader.GetInt32(5) == 1,
                SourceBook = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }
        return null;
    }

    /// <summary>
    /// İngilizce terim adına göre arar.
    /// </summary>
    public async Task<GlossaryEntry?> GetTermByEnglishAsync(string englishTerm, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var sql = "SELECT Id, EnglishTerm, TurkishTerm, EnglishDefinition, Policy, IsReviewed, SourceBook FROM Terms WHERE EnglishTerm = @EnglishTerm COLLATE NOCASE;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@EnglishTerm", englishTerm.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new GlossaryEntry
            {
                Id = reader.GetInt32(0),
                EnglishTerm = reader.GetString(1),
                TurkishTerm = reader.IsDBNull(2) ? null : reader.GetString(2),
                EnglishDefinition = reader.IsDBNull(3) ? null : reader.GetString(3),
                Policy = (GlossingPolicy)reader.GetInt32(4),
                IsReviewed = reader.GetInt32(5) == 1,
                SourceBook = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
        }
        return null;
    }

    /// <summary>
    /// ID'ye göre terim siler.
    /// </summary>
    public async Task<bool> DeleteTermAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var sql = "DELETE FROM Terms WHERE Id = @Id;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }
}
