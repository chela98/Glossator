namespace Glossator.Tests.Serialization;

using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using YamlDotNet.Core;
using Xunit;

public class GlossarySerializationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GlossarySerializationService _service = new();

    public GlossarySerializationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"GlossatorSerial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task YamlSerialization_RoundTrip_PreservesAllDataAndSortsAlphabetically()
    {
        // Arrange
        var tempFile = Path.Combine(_tempDir, "glossary.yaml");
        var inputTerms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "zero-shot learning", TurkishTerm = "sıfır atışlı öğrenme", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = true },
            new() { EnglishTerm = "autonomous agents", TurkishTerm = "otonom ajanlar", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = true },
            new() { EnglishTerm = "machine learning", TurkishTerm = "makine öğrenimi", Policy = GlossingPolicy.AlwaysTurkish, IsReviewed = true }
        };

        // Act
        await _service.ExportAsync(inputTerms, tempFile, SerializationFormat.Yaml);
        var imported = await _service.ImportAsync(tempFile);

        // Assert: Deterministik alfabetik sıralama (A -> M -> Z)
        Assert.Equal(3, imported.Count);
        Assert.Equal("autonomous agents", imported[0].EnglishTerm);
        Assert.Equal("otonom ajanlar", imported[0].TurkishTerm);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, imported[0].Policy);
        Assert.True(imported[0].IsReviewed);

        Assert.Equal("machine learning", imported[1].EnglishTerm);
        Assert.Equal("zero-shot learning", imported[2].EnglishTerm);
    }

    [Fact]
    public async Task JsonSerialization_RoundTrip_PreservesAllData()
    {
        // Arrange
        var tempFile = Path.Combine(_tempDir, "glossary.json");
        var inputTerms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "REST API", TurkishTerm = null, Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true, SourceBook = "Arch" }
        };

        // Act
        await _service.ExportAsync(inputTerms, tempFile, SerializationFormat.Json);
        var imported = await _service.ImportAsync(tempFile);

        // Assert
        Assert.Single(imported);
        Assert.Equal("REST API", imported[0].EnglishTerm);
        Assert.Null(imported[0].TurkishTerm);
        Assert.Equal(GlossingPolicy.AlwaysEnglish, imported[0].Policy);
        Assert.True(imported[0].IsReviewed);
        Assert.Equal("Arch", imported[0].SourceBook);
    }

    [Fact]
    public async Task ExportAndImport_WithTurkishSpecialCharactersAndYamlSymbols_PreservesVerbatim()
    {
        // Arrange
        var tempFile = Path.Combine(_tempDir, "complex_terms.yaml");
        var complexTerms = new List<GlossaryEntry>
        {
            new()
            {
                EnglishTerm = "knowledge base: graph-augmented",
                TurkishTerm = "bilgi tabanı: çizge ile güçlendirilmiş (Örnek: Şeyler & İşler)",
                EnglishDefinition = "Definition with quotes: \"Special\" and symbols: [-, :, #]",
                Policy = GlossingPolicy.ProgressiveGloss,
                IsReviewed = true,
                SourceBook = "Yapay Zekâ ve İleri Doğal Dil İşleme"
            }
        };

        // Act
        await _service.ExportAsync(complexTerms, tempFile, SerializationFormat.Yaml);
        var imported = await _service.ImportAsync(tempFile);

        // Assert
        Assert.Single(imported);
        Assert.Equal(complexTerms[0].EnglishTerm, imported[0].EnglishTerm);
        Assert.Equal(complexTerms[0].TurkishTerm, imported[0].TurkishTerm);
        Assert.Equal(complexTerms[0].EnglishDefinition, imported[0].EnglishDefinition);
        Assert.Equal(complexTerms[0].SourceBook, imported[0].SourceBook);
    }

    [Theory]
    [InlineData(SerializationFormat.Yaml, "glossary.yaml")]
    [InlineData(SerializationFormat.Json, "glossary.json")]
    public async Task ExportAndImport_WhenGivenDirectoryPath_AutoDiscoversExpectedFile(SerializationFormat format, string expectedFileName)
    {
        // Arrange
        var subDir = Path.Combine(_tempDir, format.ToString());
        Directory.CreateDirectory(subDir);
        var terms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "Discovery Test", TurkishTerm = "Keşif Testi", Policy = GlossingPolicy.AlwaysTurkish }
        };

        // Act
        await _service.ExportAsync(terms, subDir, format);
        var imported = await _service.ImportAsync(subDir);

        // Assert
        Assert.True(File.Exists(Path.Combine(subDir, expectedFileName)));
        Assert.Single(imported);
        Assert.Equal("Discovery Test", imported[0].EnglishTerm);
        Assert.Equal("Keşif Testi", imported[0].TurkishTerm);
    }

    [Fact]
    public async Task ExportAsync_WhenPathHasNoExtension_AppendsFormatExtension()
    {
        // Arrange
        var pathWithoutExt = Path.Combine(_tempDir, "custom_glossary");
        var terms = new List<GlossaryEntry> { new() { EnglishTerm = "Embedding", TurkishTerm = "Gömme" } };

        // Act
        await _service.ExportAsync(terms, pathWithoutExt, SerializationFormat.Json);

        // Assert
        Assert.True(File.Exists(pathWithoutExt + ".json"));
        var imported = await _service.ImportAsync(pathWithoutExt + ".json");
        Assert.Single(imported);
        Assert.Equal("Embedding", imported[0].EnglishTerm);
    }

    [Fact]
    public async Task ImportAsync_WhenJsonStoredWithoutJsonExtension_DetectsJsonFromContent()
    {
        // Arrange
        var txtPath = Path.Combine(_tempDir, "glossary.txt");
        await File.WriteAllTextAsync(txtPath, """
            {
              "version": "1.0",
              "terms": [
                { "english": "RAG", "turkish": null, "policy": 1, "reviewed": true }
              ]
            }
            """);

        // Act
        var imported = await _service.ImportAsync(txtPath);

        // Assert
        Assert.Single(imported);
        Assert.Equal("RAG", imported[0].EnglishTerm);
        Assert.Equal(GlossingPolicy.AlwaysEnglish, imported[0].Policy);
    }

    [Fact]
    public async Task ImportAsync_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var missingPath = Path.Combine(_tempDir, "does_not_exist.yaml");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.ImportAsync(missingPath));
    }

    [Fact]
    public async Task ImportAsync_WithCorruptYamlSyntax_ThrowsYamlException()
    {
        // Arrange
        var corruptFile = Path.Combine(_tempDir, "corrupt.yaml");
        await File.WriteAllTextAsync(corruptFile, "terms: [unclosed array\n  invalid: {yaml");

        // Act & Assert
        await Assert.ThrowsAnyAsync<YamlException>(() => _service.ImportAsync(corruptFile));
    }

    [Fact]
    public async Task ImportAsync_WhenYamlIsEmpty_ReturnsEmptyList()
    {
        // Arrange
        var emptyFile = Path.Combine(_tempDir, "empty.yaml");
        await File.WriteAllTextAsync(emptyFile, string.Empty);

        // Act
        var imported = await _service.ImportAsync(emptyFile);

        // Assert
        Assert.Empty(imported);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }
}
