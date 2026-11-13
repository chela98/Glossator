namespace Glossator.Core.Security;

using System.IO.Compression;
using System.Security;

public static class SafeZipExtractor
{
    public const long MaxTotalExtractionBytes = 1024L * 1024L * 1024L; // 1 GB Safety limit
    public const int MaxEntryLimit = 10_000;

    public static void ExtractSafely(string archivePath, string destinationDirectory, bool overwrite = false)
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
            if (entry.Length < 0)
            {
                throw new InvalidDataException($"Geçersiz arşiv girdi boyutu: {entry.FullName}");
            }

            try
            {
                totalBytes = checked(totalBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("Arşiv boyutu izin verilen 1 GB güvenlik sınırını aştı (Decompression Bomb).", ex);
            }

            if (totalBytes > MaxTotalExtractionBytes)
            {
                throw new InvalidDataException("Arşiv boyutu izin verilen 1 GB güvenlik sınırını aştı (Decompression Bomb).");
            }

            string destPath;
            try
            {
                destPath = PathSanitizer.ResolveWithinRoot(fullDestDir, entry.FullName);
            }
            catch (SecurityException)
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

            if (!overwrite && File.Exists(destPath))
            {
                throw new IOException($"Arşiv içinde çakışan hedef dosya tespit edildi (overwrite devre dışı): {entry.FullName}");
            }

            entry.ExtractToFile(destPath, overwrite);
        }
    }
}
