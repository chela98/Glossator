namespace Glossator.App.Services;

/// <summary>
/// ViewModel katmanının WPF dosya seçici, onay diyaloğu ve harici işlem çağrılarını soyutlar.
/// </summary>
public interface IStudioDialogService
{
    string? PickEpubFile();
    string? PickEpubFolder();
    string? PickYamlOpenFile(string? initialDirectory = null);
    string? PickYamlSaveFile(string defaultFileName, string? initialDirectory = null);
    string? PickEpubSaveFile(string defaultFileName, string? initialDirectory = null);

    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Onayla",
        string closeButtonText = "Vazgeç");

    void OpenUrlInBrowser(string url);
    void OpenFolderInExplorer(string directoryPath);
    void CopyTextToClipboard(string text);
}
