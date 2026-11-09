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
    /// Tüm kitapların paylaştığı Git-dostu merkezi YAML sözlük dosya yolunu döner.
    /// </summary>
    public static string GetMasterGlossaryYamlPath()
    {
        return Path.Combine(GetGlossatorHomeDirectory(), "master_glossary.yaml");
    }

    /// <summary>
    /// Kullanıcıya özel API anahtarları ve yapılandırmaların Git'e sızmadan güvenle saklandığı dosya yolunu döner (~/.glossator/user_settings.json).
    /// </summary>
    public static string GetUserSettingsPath()
    {
        return Path.Combine(GetGlossatorHomeDirectory(), "user_settings.json");
    }
}
