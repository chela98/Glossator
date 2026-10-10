namespace Glossator.Core.Serialization;

using System.Text.Json;
using Glossator.Core.Models.Glossary;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public sealed class GlossarySerializationService : IGlossarySerializationService
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task ExportAsync(
        IEnumerable<GlossaryEntry> terms,
        string filePath,
        SerializationFormat format,
        CancellationToken cancellationToken = default)
    {
        // Git diff karmaşasını önlemek için deterministik alfabetik sıralama
        var orderedTerms = terms
            .OrderBy(t => t.EnglishTerm, StringComparer.OrdinalIgnoreCase)
            .Select(t => new GlossaryItemModel
            {
                English = t.EnglishTerm,
                Turkish = t.TurkishTerm,
                Policy = t.Policy,
                Definition = t.EnglishDefinition,
                Reviewed = t.IsReviewed,
                Source = t.SourceBook
            })
            .ToList();

        var model = new GlossaryFileModel
        {
            Version = "1.0",
            LastUpdated = DateTime.UtcNow.ToString("o"),
            Terms = orderedTerms
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (format == SerializationFormat.Yaml)
        {
            var yamlContent = YamlSerializer.Serialize(model);
            await File.WriteAllTextAsync(filePath, yamlContent, cancellationToken);
        }
        else
        {
            var jsonContent = JsonSerializer.Serialize(model, JsonOptions);
            await File.WriteAllTextAsync(filePath, jsonContent, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<GlossaryEntry>> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Sözlük dosyası bulunamadı.", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        GlossaryFileModel? model;

        var isJson = filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     content.TrimStart().StartsWith("{");

        if (isJson)
        {
            model = JsonSerializer.Deserialize<GlossaryFileModel>(content, JsonOptions);
        }
        else
        {
            model = YamlDeserializer.Deserialize<GlossaryFileModel>(content);
        }

        if (model == null || model.Terms == null)
        {
            return [];
        }

        return model.Terms.Select(item => new GlossaryEntry
        {
            EnglishTerm = item.English,
            TurkishTerm = item.Turkish,
            Policy = item.Policy,
            EnglishDefinition = item.Definition,
            IsReviewed = item.Reviewed,
            SourceBook = item.Source
        }).ToList();
    }
}
