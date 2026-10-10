using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;
using Glossator.Core.Security;
using Glossator.Core.Workspace;

namespace Glossator.Core.Packaging;

/// <summary>
/// Çevrilen EPUB bileşenlerini IDPF / W3C EPUB 2 ve EPUB 3 standartlarına uygun
/// tek bir .epub arşivine paketleyen evrensel motor.
/// </summary>
public static class EpubPackager
{
    private static readonly HtmlParser Parser = new();
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DctermsNs = "http://purl.org/dc/terms/";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace NcxNs = "http://www.daisy.org/z3986/2005/ncx/";

    /// <summary>
    /// Aktif IWorkspaceSession üzerinden staging klasöründe arşivi oluşturup nihai hedefe atomik teslim eder.
    /// </summary>
    public static async Task<string> PackageAsync(IWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        var stagingArchivePath = Path.Combine(session.StagingDirectory, "package.staging.epub");
        CreateEpubArchive(session.Package, stagingArchivePath);
        await session.CommitOutputAsync(stagingArchivePath, cancellationToken);
        return session.ResolvedOutputPath;
    }

    /// <summary>
    /// Verilen paketteki tüm çevirileri ve kaynakları birleştirerek geçerli bir .epub dosyası üretir.
    /// </summary>
    public static string Package(EpubPackage package, string? customOutputPath = null)
    {
        var rootDir = package.RootDirectory;
        var outputPath = customOutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = PathSanitizer.ResolveSafeOutputPath(rootDir, null, package.Title);
        }

        var destDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(destDir);
        var tempDeliveryPath = Path.Combine(destDir, $"{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.delivery.tmp");

        try
        {
            CreateEpubArchive(package, tempDeliveryPath);
            File.Move(tempDeliveryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempDeliveryPath))
            {
                try { File.Delete(tempDeliveryPath); } catch { /* Ignore */ }
            }
        }

        return outputPath;
    }

    /// <summary>
    /// Belirtilen hedef yola EPUB arşivini fiziksel olarak yazar.
    /// </summary>
    public static void CreateEpubArchive(EpubPackage package, string destinationArchiveFile)
    {
        var rootDir = package.RootDirectory;
        var opfDir = package.OpfDirectory;

        // 1. Çevrilmiş HTML dosyalarının haritasını çıkar (orijinal -> cevrilmis)
        var translationMap = DiscoverTranslations(package);

        // 2. Geçici önbellekte güncellenmiş TOC ve OPF içeriklerini hazırla
        var (syncedNcxContent, ncxRelativePath) = PrepareSynchronizedNcx(package, translationMap);
        var (syncedNavContent, navRelativePath) = PrepareSynchronizedNav(package, translationMap);
        var (syncedOpfContent, opfRelativePath) = PrepareSynchronizedOpf(package, translationMap);

        // 3. Standartlara uygun ZIP (.epub) oluştur
        using (var fs = new FileStream(destinationArchiveFile, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            // KURAL 1: mimetype dosyası MUTLAKA İLK SIRADA ve SIKIŞTIRMASIZ (NoCompression) yazılmalı
            var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var mw = new StreamWriter(mimetypeEntry.Open(), Encoding.ASCII))
            {
                mw.Write("application/epub+zip");
            }

            // KURAL 2: META-INF/container.xml ve diğer meta dosyaları ekle
            var metaInfDir = Path.Combine(rootDir, "META-INF");
            if (Directory.Exists(metaInfDir))
            {
                foreach (var file in Directory.GetFiles(metaInfDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
                    AddFileToZip(zip, file, relativePath, CompressionLevel.Optimal);
                }
            }
            else
            {
                // Standart container.xml oluştur
                var containerEntry = zip.CreateEntry("META-INF/container.xml", CompressionLevel.Optimal);
                using var cw = new StreamWriter(containerEntry.Open(), Encoding.UTF8);
                cw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"" + opfRelativePath + "\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            }

            // KURAL 3: Güncellenmiş OPF'yi yaz
            var opfEntry = zip.CreateEntry(opfRelativePath, CompressionLevel.Optimal);
            using (var ow = new StreamWriter(opfEntry.Open(), Encoding.UTF8))
            {
                ow.Write(syncedOpfContent);
            }

            // KURAL 4: Güncellenmiş NCX'i yaz (varsa)
            if (!string.IsNullOrEmpty(ncxRelativePath) && !string.IsNullOrEmpty(syncedNcxContent))
            {
                var ncxEntry = zip.CreateEntry(ncxRelativePath, CompressionLevel.Optimal);
                using var nw = new StreamWriter(ncxEntry.Open(), Encoding.UTF8);
                nw.Write(syncedNcxContent);
            }

            // KURAL 5: Güncellenmiş HTML Navigasyonu yaz (varsa)
            if (!string.IsNullOrEmpty(navRelativePath) && !string.IsNullOrEmpty(syncedNavContent))
            {
                var navEntry = zip.CreateEntry(navRelativePath, CompressionLevel.Optimal);
                using var navW = new StreamWriter(navEntry.Open(), Encoding.UTF8);
                navW.Write(syncedNavContent);
            }

            // KURAL 6: Manifestteki tüm kaynakları (Görseller, Fontlar, CSS ve Belgeler) ekle
            var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mimetype",
                opfRelativePath
            };
            if (!string.IsNullOrEmpty(ncxRelativePath)) handledPaths.Add(ncxRelativePath);
            if (!string.IsNullOrEmpty(navRelativePath)) handledPaths.Add(navRelativePath);

            foreach (var item in package.Manifest.Values)
            {
                // Eğer bu dosyanın çevrilmiş hali varsa, çevrilmiş olanı zip'e ekle
                string actualFileOnDisk;
                string zipEntryName;

                if (translationMap.TryGetValue(item.Href, out var translatedHref))
                {
                    actualFileOnDisk = Path.Combine(opfDir, translatedHref);
                    // Zip içindeki adı da translatedHref olarak kaydedelim
                    var opfRelativePrefix = Path.GetRelativePath(rootDir, opfDir).Replace('\\', '/');
                    zipEntryName = opfRelativePrefix == "." ? translatedHref : $"{opfRelativePrefix}/{translatedHref}";
                }
                else
                {
                    // Orijinal dosya (görsel, css, font veya çevrilmemiş kapak vb.)
                    actualFileOnDisk = Path.Combine(opfDir, item.Href);
                    var opfRelativePrefix = Path.GetRelativePath(rootDir, opfDir).Replace('\\', '/');
                    zipEntryName = opfRelativePrefix == "." ? item.Href : $"{opfRelativePrefix}/{item.Href}";
                }

                if (handledPaths.Contains(zipEntryName)) continue;
                handledPaths.Add(zipEntryName);

                if (File.Exists(actualFileOnDisk))
                {
                    if (actualFileOnDisk.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        actualFileOnDisk.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
                    {
                        var rawHtml = File.ReadAllText(actualFileOnDisk, Encoding.UTF8);
                        var validXhtml = EnsureValidXhtml(rawHtml);
                        var htmlEntry = zip.CreateEntry(zipEntryName, CompressionLevel.Optimal);
                        using var hw = new StreamWriter(htmlEntry.Open(), Encoding.UTF8);
                        hw.Write(validXhtml);
                    }
                    else
                    {
                        AddFileToZip(zip, actualFileOnDisk, zipEntryName, CompressionLevel.Optimal);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Paketteki HTML dosyalarından hangilerinin *_tr.html karşılığı olduğunu tespit eder.
    /// </summary>
    public static Dictionary<string, string> DiscoverTranslations(EpubPackage package)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.Manifest.Values.Where(m => m.IsDocument))
        {
            var fileName = Path.GetFileName(item.Href);
            var dirName = Path.GetDirectoryName(item.Href) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);

            // Zaten _tr dosyasıysa atla
            if (baseName.EndsWith("_tr", StringComparison.OrdinalIgnoreCase)) continue;

            var trFileName = $"{baseName}_tr{ext}";
            var trRelativeHref = string.IsNullOrEmpty(dirName) ? trFileName : Path.Combine(dirName, trFileName).Replace('\\', '/');
            var trFullPath = Path.Combine(package.OpfDirectory, trRelativeHref);

            if (File.Exists(trFullPath))
            {
                map[item.Href] = trRelativeHref;
            }
        }

        return map;
    }

    /// <summary>
    /// OPF dosyasını Türkçe metadata ve çevrilmiş manifest/spine linkleriyle günceller.
    /// </summary>
    private static (string Content, string RelativePath) PrepareSynchronizedOpf(
        EpubPackage package,
        Dictionary<string, string> translationMap)
    {
        var opfPath = ResolveOpfFullPath(package);
        var rootDir = package.RootDirectory;
        var relativePath = Path.GetRelativePath(rootDir, opfPath).Replace('\\', '/');

        var doc = XDocument.Load(opfPath);

        // 1. Dili güncelle
        var langEl = doc.Descendants(DcNs + "language").FirstOrDefault()
                     ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "language");
        if (langEl != null) langEl.Value = "tr";

        var metaLangs = doc.Descendants().Where(e =>
            e.Name.LocalName == "meta" &&
            (e.Attribute("property")?.Value == "dcterms:language" || e.Attribute("id")?.Value == "meta-language"));
        foreach (var ml in metaLangs) ml.Value = "tr";

        // 2. Başlığa (Türkçe) ekle
        var titleEl = doc.Descendants(DcNs + "title").FirstOrDefault()
                      ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
        if (titleEl != null && !titleEl.Value.Contains("Türkçe", StringComparison.OrdinalIgnoreCase))
        {
            titleEl.Value = $"{titleEl.Value} (Türkçe)";
        }

        // 3. Manifest öğelerini güncelle: Orijinal HTML yerine _tr.html dosyalarını eşle
        var itemElements = doc.Descendants().Where(e => e.Name.LocalName == "item").ToList();
        foreach (var itemEl in itemElements)
        {
            var href = itemEl.Attribute("href")?.Value;
            if (!string.IsNullOrEmpty(href) && translationMap.TryGetValue(href, out var trHref))
            {
                itemEl.SetAttributeValue("href", trHref);
            }
        }

        return (doc.ToString(), relativePath);
    }

    /// <summary>
    /// toc.ncx dosyasını dinamik olarak tarar, linkleri *_tr.html'e yönlendirir
    /// ve hedef belgedeki gerçek Türkçe başlığı sıfır LLM maliyetiyle eşler.
    /// </summary>
    private static (string? Content, string? RelativePath) PrepareSynchronizedNcx(
        EpubPackage package,
        Dictionary<string, string> translationMap)
    {
        var ncxItem = package.Manifest.Values.FirstOrDefault(m =>
            m.MediaType.Equals("application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase) ||
            m.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));

        if (ncxItem == null) return (null, null);

        var ncxFullPath = Path.Combine(package.OpfDirectory, ncxItem.Href);
        if (!File.Exists(ncxFullPath)) return (null, null);

        var rootDir = package.RootDirectory;
        var relativePath = Path.GetRelativePath(rootDir, ncxFullPath).Replace('\\', '/');

        var doc = XDocument.Load(ncxFullPath);

        // Her navPoint için src ve text eşitle
        var navPoints = doc.Descendants().Where(e => e.Name.LocalName == "navPoint").ToList();

        // Performans için açılan HTML DOM'larını önbelleğe al
        var htmlDomCache = new Dictionary<string, IDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var np in navPoints)
        {
            var contentEl = np.Elements().FirstOrDefault(e => e.Name.LocalName == "content");
            var labelEl = np.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                            .Elements().FirstOrDefault(e => e.Name.LocalName == "text");

            var src = contentEl?.Attribute("src")?.Value;
            if (string.IsNullOrEmpty(src)) continue;

            var (srcFile, anchor) = SplitHref(src);

            if (translationMap.TryGetValue(srcFile, out var trFile))
            {
                // 1. Linki *_tr.html olarak güncelle
                var newSrc = string.IsNullOrEmpty(anchor) ? trFile : $"{trFile}#{anchor}";
                contentEl!.SetAttributeValue("src", newSrc);

                // 2. Başlığı Türkçe dosyadan dinamik çek
                var trFullPath = Path.Combine(package.OpfDirectory, trFile);
                if (File.Exists(trFullPath) && labelEl != null)
                {
                    var updatedTitle = ExtractHeadingFromDocument(trFullPath, anchor, htmlDomCache);
                    if (!string.IsNullOrWhiteSpace(updatedTitle))
                    {
                        labelEl.Value = updatedTitle;
                    }
                }
            }
        }

        return (doc.ToString(), relativePath);
    }

    /// <summary>
    /// EPUB 3 HTML5 Navigasyon dosyasını (toc.xhtml / toc01.html) tarar,
    /// linkleri ve başlıkları dinamik olarak günceller.
    /// </summary>
    private static (string? Content, string? RelativePath) PrepareSynchronizedNav(
        EpubPackage package,
        Dictionary<string, string> translationMap)
    {
        var navItem = package.Manifest.Values.FirstOrDefault(m =>
            m.Properties?.Contains("nav", StringComparison.OrdinalIgnoreCase) == true);

        if (navItem == null) return (null, null);

        var navFullPath = Path.Combine(package.OpfDirectory, navItem.Href);
        if (!File.Exists(navFullPath)) return (null, null);

        var rootDir = package.RootDirectory;
        var relativePath = Path.GetRelativePath(rootDir, navFullPath).Replace('\\', '/');

        var navContent = File.ReadAllText(navFullPath);
        var navDoc = Parser.ParseDocument(navContent);

        var htmlDomCache = new Dictionary<string, IDocument>(StringComparer.OrdinalIgnoreCase);
        var links = navDoc.QuerySelectorAll("a[href]");

        foreach (var a in links)
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var (srcFile, anchor) = SplitHref(href);

            if (translationMap.TryGetValue(srcFile, out var trFile))
            {
                var newHref = string.IsNullOrEmpty(anchor) ? trFile : $"{trFile}#{anchor}";
                a.SetAttribute("href", newHref);

                var trFullPath = Path.Combine(package.OpfDirectory, trFile);
                if (File.Exists(trFullPath))
                {
                    var updatedTitle = ExtractHeadingFromDocument(trFullPath, anchor, htmlDomCache);
                    if (!string.IsNullOrWhiteSpace(updatedTitle))
                    {
                        a.TextContent = updatedTitle;
                    }
                }
            }
        }

        var serializedNav = navDoc.DocumentElement != null 
            ? navDoc.ToStrictXhtml() 
            : navContent;

        return (serializedNav, relativePath);
    }

    /// <summary>
    /// Hedef HTML belgesinde belirtilen çapa (anchor) veya ilk başlık etiketini bulup temiz metnini döner.
    /// </summary>
    private static string? ExtractHeadingFromDocument(
        string htmlPath,
        string? anchor,
        Dictionary<string, IDocument> cache)
    {
        try
        {
            if (!cache.TryGetValue(htmlPath, out var doc))
            {
                var text = File.ReadAllText(htmlPath);
                doc = Parser.ParseDocument(text);
                cache[htmlPath] = doc;
            }

            if (!string.IsNullOrEmpty(anchor))
            {
                var targetEl = doc.GetElementById(anchor) ?? doc.QuerySelector($"[name='{anchor}']");
                if (targetEl != null)
                {
                    if (Regex.IsMatch(targetEl.TagName, @"^H[1-6]$", RegexOptions.IgnoreCase))
                    {
                        return CleanTitle(targetEl.TextContent);
                    }

                    var childHeading = targetEl.QuerySelector("h1, h2, h3, h4, h5, h6");
                    if (childHeading != null)
                    {
                        return CleanTitle(childHeading.TextContent);
                    }
                }
            }

            // Çapa yoksa belgenin ilk ana başlığını al
            var topHeading = doc.QuerySelector("h1, h2");
            if (topHeading != null)
            {
                return CleanTitle(topHeading.TextContent);
            }
        }
        catch
        {
            // fallback
        }

        return null;
    }

    private static (string File, string? Anchor) SplitHref(string href)
    {
        var hashIdx = href.IndexOf('#');
        if (hashIdx >= 0)
        {
            return (href[..hashIdx], href[(hashIdx + 1)..]);
        }
        return (href, null);
    }

    private static string ResolveOpfFullPath(EpubPackage package)
    {
        var direct = Directory.GetFiles(package.OpfDirectory, "*.opf");
        if (direct.Length > 0) return direct[0];

        var all = Directory.GetFiles(package.RootDirectory, "*.opf", SearchOption.AllDirectories);
        if (all.Length > 0) return all[0];

        throw new FileNotFoundException("content.opf bulunamadı.");
    }

    private static void AddFileToZip(ZipArchive zip, string sourceFilePath, string entryName, CompressionLevel level)
    {
        var entry = zip.CreateEntry(entryName, level);
        using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var entryStream = entry.Open();
        sourceStream.CopyTo(entryStream);
    }

    private static string SanitizeFileName(string raw)
    {
        return PathSanitizer.SanitizeFileName(raw);
    }

    private static string CleanTitle(string raw)
    {
        return Regex.Replace(raw, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Google Play Books, Apple Books ve W3C EPUB standartlarına uygun katı XHTML/XML
    /// standartlarını (self-closing etiketler, 2.125 HTML5 entity'sinin XML karşılıkları, DOCTYPE) sağlar.
    /// </summary>
    public static string EnsureValidXhtml(string htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent)) return htmlContent;
        var doc = Parser.ParseDocument(htmlContent);
        return doc.ToStrictXhtml();
    }
}
