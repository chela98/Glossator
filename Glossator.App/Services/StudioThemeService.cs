using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Glossator.App.Services;

/// <summary>
/// Uygulamanın Dark ve Light Fluent tema tokenlarını ve WPF-UI kaynaklarını çalışma anında senkronize eder.
/// </summary>
public sealed class StudioThemeService
{
    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["StudioSidebarBrush"] = "#111318",
        ["StudioCanvasBrush"] = "#161920",
        ["StudioCardBrush"] = "#1C2029",
        ["StudioCardElevatedBrush"] = "#222733",
        ["StudioHoverBrush"] = "#262C3A",
        ["StudioBorderBrush"] = "#2A303E",
        ["StudioAccentBrush"] = "#3B82F6",
        ["StudioAccentSoftBrush"] = "#1F3B82F6",
        ["StudioSuccessSoftBrush"] = "#1F10B981",
        ["StudioWarningSoftBrush"] = "#1FF59E0B",
        ["StudioTerminalBrush"] = "#0D0F14",
        ["DataGridAlternatingRowBrush"] = "#07FFFFFF",
        ["DataGridGridLineBrush"] = "#16FFFFFF",
        ["StudioAccentForegroundBrush"] = "#60A5FA",
        ["StudioOnAccentSoftTextBrush"] = "#FFFFFF"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["StudioSidebarBrush"] = "#F3F5F8",
        ["StudioCanvasBrush"] = "#F8FAFC",
        ["StudioCardBrush"] = "#FFFFFF",
        ["StudioCardElevatedBrush"] = "#EEF2F7",
        ["StudioHoverBrush"] = "#E6ECF5",
        ["StudioBorderBrush"] = "#D8E0EC",
        ["StudioAccentBrush"] = "#2563EB",
        ["StudioAccentSoftBrush"] = "#182563EB",
        ["StudioSuccessSoftBrush"] = "#1A10B981",
        ["StudioWarningSoftBrush"] = "#24F59E0B",
        ["StudioTerminalBrush"] = "#EFF3F8",
        ["DataGridAlternatingRowBrush"] = "#050F172A",
        ["DataGridGridLineBrush"] = "#1A0F172A",
        ["StudioAccentForegroundBrush"] = "#1D4ED8",
        ["StudioOnAccentSoftTextBrush"] = "#0F172A"
    };

    public bool IsDarkTheme { get; private set; } = true;

    public void ApplyTheme(bool isDark)
    {
        IsDarkTheme = isDark;

        if (Application.Current is null)
            return;

        var targetTheme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(targetTheme, WindowBackdropType.Mica, updateAccent: true);

        var palette = isDark ? DarkPalette : LightPalette;
        foreach (var (key, hex) in palette)
        {
            Application.Current.Resources[key] = CreateFrozenBrush(hex);
        }

        foreach (Window win in Application.Current.Windows)
        {
            WindowBackgroundManager.UpdateBackground(win, targetTheme, WindowBackdropType.Mica);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
