using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Glossator.App.Engine;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.Core.Data;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

public enum ChapterFilterMode
{
    All,
    Untranslated,
    Completed,
    ChaptersOnly
}

/// <summary>
/// Aktif açılan EPUB kitabının meta verisini, motor ayarlarını, bölüm seçimini ve paketleme işlemlerini yönetir.
/// </summary>
public sealed class BookWorkspaceViewModel : ObservableObject
{
    private readonly CheckpointStore _checkpointStore;
    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;

    private EpubPackage? _activePackage;
    private string? _activeBookPath;

    private string _bookTitle = "Henüz Kitap Açılmadı";
    private string _bookAuthor = "Kütüphaneden bir kitap seçin veya pencereye .epub bırakın";
    private string _bookPublisher = string.Empty;
    private string _opfDirectory = string.Empty;
    private string? _coverImagePath;
    private string _coverFallbackMonogram = "GL";
    private string _coverFallbackHex = "#1E3A8A";
    private string _bookProgressText = "0 / 0 Bölüm";
    private double _bookProgressPercentage;

    private bool _isEngineOptionsExpanded;
    private int _workersCount = 4;
    private bool _isFreshMode;
    private bool _isTestMode;
    private string _sectionLimitText = string.Empty;
    private bool _syncingTestMode;

    private string _chapterSearchText = string.Empty;
    private ChapterFilterMode _selectedChapterFilter = ChapterFilterMode.All;
    private string _chapterRangeText = string.Empty;
    private string _selectedChaptersSummary = "Seçili: 0 / 0 bölüm";
    private int _filteredChaptersCount;

    private bool _isLocked;
    private bool _isTranslating;

    public BookWorkspaceViewModel(
        CheckpointStore checkpointStore,
        IStudioDialogService dialogService,
        BannerService bannerService)
    {
        _checkpointStore = checkpointStore;
        _dialogService = dialogService;
        _bannerService = bannerService;

        FilteredChapters = CollectionViewSource.GetDefaultView(Chapters);
        FilteredChapters.Filter = FilterChapter;

        ToggleEngineOptionsCommand = new RelayCommand(() => IsEngineOptionsExpanded = !IsEngineOptionsExpanded);
        ApplyRangeCommand = new RelayCommand(ApplyRange, () => HasActiveBook && !IsLocked);
        ResetChapterFiltersCommand = new RelayCommand(ResetChapterFilters);
        SelectAllVisibleCommand = new RelayCommand(SelectAllVisible, () => HasActiveBook && !IsLocked);
        SelectUntranslatedCommand = new RelayCommand(SelectUntranslated, () => HasActiveBook && !IsLocked);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasActiveBook && !IsLocked);
        ResetChapterCheckpointCommand = new AsyncRelayCommand(ResetSingleChapterCheckpointAsync, _ => HasActiveBook && !IsLocked);
        ResetSelectedCheckpointsCommand = new AsyncRelayCommand(ResetSelectedCheckpointsAsync, () => HasActiveBook && !IsLocked);
        TranslateSelectedCommand = new AsyncRelayCommand(async () =>
        {
            if (StartTranslationRequested != null) await StartTranslationRequested();
        }, () => HasActiveBook && !IsLocked && !IsTranslating);
        PackageEpubCommand = new AsyncRelayCommand(PackageEpubAsync, () => HasActiveBook && !IsLocked);
        OpenOpfDirectoryCommand = new RelayCommand(OpenOpfDirectory, () => HasActiveBook);
        ScanBookGlossaryCommand = new AsyncRelayCommand(async () =>
        {
            if (ScanBookGlossaryRequested != null) await ScanBookGlossaryRequested();
        }, () => HasActiveBook && !IsLocked);
        OpenEpubFileCommand = new AsyncRelayCommand(async () =>
        {
            if (PickBookRequested != null) await PickBookRequested();
        }, () => !IsLocked);
    }

    public ObservableCollection<ChapterItemViewModel> Chapters { get; } = [];
    public ICollectionView FilteredChapters { get; }

    public Func<Task>? StartTranslationRequested { get; set; }
    public Func<Task>? ScanBookGlossaryRequested { get; set; }
    public Func<Task>? PickBookRequested { get; set; }
    public Action<bool, string?>? BusyChanged { get; set; }
    public Action? PackageRefreshLibraryRequested { get; set; }

    public EpubPackage? ActivePackage => _activePackage;
    public string? ActiveBookPath => _activeBookPath;
    public bool HasActiveBook => _activePackage != null;

    public string BookTitle { get => _bookTitle; private set => SetProperty(ref _bookTitle, value); }
    public string BookAuthor { get => _bookAuthor; private set => SetProperty(ref _bookAuthor, value); }
    public string BookPublisher { get => _bookPublisher; private set => SetProperty(ref _bookPublisher, value); }
    public string OpfDirectory { get => _opfDirectory; private set => SetProperty(ref _opfDirectory, value); }
    public string? CoverImagePath { get => _coverImagePath; private set => SetProperty(ref _coverImagePath, value); }
    public string CoverFallbackMonogram { get => _coverFallbackMonogram; private set => SetProperty(ref _coverFallbackMonogram, value); }
    public string CoverFallbackHex { get => _coverFallbackHex; private set => SetProperty(ref _coverFallbackHex, value); }
    public string BookProgressText { get => _bookProgressText; private set => SetProperty(ref _bookProgressText, value); }
    public double BookProgressPercentage { get => _bookProgressPercentage; private set => SetProperty(ref _bookProgressPercentage, value); }

    public bool IsEngineOptionsExpanded { get => _isEngineOptionsExpanded; set => SetProperty(ref _isEngineOptionsExpanded, value); }

    public int WorkersCount
    {
        get => _workersCount;
        set => SetProperty(ref _workersCount, Math.Clamp(value, 1, 8));
    }

    public bool IsFreshMode { get => _isFreshMode; set => SetProperty(ref _isFreshMode, value); }

    public bool IsTestMode
    {
        get => _isTestMode;
        set
        {
            if (!SetProperty(ref _isTestMode, value) || _syncingTestMode)
                return;

            _syncingTestMode = true;
            try
            {
                if (value && string.IsNullOrWhiteSpace(SectionLimitText))
                {
                    SectionLimitText = "2";
                }
                else if (!value && SectionLimitText.Trim() == "2")
                {
                    SectionLimitText = string.Empty;
                }
            }
            finally
            {
                _syncingTestMode = false;
            }
        }
    }

    public string SectionLimitText
    {
        get => _sectionLimitText;
        set
        {
            if (!SetProperty(ref _sectionLimitText, value) || _syncingTestMode)
                return;

            _syncingTestMode = true;
            try
            {
                if (int.TryParse(value.Trim(), out var n) && n > 0)
                {
                    SetProperty(ref _isTestMode, n == 2, nameof(IsTestMode));
                }
                else
                {
                    SetProperty(ref _isTestMode, false, nameof(IsTestMode));
                }
            }
            finally
            {
                _syncingTestMode = false;
            }
        }
    }

    public string ChapterSearchText
    {
        get => _chapterSearchText;
        set
        {
            if (SetProperty(ref _chapterSearchText, value))
                RefreshChaptersFilter();
        }
    }

    public ChapterFilterMode SelectedChapterFilter
    {
        get => _selectedChapterFilter;
        set
        {
            if (SetProperty(ref _selectedChapterFilter, value))
                RefreshChaptersFilter();
        }
    }

    public string ChapterRangeText { get => _chapterRangeText; set => SetProperty(ref _chapterRangeText, value); }
    public string SelectedChaptersSummary { get => _selectedChaptersSummary; private set => SetProperty(ref _selectedChaptersSummary, value); }

    public int FilteredChaptersCount
    {
        get => _filteredChaptersCount;
        private set
        {
            if (SetProperty(ref _filteredChaptersCount, value))
                OnPropertyChanged(nameof(HasNoFilteredChapters));
        }
    }

    public bool HasNoFilteredChapters => HasActiveBook && FilteredChaptersCount == 0;

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
                NotifyCommandStates();
        }
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            if (SetProperty(ref _isTranslating, value))
                NotifyCommandStates();
        }
    }

    public ICommand ToggleEngineOptionsCommand { get; }
    public ICommand ApplyRangeCommand { get; }
    public ICommand ResetChapterFiltersCommand { get; }
    public ICommand SelectAllVisibleCommand { get; }
    public ICommand SelectUntranslatedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ResetChapterCheckpointCommand { get; }
    public ICommand ResetSelectedCheckpointsCommand { get; }
    public ICommand TranslateSelectedCommand { get; }
    public ICommand PackageEpubCommand { get; }
    public ICommand OpenOpfDirectoryCommand { get; }
    public ICommand ScanBookGlossaryCommand { get; }
    public ICommand OpenEpubFileCommand { get; }

    public void LoadPackage(EpubPackage package, string bookPath)
    {
        _activePackage = package;
        _activeBookPath = bookPath;

        var fallback = BookCoverFallback.ForBook(package.Title, package.Author);
        BookTitle = package.Title;
        BookAuthor = !string.IsNullOrWhiteSpace(package.Author) ? package.Author : "Bilinmeyen Yazar";
        BookPublisher = RecentBooksManager.DetectPublisher(package);
        OpfDirectory = package.OpfDirectory;
        CoverImagePath = package.ResolveCoverImageFullPath();
        CoverFallbackMonogram = fallback.Monogram;
        CoverFallbackHex = fallback.BackgroundHex;

        foreach (var ch in Chapters)
        {
            ch.PropertyChanged -= OnChapterPropertyChanged;
        }
        Chapters.Clear();

        var docs = RecentBooksManager.GetTranslatableDocuments(package);
        int idx = 1;
        foreach (var doc in docs)
        {
            var state = DocumentTranslationStatus.GetState(package.OpfDirectory, doc.Href);
            var vm = new ChapterItemViewModel
            {
                OrderNumber = idx++,
                Item = doc,
                State = state,
                IsSelected = state != ChapterTranslationState.Completed
            };
            vm.PropertyChanged += OnChapterPropertyChanged;
            Chapters.Add(vm);
        }

        OnPropertyChanged(nameof(HasActiveBook));
        UpdateBookProgressFromChapters();
        UpdateSelectedChaptersSummary();
        RefreshChaptersFilter();
        NotifyCommandStates();
    }

    public void RefreshChapterStatesFromDisk()
    {
        if (_activePackage == null) return;
        foreach (var ch in Chapters)
        {
            ch.State = DocumentTranslationStatus.GetState(_activePackage.OpfDirectory, ch.Href);
        }
        UpdateBookProgressFromChapters();
    }

    public void ApplyLiveProgress(StudioProgressUpdate update)
    {
        if (!update.ChapterState.HasValue) return;

        var matching = Chapters.FirstOrDefault(c => c.Href.Equals(update.ChapterHref, StringComparison.OrdinalIgnoreCase));
        if (matching != null && matching.State != update.ChapterState.Value)
        {
            matching.State = update.ChapterState.Value;
            UpdateBookProgressFromChapters();
        }
    }

    public List<EpubItem> GetSelectedChapterItems() => Chapters.Where(c => c.IsSelected).Select(c => c.Item).ToList();

    private void OnChapterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChapterItemViewModel.IsSelected))
        {
            UpdateSelectedChaptersSummary();
        }
    }

    private bool FilterChapter(object obj)
    {
        if (obj is not ChapterItemViewModel ch) return false;

        if (SelectedChapterFilter == ChapterFilterMode.Untranslated && ch.State == ChapterTranslationState.Completed)
            return false;
        if (SelectedChapterFilter == ChapterFilterMode.Completed && ch.State != ChapterTranslationState.Completed)
            return false;
        if (SelectedChapterFilter == ChapterFilterMode.ChaptersOnly && ch.Item.Role != EpubDocumentRole.Chapter)
            return false;

        var query = ChapterSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return ch.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               ch.Href.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshChaptersFilter()
    {
        FilteredChapters.Refresh();
        FilteredChaptersCount = FilteredChapters.Cast<object>().Count();
    }

    private void ResetChapterFilters()
    {
        ChapterSearchText = string.Empty;
        SelectedChapterFilter = ChapterFilterMode.All;
    }

    private void ApplyRange()
    {
        if (Chapters.Count == 0)
        {
            _bannerService.Show("Kitap Seçilmedi", "Önce bir kitap açın.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ChapterRangeText))
        {
            _bannerService.Show("Aralık Boş", "Örnek aralık girin: 1-5, 8 veya 10-12.", InfoBarSeverity.Warning);
            return;
        }

        var selectedNumbers = ChapterRangeParser.Parse(ChapterRangeText, Chapters.Count);
        if (selectedNumbers.Count == 0)
        {
            _bannerService.Show("Geçersiz Aralık", $"1 ile {Chapters.Count} arasında geçerli bölüm numarası veya aralık yazın (ör: 1-5, 8).", InfoBarSeverity.Warning);
            return;
        }

        foreach (var ch in Chapters)
        {
            ch.IsSelected = selectedNumbers.Contains(ch.OrderNumber);
        }

        UpdateSelectedChaptersSummary();
        _bannerService.Show("Aralık Seçildi", $"{selectedNumbers.Count} bölüm seçildi.", InfoBarSeverity.Informational);
    }

    private void SelectAllVisible()
    {
        foreach (var ch in FilteredChapters.OfType<ChapterItemViewModel>())
        {
            ch.IsSelected = true;
        }
        UpdateSelectedChaptersSummary();
    }

    private void SelectUntranslated()
    {
        foreach (var ch in Chapters)
        {
            ch.IsSelected = ch.State != ChapterTranslationState.Completed;
        }
        UpdateSelectedChaptersSummary();
    }

    private void ClearSelection()
    {
        foreach (var ch in Chapters)
        {
            ch.IsSelected = false;
        }
        UpdateSelectedChaptersSummary();
    }

    private void UpdateSelectedChaptersSummary()
    {
        var selectedCount = Chapters.Count(c => c.IsSelected);
        SelectedChaptersSummary = $"Seçili: {selectedCount} / {Chapters.Count} bölüm";
    }

    private void UpdateBookProgressFromChapters()
    {
        if (Chapters.Count == 0)
        {
            BookProgressPercentage = 0;
            BookProgressText = "0 / 0 Bölüm";
            return;
        }

        int done = Chapters.Count(c => c.State == ChapterTranslationState.Completed);
        double pct = Math.Round((double)done / Chapters.Count * 100, 1);
        BookProgressPercentage = pct;
        BookProgressText = $"{done} / {Chapters.Count} Bölüm (%{pct:F1})";
    }

    private Task ResetSingleChapterCheckpointAsync(object? param)
    {
        if (param is not ChapterItemViewModel ch) return Task.CompletedTask;
        return ResetCheckpointsCoreAsync(
            [ch],
            "Bölüm Önbelleğini Sıfırla",
            $"'{ch.DisplayTitle}' bölümünün SQLite checkpoint ve kısmi/tam çıktı dosyaları (.tr/.part/.done) silinsin mi?");
    }

    private Task ResetSelectedCheckpointsAsync()
    {
        if (_activePackage == null)
        {
            _bannerService.Show("Kitap Seçilmedi", "Önce bir kitap açın.", InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        var selected = Chapters.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _bannerService.Show("Bölüm Seçilmedi", "Checkpoint'ini temizlemek için en az bir bölüm seçin.", InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        return ResetCheckpointsCoreAsync(
            selected,
            "Checkpoint Temizleme",
            $"Seçili {selected.Count} bölümün SQLite checkpoint önbelleği ve kısmi/tam çıktı dosyaları (.part / .done) silinsin mi?");
    }

    private async Task ResetCheckpointsCoreAsync(IReadOnlyList<ChapterItemViewModel> targets, string title, string prompt)
    {
        if (_activePackage == null || targets.Count == 0) return;

        if (!await _dialogService.ConfirmAsync(title, prompt, "Evet, Temizle"))
            return;

        try
        {
            BusyChanged?.Invoke(true, "Bölüm checkpoint önbellekleri temizleniyor...");
            var bookId = _activePackage.Title.Trim();

            foreach (var ch in targets)
            {
                var fullPath = Path.Combine(_activePackage.OpfDirectory, ch.Href);
                var dir = Path.GetDirectoryName(fullPath) ?? _activePackage.OpfDirectory;
                var fileName = Path.GetFileName(fullPath);
                var key = Path.GetFileNameWithoutExtension(fileName);
                var ext = string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) ? ".html" : Path.GetExtension(fileName);
                var baseOut = Path.Combine(dir, $"{key}_tr{ext}");

                await _checkpointStore.ClearCheckpointsAsync(bookId, fileName);

                foreach (var path in new[] { baseOut, baseOut + ".part", baseOut + ".done" })
                {
                    if (File.Exists(path)) File.Delete(path);
                }

                ch.State = ChapterTranslationState.Pending;
            }

            UpdateBookProgressFromChapters();
            PackageRefreshLibraryRequested?.Invoke();
            _bannerService.Show("Checkpoint Temizlendi", $"{targets.Count} bölümün önbelleği sıfırlandı.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Checkpoint Silinemedi", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private async Task PackageEpubAsync()
    {
        if (_activePackage == null)
        {
            _bannerService.Show("Kitap Seçilmedi", "Paketlemek için önce bir kitap açın.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            BusyChanged?.Invoke(true, "Çevrilmiş bölümler taranıyor...");
            var discovered = await Task.Run(() => EpubPackager.DiscoverTranslations(_activePackage));
            if (discovered.Count == 0)
            {
                _bannerService.Show("Çeviri Bulunamadı", "Henüz paketlenecek tam çevrilmiş bölüm (_tr.xhtml / _tr.html) bulunamadı.", InfoBarSeverity.Warning);
                return;
            }

            var safeTitle = string.Concat(_activePackage.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var defaultName = $"{safeTitle}_Turkce.epub";
            var savePath = _dialogService.PickEpubSaveFile(defaultName, _activePackage.OpfDirectory);
            if (string.IsNullOrWhiteSpace(savePath)) return;

            BusyChanged?.Invoke(true, $"Türkçe EPUB oluşturuluyor ({discovered.Count} çevrilmiş dosya)...");
            var output = await Task.Run(() => EpubPackager.Package(_activePackage, savePath));

            _bannerService.Show(
                "EPUB Paketlendi",
                $"{discovered.Count} çevrilmiş bölümle '{Path.GetFileName(output)}' oluşturuldu.",
                InfoBarSeverity.Success,
                actionLabel: "Klasörü Aç",
                onAction: () =>
                {
                    var dir = Path.GetDirectoryName(output);
                    if (!string.IsNullOrWhiteSpace(dir))
                        _dialogService.OpenFolderInExplorer(dir);
                });
        }
        catch (Exception ex)
        {
            _bannerService.Show("Paketleme Hatası", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            BusyChanged?.Invoke(false, "Hazır.");
        }
    }

    private void OpenOpfDirectory()
    {
        if (string.IsNullOrWhiteSpace(OpfDirectory) || !Directory.Exists(OpfDirectory))
            return;

        try
        {
            _dialogService.OpenFolderInExplorer(OpfDirectory);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Klasör Açılamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void NotifyCommandStates()
    {
        ((RelayCommand)ApplyRangeCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SelectAllVisibleCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SelectUntranslatedCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ClearSelectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ResetChapterCheckpointCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ResetSelectedCheckpointsCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)TranslateSelectedCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)PackageEpubCommand).NotifyCanExecuteChanged();
        ((RelayCommand)OpenOpfDirectoryCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ScanBookGlossaryCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)OpenEpubFileCommand).NotifyCanExecuteChanged();
    }
}
