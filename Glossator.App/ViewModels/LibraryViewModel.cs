using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.Core.Models.Epub;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

/// <summary>
/// Kütüphane görünümünü ve kenar çubuğundaki "Son Kitaplar" hızlı geçiş listesini yönetir.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;

    private string _searchQuery = string.Empty;
    private string? _activeBookPath;
    private bool _isLocked;
    private int _filteredCount;

    public LibraryViewModel(IStudioDialogService dialogService, BannerService bannerService)
    {
        _dialogService = dialogService;
        _bannerService = bannerService;

        FilteredRecentBooks = CollectionViewSource.GetDefaultView(RecentBooks);
        FilteredRecentBooks.Filter = FilterBook;

        OpenBookFileCommand = new AsyncRelayCommand(PickAndOpenEpubAsync, () => !IsLocked);
        OpenFolderCommand = new AsyncRelayCommand(PickAndOpenFolderAsync, () => !IsLocked);
        OpenBookEntryCommand = new AsyncRelayCommand(OpenBookEntryAsync, _ => !IsLocked);
        RemoveBookEntryCommand = new AsyncRelayCommand(RemoveBookEntryAsync, _ => !IsLocked);
        OpenBookFolderCommand = new RelayCommand(OpenBookFolder);
        CleanMissingBooksCommand = new AsyncRelayCommand(CleanMissingBooksAsync, () => !IsLocked);
        ClearSearchCommand = new RelayCommand(() => SearchQuery = string.Empty);
    }

    public ObservableCollection<RecentBookEntry> RecentBooks { get; } = [];
    public ICollectionView FilteredRecentBooks { get; }

    public Func<string, Task>? OpenBookRequested { get; set; }
    public Action<bool, string?>? BusyChanged { get; set; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                RefreshFilter();
            }
        }
    }

    public string? ActiveBookPath
    {
        get => _activeBookPath;
        set => SetProperty(ref _activeBookPath, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
            {
                ((AsyncRelayCommand)OpenBookFileCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)OpenFolderCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)OpenBookEntryCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)RemoveBookEntryCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)CleanMissingBooksCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasRecentBooks => RecentBooks.Count > 0;
    public int FilteredCount { get => _filteredCount; private set => SetProperty(ref _filteredCount, value); }
    public string LibrarySummaryText => $"Kayıtlı {RecentBooks.Count} kitap (Gösterilen: {FilteredCount})";

    public ICommand OpenBookFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenBookEntryCommand { get; }
    public ICommand RemoveBookEntryCommand { get; }
    public ICommand OpenBookFolderCommand { get; }
    public ICommand CleanMissingBooksCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public async Task RefreshAsync()
    {
        var items = await RecentBooksManager.LoadRecentBooksAsync();
        RecentBooks.Clear();
        foreach (var item in items)
        {
            RecentBooks.Add(item);
        }

        OnPropertyChanged(nameof(HasRecentBooks));
        RefreshFilter();
    }

    public async Task<RecentBookEntry> RecordOpenedAsync(EpubPackage package, string bookPath)
    {
        ActiveBookPath = bookPath;
        var entry = await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package);
        await RefreshAsync();
        return entry;
    }

    private bool FilterBook(object obj)
    {
        if (obj is not RecentBookEntry entry) return false;
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(entry.Author) && entry.Author.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(entry.Publisher) && entry.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFilter()
    {
        FilteredRecentBooks.Refresh();
        FilteredCount = FilteredRecentBooks.Cast<object>().Count();
        OnPropertyChanged(nameof(LibrarySummaryText));
    }

    private async Task PickAndOpenEpubAsync()
    {
        var file = _dialogService.PickEpubFile();
        if (!string.IsNullOrWhiteSpace(file) && OpenBookRequested != null)
        {
            await OpenBookRequested(file);
        }
    }

    private async Task PickAndOpenFolderAsync()
    {
        var folder = _dialogService.PickEpubFolder();
        if (!string.IsNullOrWhiteSpace(folder) && OpenBookRequested != null)
        {
            await OpenBookRequested(folder);
        }
    }

    private async Task OpenBookEntryAsync(object? parameter)
    {
        var entry = parameter as RecentBookEntry;
        var path = entry?.Path ?? parameter as string;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _bannerService.Show("Kitap Bulunamadı", $"Seçilen kitap diskte bulunamadı: {path}", InfoBarSeverity.Warning);
            return;
        }

        if (OpenBookRequested != null)
        {
            await OpenBookRequested(path);
        }
    }

    private async Task RemoveBookEntryAsync(object? parameter)
    {
        if (parameter is not RecentBookEntry entry) return;

        try
        {
            BusyChanged?.Invoke(true, "Kitap kaydı listeden çıkarılıyor...");
            await RecentBooksManager.RemoveBookAsync(entry.Path);
            await RefreshAsync();
            _bannerService.Show("Kitap Kaldırıldı", $"'{entry.Title}' kütüphane listesinden çıkarıldı (diskteki dosyalara dokunulmadı).", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Kaldırma Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private void OpenBookFolder(object? parameter)
    {
        if (parameter is not RecentBookEntry entry) return;
        try
        {
            var dir = File.Exists(entry.Path) ? Path.GetDirectoryName(entry.Path) : entry.Path;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                _bannerService.Show("Klasör Bulunamadı", $"Klasör diskte yok: {entry.Path}", InfoBarSeverity.Warning);
                return;
            }
            _dialogService.OpenFolderInExplorer(dir);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Klasör Açılamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CleanMissingBooksAsync()
    {
        try
        {
            BusyChanged?.Invoke(true, "Eksik kitap kayıtları temizleniyor...");
            int removed = await RecentBooksManager.CleanMissingBooksAsync();
            await RefreshAsync();
            _bannerService.Show(
                "Kütüphane Temizlendi",
                removed > 0 ? $"Diskte bulunmayan {removed} kayıtlı kitap listeden temizlendi." : "Kayıtlı tüm kitaplar diskte mevcut.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Temizleme Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }
}
