using System.IO.Compression;
using System.Xml.Linq;
using Glossator.Core.Models.Epub;
using Glossator.Core.Security;

namespace Glossator.Core.Parsing;

public static class EpubManifestReader
{
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";

    /// <summary>
    /// Verilen .epub dosyasından, klasörden veya doğrudan OPF dosyasından paketi okur.
    /// </summary>
    public static EpubPackage Read(string inputPath)
    {
        string? sourceArchive = null;
        string workingDirectory;

        // 1. Senaryo: Doğrudan .epub (ZIP arşivi) verilmiş
        if (File.Exists(inputPath) && inputPath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            sourceArchive = Path.GetFullPath(inputPath);
            workingDirectory = ExtractToTempDirectory(sourceArchive);
        }
        // 2. Senaryo: Zaten açılmış bir klasör verilmiş
        else if (Directory.Exists(inputPath))
        {
            workingDirectory = Path.GetFullPath(inputPath);
        }
        // 3. Senaryo: Doğrudan content.opf dosyası verilmiş
        else if (File.Exists(inputPath) && inputPath.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
        {
            workingDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
        }
        else
        {
            throw new FileNotFoundException($"Belirtilen dosya veya dizin bulunamadı: {inputPath}");
        }

        var opfPath = ResolveOpfPath(workingDirectory);
        var doc = XDocument.Load(opfPath);
        var opfDirectory = Path.GetDirectoryName(Path.GetFullPath(opfPath)) ?? string.Empty;

        // Metadata Ayrıştırma
        var title = doc.Descendants(DcNs + "title").FirstOrDefault()?.Value
                    ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value
                    ?? "Bilinmeyen Başlık";

        var language = doc.Descendants(DcNs + "language").FirstOrDefault()?.Value
                       ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "language")?.Value
                       ?? "en";

        var author = doc.Descendants(DcNs + "creator").FirstOrDefault()?.Value
                     ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value;

        // Manifest Ayrıştırma
        var manifest = new Dictionary<string, EpubItem>(StringComparer.OrdinalIgnoreCase);
        var itemElements = doc.Descendants().Where(e => e.Name.LocalName == "item");

        foreach (var el in itemElements)
        {
            var id = el.Attribute("id")?.Value;
            var href = el.Attribute("href")?.Value;
            var mediaType = el.Attribute("media-type")?.Value;
            var properties = el.Attribute("properties")?.Value;

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(mediaType))
            {
                manifest[id] = new EpubItem
                {
                    Id = id,
                    Href = Uri.UnescapeDataString(href),
                    MediaType = mediaType,
                    Properties = properties
                };
            }
        }

        // Spine Ayrıştırma
        var spine = new List<EpubItem>();
        var itemrefElements = doc.Descendants().Where(e => e.Name.LocalName == "itemref");

        foreach (var el in itemrefElements)
        {
            var idref = el.Attribute("idref")?.Value;
            if (!string.IsNullOrEmpty(idref) && manifest.TryGetValue(idref, out var item))
            {
                spine.Add(item);
            }
        }

        var package = new EpubPackage
        {
            Title = title,
            Language = language,
            Author = author,
            OpfDirectory = opfDirectory,
            SourceArchivePath = sourceArchive,
            Manifest = manifest,
            Spine = spine
        };

        // Semantik rolleri ve başlıkları otomatik sınıflandır
        EpubDocumentClassifier.ClassifyPackage(package);

        return package;
    }

    /// <summary>
    /// .epub dosyasını işletim sisteminin geçici klasörüne açar.
    /// </summary>
    private static string ExtractToTempDirectory(string epubFilePath)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(epubFilePath);
        var tempDir = Path.Combine(Path.GetTempPath(), "Glossator", fileNameWithoutExt);

        // Eğer klasör varsa temizleyip güncelini açalım
        if (Directory.Exists(tempDir))
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* Kilitli dosya varsa devam et */ }
        }

        Directory.CreateDirectory(tempDir);
        SafeZipExtractor.ExtractSafely(epubFilePath, tempDir);

        return tempDir;
    }

    /// <summary>
    /// Klasör içindeki META-INF/container.xml dosyasını veya ilk .opf dosyasını bulur.
    /// </summary>
    private static string ResolveOpfPath(string directoryPath)
    {
        var containerPath = Path.Combine(directoryPath, "META-INF", "container.xml");
        if (File.Exists(containerPath))
        {
            var containerDoc = XDocument.Load(containerPath);
            var rootfileElement = containerDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
            var fullPath = rootfileElement?.Attribute("full-path")?.Value;

            if (!string.IsNullOrEmpty(fullPath))
            {
                var candidate = Path.Combine(directoryPath, fullPath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var opfFiles = Directory.GetFiles(directoryPath, "*.opf", SearchOption.AllDirectories);
        if (opfFiles.Length > 0)
        {
            return opfFiles[0];
        }

        throw new FileNotFoundException($"'{directoryPath}' dizininde geçerli bir EPUB paket dosyası (.opf) bulunamadı.");
    }
}