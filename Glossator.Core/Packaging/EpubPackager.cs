using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;
using Glossator.Core.Security;

namespace Glossator.Core.Packaging;

/// <summary>
/// Çevrilen EPUB bileşenlerini IDPF / W3C EPUB 2 ve EPUB 3 standartlarına uygun
/// tek bir .epub arşivine paketleyen evrensel motor.
/// </summary>
public static class EpubPackager
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HtmlParser Parser = new();
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

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
                using var cw = new StreamWriter(containerEntry.Open(), Utf8WithoutBom);
                cw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"" + opfRelativePath + "\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            }

            // KURAL 3: Güncellenmiş OPF'yi yaz
            var opfEntry = zip.CreateEntry(opfRelativePath, CompressionLevel.Optimal);
            using (var ow = new StreamWriter(opfEntry.Open(), Utf8WithoutBom))
            {
                ow.Write(syncedOpfContent);
            }

            // KURAL 4: Güncellenmiş NCX'i yaz (varsa)
            if (!string.IsNullOrEmpty(ncxRelativePath) && !string.IsNullOrEmpty(syncedNcxContent))
            {
                var ncxEntry = zip.CreateEntry(ncxRelativePath, CompressionLevel.Optimal);
                using var nw = new StreamWriter(ncxEntry.Open(), Utf8WithoutBom);
                nw.Write(syncedNcxContent);
            }

            // KURAL 5: Güncellenmiş HTML Navigasyonu yaz (varsa)
            if (!string.IsNullOrEmpty(navRelativePath) && !string.IsNullOrEmpty(syncedNavContent))
            {
                var navEntry = zip.CreateEntry(navRelativePath, CompressionLevel.Optimal);
                using var navW = new StreamWriter(navEntry.Open(), Utf8WithoutBom);
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
                    var safeHref = item.Href.Replace('\\', '/').TrimStart('/');
                    if (safeHref.Contains("../") || safeHref.Contains("/..")) continue;
                    actualFileOnDisk = Path.Combine(opfDir, safeHref);
                    var opfRelativePrefix = Path.GetRelativePath(rootDir, opfDir).Replace('\\', '/');
                    zipEntryName = opfRelativePrefix == "." ? safeHref : $"{opfRelativePrefix}/{safeHref}";
                    if (!Path.GetFullPath(actualFileOnDisk).StartsWith(Path.GetFullPath(rootDir), StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (handledPaths.Contains(zipEntryName)) continue;
                handledPaths.Add(zipEntryName);

                if (File.Exists(actualFileOnDisk))
                {
                    if (actualFileOnDisk.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        actualFileOnDisk.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
                    {
                        var rawHtml = File.ReadAllText(actualFileOnDisk, Utf8WithoutBom);
                        var validXhtml = SynchronizeDocument(rawHtml, item.Href, translationMap);
                        var htmlEntry = zip.CreateEntry(zipEntryName, CompressionLevel.Optimal);
                        using var hw = new StreamWriter(htmlEntry.Open(), Utf8WithoutBom);
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
    /// Paketteki HTML/XHTML belgelerinden hangilerinin çevrilmiş karşılığı olduğunu tespit eder.
    /// Hem orijinal uzantıyı (.xhtml / .html) hem de alternatif uzantı varyasyonlarını destekler.
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

            // 1. Öncelik: Orijinal uzantı ile ara (örn: .xhtml)
            var trFileName = $"{baseName}_tr{ext}";
            var trRelativeHref = string.IsNullOrEmpty(dirName) ? trFileName : Path.Combine(dirName, trFileName).Replace('\\', '/');
            var trFullPath = Path.Combine(package.OpfDirectory, trRelativeHref);

            if (File.Exists(trFullPath) && !IsPartialOnly(trFullPath))
            {
                map[item.Href] = trRelativeHref;
                continue;
            }

            // 2. Alternatif: .html, .xhtml, .htm uzantı varyasyonlarını ara
            var candidateExtensions = new[] { ".xhtml", ".html", ".htm" };
            foreach (var altExt in candidateExtensions)
            {
                if (altExt.Equals(ext, StringComparison.OrdinalIgnoreCase)) continue;

                var altFileName = $"{baseName}_tr{altExt}";
                var altRelativeHref = string.IsNullOrEmpty(dirName) ? altFileName : Path.Combine(dirName, altFileName).Replace('\\', '/');
                var altFullPath = Path.Combine(package.OpfDirectory, altRelativeHref);

                if (File.Exists(altFullPath) && !IsPartialOnly(altFullPath))
                {
                    map[item.Href] = altRelativeHref;
                    break;
                }
            }
        }

        return map;
    }

    private static bool IsPartialOnly(string translatedFullPath)
        => File.Exists(translatedFullPath + ".part") && !File.Exists(translatedFullPath + ".done");

    /// <summary>
    /// OPF dosyasını Türkçe metadata ve çevrilmiş manifest/spine linkleriyle günceller.
    /// </summary>
    public static (string Content, string RelativePath) PrepareSynchronizedOpf(
        EpubPackage package,
        Dictionary<string, string> translationMap,
        string targetLanguage = "tr")
    {
        var opfPath = ResolveOpfFullPath(package);
        var rootDir = package.RootDirectory;
        var relativePath = Path.GetRelativePath(rootDir, opfPath).Replace('\\', '/');

        var doc = XDocument.Load(opfPath);

        // 1. Dili güncelle: Tüm dc:language ve meta language tanımlarını hedef dile çek
        var langElements = doc.Descendants().Where(e =>
            e.Name == DcNs + "language" || e.Name.LocalName == "language").ToList();

        if (langElements.Count > 0)
        {
            langElements[0].Value = targetLanguage;
            for (int i = 1; i < langElements.Count; i++)
            {
                langElements[i].Remove();
            }
        }
        else
        {
            var metadataEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            metadataEl?.Add(new XElement(DcNs + "language", targetLanguage));
        }

        var metaLangs = doc.Descendants().Where(e =>
            e.Name.LocalName == "meta" &&
            (e.Attribute("property")?.Value == "dcterms:language" ||
             e.Attribute("id")?.Value == "meta-language" ||
             e.Attribute("name")?.Value == "dcterms.language")).ToList();
        foreach (var ml in metaLangs) ml.Value = targetLanguage;

        // Kök <package> etiketindeki xml:lang ve lang özniteliklerini senkronize et
        if (doc.Root != null)
        {
            var xmlLangAttr = doc.Root.Attribute(XNamespace.Xml + "lang") 
                           ?? doc.Root.Attributes().FirstOrDefault(a => a.Name.LocalName == "lang");
            if (xmlLangAttr != null)
            {
                xmlLangAttr.Value = targetLanguage;
            }
        }

        // 2. Başlığa (Türkçe) ekle
        var titleEl = doc.Descendants(DcNs + "title").FirstOrDefault()
                      ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
        if (titleEl != null && !titleEl.Value.Contains("Türkçe", StringComparison.OrdinalIgnoreCase))
        {
            titleEl.Value = $"{titleEl.Value} (Türkçe)";
        }

        // 3. Manifest öğelerini güncelle: Orijinal HTML yerine _tr dosyalarını eşle ve media-type'ı garantiye al
        var itemElements = doc.Descendants().Where(e => e.Name.LocalName == "item").ToList();
        foreach (var itemEl in itemElements)
        {
            var href = itemEl.Attribute("href")?.Value;
            if (!string.IsNullOrEmpty(href) && translationMap.TryGetValue(href, out var trHref))
            {
                itemEl.SetAttributeValue("href", trHref);
                if (trHref.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                    trHref.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    itemEl.SetAttributeValue("media-type", "application/xhtml+xml");
                }
            }
        }

        // 4. EPUB 3 dcterms:modified meta etiketini güncelle (W3C standardı)
        var modifiedEl = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "meta" &&
            (e.Attribute("property")?.Value == "dcterms:modified" || e.Attribute("id")?.Value == "meta-modified"));
        if (modifiedEl != null)
        {
            modifiedEl.Value = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        return (SerializeXDocument(doc), relativePath);
    }

    private static string SerializeXDocument(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = Utf8WithoutBom,
            Indent = true
        };
        using (var writer = System.Xml.XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }
        return Utf8WithoutBom.GetString(ms.ToArray());
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

        // 1. Kök ncx dilini ve docTitle'ı senkronize et
        if (doc.Root != null)
        {
            var xmlLang = doc.Root.Attribute(XNamespace.Xml + "lang")
                       ?? doc.Root.Attributes().FirstOrDefault(a => a.Name.LocalName == "lang");
            if (xmlLang != null)
            {
                xmlLang.Value = "tr";
            }
        }

        var docTitleText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "docTitle")?
                              .Descendants().FirstOrDefault(e => e.Name.LocalName == "text");
        if (docTitleText != null && !docTitleText.Value.Contains("Türkçe", StringComparison.OrdinalIgnoreCase))
        {
            docTitleText.Value = $"{docTitleText.Value} (Türkçe)";
        }

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
            var currentDir = Path.GetDirectoryName(ncxItem.Href)?.Replace('\\', '/') ?? string.Empty;
            var targetInOpf = string.IsNullOrEmpty(currentDir)
                ? NormalizeRelativePath(srcFile)
                : NormalizeRelativePath($"{currentDir}/{srcFile}");

            if (translationMap.TryGetValue(targetInOpf, out var trFile))
            {
                // 1. Linki goreli derinligi koruyarak guncelle
                var newSrc = RewriteHref(src, ncxItem.Href, translationMap);
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

        return (SerializeXDocument(doc), relativePath);
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
            var currentDir = Path.GetDirectoryName(navItem.Href)?.Replace('\\', '/') ?? string.Empty;
            var targetInOpf = string.IsNullOrEmpty(currentDir)
                ? NormalizeRelativePath(srcFile)
                : NormalizeRelativePath($"{currentDir}/{srcFile}");

            if (translationMap.TryGetValue(targetInOpf, out var trFile))
            {
                // 1. Linki goreli derinligi koruyarak guncelle
                var newHref = RewriteHref(href, navItem.Href, translationMap);
                a.SetAttribute("href", newHref);

                var trFullPath = Path.Combine(package.OpfDirectory, trFile);
                if (File.Exists(trFullPath))
                {
                    var navParent = a.Closest("nav");
                    var navType = navParent?.GetAttribute("epub:type") ?? navParent?.GetAttribute("type") ?? "";
                    var isLandmarkOrPageList = navType.Contains("landmarks", StringComparison.OrdinalIgnoreCase) ||
                                               navType.Contains("page-list", StringComparison.OrdinalIgnoreCase);

                    if (!isLandmarkOrPageList)
                    {
                        var updatedTitle = ExtractHeadingFromDocument(trFullPath, anchor, htmlDomCache);
                        if (!string.IsNullOrWhiteSpace(updatedTitle))
                        {
                            a.TextContent = updatedTitle;
                        }
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

                    // Boş çapa yanındaki başlık: <a id="..."></a><h2>...</h2>
                    var nextEl = targetEl.NextElementSibling;
                    while (nextEl != null)
                    {
                        if (Regex.IsMatch(nextEl.TagName, @"^H[1-6]$", RegexOptions.IgnoreCase))
                        {
                            return CleanTitle(nextEl.TextContent);
                        }
                        if (nextEl.TagName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
                            nextEl.TagName.Equals("div", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        nextEl = nextEl.NextElementSibling;
                    }

                    var parentHeading = targetEl.ParentElement?.QuerySelector("h1, h2, h3, h4, h5, h6");
                    if (parentHeading != null)
                    {
                        return CleanTitle(parentHeading.TextContent);
                    }
                }

                // Çapa belirtilmiş ama başlık bulunamadıysa alt başlık etiketini ezmemek için null dön
                return null;
            }

            // Sadece çapa hiç verilmemişse belgenin ilk ana başlığını al
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

    private static string CleanTitle(string raw)
    {
        return Regex.Replace(raw, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Google Play Books, Apple Books ve W3C EPUB standartlarına uygun katı XHTML/XML
    /// standartlarını (self-closing etiketler, 2.125 HTML5 entity'sinin XML karşılıkları, DOCTYPE) sağlar.
    /// </summary>
    public static string EnsureValidXhtml(string htmlContent, string targetLanguage = "tr")
    {
        return SynchronizeDocument(htmlContent, string.Empty, [], targetLanguage);
    }

    /// <summary>
    /// HTML/XHTML belgesini W3C EPUB 3 ve XHTML standartlarına göre senkronize eder:
    /// 1. Tüm iç a[href] çapraz bağlantılarını çevrilmiş dosya adlarıyla günceller (W3C RSC-007 ve 404 önleme).
    /// 2. Kök html etiketine lang="tr" ve xml:lang="tr" özniteliklerini enjekte eder; gövdedeki çelişkili yabancı dil etiketlerini temizler.
    /// 3. StrictXhtmlMarkupFormatter ile katı XML olarak serileştirir.
    /// </summary>
    public static string SynchronizeDocument(
        string htmlContent,
        string documentHrefInOpf,
        Dictionary<string, string> translationMap,
        string targetLanguage = "tr")
    {
        if (string.IsNullOrWhiteSpace(htmlContent)) return htmlContent;
        htmlContent = Parsing.HtmlSegmenter.NormalizeXhtmlContent(htmlContent);
        var doc = Parser.ParseDocument(htmlContent);

        // 1. Kök dil öznitelikleri (VoiceOver, heceleme ve sözlük motorları için)
        var htmlElement = doc.DocumentElement;
        if (htmlElement != null)
        {
            htmlElement.SetAttribute("lang", targetLanguage);
            htmlElement.SetAttribute("xml:lang", targetLanguage);
            if (string.IsNullOrEmpty(htmlElement.GetAttribute("xmlns")))
            {
                htmlElement.SetAttribute("xmlns", "http://www.w3.org/1999/xhtml");
            }
            doc.CleanConflictingLanguageAttributes();
        }

        // 2. Çapraz bağlantıları (a[href]) güncelle
        if (translationMap != null && translationMap.Count > 0)
        {
            var links = doc.QuerySelectorAll("a[href]");
            foreach (var link in links)
            {
                var href = link.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    var rewritten = RewriteHref(href, documentHrefInOpf, translationMap);
                    if (rewritten != href)
                    {
                        link.SetAttribute("href", rewritten);
                    }
                }
            }
        }

        return doc.ToStrictXhtml(targetLanguage);
    }

    /// <summary>
    /// Bir belgenin içindeki href yolunu çevrilmiş dosya eşleşmelerine göre yeniden yazar.
    /// Anchor linklerini (#fn1, #sec1) ve göreceli dizin yollarını (../) eksiksiz korur.
    /// </summary>
    public static string RewriteHref(
        string originalHref,
        string currentDocHref,
        Dictionary<string, string> translationMap)
    {
        if (string.IsNullOrWhiteSpace(originalHref)) return originalHref;

        // Sayfa içi çapa veya harici linkleri atla
        if (originalHref.StartsWith("#") ||
            originalHref.Contains("://") ||
            originalHref.StartsWith("//") ||
            originalHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            originalHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return originalHref;
        }

        var hashIdx = originalHref.IndexOf('#');
        var pathPart = hashIdx >= 0 ? originalHref[..hashIdx] : originalHref;
        var hashPart = hashIdx >= 0 ? originalHref[hashIdx..] : string.Empty;

        if (string.IsNullOrWhiteSpace(pathPart)) return originalHref;

        var currentDir = Path.GetDirectoryName(currentDocHref)?.Replace('\\', '/') ?? string.Empty;

        // 1. Hedef dosyanın OpfDirectory'ye göre normalize edilmiş yolunu bul
        string targetInOpf = string.IsNullOrEmpty(currentDir)
            ? NormalizeRelativePath(pathPart)
            : NormalizeRelativePath($"{currentDir}/{pathPart}");

        // 2. translationMap'te birebir eşleşme var mı?
        if (translationMap.TryGetValue(targetInOpf, out var translatedTargetInOpf))
        {
            string newRelativePath = string.IsNullOrEmpty(currentDir)
                ? translatedTargetInOpf
                : Path.GetRelativePath(currentDir, translatedTargetInOpf).Replace('\\', '/');

            return $"{newRelativePath}{hashPart}";
        }

        // 3. Dosya adı bazında eşleşme (klasör prefiksi farklıysa fallback)
        var targetFileName = Path.GetFileName(pathPart);
        foreach (var (origKey, trVal) in translationMap)
        {
            if (Path.GetFileName(origKey).Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                var trFileName = Path.GetFileName(trVal);
                var targetDir = Path.GetDirectoryName(pathPart)?.Replace('\\', '/') ?? string.Empty;
                var newPath = string.IsNullOrEmpty(targetDir) ? trFileName : $"{targetDir}/{trFileName}";
                return $"{newPath}{hashPart}";
            }
        }

        return originalHref;
    }

    private static string NormalizeRelativePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                    stack.RemoveAt(stack.Count - 1);
                else
                    stack.Add(part);
            }
            else
            {
                stack.Add(part);
            }
        }
        return string.Join('/', stack);
    }
}
