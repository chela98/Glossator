using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossator.Tests.Rules;

public class TermGlossingPostProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteGlossaryRepository _repository;
    private readonly TermUsageTracker _tracker;
    private readonly TermGlossingPostProcessor _sut;

    public TermGlossingPostProcessorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_postproc_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(_dbPath);
        factory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(factory);
        _tracker = new TermUsageTracker("ch01");
        _sut = new TermGlossingPostProcessor(_repository, _tracker);
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
    public async Task GetTermsForSectionAsync_AppliesPoliciesAndProgressiveTransitionsAcrossChapterScope()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "Kubernetes", Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "Embedding", TurkishTerm = "Gömme", Policy = GlossingPolicy.AlwaysTurkish, IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "BlankTurkish", TurkishTerm = null, Policy = GlossingPolicy.AlwaysTurkish, IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "Context Window", TurkishTerm = "Bağlam Penceresi", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = true }
        ]);

        // Act: 1. bölüm içi ilk kullanım (Context Window -> Hibrit)
        var (alwaysEn1, mappings1) = await _sut.GetTermsForSectionAsync(
            "Deploying an Embedding model on Kubernetes expands the Context Window alongside BlankTurkish.");

        // 2. aynı bölüm ikinci kullanım (Context Window -> Saf Türkçe)
        var (_, mappings2) = await _sut.GetTermsForSectionAsync(
            "The Context Window remains bounded.");

        // 3. yeni bölüm -> scope sıfırlanmalı, Context Window yeniden Hibrit olmalı
        _sut.BeginChapterScope("ch02");
        var (_, mappings3) = await _sut.GetTermsForSectionAsync(
            "Chapter 2 revisits the Context Window limit.");

        // Assert
        Assert.Equal(["Kubernetes"], alwaysEn1);
        Assert.Equal("Gömme", mappings1["Embedding"]);
        Assert.Equal("Bağlam Penceresi (Context Window)", mappings1["Context Window"]);
        Assert.False(mappings1.ContainsKey("BlankTurkish"));

        Assert.Equal("Bağlam Penceresi", mappings2["Context Window"]);
        Assert.Equal("Bağlam Penceresi (Context Window)", mappings3["Context Window"]);
    }

    [Fact]
    public async Task GetTermsForSectionAsync_WhenIsHeadingOrWhitespace_ReturnsEmptyAndDoesNotIncrementTracker()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Agent",
            TurkishTerm = "Ajan",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        // Act
        var headingResult = await _sut.GetTermsForSectionAsync("Using an Agent effectively", isHeading: true);
        var whitespaceResult = await _sut.GetTermsForSectionAsync("   ", isHeading: false);
        var bodyResult = await _sut.GetTermsForSectionAsync("The Agent executes tools.", isHeading: false);

        // Assert
        Assert.Empty(headingResult.AlwaysEnglishTerms);
        Assert.Empty(headingResult.GlossaryTerms);
        Assert.Empty(whitespaceResult.AlwaysEnglishTerms);
        Assert.Empty(whitespaceResult.GlossaryTerms);

        // Başlık ve boşluk çağrıları sayacı artırmadığı için gövdedeki ilk kullanım Hibrit olmalı
        Assert.Equal("Ajan (Agent)", bodyResult.GlossaryTerms["Agent"]);
        Assert.Equal(1, _tracker.GetCurrentCount("Agent"));
    }

    [Fact]
    public async Task RegisterCachedSectionUsageAsync_RehydratesTrackerStateSoResumedSectionUsesPureTurkish()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Vector Store",
            TurkishTerm = "Vektör Deposu",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        // Act: Checkpoint'ten gelen tamamlanmış bir bölümü yeniden oynat
        await _sut.RegisterCachedSectionUsageAsync("A Vector Store persists embeddings.");
        await _sut.RegisterCachedSectionUsageAsync("Vector Store", isHeading: true);
        var resumed = await _sut.GetTermsForSectionAsync("Querying the Vector Store is fast.");

        // Assert
        Assert.Equal(2, _tracker.GetCurrentCount("Vector Store"));
        Assert.Equal("Vektör Deposu", resumed.GlossaryTerms["Vector Store"]);
    }

    [Fact]
    public async Task GetTermsForSectionAsync_DeduplicatesAlwaysEnglishTermsAndOrdersByLongestTermFirst()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "API", Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "REST API", Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true }
        ]);

        // Act
        var (alwaysEn, _) = await _sut.GetTermsForSectionAsync("This REST API uses a modern REST API gateway.");

        // Assert: En uzun terim önce sıralanmalı ve mükerrer kayıt bulunmamalı
        Assert.Equal(["REST API", "API"], alwaysEn);
    }
}
