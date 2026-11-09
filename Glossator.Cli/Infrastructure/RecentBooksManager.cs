using System.Text.Json;
using Glossator.Core.Data;
using Glossator.Core.Models.Epub;

namespace Glossator.Cli.Infrastructure;

public sealed record RecentBookEntry
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public string? Publisher { get; init; }
    public int TotalChapters { get; init; }
    public int TranslatedChapters { get; init; }
    public DateTime LastOpened { get; init; } = DateTime.UtcNow;

    public double ProgressPercentage => TotalChapters == 0 ? 0 : Math.Round((double)TranslatedChapters / TotalChapters * 100, 1);
}

public static class RecentBooksManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string GetRecentBooksJsonPath()
    {
        return Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), "recent_books.json");
    }

    public static async Task<List<RecentBookEntry>> LoadRecentBooksAsync(CancellationToken ct = default)
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

    public static async Task SaveRecentBooksAsync(List<RecentBookEntry> books, CancellationToken ct = default)
    {
        var filePath = GetRecentBooksJsonPath();
        var json = JsonSerializer.Serialize(books, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public static async Task<bool> RemoveBookAsync(string bookPath, CancellationToken ct = default)
    {
        var books = await LoadRecentBooksAsync(ct);
        var removed = books.RemoveAll(b => b.Path.Equals(bookPath, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            await SaveRecentBooksAsync(books, ct);
        }
        return removed;
    }

    public static async Task<int> CleanMissingBooksAsync(CancellationToken ct = default)
    {
        var books = await LoadRecentBooksAsync(ct);
        var initialCount = books.Count;
        books.RemoveAll(b => !File.Exists(b.Path) && !Directory.Exists(b.Path));
        if (books.Count != initialCount)
        {
            await SaveRecentBooksAsync(books, ct);
        }
        return initialCount - books.Count;
    }


    public static async Task<RecentBookEntry> RegisterOrUpdateAsync(string bookPath, EpubPackage package, CancellationToken ct = default)
    {
        var fullPath = System.IO.Path.GetFullPath(bookPath);
        var books = await LoadRecentBooksAsync(ct);

        var translatableDocs = package.Spine
            .Where(s => s.IsDocument && s.Role != EpubDocumentRole.Index && s.Role != EpubDocumentRole.Cover && s.Role != EpubDocumentRole.Navigation)
            .ToList();

        var totalChapters = translatableDocs.Count;
        var translatedCount = 0;

        foreach (var doc in translatableDocs)
        {
            if (DocumentTranslationStatus.IsDocumentTranslated(package.OpfDirectory, doc.Href))
            {
                translatedCount++;
            }
        }

        var publisher = DetectPublisher(package);

        var existing = books.FirstOrDefault(b => b.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            books.Remove(existing);
        }

        var entry = new RecentBookEntry
        {
            Path = fullPath,
            Title = package.Title,
            Author = package.Author,
            Publisher = publisher,
            TotalChapters = totalChapters,
            TranslatedChapters = translatedCount,
            LastOpened = DateTime.UtcNow
        };

        books.Insert(0, entry);
        if (books.Count > 10)
        {
            books = books.Take(10).ToList();
        }

        await SaveRecentBooksAsync(books, ct);
        return entry;
    }

    public static int CountExistingTranslations(string bookDir)
    {
        try
        {
            if (!Directory.Exists(bookDir)) return 0;
            var htmlCount = Directory.GetFiles(bookDir, "*_tr.html", SearchOption.AllDirectories).Length;
            var xhtmlCount = Directory.GetFiles(bookDir, "*_tr.xhtml", SearchOption.AllDirectories).Length;
            return htmlCount + xhtmlCount;
        }
        catch
        {
            return 0;
        }
    }

    public static string DetectPublisher(EpubPackage package)
    {
        var allText = $"{package.Title} {package.Author} {package.OpfDirectory}".ToLowerInvariant();
        if (allText.Contains("packt")) return "Packt";
        if (allText.Contains("wiley")) return "Wiley";
        if (allText.Contains("oreilly") || allText.Contains("o'reilly") ||
            (Directory.Exists(package.OpfDirectory) && Directory.GetFiles(package.OpfDirectory, "*.html").Any(f => Path.GetFileName(f).StartsWith("ch", StringComparison.OrdinalIgnoreCase))))
        {
            return "O'Reilly";
        }
        return "Teknik Yayıncı";
    }
}
