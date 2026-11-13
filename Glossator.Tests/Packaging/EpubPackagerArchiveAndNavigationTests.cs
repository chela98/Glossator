using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Xunit;

namespace Glossator.Tests.Packaging;

public class EpubPackagerArchiveAndNavigationTests : IDisposable
{
    private readonly string _tempDir;

    public EpubPackagerArchiveAndNavigationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossator_packager_{Guid.NewGuid():N}");
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
    public void CreateEpubArchive_WritesMimetypeFirstAndUncompressed_AndGeneratesContainerWhenMissing()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "book_root", "OEBPS");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Archive Spec</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine><itemref idref="ch1" /></spine>
            </package>
            """);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), "<html><body><h1>Original</h1></body></html>");
        File.WriteAllText(Path.Combine(opfDir, "ch01_tr.xhtml"), "<html><body><h1>Birinci Bölüm</h1></body></html>");
        File.WriteAllText(Path.Combine(opfDir, "ch01_tr.xhtml.done"), "");

        var package = new EpubPackage
        {
            Title = "Archive Spec",
            OpfDirectory = opfDir,
            Manifest = new Dictionary<string, EpubItem>
            {
                ["ch1"] = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" }
            }
        };
        var outEpub = Path.Combine(_tempDir, "out.epub");

        // Act
        EpubPackager.CreateEpubArchive(package, outEpub);

        // Assert: Binary ZIP header starts with local file header for uncompressed "mimetype"
        var rawBytes = File.ReadAllBytes(outEpub);
        Assert.Equal((byte)'P', rawBytes[0]);
        Assert.Equal((byte)'K', rawBytes[1]);
        // Offset 8..9 in local file header is compression method (0x0000 = Store/NoCompression)
        Assert.Equal(0, BitConverter.ToUInt16(rawBytes, 8));
        Assert.Equal("mimetype", Encoding.ASCII.GetString(rawBytes, 30, 8));

        using var zip = ZipFile.OpenRead(outEpub);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        using var mimeReader = new StreamReader(zip.Entries[0].Open(), Encoding.ASCII);
        Assert.Equal("application/epub+zip", mimeReader.ReadToEnd());

        var containerEntry = zip.GetEntry("META-INF/container.xml");
        Assert.NotNull(containerEntry);
    }

    [Fact]
    public void CreateEpubArchive_SynchronizesNcxAndHtml5Nav_UpdatingTocWhilePreservingLandmarks()
    {
        // Arrange
        var rootDir = Path.Combine(_tempDir, "toc_book");
        var opfDir = Path.Combine(rootDir, "OEBPS");
        Directory.CreateDirectory(Path.Combine(rootDir, "META-INF"));
        Directory.CreateDirectory(opfDir);

        File.WriteAllText(Path.Combine(rootDir, "META-INF", "container.xml"),
            "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

        File.WriteAllText(Path.Combine(opfDir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Systems Design</dc:title>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml" />
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                <item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine toc="ncx"><itemref idref="ch1" /></spine>
            </package>
            """);

        File.WriteAllText(Path.Combine(opfDir, "toc.ncx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" xml:lang="en" version="2005-1">
              <docTitle><text>Systems Design</text></docTitle>
              <navMap>
                <navPoint id="np1" playOrder="1">
                  <navLabel><text>Chapter 1</text></navLabel>
                  <content src="ch01.xhtml#sec-a" />
                </navPoint>
              </navMap>
            </ncx>
            """);

        File.WriteAllText(Path.Combine(opfDir, "nav.xhtml"), """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <body>
                <nav epub:type="toc"><ol><li><a href="ch01.xhtml#sec-a">Chapter 1</a></li></ol></nav>
                <nav epub:type="landmarks"><ol><li><a href="ch01.xhtml">Start of Text</a></li></ol></nav>
              </body>
            </html>
            """);

        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), "<html><body><h1 id=\"sec-a\">Chapter 1</h1></body></html>");
        File.WriteAllText(Path.Combine(opfDir, "ch01_tr.xhtml"), "<html><body><h1 id=\"sec-a\">Bölüm 1: Mimari</h1></body></html>");
        File.WriteAllText(Path.Combine(opfDir, "ch01_tr.xhtml.done"), "");

        var package = new EpubPackage
        {
            Title = "Systems Design",
            OpfDirectory = opfDir,
            Manifest = new Dictionary<string, EpubItem>
            {
                ["ncx"] = new EpubItem { Id = "ncx", Href = "toc.ncx", MediaType = "application/x-dtbncx+xml" },
                ["nav"] = new EpubItem { Id = "nav", Href = "nav.xhtml", MediaType = "application/xhtml+xml", Properties = "nav" },
                ["ch1"] = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" }
            }
        };

        var outEpub = Path.Combine(_tempDir, "synced_nav.epub");

        // Act
        EpubPackager.CreateEpubArchive(package, outEpub);

        // Assert
        using var zip = ZipFile.OpenRead(outEpub);
        using var ncxStream = zip.GetEntry("OEBPS/toc.ncx")!.Open();
        var ncxDoc = XDocument.Load(ncxStream);
        Assert.Equal("tr", ncxDoc.Root!.Attribute(XNamespace.Xml + "lang")?.Value);
        Assert.Contains("(Türkçe)", ncxDoc.Descendants().First(e => e.Name.LocalName == "docTitle").Value);
        Assert.Equal("ch01_tr.xhtml#sec-a", ncxDoc.Descendants().First(e => e.Name.LocalName == "content").Attribute("src")?.Value);
        Assert.Equal("Bölüm 1: Mimari", ncxDoc.Descendants().First(e => e.Name.LocalName == "navLabel").Value);

        using var navStream = new StreamReader(zip.GetEntry("OEBPS/nav.xhtml")!.Open());
        var navText = navStream.ReadToEnd();
        Assert.Contains("href=\"ch01_tr.xhtml#sec-a\">Bölüm 1: Mimari</a>", navText);
        // landmarks nav preserves original landmark text while still rewriting href
        Assert.Contains("href=\"ch01_tr.xhtml\">Start of Text</a>", navText);
    }

    [Fact]
    public void CreateEpubArchive_BlocksManifestPathTraversalOutsideRootDirectory()
    {
        // Arrange: Create an existing file OUTSIDE rootDir to prove blocking is active (not FileNotFound)
        var secretOutside = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(secretOutside, "TOP_SECRET_DATA");

        var rootDir = Path.Combine(_tempDir, "safe_root");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Safe</dc:title></metadata>
              <manifest><item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml" /></manifest>
              <spine><itemref idref="ch1" /></spine>
            </package>
            """);
        File.WriteAllText(Path.Combine(rootDir, "ch01.xhtml"), "<html><body><p>Safe</p></body></html>");

        var package = new EpubPackage
        {
            Title = "Safe",
            OpfDirectory = rootDir,
            Manifest = new Dictionary<string, EpubItem>
            {
                ["ch1"] = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" },
                ["escape"] = new EpubItem { Id = "escape", Href = "../secret.txt", MediaType = "text/plain" }
            }
        };
        var outEpub = Path.Combine(_tempDir, "traversal.epub");

        // Act
        EpubPackager.CreateEpubArchive(package, outEpub);

        // Assert
        using var zip = ZipFile.OpenRead(outEpub);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("secret.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureValidXhtml_ConvertsRawHtmlFragmentIntoLanguageTaggedXhtml()
    {
        // Arrange
        var rawHtml = "<html><head><title>Test</title></head><body><p>Hello<br>World</p></body></html>";

        // Act
        var result = EpubPackager.EnsureValidXhtml(rawHtml, "tr");

        // Assert
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", result.TrimStart());
        Assert.Contains("lang=\"tr\"", result);
        Assert.Contains("<br />", result);
    }

    [Fact]
    public void Package_DeliversEpubAtomicallyWithoutLeftoverTempFiles()
    {
        // Arrange
        var rootDir = Path.Combine(_tempDir, "delivery");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "content.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Atomic Book</dc:title></metadata>
              <manifest><item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml" /></manifest>
              <spine><itemref idref="ch1" /></spine>
            </package>
            """);
        File.WriteAllText(Path.Combine(rootDir, "ch01.xhtml"), "<html><body><p>Ok</p></body></html>");

        var package = new EpubPackage
        {
            Title = "Atomic Book",
            OpfDirectory = rootDir,
            Manifest = new Dictionary<string, EpubItem>
            {
                ["ch1"] = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml" }
            }
        };
        var outputTarget = Path.Combine(_tempDir, "published", "atomic_tr.epub");

        // Act
        var actualPath = EpubPackager.Package(package, outputTarget);

        // Assert
        Assert.Equal(outputTarget, actualPath);
        Assert.True(File.Exists(actualPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(actualPath)!, "*.delivery.tmp"));
    }
}
