namespace Glossator.Core.Security;

using System.Buffers;
using System.Collections.Frozen;
using System.Security;
using System.Text.RegularExpressions;

public static partial class PathSanitizer
{
    private static readonly FrozenSet<string> ReservedDeviceNames = new[]
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
        "CLOCK$"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // İşletim sisteminden bağımsız taşınabilir geçersiz dosya adı karakterleri (Windows + POSIX)
    private static readonly SearchValues<char> PortableInvalidFileNameChars = SearchValues.Create(
        Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .Distinct()
            .ToArray());

    [GeneratedRegex(@"\s+", RegexOptions.None, 250)]
    private static partial Regex MultipleWhitespaceRegex();

    public static string SanitizeFileName(string rawFileName, int maxBaseLength = 100)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "Glossator_Book";
        }

        var cleanChars = rawFileName.Where(c => c >= 32 && !PortableInvalidFileNameChars.Contains(c)).ToArray();
        var clean = new string(cleanChars).Trim();
        clean = MultipleWhitespaceRegex().Replace(clean, "_").Trim('_', '.');

        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = "Glossator_Book";
        }

        var baseName = Path.GetFileNameWithoutExtension(clean).TrimEnd('_', '.');
        var ext = Path.GetExtension(clean);

        if (ReservedDeviceNames.Contains(baseName))
        {
            baseName = $"_{baseName}";
        }

        if (baseName.Length > maxBaseLength)
        {
            baseName = baseName[..maxBaseLength].TrimEnd('_', '.');
        }

        return $"{baseName}{ext}";
    }

    public static string ResolveWithinRoot(string rootDirectory, string relativePath)
        => ResolveWithinRoot(rootDirectory, rootDirectory, relativePath);

    public static string ResolveWithinRoot(string allowedRootDirectory, string baseDirectory, string relativePath)
    {
        if (!TryResolveWithinRoot(allowedRootDirectory, baseDirectory, relativePath, out var resolvedPath))
        {
            throw new SecurityException($"Güvenlik ihlali: '{relativePath}' yolu kök dizin ('{allowedRootDirectory}') dışına çıkıyor.");
        }

        return resolvedPath;
    }

    public static bool TryResolveWithinRoot(
        string allowedRootDirectory,
        string baseDirectory,
        string? relativePath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(allowedRootDirectory) ||
            string.IsNullOrWhiteSpace(baseDirectory) ||
            string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (relativePath.IndexOf('\0') >= 0)
        {
            return false;
        }

        var trimmed = relativePath.Trim();
        if (trimmed.StartsWith('/') ||
            trimmed.StartsWith('\\') ||
            (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':') ||
            Path.IsPathRooted(trimmed))
        {
            return false;
        }

        var normalizedRelative = trimmed
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedRelative))
        {
            return false;
        }

        var rootFull = Path.GetFullPath(allowedRootDirectory);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        var baseFull = Path.GetFullPath(baseDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var baseWithSeparator = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        if (!baseWithSeparator.StartsWith(rootFull, comparison))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(baseFull, normalizedRelative));
        var isExactRoot = string.Equals(
            candidate.TrimEnd(Path.DirectorySeparatorChar),
            rootFull.TrimEnd(Path.DirectorySeparatorChar),
            comparison);

        if (!isExactRoot && !candidate.StartsWith(rootFull, comparison))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    public static string ResolveSafeOutputPath(string inputPath, string? explicitOutput, string bookTitle)
    {
        if (!string.IsNullOrWhiteSpace(explicitOutput))
        {
            return Path.GetFullPath(explicitOutput);
        }

        var safeTitle = SanitizeFileName(bookTitle);
        var fileName = $"{safeTitle}_TR.epub";

        if (File.Exists(inputPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))
                      ?? Environment.CurrentDirectory;
            return Path.Combine(dir, fileName);
        }

        if (Directory.Exists(inputPath))
        {
            var fullDir = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(fullDir)?.FullName ?? fullDir;
            return Path.Combine(parent, fileName);
        }

        return Path.Combine(Environment.CurrentDirectory, fileName);
    }
}
