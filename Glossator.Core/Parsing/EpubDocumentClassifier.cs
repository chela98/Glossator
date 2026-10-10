using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;

namespace Glossator.Core.Parsing;

/// <summary>
/// Herhangi bir EPUB kitabındaki belgeleri standartlara (EPUB 3 / EPUB 2) ve semantik
/// etiketlere göre dosya adı kalıplarına bağımlı kalmaksızın sınıflandırır.
/// </summary>
public static class EpubDocumentClassifier
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Verilen bir EPUB öğesinin semantik rolünü belirler.
    /// </summary>
    public static EpubDocumentRole Classify(EpubItem item, string? opfDirectory = null)
    {
        if (!item.IsDocument)
        {
            if (item.MediaType.Equals("application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase) ||
                item.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase))
            {
                return EpubDocumentRole.Navigation;
            }

            return EpubDocumentRole.NonDocument;
        }

        // 1. EPUB 3 Navigation Belgesi (properties="nav")
        if (item.Properties?.Contains("nav", StringComparison.OrdinalIgnoreCase) == true)
        {
            return EpubDocumentRole.Navigation;
        }

        var fileName = Path.GetFileName(item.Href).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        // 2. Gezinme (TOC) Belgesi Heuristiği
        if (baseName.Equals("toc", StringComparison.OrdinalIgnoreCase) ||
            baseName.StartsWith("toc", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("nav", StringComparison.OrdinalIgnoreCase))
        {
            return EpubDocumentRole.Navigation;
        }

        // 3. Kapak Sayfası
        if (baseName.Contains("cover") || baseName.Contains("titlepage"))
        {
            return EpubDocumentRole.Cover;
        }

        // 4. Alfabetik Dizin / Fihrist (Index) - LLM Çevirisinden muaf
        if (baseName.StartsWith("ix") ||
            baseName.Contains("index") ||
            baseName.Contains("fihrist"))
        {
            return EpubDocumentRole.Index;
        }

        // 5. Destekleyici / Ek Sayfalar (Auxiliary)
        if (baseName.Contains("glossary") || baseName.Contains("sozluk") ||
            baseName.Contains("colophon") || baseName.Contains("author") ||
            baseName.Contains("preface") || baseName.Contains("foreword") ||
            baseName.Contains("dedication") || baseName.Contains("praise") ||
            baseName.Contains("appendix") || baseName.Contains("ack"))
        {
            return EpubDocumentRole.Auxiliary;
        }

        // 6. Dosya içeriğine dayalı derin semantik kontrol (varsa)
        if (!string.IsNullOrEmpty(opfDirectory))
        {
            var fullPath = Path.Combine(opfDirectory, item.Href);
            if (File.Exists(fullPath))
            {
                var roleFromContent = InspectDocumentContent(fullPath);
                if (roleFromContent.HasValue)
                {
                    return roleFromContent.Value;
                }
            }
        }

        // 7. Geriye kalan tüm gövde belgeleri ana okuma içeriği (Chapter)'dir
        return EpubDocumentRole.Chapter;
    }

    /// <summary>
    /// Bir paketin içindeki tüm Spine belgelerinin rollerini ve başlıklarını topluca çözer.
    /// </summary>
    public static void ClassifyPackage(EpubPackage package)
    {
        // 1. Manifest ve Spine rollerini ata
        foreach (var item in package.Manifest.Values)
        {
            item.Role = Classify(item, package.OpfDirectory);
        }

        // 2. Belgelerin gerçek başlıklarını HTML DOM'undan dinamik oku
        foreach (var item in package.Spine.Where(s => s.IsDocument))
        {
            var fullPath = Path.Combine(package.OpfDirectory, item.Href);
            if (File.Exists(fullPath))
            {
                item.Title = ExtractDocumentTitle(fullPath);
            }
        }
    }

    /// <summary>
    /// HTML belgesinin içindeki ana başlığı (h1, h2 veya title) dinamik olarak çeker.
    /// Sıfır hardcoding: Dosya adı ne olursa olsun yazarın gerçek başlığını bulur.
    /// </summary>
    public static string ExtractDocumentTitle(string htmlFilePath)
    {
        try
        {
            var content = File.ReadAllText(htmlFilePath);
            var doc = Parser.ParseDocument(content);

            // 1. Öncelik: h1 başlığı
            var h1 = doc.QuerySelector("h1")?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(h1))
            {
                return CleanTitle(h1);
            }

            // 2. Öncelik: section data-pdf-bookmark veya epub:type başlığı
            var sectionWithBookmark = doc.QuerySelector("[data-pdf-bookmark]")?.GetAttribute("data-pdf-bookmark");
            if (!string.IsNullOrWhiteSpace(sectionWithBookmark))
            {
                return CleanTitle(sectionWithBookmark);
            }

            // 3. Öncelik: h2 başlığı
            var h2 = doc.QuerySelector("h2")?.TextContent?.Trim();
            if (!string.IsNullOrWhiteSpace(h2))
            {
                return CleanTitle(h2);
            }

            // 4. Öncelik: <title> etiketi
            var title = doc.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return CleanTitle(title);
            }
        }
        catch
        {
            // Fallback to filename
        }

        return Path.GetFileNameWithoutExtension(htmlFilePath);
    }

    private static EpubDocumentRole? InspectDocumentContent(string fullPath)
    {
        try
        {
            using var reader = new StreamReader(fullPath);
            var buffer = new char[2048];
            var read = reader.Read(buffer, 0, buffer.Length);
            var snippet = new string(buffer, 0, read).ToLowerInvariant();

            if (snippet.Contains("epub:type=\"toc\"") || snippet.Contains("data-type=\"toc\""))
                return EpubDocumentRole.Navigation;

            if (snippet.Contains("epub:type=\"index\"") || snippet.Contains("data-type=\"index\""))
                return EpubDocumentRole.Index;

            if (snippet.Contains("epub:type=\"glossary\"") || snippet.Contains("data-type=\"glossary\""))
                return EpubDocumentRole.Auxiliary;

            if (snippet.Contains("epub:type=\"chapter\"") || snippet.Contains("data-type=\"chapter\""))
                return EpubDocumentRole.Chapter;

            if (snippet.Contains("epub:type=\"colophon\"") || snippet.Contains("data-type=\"colophon\"") ||
                snippet.Contains("epub:type=\"preface\"") || snippet.Contains("data-type=\"preface\""))
                return EpubDocumentRole.Auxiliary;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string CleanTitle(string raw)
    {
        var cleaned = Regex.Replace(raw, @"\s+", " ").Trim();
        return cleaned;
    }
}
