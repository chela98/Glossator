using System.Reflection;

namespace Glossator.AI.Prompts;

/// <summary>
/// Sistem istemlerini (prompt) derlenmiş gömülü kaynaklardan (Embedded Resource) veya
/// uygulama çıktı dizininden çalışma dizininden bağımsız olarak yükleyen servis.
/// </summary>
public static class PromptLoader
{
    public static async Task<string> LoadPromptAsync(string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // 1. Birincil Kaynak: Assembly Embedded Resource (100% taşınabilir, disk yoluna bağımsız)
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

        // 2. İkincil Kaynak: AppContext.BaseDirectory/Prompts/<fileName>
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", fileName);
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, ct);
        }

        // 3. Üçüncül Kaynak: AppContext.BaseDirectory/<fileName>
        var directPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(directPath))
        {
            return await File.ReadAllTextAsync(directPath, ct);
        }

        // 4. Son Çare: Mevcut çalışma dizini
        var workingDirPath = Path.Combine(Directory.GetCurrentDirectory(), "Prompts", fileName);
        if (File.Exists(workingDirPath))
        {
            return await File.ReadAllTextAsync(workingDirPath, ct);
        }

        throw new FileNotFoundException(
            $"Prompt dosyası '{fileName}' gömülü kaynaklarda veya '{AppContext.BaseDirectory}' altında bulunamadı.");
    }
}
