using System.Text.Json.Nodes;
using Glossator.AI.Configuration;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.App.ViewModels;
using Glossator.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glossator.Tests.App;

[Collection("GlobalDiagnosticsAndConsole")]
public sealed class StudioThemeAndAboutTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalGlossatorHome;

    public StudioThemeAndAboutTests()
    {
        _originalGlossatorHome = Environment.GetEnvironmentVariable("GLOSSATOR_HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), $"glossator_theme_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempHome);
    }

    [Fact]
    public async Task SaveThemePreferenceAsync_PersistsLightAndDarkThemesAndUpdatesConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new UserSettingsService(Options.Create(new GlossatorAiOptions()), config);

        Assert.True(service.IsDarkThemePreferred);

        await service.SaveThemePreferenceAsync(false);
        Assert.False(service.IsDarkThemePreferred);
        Assert.Equal("Light", config["Studio:Theme"]);

        var settingsPath = GlossatorPaths.GetUserSettingsPath();
        var rootLight = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal("Light", rootLight["Studio"]?["Theme"]?.GetValue<string>());

        await service.SaveThemePreferenceAsync(true);
        Assert.True(service.IsDarkThemePreferred);
        Assert.Equal("Dark", config["Studio:Theme"]);

        var rootDark = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal("Dark", rootDark["Studio"]?["Theme"]?.GetValue<string>());
    }

    [Fact]
    public async Task SaveStudioPreferencesAsync_PreservesPersistedThemePreference()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new UserSettingsService(Options.Create(new GlossatorAiOptions()), config);

        await service.SaveThemePreferenceAsync(false);
        await service.SaveStudioPreferencesAsync("http://localhost:4317", "Glossator.Studio", 43.25);

        var settingsPath = GlossatorPaths.GetUserSettingsPath();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal("Light", root["Studio"]?["Theme"]?.GetValue<string>());
        Assert.Equal(43.25, root["Studio"]?["UsdToTryRate"]?.GetValue<double>());
    }

    [Fact]
    public void StudioThemeService_ApplyTheme_WithoutWpfApplication_UpdatesStateWithoutThrowing()
    {
        var themeService = new StudioThemeService();
        Assert.True(themeService.IsDarkTheme);

        themeService.ApplyTheme(false);
        Assert.False(themeService.IsDarkTheme);

        themeService.ApplyTheme(true);
        Assert.True(themeService.IsDarkTheme);
    }

    [Fact]
    public void AboutViewModel_UsesPersonalOpenSourceCopyAndUpdatedDeveloperRole()
    {
        var vm = new AboutViewModel(new StubDialogService(), new BannerService());

        Assert.Equal(".NET & AI Developer", vm.RoleSummary);
        Assert.Contains("açık kaynak", vm.ProjectDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stüdyo", vm.ProjectDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yaşadığım", vm.ProjectDescription, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(vm.ArchitectureBadges, b => b.Contains("Microsoft Agent Framework", StringComparison.Ordinal));
        Assert.Contains(vm.ArchitectureBadges, b => b.Contains("Multi-Provider Discovery", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.ArchitectureBadges, b => b.Contains("408", StringComparison.Ordinal));

        Assert.Equal("4-Aşamalı Çeviri ve Doğrulama Hattı", vm.PipelineTitle);
        Assert.Equal(4, vm.PipelineStages.Count);
        Assert.Equal(["1", "2", "3", "4"], vm.PipelineStages.Select(s => s.StepNumber).ToArray());
        Assert.All(vm.PipelineStages, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
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
