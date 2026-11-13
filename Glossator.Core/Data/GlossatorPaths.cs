namespace Glossator.Core.Data;

public static class GlossatorPaths
{
    /// <summary>
    /// Glossator uygulamasının kullanıcı profili altındaki merkezi çalışma dizinini döner (~/.glossator).
    /// </summary>
    public static string GetGlossatorHomeDirectory()
    {
        var envHome = Environment.GetEnvironmentVariable("GLOSSATOR_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            if (!Directory.Exists(envHome))
            {
                Directory.CreateDirectory(envHome);
            }
            return envHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeDir = Path.Combine(userProfile, ".glossator");
        if (!Directory.Exists(homeDir))
        {
            Directory.CreateDirectory(homeDir);
        }
        return homeDir;
    }

    /// <summary>
    /// Tüm kitapların paylaştığı ve sürekli genişleyen merkezi SQLite veritabanı dosya yolunu döner.
    /// GLOSSATOR_DB ortam değişkeni tanımlıysa bu yolu ezer.
    /// </summary>
    public static string GetMasterDatabasePath()
    {
        var envOverride = Environment.GetEnvironmentVariable("GLOSSATOR_DB");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }

        return Path.Combine(GetGlossatorHomeDirectory(), "glossator.db");
    }

    /// <summary>
    /// Uygulama veya çalışma dizininde bulunan yerel glossary.yaml dosya yolunu döner (varsa).
    /// </summary>
    public static string? FindActiveLocalGlossaryYamlPath(string? bookDir = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(bookDir))
        {
            candidates.Add(Path.Combine(bookDir, "glossary.yaml"));
        }
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "glossary.yaml"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "glossary.yaml"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Kullanıcıya özel API anahtarları ve yapılandırmaların Git'e sızmadan güvenle saklandığı dosya yolunu döner (~/.glossator/user_settings.json).
    /// </summary>
    public static string GetUserSettingsPath()
    {
        return Path.Combine(GetGlossatorHomeDirectory(), "user_settings.json");
    }
}
