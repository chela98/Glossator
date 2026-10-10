namespace Glossator.Core.Security;

using System.IO.Compression;
using System.Security;

public static class SafeZipExtractor
{
    public const long MaxTotalExtractionBytes = 1024L * 1024L * 1024L; // 1 GB Safety limit
    public const int MaxEntryLimit = 10_000;

    public static void ExtractSafely(string archivePath, string destinationDirectory)
    {
        var fullDestDir = Path.GetFullPath(destinationDirectory);
        if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDestDir += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxEntryLimit)
        {
            throw new InvalidDataException($"Arşiv giriş sayısı sınırını aşıyor: {archive.Entries.Count} > {MaxEntryLimit}");
        }

        long totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            totalBytes += entry.Length;
            if (totalBytes > MaxTotalExtractionBytes)
            {
                throw new InvalidDataException("Arşiv boyutu izin verilen 1 GB güvenlik sınırını aştı (Decompression Bomb).");
            }

            var destPath = Path.GetFullPath(Path.Combine(fullDestDir, entry.FullName));
            if (!destPath.StartsWith(fullDestDir, StringComparison.Ordinal))
            {
                throw new SecurityException($"Zip Slip saldırısı tespit edildi: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            var parentDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
