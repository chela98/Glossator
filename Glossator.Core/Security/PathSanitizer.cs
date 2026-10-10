namespace Glossator.Core.Security;

using System.Text.RegularExpressions;

public static partial class PathSanitizer
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
        "CLOCK$"
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    public static string SanitizeFileName(string rawFileName, int maxBaseLength = 100)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "Glossator_Book";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleanChars = rawFileName.Where(c => !invalidChars.Contains(c) && c >= 32).ToArray();
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
