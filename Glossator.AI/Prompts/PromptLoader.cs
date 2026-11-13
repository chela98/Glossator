using System.Reflection;

namespace Glossator.AI.Prompts;

/// <summary>
/// promptları deterministik bir hiyerarşiyle yükleyen servis:
/// 1. Birincil Öncelik: EXE yanındaki "Prompts/{fileName}" klasörü veya doğrudan EXE yanı "{fileName}" (kullanıcı özelleştirmesi).
/// 2. İkincil Öncelik (Fallback): Derlenmiş DLL içindeki gömülü kaynak (Embedded Resource).
/// Rastgele terminal dizinleri (CWD) taranmaz; böylece istem dışı eski prompt gölgelemesi (shadowing) engellenir.
/// </summary>
public static class PromptLoader
{
    public static Task<string> LoadPromptAsync(string fileName, CancellationToken ct = default) =>
        LoadPromptAsync(fileName, AppContext.BaseDirectory, ct);

    internal static async Task<string> LoadPromptAsync(string fileName, string baseDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // 1. Birincil Öncelik: baseDirectory / Prompts / <fileName> (EXE yanındaki Prompts klasörü)
        var exePromptsPath = Path.Combine(baseDirectory, "Prompts", fileName);
        if (File.Exists(exePromptsPath))
        {
            return await File.ReadAllTextAsync(exePromptsPath, ct);
        }

        // 2. İkincil Öncelik: Doğrudan baseDirectory / <fileName> (Doğrudan EXE yanı)
        var directExePath = Path.Combine(baseDirectory, fileName);
        if (File.Exists(directExePath))
        {
            return await File.ReadAllTextAsync(directExePath, ct);
        }

        // 3. Nihai Güvenli Kaynak (Embedded Resource Fallback):
        // Disk üzerinde kullanıcı tarafından özelleştirilmiş dosya yoksa DLL içine derlenmiş varsayılan istemi kullan.
        var resourceContent = await TryLoadEmbeddedPromptAsync(fileName, ct);
        if (resourceContent != null)
        {
            return resourceContent;
        }

        throw new FileNotFoundException(
            $"Prompt dosyası '{fileName}' disk üzerinde ({baseDirectory}) veya gömülü kaynaklarda bulunamadı.");
    }

    public static string? GetActiveDiskPath(string fileName) =>
        GetActiveDiskPath(fileName, AppContext.BaseDirectory);

    internal static string? GetActiveDiskPath(string fileName, string baseDirectory)
    {
        var exePromptsPath = Path.Combine(baseDirectory, "Prompts", fileName);
        if (File.Exists(exePromptsPath)) return exePromptsPath;

        var directExePath = Path.Combine(baseDirectory, fileName);
        if (File.Exists(directExePath)) return directExePath;

        return null;
    }

    public static async Task<string?> TryLoadEmbeddedPromptAsync(string fileName, CancellationToken ct = default)
    {
        var assembly = typeof(PromptLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Prompts.{fileName}", StringComparison.OrdinalIgnoreCase) ||
                                 n.EndsWith($".{fileName}", StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct);
            }
        }

        return null;
    }
}
