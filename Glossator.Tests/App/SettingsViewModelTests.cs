using System.Text.Json.Nodes;
using Glossator.AI.Configuration;
using Glossator.AI.Services;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.App.ViewModels;
using Glossator.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glossator.Tests.App;

[Collection("GlobalDiagnosticsAndConsole")]
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalGlossatorHome;

    public SettingsViewModelTests()
    {
        _originalGlossatorHome = Environment.GetEnvironmentVariable("GLOSSATOR_HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), $"glossator_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempHome);
    }

    [Fact]
    public void ReasoningEfforts_ContainsNoneMinimalLowMediumHighAndXHigh()
    {
        var vm = CreateViewModel(CreateSampleOptions(), new FakeProviderDiscoveryService());

        Assert.Equal(
            ["(Varsayılan)", "none", "minimal", "low", "medium", "high", "xhigh"],
            vm.ReasoningEfforts);
    }

    [Fact]
    public async Task LoadAndSave_PreservesNoneMinimalXHighAndCustomEffortWithoutDataLoss()
    {
        var opts = CreateSampleOptions();
        opts.Agents["StyleAnalyst"] = new AgentRoleOptions { Provider = "Gemini", ModelId = "gemini-2.5-flash", ReasoningEffort = "none" };
        opts.Agents["Translator"] = new AgentRoleOptions { Provider = "OpenAI", ModelId = "gpt-5", ReasoningEffort = "xhigh" };
        opts.Agents["Reviewer"] = new AgentRoleOptions { Provider = "Gemini", ModelId = "gemini-3.8-flash", ReasoningEffort = "ultra" };

        var vm = CreateViewModel(opts, new FakeProviderDiscoveryService());
        vm.LoadFromSettings();

        Assert.Equal("none", vm.Agents[0].ReasoningEffort);
        Assert.Equal("xhigh", vm.Agents[1].ReasoningEffort);
        Assert.Equal("ultra", vm.Agents[2].ReasoningEffort);
        Assert.Contains("ultra", vm.ReasoningEfforts);
        Assert.False(vm.IsSettingsDirty);

        vm.Agents[0].ReasoningEffort = "minimal";
        vm.Agents[2].ReasoningEffort = "(Varsayılan)";
        Assert.True(vm.IsSettingsDirty);

        var saved = await vm.SaveAiSettingsAsync();
        Assert.True(saved);
        Assert.False(vm.IsSettingsDirty);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(GlossatorPaths.GetUserSettingsPath()))!.AsObject();
        var agents = json["GlossatorAi"]?["Agents"]?.AsObject();
        Assert.Equal("minimal", agents?["StyleAnalyst"]?["ReasoningEffort"]?.GetValue<string>());
        Assert.Equal("xhigh", agents?["Translator"]?["ReasoningEffort"]?.GetValue<string>());
        Assert.Equal(string.Empty, agents?["Reviewer"]?["ReasoningEffort"]?.GetValue<string>());
    }

    [Fact]
    public async Task PingAndFetchModels_PopulateProviderAndAgentModelsWhilePreservingManualEntries()
    {
        var fakeDiscovery = new FakeProviderDiscoveryService
        {
            OnPingAsync = (endpoint, _, _, _) =>
            {
                if (endpoint.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ProviderPingResult(
                        true,
                        200,
                        88,
                        "Bağlandı (88 ms · 3 model)",
                        ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-3.8-flash"]));
                }

                return Task.FromResult(new ProviderPingResult(
                    true,
                    200,
                    45,
                    "Bağlandı (45 ms · 2 model)",
                    ["gpt-4o", "gpt-5"]));
            },
            OnFetchModelsAsync = (endpoint, _, _) =>
            {
                if (endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ProviderModelsResult(
                        true,
                        200,
                        51,
                        ["gpt-4o", "gpt-5", "gpt-5-mini"],
                        "3 model çekildi (51 ms)."));
                }

                return Task.FromResult(new ProviderModelsResult(false, 404, 20, [], "/models kapalı"));
            }
        };

        var opts = CreateSampleOptions();
        opts.ActiveProvider = "Gemini";
        opts.Agents["StyleAnalyst"] = new AgentRoleOptions { Provider = "", ModelId = "custom-style-model" };
        opts.Agents["Translator"] = new AgentRoleOptions { Provider = "Gemini", ModelId = "gemini-3.8-flash" };
        opts.Agents["Reviewer"] = new AgentRoleOptions { Provider = "OpenAI", ModelId = "custom-reviewer-model" };

        var vm = CreateViewModel(opts, fakeDiscovery);
        vm.LoadFromSettings();

        // Ping currently selected Gemini provider
        Assert.Equal("Gemini", vm.SelectedEditProvider);
        await vm.PingCurrentProviderAsync();

        Assert.True(vm.ProviderStatusSucceeded);
        Assert.Contains("88 ms", vm.ProviderStatusMessage, StringComparison.Ordinal);
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro", "gemini-3.8-flash"], vm.EditProviderModels);
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro", "gemini-3.8-flash"], vm.Agents[0].AvailableModels);
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro", "gemini-3.8-flash"], vm.Agents[1].AvailableModels);
        Assert.Empty(vm.Agents[2].AvailableModels);

        // Manual / existing model text must never be wiped by list updates
        Assert.Equal("gemini-2.5-flash", vm.EditDefaultModel);
        Assert.Equal("custom-style-model", vm.Agents[0].ModelId);
        Assert.Equal("gemini-3.8-flash", vm.Agents[1].ModelId);
        Assert.False(vm.IsSettingsDirty);

        // Switch to OpenAI, fetch OpenAI models -> OpenAI agent receives them
        vm.SelectedEditProvider = "OpenAI";
        Assert.Equal(string.Empty, vm.ProviderStatusMessage);
        await vm.FetchModelsForCurrentProviderAsync();

        Assert.Equal(["gpt-4o", "gpt-5", "gpt-5-mini"], vm.EditProviderModels);
        Assert.Equal(["gpt-4o", "gpt-5", "gpt-5-mini"], vm.Agents[2].AvailableModels);
        Assert.Equal("custom-reviewer-model", vm.Agents[2].ModelId);

        // Switch ActiveProvider to OpenAI -> StyleAnalyst (following active provider) switches to OpenAI models!
        vm.SelectedActiveProvider = "OpenAI";
        Assert.Equal(["gpt-4o", "gpt-5", "gpt-5-mini"], vm.Agents[0].AvailableModels);
        Assert.Equal("custom-style-model", vm.Agents[0].ModelId);

        // Switching back to Gemini restores cached ping status
        vm.SelectedEditProvider = "Gemini";
        Assert.Contains("88 ms", vm.ProviderStatusMessage, StringComparison.Ordinal);

        // Editing endpoint invalidates cached status for that provider
        vm.EditEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";
        Assert.Equal(string.Empty, vm.ProviderStatusMessage);
        Assert.Null(vm.ProviderStatusSucceeded);
    }

    [Fact]
    public void LoadFromSettings_WhenProvidersEmpty_PopulatesReadyPresetProfilesKeepingEvrenActive()
    {
        var emptyOptions = new GlossatorAiOptions();
        var vm = CreateViewModel(emptyOptions, new FakeProviderDiscoveryService());

        vm.LoadFromSettings();

        Assert.Contains("Evren", vm.ProviderNames);
        Assert.Contains("Gemini", vm.ProviderNames);
        Assert.Contains("OpenAI", vm.ProviderNames);
        Assert.Contains("Ollama", vm.ProviderNames);
        Assert.Equal("Evren", vm.SelectedActiveProvider);
        Assert.Equal("Evren", vm.SelectedEditProvider);
    }

    [Fact]
    public void ZeroConfigConfigurationChain_LoadsExampleDefaultsAndLetsUserSettingsOverride()
    {
        var examplePath = FindRepoRelativeFile("Glossator.Cli", "appsettings.example.json");
        var tempDir = Path.Combine(Path.GetTempPath(), $"glossator_cfg_chain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var exampleCopy = Path.Combine(tempDir, "appsettings.example.json");
            File.Copy(examplePath, exampleCopy);
            var userSettingsPath = Path.Combine(tempDir, "user_settings.json");

            var baseConfig = new ConfigurationBuilder()
                .SetBasePath(tempDir)
                .AddJsonFile("appsettings.example.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false)
                .Build();

            var baseOptions = baseConfig.GetSection("GlossatorAi").Get<GlossatorAiOptions>()!;
            Assert.Equal("Evren", baseOptions.ActiveProvider);
            Assert.True(baseOptions.Providers.ContainsKey("Evren"));
            Assert.True(baseOptions.Providers.ContainsKey("Gemini"));
            Assert.True(baseOptions.Providers.ContainsKey("OpenAI"));
            Assert.True(baseOptions.Providers.ContainsKey("Ollama"));

            File.WriteAllText(userSettingsPath, """
                {
                  "GlossatorAi": {
                    "ActiveProvider": "Ollama",
                    "Agents": {
                      "Translator": {
                        "Provider": "",
                        "ModelId": ""
                      }
                    }
                  }
                }
                """);

            var overriddenConfig = new ConfigurationBuilder()
                .SetBasePath(tempDir)
                .AddJsonFile("appsettings.example.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false)
                .Build();

            var overriddenOptions = overriddenConfig.GetSection("GlossatorAi").Get<GlossatorAiOptions>()!;
            Assert.Equal("Ollama", overriddenOptions.ActiveProvider);
            var (resolvedProvider, _, resolvedModel) = overriddenOptions.ResolveAgent("Translator");
            Assert.Equal("Ollama", resolvedProvider);
            Assert.Equal("qwen2.5:14b", resolvedModel);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string FindRepoRelativeFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repo file: {Path.Combine(segments)}");
    }

    private static SettingsViewModel CreateViewModel(GlossatorAiOptions options, IProviderDiscoveryService discoveryService)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var settingsService = new UserSettingsService(Options.Create(options), config);
        return new SettingsViewModel(settingsService, new StubDialogService(), new BannerService(), discoveryService);
    }

    private static GlossatorAiOptions CreateSampleOptions() => new()
    {
        ActiveProvider = "Gemini",
        Providers = new Dictionary<string, AiProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gemini"] = new()
            {
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
                ApiKey = "AIza-test-key",
                DefaultModel = "gemini-2.5-flash"
            },
            ["OpenAI"] = new()
            {
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "sk-test-key",
                DefaultModel = "gpt-4o"
            }
        }
    };

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

    private sealed class FakeProviderDiscoveryService : IProviderDiscoveryService
    {
        public Func<string, string?, string?, CancellationToken, Task<ProviderPingResult>>? OnPingAsync { get; set; }
        public Func<string, string?, CancellationToken, Task<ProviderModelsResult>>? OnFetchModelsAsync { get; set; }

        public Task<ProviderPingResult> PingAsync(string endpoint, string? apiKey, string? defaultModel = null, CancellationToken cancellationToken = default)
            => OnPingAsync?.Invoke(endpoint, apiKey, defaultModel, cancellationToken)
               ?? Task.FromResult(new ProviderPingResult(true, 200, 10, "Bağlandı (10 ms)", []));

        public Task<ProviderModelsResult> FetchModelsAsync(string endpoint, string? apiKey, CancellationToken cancellationToken = default)
            => OnFetchModelsAsync?.Invoke(endpoint, apiKey, cancellationToken)
               ?? Task.FromResult(new ProviderModelsResult(true, 200, 10, [], "0 model"));
    }

    private sealed class StubDialogService : IStudioDialogService
    {
        public string? PickEpubFile() => null;
        public string? PickEpubFolder() => null;
        public string? PickYamlOpenFile(string? initialDirectory = null) => null;
        public string? PickYamlSaveFile(string defaultFileName, string? initialDirectory = null) => null;
        public string? PickEpubSaveFile(string defaultFileName, string? initialDirectory = null) => null;
        public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText = "Onayla", string closeButtonText = "Vazgeç") => Task.FromResult(true);
        public void OpenUrlInBrowser(string url) { }
        public void OpenFolderInExplorer(string directoryPath) { }
        public void CopyTextToClipboard(string text) { }
    }
}
