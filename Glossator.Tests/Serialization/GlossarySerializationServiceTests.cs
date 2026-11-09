namespace Glossator.Tests.Serialization;

using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Xunit;

public class GlossarySerializationServiceTests
{
    [Fact]
    public async Task YamlSerialization_RoundTrip_PreservesAllDataAndSortsAlphabetically()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"glossary_{Guid.NewGuid():N}.yaml");
        var service = new GlossarySerializationService();

        var inputTerms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "zero-shot learning", TurkishTerm = "sıfır atışlı öğrenme", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = true },
            new() { EnglishTerm = "autonomous agents", TurkishTerm = "otonom ajanlar", Policy = GlossingPolicy.ProgressiveGloss, IsReviewed = true },
            new() { EnglishTerm = "machine learning", TurkishTerm = "makine öğrenimi", Policy = GlossingPolicy.AlwaysTurkish, IsReviewed = true }
        };

        try
        {
            await service.ExportAsync(inputTerms, tempFile, SerializationFormat.Yaml);
            var imported = await service.ImportAsync(tempFile);

            Assert.Equal(3, imported.Count);

            // Deterministik alfabetik sıralama kontrolü: A -> M -> Z
            Assert.Equal("autonomous agents", imported[0].EnglishTerm);
            Assert.Equal("otonom ajanlar", imported[0].TurkishTerm);
            Assert.Equal(GlossingPolicy.ProgressiveGloss, imported[0].Policy);

            Assert.Equal("machine learning", imported[1].EnglishTerm);
            Assert.Equal("zero-shot learning", imported[2].EnglishTerm);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task JsonSerialization_RoundTrip_PreservesAllData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"glossary_{Guid.NewGuid():N}.json");
        var service = new GlossarySerializationService();

        var inputTerms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "REST API", TurkishTerm = null, Policy = GlossingPolicy.AlwaysEnglish, IsReviewed = true }
        };

        try
        {
            await service.ExportAsync(inputTerms, tempFile, SerializationFormat.Json);
            var imported = await service.ImportAsync(tempFile);

            Assert.Single(imported);
            Assert.Equal("REST API", imported[0].EnglishTerm);
            Assert.Equal(GlossingPolicy.AlwaysEnglish, imported[0].Policy);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportAndImport_WithTurkishSpecialCharactersAndYamlSymbols_PreservesVerbatim()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"complex_terms_{Guid.NewGuid():N}.yaml");
        var service = new GlossarySerializationService();

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

        try
        {
            await service.ExportAsync(complexTerms, tempFile, SerializationFormat.Yaml);
            var imported = await service.ImportAsync(tempFile);

            Assert.Single(imported);
            Assert.Equal(complexTerms[0].EnglishTerm, imported[0].EnglishTerm);
            Assert.Equal(complexTerms[0].TurkishTerm, imported[0].TurkishTerm);
            Assert.Equal(complexTerms[0].EnglishDefinition, imported[0].EnglishDefinition);
            Assert.Equal(complexTerms[0].SourceBook, imported[0].SourceBook);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ImportAsync_WhenGivenDirectoryPath_AutomaticallyDiscoversGlossaryYaml()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SerialDir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var service = new GlossarySerializationService();

        var terms = new List<GlossaryEntry>
        {
            new() { EnglishTerm = "Discovery Test", TurkishTerm = "Keşif Testi" }
        };

        try
        {
            // Doğrudan dizin verilir (dosya adı verilmez)
            await service.ExportAsync(terms, tempDir, SerializationFormat.Yaml);
            Assert.True(File.Exists(Path.Combine(tempDir, "glossary.yaml")));

            var imported = await service.ImportAsync(tempDir);
            Assert.Single(imported);
            Assert.Equal("Discovery Test", imported[0].EnglishTerm);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ImportAsync_WithCorruptYamlSyntax_ThrowsException()
    {
        var corruptFile = Path.Combine(Path.GetTempPath(), $"corrupt_{Guid.NewGuid():N}.yaml");
        var service = new GlossarySerializationService();

        try
        {
            await File.WriteAllTextAsync(corruptFile, "terms: [unclosed array\n  invalid: {yaml");
            await Assert.ThrowsAnyAsync<Exception>(() => service.ImportAsync(corruptFile));
        }
        finally
        {
            if (File.Exists(corruptFile)) File.Delete(corruptFile);
        }
    }

    [Fact]
    public void LoadDefaultCoreTerms_ReadsFromEmbeddedResourceYamlSuccessfully()
    {
        var terms = SqliteGlossaryRepository.LoadDefaultCoreTerms();

        Assert.NotNull(terms);
        Assert.True(terms.Count >= 20, $"Expected at least 20 seed terms, but got {terms.Count}");

        Assert.Contains(terms, t => t.English == "LLM" && t.Policy == GlossingPolicy.AlwaysEnglish);
        Assert.Contains(terms, t => t.English == "data" && t.Policy == GlossingPolicy.AlwaysTurkish);
        Assert.Contains(terms, t => t.English == "boilerplate" && t.Policy == GlossingPolicy.ProgressiveGloss);
    }
}
