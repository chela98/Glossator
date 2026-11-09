namespace Glossator.Tests.Serialization;

using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Microsoft.Data.Sqlite;
using Xunit;

public class GlossaryMasterSeedTests : IDisposable
{
    private readonly string _testEnvironmentDir;
    private readonly string _testDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteGlossaryRepository _repository;
    private readonly GlossarySerializationService _serialization;

    public GlossaryMasterSeedTests()
    {
        // Kullanıcının gerçek ~/.glossator veya masaüstü dizinlerini kirletmeden izole temp ortamı oluştur
        _testEnvironmentDir = Path.Combine(Path.GetTempPath(), "GlossatorSeedEnv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testEnvironmentDir);

        _testDbPath = Path.Combine(_testEnvironmentDir, "test_glossator.db");
        _factory = new SqliteConnectionFactory(_testDbPath);
        _factory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(_factory);
        _serialization = new GlossarySerializationService();
    }

    [Fact]
    public async Task MasterSeedPipeline_WithIsolatedEnvironment_ExportsAndImportsWithoutDataLoss()
    {
        // Deterministik tohum veri seti
        var seedEntries = new List<GlossaryEntry>
        {
            new()
            {
                EnglishTerm = "Retrieval-Augmented Generation",
                TurkishTerm = "Geri Getirmeyle Güçlendirilmiş Üretim",
                Policy = GlossingPolicy.ProgressiveGloss,
                EnglishDefinition = "AI framework that retrieves facts from external knowledge base",
                IsReviewed = true,
                SourceBook = "AI Architecture"
            },
            new()
            {
                EnglishTerm = "Large Language Model",
                TurkishTerm = "Büyük Dil Modeli",
                Policy = GlossingPolicy.AlwaysEnglish,
                EnglishDefinition = "Deep learning model trained on vast amounts of text",
                IsReviewed = true,
                SourceBook = "AI Architecture"
            },
            new()
            {
                EnglishTerm = "Vector Database",
                TurkishTerm = "Vektör Veri Tabanı",
                Policy = GlossingPolicy.AlwaysTurkish,
                EnglishDefinition = "Database optimized for multi-dimensional vector search",
                IsReviewed = true,
                SourceBook = "AI Architecture"
            }
        };

        // 1. İzole YAML'a aktar
        var targetYaml = Path.Combine(_testEnvironmentDir, "master_glossary.yaml");
        await _serialization.ExportAsync(seedEntries, targetYaml, SerializationFormat.Yaml);
        Assert.True(File.Exists(targetYaml));

        // 2. YAML'dan içe aktar
        var imported = await _serialization.ImportAsync(targetYaml);
        Assert.Equal(3, imported.Count);

        // 3. Veritabanına toplu kaydet
        var insertedCount = await _repository.AddOrUpdateTermsBatchAsync(imported);
        Assert.Equal(3, insertedCount);

        // 4. Veritabanından geri sorgula ve doğrula
        var allTerms = await _repository.GetAllTermsAsync();
        Assert.Equal(3, allTerms.Count);
        Assert.Contains(allTerms, t => t.EnglishTerm == "Large Language Model" && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(allTerms, t => t.EnglishTerm == "Vector Database" && t.Policy == GlossingPolicy.AlwaysTurkish);
        Assert.Contains(allTerms, t => t.EnglishTerm == "Retrieval-Augmented Generation" && t.Policy == GlossingPolicy.ProgressiveGloss);
    }

    [Fact]
    public async Task SeedDefaultTurkishTranslationsAsync_OnFreshDatabase_PopulatesCoreTermsSuccessfully()
    {
        // Başlangıçta veritabanı boş olmalı
        var initialTerms = await _repository.GetAllTermsAsync();
        Assert.Empty(initialTerms);

        // Gömülü tohum sözlüğü işlet
        await _repository.SeedDefaultTurkishTranslationsAsync();

        var seeded = await _repository.GetAllTermsAsync();
        Assert.NotEmpty(seeded);
        Assert.True(seeded.Count >= 10, "Gömülü tohum sözlük en az 10 temel terim içermelidir.");

        // Önemli dokunulmazların ve terimlerin varlığını doğrula
        Assert.Contains(seeded, t => t.EnglishTerm.Equals("API", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(seeded, t => t.EnglishTerm.Equals("Docker", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(seeded, t => t.EnglishTerm.Equals("LLM", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);

        // İkinci kez çağrıldığında mükerrer kayıt oluşmamalı (Idempotent)
        await _repository.SeedDefaultTurkishTranslationsAsync();
        var secondRun = await _repository.GetAllTermsAsync();
        Assert.Equal(seeded.Count, secondRun.Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testEnvironmentDir))
            {
                Directory.Delete(_testEnvironmentDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
