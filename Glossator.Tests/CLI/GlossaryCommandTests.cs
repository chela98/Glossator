using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glossator.Tests.CLI;

public class GlossaryCommandTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteGlossaryRepository _repository;

    public GlossaryCommandTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_cli_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _repository = new SqliteGlossaryRepository(_connectionFactory);

        using var conn = _connectionFactory.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            """;
        cmd.ExecuteNonQuery();
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
        retrieved.TurkishTerm = "Özerk Ajan";
        await _repository.AddOrUpdateTermAsync(retrieved);

        var updated = await _repository.GetTermByIdAsync(retrieved.Id);
        Assert.NotNull(updated);
        Assert.Equal("Özerk Ajan", updated.TurkishTerm);
    }

    [Fact]
    public async Task SearchTermsAsync_FindsMatchesInEnglishAndTurkish()
    {
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Deep Reinforcement Learning",
            TurkishTerm = "Derin Pekiştirmeli Öğrenme",
            Policy = GlossingPolicy.ProgressiveGloss
        });

        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Supervised Learning",
            TurkishTerm = "Gözetimli Öğrenme",
            Policy = GlossingPolicy.AlwaysTurkish
        });

        var searchEn = await _repository.SearchTermsAsync("Reinforcement");
        Assert.Single(searchEn);
        Assert.Equal("Deep Reinforcement Learning", searchEn[0].EnglishTerm);

        var searchTr = await _repository.SearchTermsAsync("Öğrenme");
        Assert.Equal(2, searchTr.Count);
    }

    [Fact]
    public async Task DeleteTermAsync_RemovesTermSuccessfully()
    {
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Transient Term",
            TurkishTerm = "Geçici Terim"
        });

        var term = await _repository.GetTermByEnglishAsync("Transient Term");
        Assert.NotNull(term);

        var deleted = await _repository.DeleteTermAsync(term.Id);
        Assert.True(deleted);

        var check = await _repository.GetTermByIdAsync(term.Id);
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
