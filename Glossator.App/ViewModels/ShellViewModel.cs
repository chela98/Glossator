using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Glossator.AI.Diagnostics;
using Glossator.App.Engine;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.Core.Data;
using Glossator.Core.Parsing;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

/// <summary>
/// Ana kabuk penceresi, navigasyon, alt çıktı çekmecesi, sürükle-bırak ve çoklu ajan çeviri oturumunu yönetir.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly StudioLocalizationEngine _engine;
    private readonly UserSettingsService _settingsService;
    private readonly CheckpointStore _checkpointStore;
    private readonly StudioTelemetryMonitor _telemetryMonitor;
    private readonly IStudioDialogService _dialogService;
    private readonly StudioThemeService _themeService;

    private AppPage _currentPage = AppPage.Library;
    private bool _isBusy;
    private bool _isTranslating;
    private bool _isStopping;
    private bool _isOutputDrawerExpanded;

    private string _liveOperationText = "Hazır.";
    private string _overallPercentageText = "%0.0";
    private double _overallPercentage;

    private CancellationTokenSource? _translationCts;
    private Task? _activeTranslationTask;

    public ShellViewModel(
        StudioLocalizationEngine engine,
        UserSettingsService settingsService,
        CheckpointStore checkpointStore,
        StudioTelemetryMonitor telemetryMonitor,
        IStudioDialogService dialogService,
        StudioThemeService themeService,
        BannerService bannerService,
        LibraryViewModel library,
        BookWorkspaceViewModel bookWorkspace,
        GlossaryViewModel glossary,
        SettingsViewModel settings,
        TelemetryViewModel telemetry,
        AboutViewModel about)
    {
        _engine = engine;
        _settingsService = settingsService;
        _checkpointStore = checkpointStore;
        _telemetryMonitor = telemetryMonitor;
        _dialogService = dialogService;
        _themeService = themeService;

        Banner = bannerService;
        Library = library;
        BookWorkspace = bookWorkspace;
        Glossary = glossary;
        Settings = settings;
        Telemetry = telemetry;
        About = about;

        NavigateCommand = new RelayCommand(param =>
        {
            if (param is AppPage page)
            {
                CurrentPage = page;
            }
            else if (param is string str && Enum.TryParse<AppPage>(str, ignoreCase: true, out var parsed))
            {
                CurrentPage = parsed;
            }
        });

        ToggleOutputDrawerCommand = new RelayCommand(() => IsOutputDrawerExpanded = !IsOutputDrawerExpanded);
        ClearLogsCommand = new RelayCommand(() =>
        {
            LogLines.Clear();
            OnPropertyChanged(nameof(HasLogs));
        });
        CopyAllLogsCommand = new RelayCommand(CopyAllLogs, () => LogLines.Count > 0);
        CopyLogLineCommand = new RelayCommand(param =>
        {
            if (param is string line && !string.IsNullOrWhiteSpace(line))
            {
                try { _dialogService.CopyTextToClipboard(line); } catch { }
            }
        });
        StopTranslationCommand = new RelayCommand(StopTranslation, () => IsTranslating && !_isStopping);
        ToggleThemeCommand = new AsyncRelayCommand(ToggleThemeAsync);

        // Çocuk ViewModel geri çağrımlarını bağla
        Library.OpenBookRequested = async path => await OpenBookAsync(path, navigateToWorkspace: true);
        Library.BusyChanged = SetBusyState;

        BookWorkspace.PickBookRequested = () =>
        {
            var picked = _dialogService.PickEpubFile();
            return string.IsNullOrWhiteSpace(picked) ? Task.CompletedTask : OpenBookAsync(picked, navigateToWorkspace: true);
        };
        BookWorkspace.StartTranslationRequested = StartTranslationAsync;
        BookWorkspace.ScanBookGlossaryRequested = async () =>
        {
            CurrentPage = AppPage.Glossary;
            await Glossary.ImportBookTermsAsync();
        };
        BookWorkspace.BusyChanged = SetBusyState;
        BookWorkspace.PackageRefreshLibraryRequested = () => _ = RefreshLibrarySilentlyAsync();

        Glossary.ActiveBookProvider = () => (BookWorkspace.ActivePackage, BookWorkspace.ActiveBookPath);
        Glossary.BusyChanged = SetBusyState;

        _engine.ProgressChanged += OnEngineProgressChanged;
        _engine.LogMessageReceived += OnEngineLogMessageReceived;
    }

    public BannerService Banner { get; }
    public LibraryViewModel Library { get; }
    public BookWorkspaceViewModel BookWorkspace { get; }
    public GlossaryViewModel Glossary { get; }
    public SettingsViewModel Settings { get; }
    public TelemetryViewModel Telemetry { get; }
    public AboutViewModel About { get; }

    public ObservableCollection<string> LogLines { get; } = [];
    public bool HasLogs => LogLines.Count > 0;

    public AppPage CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                PropagateLockState();
        }
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        private set
        {
            if (SetProperty(ref _isTranslating, value))
            {
                BookWorkspace.IsTranslating = value;
                PropagateLockState();
                OnPropertyChanged(nameof(ShowProgressBar));
                ((RelayCommand)StopTranslationCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsOutputDrawerExpanded
    {
        get => _isOutputDrawerExpanded;
        set => SetProperty(ref _isOutputDrawerExpanded, value);
    }

    public string LiveOperationText { get => _liveOperationText; private set => SetProperty(ref _liveOperationText, value); }
    public string OverallPercentageText { get => _overallPercentageText; private set => SetProperty(ref _overallPercentageText, value); }

    public double OverallPercentage
    {
        get => _overallPercentage;
        private set
        {
            if (SetProperty(ref _overallPercentage, value))
                OnPropertyChanged(nameof(ShowProgressBar));
        }
    }

    public bool ShowProgressBar => IsTranslating || OverallPercentage > 0;

    public ICommand NavigateCommand { get; }
    public ICommand ToggleOutputDrawerCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand CopyAllLogsCommand { get; }
    public ICommand CopyLogLineCommand { get; }
    public ICommand StopTranslationCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    public bool IsDarkTheme => _themeService.IsDarkTheme;
    public SymbolRegular ThemeToggleIcon => IsDarkTheme ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;
    public string ThemeToggleLabel => IsDarkTheme ? "Aydınlık" : "Koyu";
    public string ThemeToggleToolTip => IsDarkTheme ? "Aydınlık temaya geç" : "Koyu temaya geç";

    public async Task ToggleThemeAsync()
    {
        var nextIsDark = !_themeService.IsDarkTheme;
        _themeService.ApplyTheme(nextIsDark);
        NotifyThemePropertiesChanged();
        await _settingsService.SaveThemePreferenceAsync(nextIsDark);
    }

    private void NotifyThemePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeToggleIcon));
        OnPropertyChanged(nameof(ThemeToggleLabel));
        OnPropertyChanged(nameof(ThemeToggleToolTip));
    }

    public async Task InitializeAsync()
    {
        NotifyThemePropertiesChanged();
        SetBusyState(true, "Stüdyo hazırlanıyor...");
        try
        {
            await Library.RefreshAsync();
            await Settings.InitializeAsync();
            await Glossary.LoadAsync();
            await Telemetry.InitializeAsync();

            var latestValid = Library.RecentBooks.FirstOrDefault(b => b.ExistsOnDisk);
            if (latestValid != null)
            {
                await OpenBookAsync(latestValid.Path, navigateToWorkspace: true);
            }
            else
            {
                CurrentPage = AppPage.Library;
                AddLog("Stüdyo hazır. Kütüphaneden bir kitap seçin veya .epub dosyasını pencereye bırakın.");
            }
        }
        finally
        {
            SetBusyState(false, "Hazır.");
        }
    }

    public async Task OpenBookAsync(string inputPath, bool navigateToWorkspace)
    {
        if (IsTranslating)
        {
            Banner.Show("Çeviri Devam Ediyor", "Aktif çeviri tamamlanmadan veya durdurulmadan başka bir kitap açılamaz.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            SetBusyState(true, "EPUB paketi okunuyor...");
            var package = await Task.Run(() => EpubManifestReader.Read(inputPath));

            BookWorkspace.LoadPackage(package, inputPath);
            await Library.RecordOpenedAsync(package, inputPath);

            if (navigateToWorkspace)
            {
                CurrentPage = AppPage.Workspace;
            }

            AddLog($"Kitap yüklendi: '{package.Title}' ({BookWorkspace.Chapters.Count} belge).");
            Banner.Dismiss();
        }
        catch (Exception ex)
        {
            Banner.Show("Kitap Açılamadı", ex.Message, InfoBarSeverity.Error);
            AddLog($"[Hata] Kitap açılamadı ({inputPath}): {ex.Message}");
        }
        finally
        {
            SetBusyState(false, "Hazır.");
        }
    }

    public async Task<bool> HandleClosingAsync()
    {
        if (!IsTranslating)
            return true;

        var confirm = await _dialogService.ConfirmAsync(
            "Çeviri Devam Ediyor",
            "Aktif bir çeviri işlemi çalışıyor. Güvenli şekilde durdurup tamamlanmış tüm cümle checkpoint'lerini kaydederek çıkmak istiyor musunuz?",
            "Durdur ve Çık",
            "Vazgeç");

        if (!confirm)
            return false;

        _translationCts?.Cancel();
        if (_activeTranslationTask != null)
        {
            try
            {
                await Task.WhenAny(_activeTranslationTask, Task.Delay(TimeSpan.FromSeconds(8)));
            }
            catch { }
        }

        try
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _checkpointStore.FlushAsync(flushCts.Token);
        }
        catch { }

        return true;
    }

    private async Task StartTranslationAsync()
    {
        if (BookWorkspace.ActivePackage == null)
        {
            Banner.Show("Kitap Seçilmedi", "Çeviri başlatmak için önce bir EPUB açın.", InfoBarSeverity.Warning);
            return;
        }

        var selected = BookWorkspace.GetSelectedChapterItems();
        if (selected.Count == 0)
        {
            Banner.Show("Bölüm Seçilmedi", "Lütfen listeden çevrilecek en az bir bölüm işaretleyin.", InfoBarSeverity.Warning);
            return;
        }

        if (!await Settings.EnsureSettingsSavedBeforeRunAsync())
            return;

        if (!Settings.ValidateActiveProviderKeys(out var keyErr))
        {
            Banner.Show(
                "API Anahtarı Eksik",
                $"{keyErr}\nLütfen Ayarlar sekmesinden sağlayıcı API anahtarını girin.",
                InfoBarSeverity.Error,
                actionLabel: "Ayarlar'a Git",
                onAction: () =>
                {
                    CurrentPage = AppPage.Settings;
                    Settings.SelectedSubTab = SettingsSubTab.ProvidersAndAgents;
                });
            return;
        }

        int? requestedSections = null;
        if (!string.IsNullOrWhiteSpace(BookWorkspace.SectionLimitText))
        {
            if (int.TryParse(BookWorkspace.SectionLimitText.Trim(), out var parsed) && parsed > 0)
            {
                requestedSections = parsed;
            }
            else
            {
                Banner.Show("Geçersiz Kesit Limiti", "Kesit limiti pozitif bir tam sayı olmalı veya boş bırakılmalıdır.", InfoBarSeverity.Warning);
                return;
            }
        }

        var concurrency = BookWorkspace.WorkersCount;
        var isFresh = BookWorkspace.IsFreshMode;
        var isTest = BookWorkspace.IsTestMode;

        _isStopping = false;
        _translationCts?.Dispose();
        _translationCts = new CancellationTokenSource();

        GlossatorDiagnostics.ResetSessionStats();
        _telemetryMonitor.ResetSessionCounters();

        OverallPercentage = 0;
        OverallPercentageText = "%0.0";
        LiveOperationText = "Çeviri motoru hazırlanıyor...";
        IsOutputDrawerExpanded = true;
        IsTranslating = true;

        var startTime = DateTimeOffset.UtcNow;
        AddLog($"=== YENİ ÇEVİRİ OTURUMU BAŞLADI ({selected.Count} bölüm, Worker: {concurrency}, Test: {isTest}, Fresh: {isFresh}) ===");

        try
        {
            var runTask = _engine.RunAsync(
                BookWorkspace.ActivePackage,
                selected,
                concurrency,
                isFresh,
                requestedSections,
                isTest,
                _translationCts.Token);
            _activeTranslationTask = runTask;

            var summary = await runTask;

            // Doğrudan başarılı tamamlanma sonucunu alalım
            var fullBreakdown = FormatSessionSummary(summary, out var shortBanner);
            foreach (var line in fullBreakdown.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                AddLog(line);
            }

            if (_translationCts.IsCancellationRequested)
            {
                LiveOperationText = "Durduruldu.";
                Banner.Show("Çeviri Durduruldu", "Tamamlanmış cümleler SQLite checkpoint önbelleğine kaydedildi.\n" + shortBanner, InfoBarSeverity.Warning);
            }
            else if (summary.FailedChapters.Count == 0)
            {
                OverallPercentage = 100;
                OverallPercentageText = "%100.0";
                LiveOperationText = "Tamamlandı.";
                Banner.Show("Çeviri Tamamlandı", shortBanner, InfoBarSeverity.Success);
            }
            else
            {
                var succeededChapters = Math.Max(0, selected.Count - summary.FailedChapters.Count);
                var pct = selected.Count > 0 ? Math.Clamp(Math.Round((double)succeededChapters / selected.Count * 100.0, 1), 0, 100) : 0;
                OverallPercentage = pct;
                OverallPercentageText = $"%{pct:F1}";

                var failedPreview = string.Join(", ", summary.FailedChapters.Take(3).Select(f => f.ChapterFileName));
                if (summary.FailedChapters.Count > 3)
                {
                    failedPreview += $" (+{summary.FailedChapters.Count - 3})";
                }

                if (succeededChapters > 0)
                {
                    LiveOperationText = $"Kısmi tamamlandı ({succeededChapters}/{selected.Count} bölüm tamamlandı, {summary.FailedChapters.Count} bölüm hatalı).";
                    Banner.Show(
                        $"Kısmi Tamamlanma ({summary.FailedChapters.Count} Bölüm Hatalı)",
                        $"{shortBanner}\nHatalı bölümler: {failedPreview}",
                        InfoBarSeverity.Warning);
                }
                else
                {
                    LiveOperationText = "Hata nedeniyle tamamlanamadı.";
                    var firstFailure = summary.FailedChapters[0];
                    Banner.Show(
                        "Çeviri Başarısız",
                        $"{firstFailure.ChapterFileName}: {firstFailure.Message}\n{shortBanner}",
                        InfoBarSeverity.Error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            LiveOperationText = "Kullanıcı tarafından durduruldu.";
            var partialSummary = StudioLocalizationEngine.BuildCurrentSessionSummary(DateTimeOffset.UtcNow - startTime);
            var fullBreakdown = FormatSessionSummary(partialSummary, out var shortBanner);
            foreach (var line in fullBreakdown.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                AddLog(line);
            }
            Banner.Show("Çeviri Durduruldu", "Kısmi çeviriler checkpoint veritabanına kaydedildi.\n" + shortBanner, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            LiveOperationText = "Hata nedeniyle durdu.";
            AddLog($"[KRİTİK HATA] {ex.Message}");

            if (HasUnauthorizedError(ex))
            {
                Banner.Show(
                    "Yetkilendirme Hatası (401)",
                    "Sağlayıcı API anahtarı geçersiz veya süresi dolmuş. Ayarlar sekmesinden güncelleyin.",
                    InfoBarSeverity.Error,
                    actionLabel: "Ayarlar'a Git",
                    onAction: () =>
                    {
                        CurrentPage = AppPage.Settings;
                        Settings.SelectedSubTab = SettingsSubTab.ProvidersAndAgents;
                    });
            }
            else
            {
                Banner.Show("Çeviri Hatası", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _activeTranslationTask = null;
            _translationCts?.Dispose();
            _translationCts = null;
            _isStopping = false;

            IsTranslating = false;

            if (BookWorkspace.ActivePackage != null && !string.IsNullOrWhiteSpace(BookWorkspace.ActiveBookPath))
            {
                BookWorkspace.RefreshChapterStatesFromDisk();
                await Library.RecordOpenedAsync(BookWorkspace.ActivePackage, BookWorkspace.ActiveBookPath);
            }
        }
    }

    private void StopTranslation()
    {
        if (_translationCts != null && !_translationCts.IsCancellationRequested)
        {
            _isStopping = true;
            _translationCts.Cancel();
            ((RelayCommand)StopTranslationCommand).NotifyCanExecuteChanged();
            LiveOperationText = "Durduruluyor...";
            AddLog("[Motor] Durdurma sinyali gönderildi...");
        }
    }

    private void OnEngineProgressChanged(StudioProgressUpdate update)
    {
        DispatchToUi(() =>
        {
            LiveOperationText = $"[{update.ChapterFileName}] {update.Stage}: {update.Message}";
            OverallPercentageText = $"%{update.OverallPercentage:F1}";
            OverallPercentage = update.OverallPercentage;
            BookWorkspace.ApplyLiveProgress(update);
        });
    }

    private void OnEngineLogMessageReceived(string message)
    {
        DispatchToUi(() => AddLog(message));
    }

    private void AddLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogLines.Insert(0, line);
        if (LogLines.Count > 300)
        {
            LogLines.RemoveAt(LogLines.Count - 1);
        }
        OnPropertyChanged(nameof(HasLogs));
        ((RelayCommand)CopyAllLogsCommand).NotifyCanExecuteChanged();
    }

    private void CopyAllLogs()
    {
        if (LogLines.Count == 0) return;
        try
        {
            var text = string.Join(Environment.NewLine, LogLines);
            _dialogService.CopyTextToClipboard(text);
            Banner.Show("Günlük Kopyalandı", $"{LogLines.Count} günlük satırı panoya kopyalandı.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Banner.Show("Panoya Kopyalanamadı", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private void SetBusyState(bool busy, string? statusMessage)
    {
        IsBusy = busy;
        if (!string.IsNullOrWhiteSpace(statusMessage) && !IsTranslating)
        {
            LiveOperationText = statusMessage;
        }
    }

    private void PropagateLockState()
    {
        var locked = IsTranslating || IsBusy;
        Library.IsLocked = locked;
        BookWorkspace.IsLocked = locked;
        Glossary.IsLocked = locked;
        Settings.IsLocked = locked;
        Telemetry.IsLocked = locked;
    }

    private async Task RefreshLibrarySilentlyAsync()
    {
        if (BookWorkspace.ActivePackage != null && !string.IsNullOrWhiteSpace(BookWorkspace.ActiveBookPath))
        {
            await Library.RecordOpenedAsync(BookWorkspace.ActivePackage, BookWorkspace.ActiveBookPath);
        }
    }

    private string FormatSessionSummary(StudioSessionSummary summary, out string shortBanner)
    {
        var opts = _settingsService.CurrentOptions;
        var usdToTry = _settingsService.UsdToTryRate;
        var rateSourceLabel = _settingsService.LastExchangeRateQuote?.Source ?? "Kayıtlı/Yedek";
        double totalUsd = 0;
        long totalIn = 0;
        long totalCached = 0;
        long totalVisibleOut = 0;
        long totalBilledOut = 0;
        long totalReason = 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Süre: {summary.Elapsed:hh\\:mm\\:ss} | Bölüm: {summary.ChaptersCompleted} | Kesit: {summary.SectionsCompleted} | Patch: {summary.PatchesApplied} | Red: {summary.Rejections}");

        foreach (var (stage, stats) in summary.StageStats)
        {
            var (provName, modelName, inRate, outRate, isFree) = opts.ResolveStagePricing(stage);
            var inTok = Interlocked.Read(ref stats.InputTokens);
            var cachedTok = Interlocked.Read(ref stats.CachedInputTokens);
            var visOut = Interlocked.Read(ref stats.VisibleOutputTokens);
            var billedOut = Interlocked.Read(ref stats.BilledOutputTokens);
            var reasoning = stats.ReasoningTokens;
            var costUsd = isFree ? 0.0 : (inTok / 1_000_000.0 * inRate) + (billedOut / 1_000_000.0 * outRate);

            totalUsd += costUsd;
            totalIn += inTok;
            totalCached += cachedTok;
            totalVisibleOut += visOut;
            totalBilledOut += billedOut;
            totalReason += reasoning;

            var cacheLabel = cachedTok > 0 && inTok > 0
                ? $" (Cache Hit: {cachedTok:N0} / %{(double)cachedTok * 100.0 / inTok:F1})"
                : cachedTok > 0
                    ? $" (Cache Hit: {cachedTok:N0})"
                    : string.Empty;
            var priceLabel = isFree ? "Ücretsiz" : $"${costUsd:F4} ({costUsd * usdToTry:F2} TL)";
            sb.AppendLine($"  {stage} [{provName}: {modelName}]: {stats.Calls} çağrı | In: {inTok:N0}{cacheLabel} | Out: {visOut:N0} (Billed: {billedOut:N0}, Reason: {reasoning:N0}) | {priceLabel}");
        }

        var totalTry = totalUsd * usdToTry;
        var totalCacheLabel = totalCached > 0 && totalIn > 0
            ? $" (Cache: {totalCached:N0} / %{(double)totalCached * 100.0 / totalIn:F1})"
            : totalCached > 0
                ? $" (Cache: {totalCached:N0})"
                : string.Empty;
        sb.AppendLine($"TOPLAM: In {totalIn:N0}{totalCacheLabel} | Visible {totalVisibleOut:N0} | Billed {totalBilledOut:N0} | Reason {totalReason:N0} | Maliyet: ${totalUsd:F4} ({totalTry:F2} TL, Kur: {usdToTry:F2} [{rateSourceLabel}])");

        if (summary.FailedChapters.Count > 0)
        {
            sb.AppendLine($"BAŞARISIZ BÖLÜMLER ({summary.FailedChapters.Count}):");
            foreach (var failure in summary.FailedChapters)
            {
                sb.AppendLine($"  - {failure.ChapterFileName}: {failure.Message}");
            }
        }

        shortBanner = $"Süre: {summary.Elapsed:hh\\:mm\\:ss} | Bölüm: {summary.ChaptersCompleted} | Kesit: {summary.SectionsCompleted} | Maliyet: ${totalUsd:F4} (~{totalTry:F2} TL)";
        return sb.ToString();
    }

    private static bool HasUnauthorizedError(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            return agg.Flatten().InnerExceptions.Any(HasUnauthorizedError);
        }

        Exception? cur = ex;
        while (cur != null)
        {
            if (cur is System.ClientModel.ClientResultException cre && cre.Status == 401)
                return true;

            if (cur.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                cur.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
                return true;

            cur = cur.InnerException;
        }
        return false;
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(action);
            return;
        }
        action();
    }

    public void Dispose()
    {
        _engine.ProgressChanged -= OnEngineProgressChanged;
        _engine.LogMessageReceived -= OnEngineLogMessageReceived;
        _translationCts?.Dispose();
    }
}
