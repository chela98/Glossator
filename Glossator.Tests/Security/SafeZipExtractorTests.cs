namespace Glossator.Tests.Security;

using System.IO.Compression;
using System.Security;
using Glossator.Core.Security;
using Xunit;

public class SafeZipExtractorTests
{
    [Fact]
    public void ExtractSafely_WithNormalEntriesAndDirectories_ExtractsSuccessfully()
    {
        // Arrange
        var tempZip = Path.Combine(Path.GetTempPath(), $"SafeZipNormal_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"SafeZipTest_{Guid.NewGuid():N}");

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                zip.CreateEntry("OEBPS/images/");
                var entry = zip.CreateEntry("OEBPS/chapter1.xhtml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("<html><body>Hello World</body></html>");
            }

            // Act
            SafeZipExtractor.ExtractSafely(tempZip, extractDir);

            // Assert
            var imagesDir = Path.Combine(extractDir, "OEBPS", "images");
            var extractedFile = Path.Combine(extractDir, "OEBPS", "chapter1.xhtml");
            Assert.True(Directory.Exists(imagesDir));
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
        // Arrange
        var tempZip = Path.Combine(Path.GetTempPath(), $"ZipSlip_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"ZipSlipTest_{Guid.NewGuid():N}");

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(maliciousPath);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("Malicious payload");
            }

            // Act & Assert
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
        // Arrange: Hedef dizin '.../Target_<guid>' iken '../Target_<guid>Malicious/pwned.txt'
        // destPath.StartsWith(fullDestDir) kontrolünü son bölü ayıracı olmadan aşmaya çalışır
        var folderName = "Target_" + Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), folderName);
        var maliciousEntryName = $"../{folderName}Malicious/pwned.txt";
        var tempZip = Path.Combine(Path.GetTempPath(), $"PrefixZip_{Guid.NewGuid():N}.zip");

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(maliciousEntryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("pwned");
            }

            // Act & Assert
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
        // Arrange
        var tempZip = Path.Combine(Path.GetTempPath(), $"ZipEntryBomb_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"ZipBombTest_{Guid.NewGuid():N}");

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                for (int i = 0; i <= SafeZipExtractor.MaxEntryLimit; i++)
                {
                    zip.CreateEntry($"entry_{i}.txt");
                }
            }

            // Act & Assert
            var ex = Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractSafely(tempZip, extractDir));
            Assert.Contains(SafeZipExtractor.MaxEntryLimit.ToString(), ex.Message);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public void ExtractSafely_WhenExceedingTotalByteLimit_ThrowsInvalidDataException()
    {
        // Arrange: 1 GB sınırını aşan iki sıkıştırılmış sıfır bloğu (1.05 GB toplam açılmış boyut)
        var tempZip = Path.Combine(Path.GetTempPath(), $"ZipByteBomb_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"ZipByteBombTest_{Guid.NewGuid():N}");

        try
        {
            var zeroChunk = new byte[1024 * 1024]; // 1 MB
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                for (int e = 0; e < 2; e++)
                {
                    var entry = zip.CreateEntry($"bomb_{e}.bin", CompressionLevel.Fastest);
                    using var stream = entry.Open();
                    for (int mb = 0; mb < 550; mb++) // 2 x 550 MB = 1.1 GB > 1 GB
                    {
                        stream.Write(zeroChunk, 0, zeroChunk.Length);
                    }
                }
            }

            // Act & Assert
            var ex = Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractSafely(tempZip, extractDir));
            Assert.Contains("1 GB", ex.Message);
            Assert.False(Directory.Exists(extractDir) && File.Exists(Path.Combine(extractDir, "bomb_1.bin")));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public void ExtractSafely_WhenArchiveDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var missingArchive = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.epub");
        var extractDir = Path.Combine(Path.GetTempPath(), $"ExtractMissing_{Guid.NewGuid():N}");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => SafeZipExtractor.ExtractSafely(missingArchive, extractDir));
    }

    [Fact]
    public void ExtractSafely_WithDuplicateEntriesAndOverwriteFalse_ThrowsIOException()
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"DupZip_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"DupZipOut_{Guid.NewGuid():N}");

        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                var e1 = zip.CreateEntry("OEBPS/dup.xhtml");
                using (var w1 = new StreamWriter(e1.Open())) w1.Write("first");
                var e2 = zip.CreateEntry("OEBPS/dup.xhtml");
                using (var w2 = new StreamWriter(e2.Open())) w2.Write("second");
            }

            Assert.Throws<IOException>(() => SafeZipExtractor.ExtractSafely(tempZip, extractDir));
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        }
    }
}
