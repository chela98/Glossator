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

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData(@"..\..\evil.bat")]
    [InlineData("/etc/shadow")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\attacker\share\evil.dll")]
    public void ExtractSafely_WithMaliciousPaths_ThrowsSecurityException(string maliciousPath)
    {
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        var extractDir = Path.Combine(Path.GetTempPath(), "ZipSlipTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(maliciousPath);
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

    [Fact]
    public void ExtractSafely_WithPrefixCollisionPath_ThrowsSecurityException()
    {
        // Hedef dizin '.../Target' iken, '.../TargetMalicious/pwned.txt' ile destPath.StartsWith(fullDestDir) kontrolünü zorlama
        var baseDir = Path.Combine(Path.GetTempPath(), "Target_" + Guid.NewGuid().ToString("N"));
        var maliciousEntryName = "../TargetMalicious/pwned.txt";
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(maliciousEntryName);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write("pwned");
            }

            Assert.Throws<SecurityException>(() => SafeZipExtractor.ExtractSafely(tempZip, baseDir));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void ExtractSafely_WhenExceedingEntryLimit_ThrowsInvalidDataException()
    {
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        var extractDir = Path.Combine(Path.GetTempPath(), "ZipBombTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // MaxEntryLimit = 10_000 sınırını aşan arşiv
                for (int i = 0; i < 10_005; i++)
                {
                    zip.CreateEntry($"entry_{i}.txt");
                }
            }

            Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractSafely(tempZip, extractDir));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }
}
