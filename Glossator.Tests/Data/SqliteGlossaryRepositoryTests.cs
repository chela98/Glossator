using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossator.Tests.Data;

public class SqliteGlossaryRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteGlossaryRepository _repository;

    public SqliteGlossaryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_repo_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _factory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch { }
    }

    [Fact]
    public void LoadDefaultCoreTerms_ReadsEmbeddedSeedYamlWithCoreEntries()
    {
        // Act
        var coreTerms = SqliteGlossaryRepository.LoadDefaultCoreTerms();

        // Assert
        Assert.True(coreTerms.Count >= 10);
        Assert.Contains(coreTerms, t => t.English.Equals("API", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(coreTerms, t => t.English.Equals("Docker", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(coreTerms, t => t.English.Equals("LLM", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);
    }

    [Fact]
    public async Task ImportTermsAsync_WhenTermIsReviewed_PreservesReviewedFieldsWithoutOverwriting()
    {
        // Arrange: Onaylanmış (IsReviewed = true) bir kayıt ekle
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Transformer",
            TurkishTerm = "Dönüştürücü",
            EnglishDefinition = "User curated definition",
            Policy = GlossingPolicy.AlwaysTurkish,
            IsReviewed = true,
            SourceBook = "Reviewed Book"
        });

        // Ayrıca onaylanmamış (IsReviewed = false) bir kayıt ekle
        await _repository.ImportTermsAsync([
            new GlossaryEntry { EnglishTerm = "UnreviewedTerm", EnglishDefinition = null }
        ], "FirstBook");

        // Act: İki terimi yeni tanımlarla içe aktar
        var processed = await _repository.ImportTermsAsync([
            new GlossaryEntry
            {
                EnglishTerm = "Transformer",
                TurkishTerm = "AttemptedOverwrite",
                EnglishDefinition = "New import definition",
                Policy = GlossingPolicy.AlwaysEnglish
            },
            new GlossaryEntry
            {
                EnglishTerm = "UnreviewedTerm",
                EnglishDefinition = "Filled definition"
            },
            new GlossaryEntry
            {
                EnglishTerm = "NewImportedTerm",
                TurkishTerm = "Yeni Terim",
                Policy = GlossingPolicy.ProgressiveGloss
            }
        ], "SecondBook");

        // Assert
        Assert.Equal(2, processed); // Transformer WHERE IsReviewed = 0 koşulu nedeniyle güncellenmez

        var reviewed = await _repository.GetTermByEnglishAsync("Transformer");
        Assert.NotNull(reviewed);
        Assert.Equal("Dönüştürücü", reviewed.TurkishTerm);
        Assert.Equal("User curated definition", reviewed.EnglishDefinition);
        Assert.Equal(GlossingPolicy.AlwaysTurkish, reviewed.Policy);
        Assert.Equal("Reviewed Book", reviewed.SourceBook);
        Assert.True(reviewed.IsReviewed);

        var unreviewed = await _repository.GetTermByEnglishAsync("UnreviewedTerm");
        Assert.NotNull(unreviewed);
        Assert.Equal("Filled definition", unreviewed.EnglishDefinition);
        Assert.Equal("FirstBook", unreviewed.SourceBook);
        Assert.False(unreviewed.IsReviewed);

        var inserted = await _repository.GetTermByEnglishAsync("NewImportedTerm");
        Assert.NotNull(inserted);
        Assert.False(inserted.IsReviewed);
    }

    [Fact]
    public async Task CleanupObsoleteTermsAsync_RemovesLegacyNoiseAndUnicodeHyphenDuplicates()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "LLMs", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "RNNs", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "developer agents agents", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "Büyük Dil Modeli (LLM)", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "multi\u2010agent", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "Vector Store", TurkishTerm = "Vektör Deposu", IsReviewed = true }
        ]);

        // Act
        await _repository.CleanupObsoleteTermsAsync();
        var remaining = await _repository.GetAllTermsAsync();

        // Assert
        var survivor = Assert.Single(remaining);
        Assert.Equal("Vector Store", survivor.EnglishTerm);
    }

    [Fact]
    public async Task SeedDefaultTurkishTranslationsAsync_PopulatesDefaultsIdempotently_AndProtectsReviewedTerms()
    {
        // Arrange: Core terimlerden biri olan "LLM" kullanıcı tarafından özelleştirilmiş ve onaylanmış olsun
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "LLM",
            TurkishTerm = "Özel Büyük Dil Modeli",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        // Act
        await _repository.SeedDefaultTurkishTranslationsAsync();
        var firstSeed = await _repository.GetAllTermsAsync();

        // İkinci çalışma idempotent olmalı
        await _repository.SeedDefaultTurkishTranslationsAsync();
        var secondSeed = await _repository.GetAllTermsAsync();

        // Assert
        Assert.Equal(firstSeed.Count, secondSeed.Count);
        Assert.Contains(secondSeed, t => t.EnglishTerm.Equals("API", StringComparison.OrdinalIgnoreCase) && t.Policy == GlossingPolicy.AlwaysEnglish);

        var customLlm = secondSeed.First(t => t.EnglishTerm.Equals("LLM", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Özel Büyük Dil Modeli", customLlm.TurkishTerm);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, customLlm.Policy);
    }

    [Fact]
    public async Task AddOrUpdateTermAsync_InsertsAndUpdatesEntry()
    {
        // Arrange
        var entry = new GlossaryEntry
        {
            EnglishTerm = "Autonomous Agent",
            TurkishTerm = "Otonom Ajan",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true,
            SourceBook = "Test"
        };

        // Act
        var id = await _repository.AddOrUpdateTermAsync(entry);
        var inserted = await _repository.GetTermByEnglishAsync("Autonomous Agent");

        entry.TurkishTerm = "Özerk Ajan";
        entry.Policy = GlossingPolicy.AlwaysTurkish;
        await _repository.AddOrUpdateTermAsync(entry);
        var updated = await _repository.GetTermByEnglishAsync("Autonomous Agent");

        // Assert
        Assert.True(id > 0);
        Assert.NotNull(inserted);
        Assert.Equal("Otonom Ajan", inserted.TurkishTerm);
        Assert.True(inserted.IsReviewed);

        Assert.NotNull(updated);
        Assert.Equal("Özerk Ajan", updated.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysTurkish, updated.Policy);
    }

    [Fact]
    public async Task UpdateTermReview_WhenTurkishTermNull_PreservesExistingTurkishTranslation()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Neural Network",
            TurkishTerm = "Yapay Sinir Ağı",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = false
        });
        var inserted = await _repository.GetTermByEnglishAsync("Neural Network");
        Assert.NotNull(inserted);

        // Act
        _repository.UpdateTermReview(inserted.Id, GlossingPolicy.AlwaysEnglish, turkishTerm: null);

        // Assert
        var afterReview = await _repository.GetTermByIdAsync(inserted.Id);
        Assert.NotNull(afterReview);
        Assert.Equal("Yapay Sinir Ağı", afterReview.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysEnglish, afterReview.Policy);
        Assert.True(afterReview.IsReviewed);
    }

    [Fact]
    public async Task UpdateTermManualAsync_AllowsUnreviewingAndClearingTurkishTerm()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Agentic Workflow",
            TurkishTerm = "Ajan İş Akışı",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });
        var existing = await _repository.GetTermByEnglishAsync("Agentic Workflow");
        Assert.NotNull(existing);

        // Act
        existing.IsReviewed = false;
        existing.Policy = GlossingPolicy.AlwaysEnglish;
        existing.TurkishTerm = "";
        await _repository.UpdateTermManualAsync(existing);

        // Assert
        var updated = await _repository.GetTermByIdAsync(existing.Id);
        Assert.NotNull(updated);
        Assert.False(updated.IsReviewed);
        Assert.Equal(GlossingPolicy.AlwaysEnglish, updated.Policy);
        Assert.Null(updated.TurkishTerm);
    }

    [Fact]
    public async Task SearchTermsAsync_MatchesAcrossEnglishTurkishAndDefinitionColumns()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "Embeddings", TurkishTerm = "Vektör Gömmeleri", EnglishDefinition = "Dense numeric vectors" },
            new GlossaryEntry { EnglishTerm = "Sharding", TurkishTerm = "Parçalama", EnglishDefinition = "Horizontal partitioning" },
            new GlossaryEntry { EnglishTerm = "Inference", TurkishTerm = "Çıkarım", EnglishDefinition = "Model serving execution" }
        ]);

        // Act
        var byEnglish = await _repository.SearchTermsAsync("embed");
        var byTurkish = await _repository.SearchTermsAsync("Parçalama");
        var byDefinition = await _repository.SearchTermsAsync("serving");

        // Assert
        Assert.Equal("Embeddings", Assert.Single(byEnglish).EnglishTerm);
        Assert.Equal("Sharding", Assert.Single(byTurkish).EnglishTerm);
        Assert.Equal("Inference", Assert.Single(byDefinition).EnglishTerm);
    }

    [Fact]
    public async Task DeleteTermAsync_AndBatchOperations_UpdateAndDeleteExpectedIds()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "Term One", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = false },
            new GlossaryEntry { EnglishTerm = "Term Two", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = false },
            new GlossaryEntry { EnglishTerm = "Term Three", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = false }
        ]);

        var t1 = await _repository.GetTermByEnglishAsync("Term One");
        var t2 = await _repository.GetTermByEnglishAsync("Term Two");
        var t3 = await _repository.GetTermByEnglishAsync("Term Three");

        // Act
        var updatedCount = await _repository.BatchUpdateManualAsync([t1!.Id, t2!.Id], isReviewed: true, policy: GlossingPolicy.AlwaysTurkish);
        var deletedSingle = await _repository.DeleteTermAsync(t3!.Id);
        var deletedBatch = await _repository.DeleteTermsBatchAsync([t1.Id, t2.Id]);

        // Assert
        Assert.Equal(2, updatedCount);
        Assert.True(deletedSingle);
        Assert.Equal(2, deletedBatch);
        Assert.Empty(await _repository.GetAllTermsAsync());
    }
}
