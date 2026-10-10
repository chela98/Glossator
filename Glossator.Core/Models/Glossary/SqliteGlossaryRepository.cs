using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Glossator.Core.Data;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Glossary;

public class SqliteGlossaryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly string _connectionString;

    public SqliteGlossaryRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        _connectionString = connectionFactory.ConnectionString;
    }

    public SqliteGlossaryRepository(string dbPath = "glossator.db")
        : this(new SqliteConnectionFactory(dbPath))
    {
    }


    /// <summary>
    /// Kitaptan çıkarılan terimleri veritabanına ekler. 
    /// DİKKAT: Eğer terim daha önce eklenmiş ve onaylanmışsa (IsReviewed=1) senin ayarlarını ASLA ezmez!
    /// </summary>
    public int ImportTerms(IEnumerable<GlossaryEntry> terms, string sourceBook)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
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
    /// Kitaptan çıkarılan terimleri asenkron olarak veritabanına ekler.
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

    /// <summary>
    /// Sektör standardı temel yapay zeka ve yazılım terimlerinin Türkçe karşılıklarını ve politikalarını veritabanına işler.
    /// Kullanıcı tarafından elle düzenlenmiş (IsReviewed=1) terimleri ASLA ezmez.
    /// </summary>
    private static readonly List<(string English, string Turkish, GlossingPolicy Policy)> DefaultCoreTerms = new()
    {
        // 1. AlwaysTurkish: Düz kavramlar ve doğrudan Türkçeleştirilecek teknik terimler
        ("ML practitioner", "makine öğrenimi uygulayıcısı", GlossingPolicy.AlwaysTurkish),
            ("ML practitioners", "makine öğrenimi uygulayıcıları", GlossingPolicy.AlwaysTurkish),
            ("data scientist", "veri bilimci", GlossingPolicy.AlwaysTurkish),
            ("data scientists", "veri bilimciler", GlossingPolicy.AlwaysTurkish),
            ("machine learning", "makine öğrenimi", GlossingPolicy.AlwaysTurkish),
            ("generative model", "üretken model", GlossingPolicy.AlwaysTurkish),
            ("generative models", "üretken modeller", GlossingPolicy.AlwaysTurkish),
            ("pretrained", "önceden eğitilmiş", GlossingPolicy.AlwaysTurkish),
            ("pretraining", "ön eğitim", GlossingPolicy.AlwaysTurkish),
            ("dataset", "veri seti", GlossingPolicy.AlwaysTurkish),
            ("datasets", "veri setleri", GlossingPolicy.AlwaysTurkish),
            ("data", "veri", GlossingPolicy.AlwaysTurkish),
            ("cost", "maliyet", GlossingPolicy.AlwaysTurkish),
            ("costs", "maliyetler", GlossingPolicy.AlwaysTurkish),
            ("model", "model", GlossingPolicy.AlwaysTurkish),
            ("models", "modeller", GlossingPolicy.AlwaysTurkish),
            ("training", "eğitim", GlossingPolicy.AlwaysTurkish),
            ("tool", "araç", GlossingPolicy.AlwaysTurkish),
            ("tools", "araçlar", GlossingPolicy.AlwaysTurkish),
            ("tool use", "araç kullanımı", GlossingPolicy.AlwaysTurkish),
            ("human oversight", "insan denetimi", GlossingPolicy.AlwaysTurkish),
            ("oversight", "denetim", GlossingPolicy.AlwaysTurkish),
            ("pipeline", "boru hattı", GlossingPolicy.AlwaysTurkish),
            ("pipelines", "boru hatları", GlossingPolicy.AlwaysTurkish),
            ("workflow", "iş akışı", GlossingPolicy.AlwaysTurkish),
            ("workflows", "iş akışları", GlossingPolicy.AlwaysTurkish),
            ("prompt", "istem", GlossingPolicy.AlwaysTurkish),
            ("prompts", "istemler", GlossingPolicy.AlwaysTurkish),
            ("embedding", "gömme", GlossingPolicy.AlwaysTurkish),
            ("embeddings", "gömmeler", GlossingPolicy.AlwaysTurkish),
            ("context window", "bağlam penceresi", GlossingPolicy.AlwaysTurkish),
            ("guardrails", "güvenlik korkulukları", GlossingPolicy.AlwaysTurkish),
            ("reasoning", "akıl yürütme", GlossingPolicy.AlwaysTurkish),
            ("decision making", "karar alma", GlossingPolicy.AlwaysTurkish),
            ("agent", "ajan", GlossingPolicy.AlwaysTurkish),
            ("agents", "ajanlar", GlossingPolicy.AlwaysTurkish),
            ("large language models", "büyük dil modelleri", GlossingPolicy.AlwaysTurkish),
            ("large language model", "büyük dil modeli", GlossingPolicy.AlwaysTurkish),
            ("activation function", "aktivasyon fonksiyonu", GlossingPolicy.AlwaysTurkish),
            ("backpropagation", "geri yayılım", GlossingPolicy.AlwaysTurkish),
            ("gradient descent", "gradyan inişi", GlossingPolicy.AlwaysTurkish),
            ("loss function", "kayıp fonksiyonu", GlossingPolicy.AlwaysTurkish),
            ("transformer", "dönüştürücü", GlossingPolicy.AlwaysTurkish),

            // 2. AlwaysEnglish: Markalar, kütüphaneler, protokoller ve sektör kısaltmaları
            ("Microsoft Agent Framework", "Microsoft Agent Framework", GlossingPolicy.AlwaysEnglish),
            ("Microsoft.Extensions.AI", "Microsoft.Extensions.AI", GlossingPolicy.AlwaysEnglish),
            ("Model Context Protocol", "Model Context Protocol", GlossingPolicy.AlwaysEnglish),
            ("Agent-to-Agent Protocol", "Agent-to-Agent Protocol", GlossingPolicy.AlwaysEnglish),
            ("LangChain", "LangChain", GlossingPolicy.AlwaysEnglish),
            ("CrewAI", "CrewAI", GlossingPolicy.AlwaysEnglish),
            ("AutoGen", "AutoGen", GlossingPolicy.AlwaysEnglish),
            ("Semantic Kernel", "Semantic Kernel", GlossingPolicy.AlwaysEnglish),
            ("DSPy", "DSPy", GlossingPolicy.AlwaysEnglish),
            ("Ollama", "Ollama", GlossingPolicy.AlwaysEnglish),
            ("vLLM", "vLLM", GlossingPolicy.AlwaysEnglish),
            ("LLM", "LLM", GlossingPolicy.AlwaysEnglish),
            ("LLMs", "LLMs", GlossingPolicy.AlwaysEnglish),
            ("API", "API", GlossingPolicy.AlwaysEnglish),
            ("SDK", "SDK", GlossingPolicy.AlwaysEnglish),
            ("RAG", "RAG", GlossingPolicy.AlwaysEnglish),
            ("REST", "REST", GlossingPolicy.AlwaysEnglish),
            ("JSON", "JSON", GlossingPolicy.AlwaysEnglish),
            ("YAML", "YAML", GlossingPolicy.AlwaysEnglish),

            // 3. ProgressiveGloss: Temel Ajan/Yapay Zeka Kavramları (İlk 2 geçişte hibrit Türkçe (İngilizce), sonra Türkçe)
            ("autonomous agent", "otonom ajan", GlossingPolicy.ProgressiveGloss),
            ("autonomous agents", "otonom ajanlar", GlossingPolicy.ProgressiveGloss),
            ("agency", "eylemlilik", GlossingPolicy.ProgressiveGloss),
            ("agentic system", "ajan tabanlı sistem", GlossingPolicy.ProgressiveGloss),
            ("agentic systems", "ajan tabanlı sistemler", GlossingPolicy.ProgressiveGloss),
            ("foundation model", "temel model", GlossingPolicy.ProgressiveGloss),
            ("foundation models", "temel modeller", GlossingPolicy.ProgressiveGloss),
            ("reinforcement learning", "pekiştirmeli öğrenme", GlossingPolicy.ProgressiveGloss),
            ("fine-tuning", "ince ayar", GlossingPolicy.ProgressiveGloss),
            ("few-shot learning", "az örnekli öğrenme", GlossingPolicy.ProgressiveGloss),
            ("zero-shot learning", "sıfır örnekli öğrenme", GlossingPolicy.ProgressiveGloss),
            ("vector database", "vektör veritabanı", GlossingPolicy.ProgressiveGloss),
            ("vector databases", "vektör veritabanları", GlossingPolicy.ProgressiveGloss),
            ("knowledge graph", "bilgi grafiği", GlossingPolicy.ProgressiveGloss),
            ("knowledge graphs", "bilgi grafikleri", GlossingPolicy.ProgressiveGloss),
            ("prompt engineering", "istem mühendisliği", GlossingPolicy.ProgressiveGloss),

            // 4. Ajan Türleri ve Sınıflandırmalar (Types of Agents)
            ("business-task agents", "iş görevi ajanları", GlossingPolicy.ProgressiveGloss),
            ("business-task agent", "iş görevi ajanı", GlossingPolicy.ProgressiveGloss),
            ("conversational agents", "konuşma ajanları", GlossingPolicy.ProgressiveGloss),
            ("conversational agent", "konuşma ajanı", GlossingPolicy.ProgressiveGloss),
            ("research agents", "araştırma ajanları", GlossingPolicy.ProgressiveGloss),
            ("research agent", "araştırma ajanı", GlossingPolicy.ProgressiveGloss),
            ("analytics agents", "analitik ajanlar", GlossingPolicy.ProgressiveGloss),
            ("analytics agent", "analitik ajan", GlossingPolicy.ProgressiveGloss),
            ("developer agents", "geliştirici ajanlar", GlossingPolicy.ProgressiveGloss),
            ("developer agent", "geliştirici ajan", GlossingPolicy.ProgressiveGloss),
            ("domain-specific agents", "alana özgü ajanlar", GlossingPolicy.ProgressiveGloss),
            ("domain-specific agent", "alana özgü ajan", GlossingPolicy.ProgressiveGloss),
            ("browser-using agents", "tarayıcı kullanan ajanlar", GlossingPolicy.ProgressiveGloss),
            ("browser-using agent", "tarayıcı kullanan ajan", GlossingPolicy.ProgressiveGloss),
            ("voice agents", "sesli ajanlar", GlossingPolicy.ProgressiveGloss),
            ("voice agent", "sesli ajan", GlossingPolicy.ProgressiveGloss),
            ("video agents", "video ajanları", GlossingPolicy.ProgressiveGloss),
            ("video agent", "video ajanı", GlossingPolicy.ProgressiveGloss),
            ("coding agents", "kodlama ajanları", GlossingPolicy.ProgressiveGloss),
            ("coding agent", "kodlama ajanı", GlossingPolicy.ProgressiveGloss),

            // 5. Pratik Kullanım Senaryosu Ajanları (Practical Use Cases)
            ("customer support agent", "müşteri destek ajanı", GlossingPolicy.ProgressiveGloss),
            ("financial services agent", "finansal hizmetler ajanı", GlossingPolicy.ProgressiveGloss),
            ("healthcare patient intake and triage agent", "sağlık hasta kabul ve triyaj ajanı", GlossingPolicy.ProgressiveGloss),
            ("IT help desk agent", "BT yardım masası ajanı", GlossingPolicy.ProgressiveGloss),
            ("legal document review agent", "hukuki belge inceleme ajanı", GlossingPolicy.ProgressiveGloss),
            ("security operations center (SOC) analyst agent", "güvenlik operasyonları merkezi (SOC) analist ajanı", GlossingPolicy.ProgressiveGloss),
            ("supply chain and logistics agent", "tedarik zinciri ve lojistik ajanı", GlossingPolicy.ProgressiveGloss),

            // 6. İleri Düzey Model Teknikleri & Mimariler (Distillation, Quantization, Synthesis)
            ("distillation", "damıtma", GlossingPolicy.ProgressiveGloss),
            ("model distillation", "model damıtma", GlossingPolicy.ProgressiveGloss),
            ("quantization", "nicemleme", GlossingPolicy.ProgressiveGloss),
            ("synthetic data", "sentetik veri", GlossingPolicy.ProgressiveGloss),
            ("synthetic data generation", "sentetik veri üretimi", GlossingPolicy.ProgressiveGloss),
            ("open-weight models", "açık ağırlıklı modeller", GlossingPolicy.ProgressiveGloss),
            ("open-weight model", "açık ağırlıklı model", GlossingPolicy.ProgressiveGloss),
            ("conversational turn-taking", "konuşmada sıra alma", GlossingPolicy.ProgressiveGloss),
            ("context-aware interaction", "bağlam duyarlı etkileşim", GlossingPolicy.ProgressiveGloss),
            ("context-aware", "bağlam duyarlı", GlossingPolicy.ProgressiveGloss),
            ("natural language understanding", "doğal dil anlama", GlossingPolicy.ProgressiveGloss),
            ("structured content generation", "yapılandırılmış içerik üretimi", GlossingPolicy.ProgressiveGloss),
            ("contextual interpretation and decision making", "bağlamsal yorumlama ve karar alma", GlossingPolicy.ProgressiveGloss),
            ("adaptive planning", "uyarlanabilir planlama", GlossingPolicy.ProgressiveGloss),
            ("information summarization", "bilgi özetleme", GlossingPolicy.ProgressiveGloss),
            ("unstructured data", "yapılandırılmamış veri", GlossingPolicy.ProgressiveGloss),
            ("code generation", "kod üretimi", GlossingPolicy.ProgressiveGloss),
            ("routine task automation", "rutin görev otomasyonu", GlossingPolicy.ProgressiveGloss),
            ("multimodal information synthesis", "çok modlu bilgi sentezi", GlossingPolicy.ProgressiveGloss),
            ("robotic process automation", "robotik süreç otomasyonu", GlossingPolicy.ProgressiveGloss),

            // 7. Mimari İlkeler & Karşılaştırma Başlıkları
            ("scalability", "ölçeklenebilirlik", GlossingPolicy.ProgressiveGloss),
            ("modularity", "modülerlik", GlossingPolicy.ProgressiveGloss),
            ("continuous learning", "sürekli öğrenme", GlossingPolicy.ProgressiveGloss),
            ("resilience", "dayanıklılık", GlossingPolicy.ProgressiveGloss),
            ("future-proofing", "geleceğe uyumluluk", GlossingPolicy.ProgressiveGloss),
            ("strengths", "güçlü yönler", GlossingPolicy.ProgressiveGloss),
            ("trade-offs", "ödünleşimler", GlossingPolicy.ProgressiveGloss),
            ("best for", "en uygun senaryolar", GlossingPolicy.ProgressiveGloss)
        };

    /// <summary>
    /// Sektör standardı temel yapay zeka ve yazılım terimlerinin Türkçe karşılıklarını ve politikalarını asenkron olarak veritabanına işler.
    /// Kullanıcı tarafından elle düzenlenmiş (IsReviewed=1) terimleri ASLA ezmez.
    /// </summary>
    public async Task SeedDefaultTurkishTranslationsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow.ToString("o");
        var insertSql = """
            INSERT INTO Terms (EnglishTerm, TurkishTerm, Policy, IsReviewed, CreatedAt, UpdatedAt)
            VALUES (@EnglishTerm, @TurkishTerm, @Policy, 0, @Now, @Now)
            ON CONFLICT(EnglishTerm) DO UPDATE SET
                TurkishTerm = excluded.TurkishTerm,
                Policy = excluded.Policy
            WHERE Terms.IsReviewed = 0;
            """;

        foreach (var (en, tr, policy) in DefaultCoreTerms)
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

    public void SeedDefaultTurkishTranslations()
    {
        SeedDefaultTurkishTranslationsAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Henüz senin onayından geçmemiş (IsReviewed=0) terimleri getirir.
    /// </summary>
    public List<GlossaryEntry> GetUnreviewedTerms()
    {
        using var connection = _connectionFactory.CreateOpenConnection();

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
        using var connection = _connectionFactory.CreateOpenConnection();

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
                Policy = excluded.Policy,
                IsReviewed = excluded.IsReviewed,
                SourceBook = COALESCE(excluded.SourceBook, Terms.SourceBook),
                UpdatedAt = excluded.UpdatedAt;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@EnglishTerm", entry.EnglishTerm.Trim());
        cmd.Parameters.AddWithValue("@TurkishTerm", (object?)entry.TurkishTerm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@EnglishDefinition", (object?)entry.EnglishDefinition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Policy", (int)entry.Policy);
        cmd.Parameters.AddWithValue("@IsReviewed", entry.IsReviewed ? 1 : 0);
        cmd.Parameters.AddWithValue("@SourceBook", (object?)entry.SourceBook ?? "CLI");
        cmd.Parameters.AddWithValue("@Now", now);

        return await cmd.ExecuteNonQueryAsync(ct);
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

    /// <summary>
    /// Metnin SHA-256 özetini (hash) hesaplar. Kaynak metin değiştiğinde tespiti sağlar.
    /// </summary>
    public static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Belirli bir bölüm ve segment için daha önce kaydedilmiş checkpoint'i döner.
    /// </summary>
    public async Task<SegmentCheckpoint?> GetCheckpointAsync(string chapterFile, int segmentIndex, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        var sql = """
            SELECT ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt
            FROM SegmentCheckpoints
            WHERE ChapterFile = @ChapterFile AND SegmentIndex = @SegmentIndex;
            """;

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@ChapterFile", chapterFile);
        command.Parameters.AddWithValue("@SegmentIndex", segmentIndex);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new SegmentCheckpoint
            {
                ChapterFile = reader.GetString(0),
                SegmentIndex = reader.GetInt32(1),
                OriginalHash = reader.GetString(2),
                TagName = reader.GetString(3),
                OriginalText = reader.GetString(4),
                TranslatedText = reader.GetString(5),
                StageStatus = reader.GetString(6),
                UpdatedAt = DateTime.Parse(reader.GetString(7))
            };
        }

        return null;
    }

    /// <summary>
    /// Bölümde tamamlanmış tüm segment checkpoint'lerini sözlük olarak döner (Hızlı O(1) arama için).
    /// </summary>
    public async Task<Dictionary<int, SegmentCheckpoint>> GetCompletedCheckpointsAsync(string chapterFile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        var sql = """
            SELECT ChapterFile, SegmentIndex, OriginalHash, TagName, OriginalText, TranslatedText, StageStatus, UpdatedAt
            FROM SegmentCheckpoints
            WHERE ChapterFile = @ChapterFile AND StageStatus = 'Completed';
            """;

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@ChapterFile", chapterFile);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<int, SegmentCheckpoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var cp = new SegmentCheckpoint
            {
                ChapterFile = reader.GetString(0),
                SegmentIndex = reader.GetInt32(1),
                OriginalHash = reader.GetString(2),
                TagName = reader.GetString(3),
                OriginalText = reader.GetString(4),
                TranslatedText = reader.GetString(5),
                StageStatus = reader.GetString(6),
                UpdatedAt = DateTime.Parse(reader.GetString(7))
            };
            result[cp.SegmentIndex] = cp;
        }

        return result;
    }

    /// <summary>
    /// Segment çevirisini atomik olarak SQLite veritabanına kaydeder veya günceller.
    /// </summary>
    public async Task SaveCheckpointAsync(SegmentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        var sql = """
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

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@ChapterFile", checkpoint.ChapterFile);
        command.Parameters.AddWithValue("@SegmentIndex", checkpoint.SegmentIndex);
        command.Parameters.AddWithValue("@OriginalHash", checkpoint.OriginalHash);
        command.Parameters.AddWithValue("@TagName", checkpoint.TagName);
        command.Parameters.AddWithValue("@OriginalText", checkpoint.OriginalText);
        command.Parameters.AddWithValue("@TranslatedText", checkpoint.TranslatedText);
        command.Parameters.AddWithValue("@StageStatus", checkpoint.StageStatus);
        command.Parameters.AddWithValue("@UpdatedAt", checkpoint.UpdatedAt.ToString("o"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Bir bölümün checkpoint önbelleğini temizler (--fresh komutunda kullanılır).
    /// </summary>
    public async Task ClearCheckpointsAsync(string chapterFile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken);

        var sql = "DELETE FROM SegmentCheckpoints WHERE ChapterFile = @ChapterFile;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@ChapterFile", chapterFile);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}