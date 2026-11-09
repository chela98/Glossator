using System.Collections.Generic;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Xunit;

namespace Glossator.Tests.Packaging;

public class EpubPackagerLinkRewriterTests
{
    [Fact]
    public void RewriteHref_SameDirectoryLink_RewritesToTranslatedFile()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["ch02.xhtml"] = "ch02_tr.xhtml"
        };

        // Act
        var rewritten = EpubPackager.RewriteHref("ch02.xhtml", "ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("ch02_tr.xhtml", rewritten);
    }

    [Fact]
    public void RewriteHref_WithAnchor_PreservesAnchorOnTranslatedFile()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["ch02.xhtml"] = "ch02_tr.xhtml"
        };

        // Act
        var rewritten = EpubPackager.RewriteHref("ch02.xhtml#section-1", "ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("ch02_tr.xhtml#section-1", rewritten);
    }

    [Fact]
    public void RewriteHref_RelativeParentDirectory_RewritesCorrectly()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["notes.xhtml"] = "notes_tr.xhtml"
        };

        // Act: text/ch01.xhtml içinden ../notes.xhtml linki
        var rewritten = EpubPackager.RewriteHref("../notes.xhtml#fn1", "text/ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("../notes_tr.xhtml#fn1", rewritten);
    }

    [Fact]
    public void RewriteHref_IntraPageAnchor_LeavesUntouched()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["ch01.xhtml"] = "ch01_tr.xhtml"
        };

        // Act
        var rewritten = EpubPackager.RewriteHref("#local-footnote-1", "ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("#local-footnote-1", rewritten);
    }

    [Fact]
    public void RewriteHref_ExternalUrl_LeavesUntouched()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["ch02.xhtml"] = "ch02_tr.xhtml"
        };

        // Act
        var rewritten = EpubPackager.RewriteHref("https://example.com/ch02.xhtml", "ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("https://example.com/ch02.xhtml", rewritten);
    }

    [Fact]
    public void RewriteHref_UntranslatedDocument_LeavesUntouched()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["ch02.xhtml"] = "ch02_tr.xhtml"
        };

        // Act: cover.xhtml çevrilmemiş
        var rewritten = EpubPackager.RewriteHref("cover.xhtml#top", "ch01.xhtml", translationMap);

        // Assert
        Assert.Equal("cover.xhtml#top", rewritten);
    }

    [Fact]
    public void SynchronizeDocument_InjectsLangTr_AndRewritesLinks()
    {
        // Arrange
        var html = """
            <!DOCTYPE html>
            <html lang="en">
            <head><title>Chapter 1</title></head>
            <body>
                <h1>Bölüm 1</h1>
                <p>İleri okuma için <a href="ch02.xhtml#part2">Bölüm 2</a> veya <a href="#fn1">dipnot 1</a>.</p>
            </body>
            </html>
            """;

        var translationMap = new Dictionary<string, string>
        {
            ["ch02.xhtml"] = "ch02_tr.xhtml"
        };

        // Act
        var synchronized = EpubPackager.SynchronizeDocument(html, "ch01.xhtml", translationMap);

        // Assert: 1. Kök dil öznitelikleri 'tr' olmalı
        Assert.Contains("lang=\"tr\"", synchronized);
        Assert.Contains("xml:lang=\"tr\"", synchronized);

        // Assert: 2. Çapraz dosya linki çevrilmiş dosyaya yönlendirilmeli (W3C RSC-007 koruması)
        Assert.Contains("href=\"ch02_tr.xhtml#part2\"", synchronized);

        // Assert: 3. Sayfa içi yerel çapa bozulmamalı
        Assert.Contains("href=\"#fn1\"", synchronized);
    }

    [Fact]
    public void DiscoverTranslations_FindsBothXhtmlAndHtmlVariations()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "GlossatorTests_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Manifestte ch01.xhtml ve ch02.html var
            // Diskte ise ch01_tr.html (uzantı mismatch durumu) ve ch02_tr.html var
            File.WriteAllText(Path.Combine(tempDir, "ch01_tr.html"), "content");
            File.WriteAllText(Path.Combine(tempDir, "ch02_tr.html"), "content");

            var package = new EpubPackage
            {
                Title = "Test Book",
                OpfDirectory = tempDir,
                Manifest = new Dictionary<string, EpubItem>
                {
                    ["ch01"] = new() { Id = "ch01", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" },
                    ["ch02"] = new() { Id = "ch02", Href = "ch02.html", MediaType = "application/xhtml+xml" }
                }
            };

            // Act
            var discovered = EpubPackager.DiscoverTranslations(package);

            // Assert
            Assert.Equal(2, discovered.Count);
            Assert.True(discovered.ContainsKey("ch01.xhtml"));
            Assert.Equal("ch01_tr.html", discovered["ch01.xhtml"]);
            Assert.True(discovered.ContainsKey("ch02.html"));
            Assert.Equal("ch02_tr.html", discovered["ch02.html"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PrepareSynchronizedOpf_EnsuresAllLanguageMetadata_AreSetToTurkish()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "GlossatorOpfTest_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var opfContent = """
                <?xml version="1.0" encoding="UTF-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" version="3.0" unique-identifier="pub-id" xml:lang="en">
                    <metadata>
                        <dc:identifier id="pub-id">123456</dc:identifier>
                        <dc:title>Sample Book</dc:title>
                        <dc:language>en</dc:language>
                        <dc:language>en-US</dc:language>
                        <meta property="dcterms:language">en</meta>
                        <meta id="meta-language">en</meta>
                    </metadata>
                    <manifest>
                        <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
                    </manifest>
                    <spine>
                        <itemref idref="ch1"/>
                    </spine>
                </package>
                """;

            var opfPath = Path.Combine(tempDir, "content.opf");
            File.WriteAllText(opfPath, opfContent);

            var package = new EpubPackage
            {
                Title = "Sample Book",
                OpfDirectory = tempDir,
                Manifest = new Dictionary<string, EpubItem>
                {
                    ["ch1"] = new() { Id = "ch1", Href = "ch1.xhtml", MediaType = "application/xhtml+xml" }
                }
            };

            var translationMap = new Dictionary<string, string>
            {
                ["ch1.xhtml"] = "ch1_tr.xhtml"
            };

            // Act
            var (syncedOpf, _) = EpubPackager.PrepareSynchronizedOpf(package, translationMap);

            // Assert
            var doc = System.Xml.Linq.XDocument.Parse(syncedOpf);
            
            // 1. Kök <package xml:lang="tr"> olmalı
            var packageXmlLang = doc.Root?.Attribute(System.Xml.Linq.XNamespace.Xml + "lang")?.Value;
            Assert.Equal("tr", packageXmlLang);

            // 2. dc:language tek ve "tr" olmalı (çelişkili duplicate en kaldırılmalı)
            var dcLangElements = doc.Descendants().Where(e => e.Name.LocalName == "language" && e.Name.Namespace.NamespaceName.Contains("dc")).ToList();
            Assert.Single(dcLangElements);
            Assert.Equal("tr", dcLangElements[0].Value);

            // 3. dcterms:language meta etiketleri "tr" olmalı
            var metaLangs = doc.Descendants().Where(e =>
                e.Name.LocalName == "meta" &&
                (e.Attribute("property")?.Value == "dcterms:language" || e.Attribute("id")?.Value == "meta-language")).ToList();
            Assert.All(metaLangs, m => Assert.Equal("tr", m.Value));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SynchronizeDocument_CleansConflictingBodyLang_AndSetsRootToTr()
    {
        // Arrange
        var html = """
            <!DOCTYPE html>
            <html xml:lang="en" lang="en" xmlns="http://www.w3.org/1999/xhtml">
            <head><title>Chapter 1</title></head>
            <body lang="en" xml:lang="en">
                <section lang="en">
                    <h1>Bölüm 1</h1>
                    <p>Türkçe metin içeriği.</p>
                </section>
            </body>
            </html>
            """;

        // Act
        var result = EpubPackager.SynchronizeDocument(html, "ch01.xhtml", []);

        // Assert
        Assert.Contains("<html", result);
        Assert.Contains("lang=\"tr\"", result);
        Assert.Contains("xml:lang=\"tr\"", result);

        // Body ve section içindeki lang="en" VoiceOver'ı bozmasın diye temizlenmiş olmalı
        Assert.DoesNotContain("<body lang=", result);
        Assert.DoesNotContain("<body xml:lang=", result);
        Assert.DoesNotContain("<section lang=", result);
    }

    [Theory]
    [InlineData("mailto:editor@oreilly.com", "mailto:editor@oreilly.com")]
    [InlineData("javascript:void(0)", "javascript:void(0)")]
    [InlineData("https://www.w3.org/standards", "https://www.w3.org/standards")]
    [InlineData("ftp://files.example.com/data.zip", "ftp://files.example.com/data.zip")]
    public void RewriteHref_WithExternalProtocols_LeavesUntouched(string href, string expected)
    {
        var translationMap = new Dictionary<string, string>
        {
            ["ch01.xhtml"] = "ch01_tr.xhtml"
        };

        var rewritten = EpubPackager.RewriteHref(href, "ch01.xhtml", translationMap);
        Assert.Equal(expected, rewritten);
    }

    [Fact]
    public void CreateEpubArchive_WritesEntriesWithoutUtf8Bom()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Glossator_BomTest_" + Guid.NewGuid().ToString("N"));
        var oebpsDir = Path.Combine(tempDir, "OEBPS");
        Directory.CreateDirectory(oebpsDir);

        try
        {
            var opfPath = Path.Combine(oebpsDir, "content.opf");
            File.WriteAllText(opfPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="pub-id">urn:uuid:12345</dc:identifier>
                    <dc:title>BOM Test</dc:title>
                    <dc:language>tr</dc:language>
                  </metadata>
                  <manifest>
                    <item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine>
                    <itemref idref="ch1"/>
                  </spine>
                </package>
                """);

            var ch01Path = Path.Combine(oebpsDir, "ch01.xhtml");
            File.WriteAllText(ch01Path, """<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml"><body><p>Test</p></body></html>""");

            var package = new EpubPackage
            {
                OpfDirectory = oebpsDir,
                Title = "BOM Test",
                Manifest = new Dictionary<string, EpubItem>
                {
                    ["ch01.xhtml"] = new() { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" }
                }
            };

            var outZip = Path.Combine(tempDir, "out.epub");
            EpubPackager.CreateEpubArchive(package, outZip);

            using var zip = System.IO.Compression.ZipFile.OpenRead(outZip);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith(".xhtml") || entry.FullName.EndsWith(".opf") || entry.FullName.EndsWith(".xml"))
                {
                    using var stream = entry.Open();
                    var headerBytes = new byte[3];
                    var read = stream.Read(headerBytes, 0, 3);
                    if (read == 3)
                    {
                        bool isBom = headerBytes[0] == 0xEF && headerBytes[1] == 0xBB && headerBytes[2] == 0xBF;
                        Assert.False(isBom, $"Entry '{entry.FullName}' contains UTF-8 BOM, which violates W3C EPUB / XML specifications.");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
    [Fact]
    public void RewriteHref_FromSubfolderNav_CalculatesRelativePathCorrectly()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["text/ch01.xhtml"] = "text/ch01_tr.xhtml"
        };

        // Act: text/toc.xhtml iÃ§inden aynÄ± klasÃ¶rdeki ch01.xhtml linki
        var rewritten = EpubPackager.RewriteHref("ch01.xhtml#sec1", "text/toc.xhtml", translationMap);

        // Assert: alt klasÃ¶r iÃ§inde olduÄŸu iÃ§in ch01_tr.xhtml#sec1 olmalÄ±
        Assert.Equal("ch01_tr.xhtml#sec1", rewritten);
    }

    [Fact]
    public void RewriteHref_FromNavInSubfolder_ToAnotherSubfolder_CalculatesRelativeDepth()
    {
        // Arrange
        var translationMap = new Dictionary<string, string>
        {
            ["text/ch01.xhtml"] = "text/ch01_tr.xhtml"
        };

        // Act: nav/toc.xhtml iÃ§inden ../text/ch01.xhtml linki
        var rewritten = EpubPackager.RewriteHref("../text/ch01.xhtml#intro", "nav/toc.xhtml", translationMap);

        // Assert: gÃ¶reli yol ../text/ch01_tr.xhtml#intro olarak hesaplanmalÄ±
        Assert.Equal("../text/ch01_tr.xhtml#intro", rewritten);
    }
}
