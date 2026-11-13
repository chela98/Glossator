using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Glossator.App.Services;

/// <summary>
/// Win32 dosya/klasör pencereleri, WPF-UI ContentDialog onayları ve harici tarayıcı/gezgin işlemlerini yürütür.
/// </summary>
public sealed class StudioDialogService : IStudioDialogService
{
    private ContentDialogHost? _dialogHost;

    public void AttachDialogHost(ContentDialogHost dialogHost)
    {
        _dialogHost = dialogHost;
    }

    public string? PickEpubFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "EPUB Kitapları (*.epub)|*.epub|Tüm Dosyalar (*.*)|*.*",
            Title = "Yerelleştirilecek EPUB Kitabını Seçin"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickEpubFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Açılmış EPUB / OEBPS Klasörünü Seçin"
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickYamlOpenFile(string? initialDirectory = null)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "YAML Sözlükleri (*.yaml;*.yml)|*.yaml;*.yml|Tüm Dosyalar (*.*)|*.*",
            Title = "İçe Aktarılacak YAML Sözlüğünü Seçin",
            InitialDirectory = ResolveSafeDirectory(initialDirectory)
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickYamlSaveFile(string defaultFileName, string? initialDirectory = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "YAML Sözlüğü (*.yaml)|*.yaml",
            FileName = defaultFileName,
            Title = "Terim Sözlüğünü Dışa Aktar",
            InitialDirectory = ResolveSafeDirectory(initialDirectory)
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickEpubSaveFile(string defaultFileName, string? initialDirectory = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "EPUB Kitabı (*.epub)|*.epub",
            FileName = defaultFileName,
            Title = "Türkçe EPUB Çıktısını Kaydet",
            InitialDirectory = ResolveSafeDirectory(initialDirectory)
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Onayla",
        string closeButtonText = "Vazgeç")
    {
        if (_dialogHost == null)
        {
            var fallback = System.Windows.MessageBox.Show(
                message,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return fallback == System.Windows.MessageBoxResult.Yes;
        }

        var textBlock = new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            LineHeight = 20
        };

        var dialog = new ContentDialog(_dialogHost)
        {
            Title = title,
            Content = textBlock,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public void OpenUrlInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void OpenFolderInExplorer(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", directoryPath) { UseShellExecute = true });
    }

    public void CopyTextToClipboard(string text)
    {
        Clipboard.SetText(text);
    }

    private static string ResolveSafeDirectory(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            return dir;

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
