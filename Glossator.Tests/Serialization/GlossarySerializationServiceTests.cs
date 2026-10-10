namespace Glossator.Tests.Serialization;

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
}
