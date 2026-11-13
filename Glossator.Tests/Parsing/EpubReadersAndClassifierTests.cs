using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class EpubReadersAndClassifierTests : IDisposable
{
    private readonly string _tempDir;

    public EpubReadersAndClassifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossator_epub_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void EpubManifestReader_Read_FromEpubArchiveWithUrlEncodedHref_ParsesMetadataManifestAndSpine()
    {
        // Arrange
        var epubPath = Path.Combine(_tempDir, "sample.epub");
        using (var fs = new FileStream(epubPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var container = zip.CreateEntry("META-INF/container.xml");
            using (var w = new StreamWriter(container.Open()))
            {
                w.Write("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles>
                        <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                      </rootfiles>
                    </container>
                    """);
            }

            var opf = zip.CreateEntry("OEBPS/content.opf");
            using (var w = new StreamWriter(opf.Open()))
            {
                w.Write("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                      <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                        <dc:title>Distributed Systems</dc:title>
                        <dc:language>en</dc:language>
                        <dc:creator>Ada Lovelace</dc:creator>
                      </metadata>
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="ch1" href="text/ch%2001.xhtml" media-type="application/xhtml+xml" />
                        <item id="css" href="styles/main.css" media-type="text/css" />
                      </manifest>
                      <spine>
                        <itemref idref="ch1" />
                      </spine>
                    </package>
                    """);
            }

            var nav = zip.CreateEntry("OEBPS/nav.xhtml");
            using (var w = new StreamWriter(nav.Open()))
                w.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav epub:type=\"toc\">TOC</nav></body></html>");

            var ch1 = zip.CreateEntry("OEBPS/text/ch 01.xhtml");
            using (var w = new StreamWriter(ch1.Open()))
                w.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Chapter One: Consensus</h1><p>Content.</p></body></html>");
        }

        // Act
        var package = EpubManifestReader.Read(epubPath);

        // Assert
        Assert.Equal("Distributed Systems", package.Title);
        Assert.Equal("en", package.Language);
        Assert.Equal("Ada Lovelace", package.Author);
        Assert.Equal(epubPath, package.SourceArchivePath);
        Assert.Equal(3, package.Manifest.Count);

        var ch1Item = Assert.Single(package.Spine);
        Assert.Equal("text/ch 01.xhtml", ch1Item.Href);
        Assert.Equal(EpubDocumentRole.Chapter, ch1Item.Role);
        Assert.Equal("Chapter One: Consensus", ch1Item.Title);
        Assert.NotNull(package.NavigationItem);
        Assert.Equal(EpubDocumentRole.NonDocument, package.Manifest["css"].Role);
    }

    [Fact]
    public void EpubManifestReader_Read_DirectoryWithoutContainerXml_FallsBackToOpfDiscovery()
    {
        // Arrange
        var subDir = Path.Combine(_tempDir, "book_dir", "OPS");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "book.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Fallback OPF Book</dc:title>
              </metadata>
              <manifest>
                <item id="ch1" href="ch01.html" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="ch1" />
              </spine>
            </package>
            """);
        File.WriteAllText(Path.Combine(subDir, "ch01.html"), "<html><body><h1>Intro</h1></body></html>");

        // Act
        var package = EpubManifestReader.Read(Path.Combine(_tempDir, "book_dir"));

        // Assert
        Assert.Equal("Fallback OPF Book", package.Title);
        Assert.Equal(subDir, package.OpfDirectory);
        Assert.Single(package.Spine);
        Assert.Equal("Intro", package.Spine[0].Title);
    }

    [Fact]
    public void EpubManifestReader_Read_WhenPathDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var missingPath = Path.Combine(_tempDir, "nonexistent.epub");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => EpubManifestReader.Read(missingPath));
    }

    [Theory]
    [InlineData("ncx", "toc.ncx", "application/x-dtbncx+xml", null, EpubDocumentRole.Navigation)]
    [InlineData("css", "style.css", "text/css", null, EpubDocumentRole.NonDocument)]
    [InlineData("navdoc", "outline.xhtml", "application/xhtml+xml", "nav", EpubDocumentRole.Navigation)]
    [InlineData("toc", "toc01.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Navigation)]
    [InlineData("cov", "cover.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Cover)]
    [InlineData("tp", "titlepage.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Cover)]
    [InlineData("idx1", "ix01.html", "application/xhtml+xml", null, EpubDocumentRole.Index)]
    [InlineData("idx2", "book-index.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Index)]
    [InlineData("glo", "glossary.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Auxiliary)]
    [InlineData("pref", "preface.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Auxiliary)]
    [InlineData("ack", "ack-01.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Auxiliary)]
    [InlineData("ch1", "chapter01.xhtml", "application/xhtml+xml", null, EpubDocumentRole.Chapter)]
    public void EpubDocumentClassifier_Classify_MapsMetadataToExpectedRole(
        string id,
        string href,
        string mediaType,
        string? properties,
        EpubDocumentRole expectedRole)
    {
        // Arrange
        var item = new EpubItem
        {
            Id = id,
            Href = href,
            MediaType = mediaType,
            Properties = properties
        };

        // Act
        var role = EpubDocumentClassifier.Classify(item);

        // Assert
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    [InlineData("sec01.xhtml", "<html xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><section epub:type=\"index\">...</section></body></html>", EpubDocumentRole.Index)]
    [InlineData("sec02.xhtml", "<html><body><section data-type=\"glossary\">...</section></body></html>", EpubDocumentRole.Auxiliary)]
    [InlineData("sec03.xhtml", "<html><body><nav epub:type=\"toc\">...</nav></body></html>", EpubDocumentRole.Navigation)]
    [InlineData("sec04.xhtml", "<html><body><section data-type=\"preface\">...</section></body></html>", EpubDocumentRole.Auxiliary)]
    [InlineData("index_looking_chapter.xhtml", "<html><body><section data-type=\"chapter\">...</section></body></html>", EpubDocumentRole.Index)]
    public void EpubDocumentClassifier_Classify_DetectsSemanticRoleFromHtmlSnippet(
        string href,
        string htmlSnippet,
        EpubDocumentRole expectedRole)
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "semantic");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, href), htmlSnippet);
        var item = new EpubItem
        {
            Id = "doc",
            Href = href,
            MediaType = "application/xhtml+xml"
        };

        // Act
        var role = EpubDocumentClassifier.Classify(item, opfDir);

        // Assert
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    [InlineData("doc_h1.xhtml", "<html><head><title>Doc Title</title></head><body><h1>Main H1</h1><div data-pdf-bookmark=\"Bookmark\"><h2>Sub H2</h2></div></body></html>", "Main H1")]
    [InlineData("doc_bookmark.xhtml", "<html><head><title>Doc Title</title></head><body><section data-pdf-bookmark=\"PDF Bookmark Title\"><h2>Heading 2</h2></section></body></html>", "PDF Bookmark Title")]
    [InlineData("doc_h2.xhtml", "<html><head><title>Doc Title</title></head><body><h2>Heading Two</h2></body></html>", "Heading Two")]
    [InlineData("doc_title.xhtml", "<html><head><title>Head Title Only</title></head><body><p>Body</p></body></html>", "Head Title Only")]
    [InlineData("fallback_filename.xhtml", "<html><body><p>No headings or title</p></body></html>", "fallback_filename")]
    public void EpubDocumentClassifier_ExtractDocumentTitle_FollowsPriorityHierarchy(
        string fileName,
        string html,
        string expectedTitle)
    {
        // Arrange
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, html);

        // Act
        var title = EpubDocumentClassifier.ExtractDocumentTitle(path);

        // Assert
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void EpubIndexReader_Read_ExtractsAndNormalizesTermsFromOreillyAndPacktSelectors()
    {
        // Arrange: 1) O'Reilly formatı (div[data-type='indexdiv'])
        var opfDir = Path.Combine(_tempDir, "index_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ix01.xhtml"), """
            <html><body>
              <div data-type="indexdiv">
                <ul>
                  <li>Vector Database, 12-15</li>
                  <li>vector database, 99</li>
                  <li><span data-type="index-term">Retrieval Augmented Generation</span>, 40</li>
                  <li>data, 10</li>
                  <li>model, 22</li>
                  <li>a</li>
                  <li>x, 3</li>
                  <li> , 18</li>
                  <li>Embedding Pipeline, 44-48</li>
                </ul>
              </div>
            </body></html>
            """);

        var oreillyPackage = new EpubPackage
        {
            OpfDirectory = opfDir,
            Spine =
            [
                new EpubItem { Id = "ix01", Href = "ix01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Index }
            ]
        };

        // 2) Packt/Wiley fallback formatı (section.index)
        var packtDir = Path.Combine(_tempDir, "packt_index");
        Directory.CreateDirectory(packtDir);
        File.WriteAllText(Path.Combine(packtDir, "index.xhtml"), """
            <html><body>
              <section class="index">
                <ul>
                  <li><a class="index-term" href="#p1">Transformer Architecture</a></li>
                </ul>
              </section>
            </body></html>
            """);

        var packtPackage = new EpubPackage
        {
            OpfDirectory = packtDir,
            Spine =
            [
                new EpubItem { Id = "index", Href = "index.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Index }
            ]
        };

        // Act
        var oreillyEntries = EpubIndexReader.Read(oreillyPackage);
        var packtEntries = EpubIndexReader.Read(packtPackage);

        // Assert
        Assert.Equal(3, oreillyEntries.Count);
        Assert.All(oreillyEntries, e =>
        {
            Assert.Equal(GlossingPolicy.AlwaysTurkish, e.Policy);
            Assert.Null(e.TurkishTerm);
        });
        Assert.Contains(oreillyEntries, e => e.EnglishTerm == "Vector Database");
        Assert.Contains(oreillyEntries, e => e.EnglishTerm == "Retrieval Augmented Generation");
        Assert.Contains(oreillyEntries, e => e.EnglishTerm == "Embedding Pipeline");
        Assert.DoesNotContain(oreillyEntries, e => e.EnglishTerm == "data");
        Assert.DoesNotContain(oreillyEntries, e => e.EnglishTerm == "model");
        Assert.DoesNotContain(oreillyEntries, e => e.EnglishTerm == "a");
        Assert.DoesNotContain(oreillyEntries, e => e.EnglishTerm == "x");

        var packtEntry = Assert.Single(packtEntries);
        Assert.Equal("Transformer Architecture", packtEntry.EnglishTerm);
    }

    [Fact]
    public void EpubGlossaryReader_Read_ExtractsDtDdPairs_AndFallsBackToDivGlossaryEntries()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "glossary_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "glossary.xhtml"), """
            <html><body>
              <dl>
                <dt>Attention</dt>
                <dd>Mechanism weighting token importance.</dd>
                <dt>attention</dt>
                <dd>Duplicate case-insensitive term should be ignored.</dd>
                <dt>OrphanTerm</dt>
                <p>Not a dd element</p>
              </dl>
            </body></html>
            """);

        var dtPackage = new EpubPackage
        {
            OpfDirectory = opfDir,
            Spine =
            [
                new EpubItem { Id = "glossary", Href = "glossary.xhtml", MediaType = "application/xhtml+xml" }
            ]
        };

        var fallbackDir = Path.Combine(_tempDir, "glossary_fallback");
        Directory.CreateDirectory(fallbackDir);
        File.WriteAllText(Path.Combine(fallbackDir, "glossary.xhtml"), """
            <html><body>
              <div class="glossary-entry">
                <strong>Latency</strong>
                <span class="definition">Elapsed time between request and response.</span>
              </div>
              <div class="glossary-entry">
                <strong>latency</strong>
                <span class="definition">Duplicate fallback entry should be ignored.</span>
              </div>
            </body></html>
            """);

        var fallbackPackage = new EpubPackage
        {
            OpfDirectory = fallbackDir,
            Spine =
            [
                new EpubItem { Id = "glossary", Href = "glossary.xhtml", MediaType = "application/xhtml+xml" }
            ]
        };

        // Act
        var dtEntries = EpubGlossaryReader.Read(dtPackage);
        var fallbackEntries = EpubGlossaryReader.Read(fallbackPackage);

        // Assert
        Assert.Equal(2, dtEntries.Count);
        Assert.Equal("Attention", dtEntries[0].EnglishTerm);
        Assert.Equal("Mechanism weighting token importance.", dtEntries[0].EnglishDefinition);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, dtEntries[0].Policy);

        Assert.Equal("OrphanTerm", dtEntries[1].EnglishTerm);
        Assert.Null(dtEntries[1].EnglishDefinition);

        var fbEntry = Assert.Single(fallbackEntries);
        Assert.Equal("Latency", fbEntry.EnglishTerm);
        Assert.Equal("Elapsed time between request and response.", fbEntry.EnglishDefinition);
        Assert.Equal(GlossingPolicy.ProgressiveGloss, fbEntry.Policy);
    }

    [Fact]
    public void EpubManifestReader_Read_ExtractsCoverImageHref_ForEpub3Epub2AndFallback()
    {
        // Arrange: EPUB 3 properties="cover-image"
        var epub3Dir = Path.Combine(_tempDir, "epub3_cover", "OEBPS");
        Directory.CreateDirectory(Path.Combine(epub3Dir, "images"));
        File.WriteAllBytes(Path.Combine(epub3Dir, "images", "book-cover.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(epub3Dir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>EPUB3 Cover</dc:title></metadata>
              <manifest>
                <item id="img1" href="images/book-cover.png" media-type="image/png" properties="cover-image" />
              </manifest>
              <spine />
            </package>
            """);

        // Arrange: EPUB 2 <meta name="cover" content="..." />
        var epub2Dir = Path.Combine(_tempDir, "epub2_cover", "OEBPS");
        Directory.CreateDirectory(Path.Combine(epub2Dir, "img"));
        File.WriteAllBytes(Path.Combine(epub2Dir, "img", "front.jpg"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(epub2Dir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>EPUB2 Cover</dc:title>
                <meta name="cover" content="front-art" />
              </metadata>
              <manifest>
                <item id="front-art" href="img/front.jpg" media-type="image/jpeg" />
              </manifest>
              <spine />
            </package>
            """);

        // Arrange: Path traversal guard (CoverImageHref escaping RootDirectory)
        var evilPackage = new EpubPackage
        {
            OpfDirectory = epub3Dir,
            CoverImageHref = "../../outside.png"
        };

        // Act
        var pkg3 = EpubManifestReader.Read(Path.Combine(_tempDir, "epub3_cover"));
        var pkg2 = EpubManifestReader.Read(Path.Combine(_tempDir, "epub2_cover"));

        // Assert
        Assert.Equal("images/book-cover.png", pkg3.CoverImageHref);
        Assert.Equal(Path.Combine(epub3Dir, "images", "book-cover.png"), pkg3.ResolveCoverImageFullPath());

        Assert.Equal("img/front.jpg", pkg2.CoverImageHref);
        Assert.Equal(Path.Combine(epub2Dir, "img", "front.jpg"), pkg2.ResolveCoverImageFullPath());

        Assert.Null(evilPackage.ResolveCoverImageFullPath());
    }
}
