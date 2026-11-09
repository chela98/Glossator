namespace Glossator.Tests.CLI;

using Glossator.Cli.Commands.Glossary;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Microsoft.Data.Sqlite;
using Xunit;

public class GlossaryCommandTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteGlossaryRepository _repository;

    public GlossaryCommandTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_cli_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _connectionFactory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(_connectionFactory);
    }

    [Fact]
    public async Task GlossaryAddCommand_ExecutesSuccessfullyAndPersistsEntry()
    {
        var command = new GlossaryAddCommand(_repository);
        var settings = new GlossaryAddSettings
        {
            EnglishTerm = "fine-tuning",
            TurkishTerm = "ince ayar",
            Policy = "ProgressiveGloss",
            Definition = "Model adaptation method",
            Reviewed = true,
            Source = "Deep Learning Book"
        };

        var exitCode = await command.ExecuteAsync(null!, settings, CancellationToken.None);
        Assert.Equal(0, exitCode);

        var saved = await _repository.GetTermByEnglishAsync("fine-tuning");
        Assert.NotNull(saved);
        Assert.Equal("ince ayar", saved.TurkishTerm);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, saved.Policy);
        Assert.True(saved.IsReviewed);
        Assert.Equal("Deep Learning Book", saved.SourceBook);
    }

    [Fact]
    public async Task AddOrUpdateTermAsync_InsertsAndUpdatesSuccessfully()
    {
        var entry = new GlossaryEntry
        {
            EnglishTerm = "Autonomous Agent",
            TurkishTerm = "Otonom Ajan",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true,
            SourceBook = "Test"
        };

        var inserted = await _repository.AddOrUpdateTermAsync(entry);
        Assert.True(inserted > 0);

        var retrieved = await _repository.GetTermByEnglishAsync("Autonomous Agent");
        Assert.NotNull(retrieved);
        Assert.Equal("Otonom Ajan", retrieved.TurkishTerm);
        Assert.True(retrieved.IsReviewed);

        // Update
        entry.TurkishTerm = "Özerk Ajan";
        entry.Policy = GlossingPolicy.AlwaysTurkish;
        await _repository.AddOrUpdateTermAsync(entry);

        var updated = await _repository.GetTermByEnglishAsync("Autonomous Agent");
        Assert.NotNull(updated);
        Assert.Equal("Özerk Ajan", updated.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysTurkish, updated.Policy);
    }

    [Fact]
    public async Task UpdateTermReview_WhenTurkishTermNull_PreservesExistingTurkishTranslation()
    {
        // Arrange
        var entry = new GlossaryEntry
        {
            EnglishTerm = "Neural Network",
            TurkishTerm = "Yapay Sinir Ağı",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = false
        };

        await _repository.AddOrUpdateTermAsync(entry);
        var inserted = await _repository.GetTermByEnglishAsync("Neural Network");
        Assert.NotNull(inserted);

        // Act: Kullanıcı sadece politikayı değiştirdi, türkçe terim parametresi null bırakıldı
        _repository.UpdateTermReview(inserted.Id, GlossingPolicy.AlwaysEnglish, turkishTerm: null);

        // Assert: Mevcut Türkçe karşılık silinmemeli, korunmalıdır!
        var afterReview = await _repository.GetTermByIdAsync(inserted.Id);
        Assert.NotNull(afterReview);
        Assert.Equal("Yapay Sinir Ağı", afterReview.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysEnglish, afterReview.Policy);
        Assert.True(afterReview.IsReviewed);
    }

    [Fact]
    public async Task DeleteTermAsync_RemovesTermSuccessfully()
    {
        var term = new GlossaryEntry
        {
            EnglishTerm = "Temp Term",
            TurkishTerm = "Geçici Terim",
            Policy = GlossingPolicy.ProgressiveGloss
        };

        await _repository.AddOrUpdateTermAsync(term);
        var retrieved = await _repository.GetTermByEnglishAsync("Temp Term");
        Assert.NotNull(retrieved);

        var deleted = await _repository.DeleteTermAsync(retrieved.Id);
        Assert.True(deleted);

        var check = await _repository.GetTermByIdAsync(retrieved.Id);
        Assert.Null(check);
    }

    [Fact]
    public async Task ExportAndImportRoundtrip_PreservesDataIntegrity()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"export_import_test_{Guid.NewGuid():N}.yaml");

        try
        {
            await _repository.AddOrUpdateTermAsync(new GlossaryEntry
            {
                EnglishTerm = "Knowledge Graph",
                TurkishTerm = "Bilgi Grafı",
                Policy = GlossingPolicy.AlwaysTurkish,
                IsReviewed = true,
                SourceBook = "Graph AI"
            });

            var terms = await _repository.GetAllTermsAsync();
            var service = new GlossarySerializationService();

            await service.ExportAsync(terms, tempFile, SerializationFormat.Yaml);
            Assert.True(File.Exists(tempFile));

            var importedTerms = await service.ImportAsync(tempFile);
            Assert.Single(importedTerms);
            Assert.Equal("Knowledge Graph", importedTerms[0].EnglishTerm);
            Assert.Equal("Bilgi Grafı", importedTerms[0].TurkishTerm);
            Assert.Equal(GlossingPolicy.AlwaysTurkish, importedTerms[0].Policy);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
