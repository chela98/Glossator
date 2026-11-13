using System.IO.Compression;
using System.Security;
using System.Xml;
using Glossator.Core.Data;
using Glossator.Core.Parsing;
using Glossator.Core.Security;

namespace Glossator.Tests.Security;

[Collection("GlobalDiagnosticsAndConsole")]
public sealed class SafeXmlAndEpubSecurityTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _prevHome;

    public SafeXmlAndEpubSecurityTests()
    {
        _prevHome = Environment.GetEnvironmentVariable("GLOSSATOR_HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), $"glossator_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _prevHome);
        try
        {
            if (Directory.Exists(_tempHome))
                Directory.Delete(_tempHome, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void SafeXmlLoader_WithDtdOrBillionLaughs_BlocksDtdWhenProhibitAndSafelyIgnoresWhenAllowed()
    {
        var xmlPath = Path.Combine(_tempHome, "dtd.xml");
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
            ]>
            <root><text>safe</text></root>
            """);

        Assert.Throws<XmlException>(() => SafeXmlLoader.Load(xmlPath, allowDtdIgnore: false));

        var doc = SafeXmlLoader.Load(xmlPath, allowDtdIgnore: true);
        Assert.Equal("safe", doc.Root?.Element("text")?.Value);
    }

    [Fact]
    public void SafeXmlLoader_WhenExceedingMaxCharactersInDocument_ThrowsXmlException()
    {
        var xmlPath = Path.Combine(_tempHome, "huge.xml");
        File.WriteAllText(xmlPath, $"<root>{new string('A', 2000)}</root>");

        Assert.Throws<XmlException>(() =>
            SafeXmlLoader.Load(xmlPath, allowDtdIgnore: false, maxCharactersInDocument: 500));
    }

    [Fact]
    public void EpubManifestReader_WhenContainerFullPathEscapesRoot_ThrowsSecurityException()
    {
        var epubPath = Path.Combine(_tempHome, "evil_container.epub");
        using (var zip = ZipFile.Open(epubPath, ZipArchiveMode.Create))
        {
            var container = zip.CreateEntry("META-INF/container.xml");
            using var w = new StreamWriter(container.Open());
            w.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="../../outside/secret.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
        }

        Assert.Throws<SecurityException>(() => EpubManifestReader.Read(epubPath));
    }

    [Fact]
    public void EpubManifestReader_ExtractsSameNamedDistinctArchivesToIsolatedAppWorkspaces()
    {
        var dirA = Path.Combine(_tempHome, "a");
        var dirB = Path.Combine(_tempHome, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var epubA = Path.Combine(dirA, "book.epub");
        var epubB = Path.Combine(dirB, "book.epub");
        CreateMinimalEpub(epubA, "Book Alpha");
        CreateMinimalEpub(epubB, "Book Beta");

        var pkgA = EpubManifestReader.Read(epubA);
        var pkgB = EpubManifestReader.Read(epubB);

        Assert.NotEqual(pkgA.RootDirectory, pkgB.RootDirectory);
        Assert.StartsWith(GlossatorPaths.GetWorkspacesDirectory(), pkgA.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(GlossatorPaths.GetWorkspacesDirectory(), pkgB.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Book Alpha", pkgA.Title);
        Assert.Equal("Book Beta", pkgB.Title);
    }

    private static void CreateMinimalEpub(string path, string title)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var cw = new StreamWriter(zip.CreateEntry("META-INF/container.xml").Open()))
        {
            cw.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
        }

        using (var ow = new StreamWriter(zip.CreateEntry("OEBPS/content.opf").Open()))
        {
            ow.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>{title}</dc:title>
                    <dc:language>en</dc:language>
                  </metadata>
                  <manifest>
                    <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml" />
                  </manifest>
                  <spine><itemref idref="ch1" /></spine>
                </package>
                """);
        }

        using (var hw = new StreamWriter(zip.CreateEntry("OEBPS/ch1.xhtml").Open()))
        {
            hw.Write($"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>{title}</h1><p>Body</p></body></html>");
        }
    }
}
