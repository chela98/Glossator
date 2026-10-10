namespace Glossator.Tests.Security;

using System.IO.Compression;
using System.Security;
using Glossator.Core.Security;
using Xunit;

public class SafeZipExtractorTests
{
    [Fact]
    public void ExtractSafely_WithNormalEntries_ExtractsSuccessfully()
    {
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        var extractDir = Path.Combine(Path.GetTempPath(), "SafeZipTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("OEBPS/chapter1.xhtml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("<html><body>Hello World</body></html>");
            }

            SafeZipExtractor.ExtractSafely(tempZip, extractDir);

            var extractedFile = Path.Combine(extractDir, "OEBPS", "chapter1.xhtml");
            Assert.True(File.Exists(extractedFile));
            Assert.Equal("<html><body>Hello World</body></html>", File.ReadAllText(extractedFile));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public void ExtractSafely_WithZipSlipEntry_ThrowsSecurityException()
    {
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        var extractDir = Path.Combine(Path.GetTempPath(), "ZipSlipTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // Kötü niyetli path traversal girdisi
                var entry = zip.CreateEntry("../../evil.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("Malicious payload");
            }

            Assert.Throws<SecurityException>(() => SafeZipExtractor.ExtractSafely(tempZip, extractDir));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }
}
