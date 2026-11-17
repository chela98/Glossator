using Glossator.Core.Data;
using Microsoft.Extensions.Configuration;

namespace Glossator.Cli.Infrastructure;

/// <summary>
/// Configures the CLI configuration source precedence so persisted Studio/CLI user settings override User Secrets.
/// </summary>
internal static class CliHostConfiguration
{
    public static IConfigurationBuilder Configure(
        IConfigurationBuilder configuration,
        string environmentName,
        string[] args,
        string? basePath = null,
        string? userSettingsPath = null,
        Action<IConfigurationBuilder>? configureUserSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configArgs = (args ?? [])
            .Where(a => a.Contains('=', StringComparison.Ordinal) || a.StartsWith('/'))
            .ToArray();

        configuration.Sources.Clear();
        configuration
            .SetBasePath(basePath ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.example.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);

        if (configureUserSecrets != null)
        {
            configureUserSecrets(configuration);
        }
        else
        {
            configuration.AddUserSecrets<Program>(optional: true);
        }

        return configuration
            .AddJsonFile(userSettingsPath ?? GlossatorPaths.GetUserSettingsPath(), optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(configArgs);
    }
}
