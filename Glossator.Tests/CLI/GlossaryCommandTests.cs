using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glossator.Cli.Commands.Glossary;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Glossator.Tests.CLI;

[Collection("GlobalDiagnosticsAndConsole")]
public class GlossaryCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteGlossaryRepository _repository;
    private readonly GlossarySerializationService _serialization = new();

    public GlossaryCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossator_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "cli_glossary.db");
        var factory = new SqliteConnectionFactory(_dbPath);
        factory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private static async Task<(int ExitCode, string Output)> RunWithTestConsoleAsync(Func<Task<int>> action)
    {
        var originalConsole = AnsiConsole.Console;
        using var testConsole = new TestConsole();
        AnsiConsole.Console = testConsole;
        try
        {
            var code = await action();
            return (code, testConsole.Output);
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    [Fact]
    public async Task GlossaryAddCommand_PersistsValidEntry_AndRejectsMissingEnglishTerm()
    {
        // Arrange
        var command = new GlossaryAddCommand(_repository);

        // Act
        var (okCode, okOut) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryAddSettings
        {
            EnglishTerm = "fine-tuning",
            TurkishTerm = "ince ayar",
            Policy = "ProgressiveGloss",
            Definition = "Model adaptation",
            Reviewed = true,
            Source = "Deep Learning Book"
        }, CancellationToken.None));

        var (errCode, errOut) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryAddSettings
        {
            EnglishTerm = "   "
        }, CancellationToken.None));

        // Assert
        Assert.Equal(0, okCode);
        Assert.Contains("fine-tuning", okOut);

        var saved = await _repository.GetTermByEnglishAsync("fine-tuning");
        Assert.NotNull(saved);
        Assert.Equal("ince ayar", saved.TurkishTerm);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, saved.Policy);
        Assert.True(saved.IsReviewed);
        Assert.Equal("Deep Learning Book", saved.SourceBook);

        Assert.Equal(1, errCode);
        Assert.Contains("--en", errOut);
    }

    [Fact]
    public async Task GlossaryListCommand_FiltersByPolicyUnreviewedAndSearchAndPaginates()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "API", Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "Backpropagation", TurkishTerm = "Geri Yayılım", Policy = GlossingPolicy.AlwaysTurkish, IsReviewed = false },
            new GlossaryEntry { EnglishTerm = "Attention Head", TurkishTerm = "Dikkat Kafası", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = false }
        ]);
        var command = new GlossaryListCommand(_repository);

        // Act
        var (code1, out1) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryListSettings
        {
            UnreviewedOnly = true,
            Policy = "AlwaysTurkish",
            Limit = 10,
            Page = 1
        }, CancellationToken.None));

        var (code2, out2) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryListSettings
        {
            Search = "Attention",
            Limit = 1,
            Page = 1
        }, CancellationToken.None));

        // Assert
        Assert.Equal(0, code1);
        Assert.Contains("Backpropagation", out1);
        Assert.DoesNotContain("Attention Head", out1);
        Assert.DoesNotContain("API", out1);

        Assert.Equal(0, code2);
        Assert.Contains("Attention Head", out2);
    }

    [Fact]
    public async Task GlossarySearchCommand_HandlesMatchesNoMatchesAndEmptyQuery()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Retrieval",
            TurkishTerm = "Geri Getirme",
            IsReviewed = true
        });
        var command = new GlossarySearchCommand(_repository);

        // Act
        var (matchCode, matchOut) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossarySearchSettings { Query = "Retrieval" }, CancellationToken.None));
        var (noneCode, noneOut) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossarySearchSettings { Query = "NoSuchTermXYZ" }, CancellationToken.None));
        var (emptyCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossarySearchSettings { Query = "  " }, CancellationToken.None));

        // Assert
        Assert.Equal(0, matchCode);
        Assert.Contains("Geri Getirme", matchOut);
        Assert.Equal(0, noneCode);
        Assert.Contains("NoSuchTermXYZ", noneOut);
        Assert.Equal(1, emptyCode);
    }

    [Fact]
    public async Task GlossaryEditCommand_UpdatesExistingEntry_AndReturnsErrorWhenNotFound()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Context Engineering",
            TurkishTerm = "Eski Çeviri",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = false
        });
        var existing = await _repository.GetTermByEnglishAsync("Context Engineering");
        var command = new GlossaryEditCommand(_repository);

        // Act
        var (okCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryEditSettings
        {
            Id = existing!.Id,
            TurkishTerm = "Bağlam Mühendisliği",
            Policy = "AlwaysTurkish",
            Reviewed = true
        }, CancellationToken.None));

        var (missingCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryEditSettings
        {
            EnglishTerm = "NonExistentTerm"
        }, CancellationToken.None));

        // Assert
        Assert.Equal(0, okCode);
        var updated = await _repository.GetTermByIdAsync(existing!.Id);
        Assert.NotNull(updated);
        Assert.Equal("Bağlam Mühendisliği", updated.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysTurkish, updated.Policy);
        Assert.True(updated.IsReviewed);
        Assert.Equal(1, missingCode);
    }

    [Fact]
    public async Task GlossaryDeleteCommand_DeletesByIdAndByPositionalTerm_AndFailsWhenMissing()
    {
        // Arrange
        await _repository.AddOrUpdateTermsBatchAsync([
            new GlossaryEntry { EnglishTerm = "TermById", IsReviewed = true },
            new GlossaryEntry { EnglishTerm = "TermByName", IsReviewed = true }
        ]);
        var byIdEntry = await _repository.GetTermByEnglishAsync("TermById");
        var command = new GlossaryDeleteCommand(_repository);

        // Act
        var (delIdCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryDeleteSettings
        {
            Id = byIdEntry!.Id
        }, CancellationToken.None));

        var (delNameCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryDeleteSettings
        {
            TermOrId = "TermByName"
        }, CancellationToken.None));

        var (missingCode, _) = await RunWithTestConsoleAsync(() => command.ExecuteAsync(null!, new GlossaryDeleteSettings
        {
            TermOrId = "DoesNotExist"
        }, CancellationToken.None));

        // Assert
        Assert.Equal(0, delIdCode);
        Assert.Equal(0, delNameCode);
        Assert.Equal(1, missingCode);
        Assert.Empty(await _repository.GetAllTermsAsync());
    }

    [Fact]
    public async Task GlossaryExportAndImportCommands_RoundtripDirectoryAndFileTargetsCorrectly()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Knowledge Graph",
            TurkishTerm = "Bilgi Grafı",
            Policy = GlossingPolicy.AlwaysTurkish,
            IsReviewed = true,
            SourceBook = "Graph AI"
        });
        var exportCmd = new GlossaryExportCommand(_repository, _serialization);
        var importCmd = new GlossaryImportCommand(_repository, _serialization);

        var exportSubDir = Path.Combine(_tempDir, "exported");
        Directory.CreateDirectory(exportSubDir);
        var jsonPath = Path.Combine(_tempDir, "custom.json");

        // Act: Klasöre YAML export + özel JSON path export
        var (expDirCode, _) = await RunWithTestConsoleAsync(() => exportCmd.ExecuteAsync(null!, new GlossaryExportSettings
        {
            FilePath = exportSubDir + Path.DirectorySeparatorChar,
            Format = "yaml"
        }, CancellationToken.None));
        var (expJsonCode, _) = await RunWithTestConsoleAsync(() => exportCmd.ExecuteAsync(null!, new GlossaryExportSettings
        {
            FilePath = jsonPath,
            Format = "json"
        }, CancellationToken.None));

        // DB'yi temizle ve yalnızca klasör yolunu vererek import'u test et
        var all = await _repository.GetAllTermsAsync();
        await _repository.DeleteTermsBatchAsync(all.Select(t => t.Id));
        Assert.Empty(await _repository.GetAllTermsAsync());

        var (impDirCode, _) = await RunWithTestConsoleAsync(() => importCmd.ExecuteAsync(null!, new GlossaryImportSettings
        {
            FilePath = exportSubDir,
            MarkAsReviewed = true
        }, CancellationToken.None));

        var (impMissingCode, _) = await RunWithTestConsoleAsync(() => importCmd.ExecuteAsync(null!, new GlossaryImportSettings
        {
            FilePath = Path.Combine(_tempDir, "missing.yaml")
        }, CancellationToken.None));

        // Assert
        Assert.Equal(0, expDirCode);
        Assert.True(File.Exists(Path.Combine(exportSubDir, "glossary.yaml")));
        Assert.Equal(0, expJsonCode);
        Assert.True(File.Exists(jsonPath));

        Assert.Equal(0, impDirCode);
        var restored = await _repository.GetTermByEnglishAsync("Knowledge Graph");
        Assert.NotNull(restored);
        Assert.Equal("Bilgi Grafı", restored.TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysTurkish, restored.Policy);
        Assert.True(restored.IsReviewed);

        Assert.Equal(1, impMissingCode);
    }
}
