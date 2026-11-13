using System.Security.Cryptography;
using System.Xml.Linq;
using Glossator.Core.Data;
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
        var doc = SafeXmlLoader.Load(opfPath, allowDtdIgnore: true);
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

        // Kapak görseli çözümleme (EPUB 3 properties="cover-image" -> EPUB 2 <meta name="cover"> -> yedek heuristik)
        var coverImageHref = ResolveCoverImageHref(doc, manifest);

        var package = new EpubPackage
        {
            Title = title,
            Language = language,
            Author = author,
            OpfDirectory = opfDirectory,
            SourceArchivePath = sourceArchive,
            Manifest = manifest,
            Spine = spine,
            CoverImageHref = coverImageHref
        };

        // Semantik rolleri ve başlıkları otomatik sınıflandır
        EpubDocumentClassifier.ClassifyPackage(package);

        return package;
    }

    private static string? ResolveCoverImageHref(XDocument doc, Dictionary<string, EpubItem> manifest)
    {
        static bool IsImage(EpubItem item) =>
            item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        // 1. EPUB 3: properties tokenları içinde "cover-image"
        foreach (var item in manifest.Values)
        {
            if (!IsImage(item) || string.IsNullOrWhiteSpace(item.Properties))
                continue;

            var tokens = item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Any(t => t.Equals("cover-image", StringComparison.OrdinalIgnoreCase)))
                return item.Href;
        }

        // 2. EPUB 2: <meta name="cover" content="<manifest-id>" />
        var metaCoverId = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")?.Value;

        if (!string.IsNullOrWhiteSpace(metaCoverId) &&
            manifest.TryGetValue(metaCoverId.Trim(), out var metaItem) &&
            IsImage(metaItem))
        {
            return metaItem.Href;
        }

        // 3. Yedek: İd veya dosya adında "cover" geçen görsel öğe
        return manifest.Values
            .FirstOrDefault(item => IsImage(item) &&
                (item.Id.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(item.Href).Contains("cover", StringComparison.OrdinalIgnoreCase)))
            ?.Href;
    }

    /// <summary>
    /// .epub dosyasını paylaşılan %TEMP% yerine uygulama kontrolündeki izole ve çakışmasız çalışma klasörüne açar.
    /// Aynı içerik özetine sahip oturum varsa çevrilmiş dosyaları koruyarak kaldığı yerden devam edilmesini sağlar.
    /// </summary>
    private static string ExtractToTempDirectory(string epubFilePath)
    {
        var rawName = Path.GetFileNameWithoutExtension(epubFilePath);
        var safeBaseName = PathSanitizer.SanitizeFileName(rawName, maxBaseLength: 48);
        string contentHash;
        using (var fs = new FileStream(epubFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            contentHash = Convert.ToHexString(SHA256.HashData(fs))[..12].ToLowerInvariant();
        }

        var workspacesRoot = GlossatorPaths.GetWorkspacesDirectory();
        var workDir = PathSanitizer.ResolveWithinRoot(workspacesRoot, $"{safeBaseName}_{contentHash}");
        var containerMarker = Path.Combine(workDir, "META-INF", "container.xml");

        if (Directory.Exists(workDir) && File.Exists(containerMarker))
        {
            return workDir;
        }

        var stagingDir = PathSanitizer.ResolveWithinRoot(workspacesRoot, $".staging_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            SafeZipExtractor.ExtractSafely(epubFilePath, stagingDir, overwrite: false);

            if (Directory.Exists(workDir) && !File.Exists(containerMarker))
            {
                Directory.Delete(workDir, recursive: true);
            }

            if (!Directory.Exists(workDir))
            {
                Directory.Move(stagingDir, workDir);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }

        return workDir;
    }

    /// <summary>
    /// Klasör içindeki META-INF/container.xml dosyasını (kök dizin dışına çıkışa izin vermeden) veya ilk .opf dosyasını bulur.
    /// </summary>
    private static string ResolveOpfPath(string directoryPath)
    {
        var containerPath = PathSanitizer.ResolveWithinRoot(directoryPath, "META-INF/container.xml");
        if (File.Exists(containerPath))
        {
            var containerDoc = SafeXmlLoader.Load(containerPath, allowDtdIgnore: false);
            var rootfileElement = containerDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
            var fullPath = rootfileElement?.Attribute("full-path")?.Value;

            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                var unescaped = Uri.UnescapeDataString(fullPath);
                var candidate = PathSanitizer.ResolveWithinRoot(directoryPath, unescaped);
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