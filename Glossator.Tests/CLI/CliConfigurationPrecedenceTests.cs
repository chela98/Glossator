using Glossator.Cli.Infrastructure;
using Glossator.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Glossator.Tests.CLI;

[Collection("GlobalDiagnosticsAndConsole")]
public sealed class CliConfigurationPrecedenceTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalGlossatorHome;

    public CliConfigurationPrecedenceTests()
    {
        _originalGlossatorHome = Environment.GetEnvironmentVariable("GLOSSATOR_HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), $"glossator_cli_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempHome);
    }

    [Fact]
    public void Configure_UserSettingsOverridesUserSecretsAndAppSettings()
    {
        var baseDir = Path.Combine(_tempHome, "clibase");
        Directory.CreateDirectory(baseDir);

        File.WriteAllText(Path.Combine(baseDir, "appsettings.example.json"), """
            {
              "GlossatorAi": {
                "Providers": {
                  "Evren": { "ApiKey": "YOUR_EVREN_API_KEY" }
                }
              }
            }
            """);

        var secretsPath = Path.Combine(_tempHome, "secrets.json");
        File.WriteAllText(secretsPath, """
            {
              "GlossatorAi": {
                "Providers": {
                  "Evren": { "ApiKey": "evren-stale-user-secret" }
                }
              }
            }
            """);

        var userSettingsPath = GlossatorPaths.GetUserSettingsPath();
        File.WriteAllText(userSettingsPath, """
            {
              "GlossatorAi": {
                "Providers": {
                  "Evren": { "ApiKey": "evren-fresh-user-settings" }
                }
              }
            }
            """);

        var builder = new ConfigurationBuilder();
        CliHostConfiguration.Configure(
            builder,
            "Production",
            ["book.epub", "--fresh", "-j", "2"],
            basePath: baseDir,
            userSettingsPath: userSettingsPath,
            configureUserSecrets: cfg => cfg.AddJsonFile(secretsPath, optional: false, reloadOnChange: false));

        var config = builder.Build();
        Assert.Equal("evren-fresh-user-settings", config["GlossatorAi:Providers:Evren:ApiKey"]);
    }

    [Fact]
    public void Configure_DefaultChainPlacesUserSecretsBeforeUserSettings()
    {
        var baseDir = Path.Combine(_tempHome, "clibase_default");
        Directory.CreateDirectory(baseDir);

        var builder = new ConfigurationBuilder();
        CliHostConfiguration.Configure(builder, "Development", [], basePath: baseDir);
        var root = (IConfigurationRoot)builder.Build();

        var providerPaths = root.Providers
            .OfType<JsonConfigurationProvider>()
            .Select(p => p.Source.Path ?? string.Empty)
            .ToList();

        var secretsIdx = providerPaths.FindIndex(p => p.EndsWith("secrets.json", StringComparison.OrdinalIgnoreCase));
        var userSettingsIdx = providerPaths.FindIndex(p => p.EndsWith("user_settings.json", StringComparison.OrdinalIgnoreCase));

        Assert.True(userSettingsIdx >= 0, "user_settings.json provider should be registered.");
        if (secretsIdx >= 0)
        {
            Assert.True(userSettingsIdx > secretsIdx, "user_settings.json must override User Secrets.");
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _originalGlossatorHome);
        try
        {
            if (Directory.Exists(_tempHome))
            {
                Directory.Delete(_tempHome, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
