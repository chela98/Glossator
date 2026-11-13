using System.IO;
using System.Text.Json;
using Glossator.Core.Data;
using Glossator.Core.Models.Epub;

namespace Glossator.App.Infrastructure;

public sealed record RecentBookEntry
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public string? Publisher { get; init; }
    public string? CoverImagePath { get; init; }
    public int TotalChapters { get; init; }
    public int TranslatedChapters { get; init; }
    public DateTime LastOpened { get; init; } = DateTime.UtcNow;

    public double ProgressPercentage => TotalChapters == 0 ? 0 : Math.Round((double)TranslatedChapters / TotalChapters * 100, 1);

    public bool ExistsOnDisk => File.Exists(Path) || Directory.Exists(Path);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ValidCoverImagePath =>
        !string.IsNullOrWhiteSpace(CoverImagePath) && File.Exists(CoverImagePath)
            ? CoverImagePath
            : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string FallbackMonogram => BookCoverFallback.ForBook(Title, Author).Monogram;

    [System.Text.Json.Serialization.JsonIgnore]
    public string FallbackBackgroundHex => BookCoverFallback.ForBook(Title, Author).BackgroundHex;
}

public static class RecentBooksManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly SemaphoreSlim IoLock = new(1, 1);

    public static string GetRecentBooksJsonPath()
    {
        return Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), "recent_books.json");
    }

    public static List<EpubItem> GetTranslatableDocuments(EpubPackage package)
    {
        return package.Spine
            .Where(item => item.IsDocument &&
                           item.Role != EpubDocumentRole.Index &&
                           item.Role != EpubDocumentRole.Cover &&
                           item.Role != EpubDocumentRole.Navigation)
            .ToList();
    }

    public static string DetectPublisher(EpubPackage package)
    {
        var allPaths = string.Join(" ", package.Manifest.Values.Select(v => v.Href));
        if (allPaths.Contains("oreilly", StringComparison.OrdinalIgnoreCase)) return "O'Reilly";
        if (allPaths.Contains("packt", StringComparison.OrdinalIgnoreCase)) return "Packt";
        if (allPaths.Contains("wiley", StringComparison.OrdinalIgnoreCase)) return "Wiley";
        if (allPaths.Contains("manning", StringComparison.OrdinalIgnoreCase)) return "Manning";
        return "Teknik Yayıncı";
    }

    public static async Task<List<RecentBookEntry>> LoadRecentBooksAsync(CancellationToken ct = default)
    {
        await IoLock.WaitAsync(ct);
        try
        {
            return await LoadUnlockedAsync(ct);
        }
        finally
        {
            IoLock.Release();
        }
    }

    private static async Task<List<RecentBookEntry>> LoadUnlockedAsync(CancellationToken ct)
    {
        var filePath = GetRecentBooksJsonPath();
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var list = JsonSerializer.Deserialize<List<RecentBookEntry>>(json, JsonOptions) ?? [];
            return list.OrderByDescending(b => b.LastOpened).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static async Task<RecentBookEntry> RegisterOrUpdateAsync(string bookPath, EpubPackage package, CancellationToken ct = default)
    {
        await IoLock.WaitAsync(ct);
        try
        {
            var normalizedPath = Path.GetFullPath(bookPath);
            var recent = await LoadUnlockedAsync(ct);
            var translatableDocs = GetTranslatableDocuments(package);
            var translatedCount = translatableDocs.Count(doc =>
                DocumentTranslationStatus.IsDocumentTranslated(package.OpfDirectory, doc.Href));

            var entry = new RecentBookEntry
            {
                Path = normalizedPath,
                Title = package.Title,
                Author = package.Author,
                Publisher = DetectPublisher(package),
                CoverImagePath = package.ResolveCoverImageFullPath(),
                TotalChapters = translatableDocs.Count,
                TranslatedChapters = translatedCount,
                LastOpened = DateTime.UtcNow
            };

            recent.RemoveAll(b => Path.GetFullPath(b.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, entry);
            if (recent.Count > 10)
            {
                recent = recent.Take(10).ToList();
            }

            await SaveListUnlockedAsync(recent, ct);
            return entry;
        }
        finally
        {
            IoLock.Release();
        }
    }

    public static async Task RemoveBookAsync(string bookPath, CancellationToken ct = default)
    {
        await IoLock.WaitAsync(ct);
        try
        {
            var normalizedPath = Path.GetFullPath(bookPath);
            var recent = await LoadUnlockedAsync(ct);
            recent.RemoveAll(b => Path.GetFullPath(b.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            await SaveListUnlockedAsync(recent, ct);
        }
        finally
        {
            IoLock.Release();
        }
    }

    public static async Task<int> CleanMissingBooksAsync(CancellationToken ct = default)
    {
        await IoLock.WaitAsync(ct);
        try
        {
            var recent = await LoadUnlockedAsync(ct);
            int removed = recent.RemoveAll(b => !File.Exists(b.Path) && !Directory.Exists(b.Path));
            if (removed > 0)
            {
                await SaveListUnlockedAsync(recent, ct);
            }
            return removed;
        }
        finally
        {
            IoLock.Release();
        }
    }

    private static async Task SaveListUnlockedAsync(List<RecentBookEntry> entries, CancellationToken ct)
    {
        var dir = GlossatorPaths.GetGlossatorHomeDirectory();
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(GetRecentBooksJsonPath(), json, ct);
    }
}
