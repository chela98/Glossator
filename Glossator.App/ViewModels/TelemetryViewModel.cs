using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Glossator.App.Infrastructure;
using Glossator.App.Services;
using Glossator.Core.Data;
using Glossator.Core.Services;
using Wpf.Ui.Controls;

namespace Glossator.App.ViewModels;

/// <summary>
/// OpenTelemetry metrikleri, Aspire bağlantı kontrolü ve canlı USD/TL kur tercihlerini yönetir.
/// </summary>
public sealed class TelemetryViewModel : ObservableObject, IDisposable
{
    private readonly UserSettingsService _settingsService;
    private readonly StudioTelemetryMonitor _telemetryMonitor;
    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;

    private string _activeWorkersText = "0";
    private string _sectionsTranslatedText = "0";
    private string _chaptersTranslatedText = "0";
    private string _speedAndTtftText = "- tok/s | - ms";
    private string _qualityText = "0 / 0 / 0";
    private string _streamedTokensText = "0";
    private string _liveTelemetryStripText = "Telemetri: Aktif İşçi: 0  |  Kesit: 0  |  Bölüm: 0  |  Hız: - tok/s  |  Akış: 0 token";

    private string _aspireEndpointSummary = string.Empty;
    private string _aspireStatusText = " ● DENETLENİYOR...";
    private bool _isAspireConnected;

    private string _otelEndpoint = string.Empty;
    private string _otelServiceName = string.Empty;
    private string _usdToTryRateText = "42.50";
    private string _exchangeRateStatusText = string.Empty;

    private bool _isCheckingAspire;
    private bool _isRefreshingRate;
    private bool _isLocked;

    public TelemetryViewModel(
        UserSettingsService settingsService,
        StudioTelemetryMonitor telemetryMonitor,
        IStudioDialogService dialogService,
        BannerService bannerService)
    {
        _settingsService = settingsService;
        _telemetryMonitor = telemetryMonitor;
        _dialogService = dialogService;
        _bannerService = bannerService;

        CheckAspireCommand = new AsyncRelayCommand(CheckAspireWithBannerAsync, () => !IsCheckingAspire);
        OpenAspireDashboardCommand = new RelayCommand(OpenAspireDashboard);
        OpenGlossatorFolderCommand = new RelayCommand(OpenGlossatorFolder);
        RefreshExchangeRateCommand = new AsyncRelayCommand(() => RefreshExchangeRateAsync(forceRefresh: true, showBannerOnResult: true), () => !IsRefreshingRate);
        SaveTelemetrySettingsCommand = new AsyncRelayCommand(SaveTelemetrySettingsAsync, () => !IsLocked);

        _telemetryMonitor.SnapshotChanged += OnSnapshotChanged;
    }

    public string ActiveWorkersText { get => _activeWorkersText; private set => SetProperty(ref _activeWorkersText, value); }
    public string SectionsTranslatedText { get => _sectionsTranslatedText; private set => SetProperty(ref _sectionsTranslatedText, value); }
    public string ChaptersTranslatedText { get => _chaptersTranslatedText; private set => SetProperty(ref _chaptersTranslatedText, value); }
    public string SpeedAndTtftText { get => _speedAndTtftText; private set => SetProperty(ref _speedAndTtftText, value); }
    public string QualityText { get => _qualityText; private set => SetProperty(ref _qualityText, value); }
    public string StreamedTokensText { get => _streamedTokensText; private set => SetProperty(ref _streamedTokensText, value); }
    public string LiveTelemetryStripText { get => _liveTelemetryStripText; private set => SetProperty(ref _liveTelemetryStripText, value); }

    public string AspireEndpointSummary { get => _aspireEndpointSummary; private set => SetProperty(ref _aspireEndpointSummary, value); }
    public string AspireStatusText { get => _aspireStatusText; private set => SetProperty(ref _aspireStatusText, value); }
    public bool IsAspireConnected { get => _isAspireConnected; private set => SetProperty(ref _isAspireConnected, value); }

    public string OtelEndpoint { get => _otelEndpoint; set => SetProperty(ref _otelEndpoint, value); }
    public string OtelServiceName { get => _otelServiceName; set => SetProperty(ref _otelServiceName, value); }
    public string UsdToTryRateText { get => _usdToTryRateText; set => SetProperty(ref _usdToTryRateText, value); }
    public string ExchangeRateStatusText { get => _exchangeRateStatusText; private set => SetProperty(ref _exchangeRateStatusText, value); }

    public bool IsCheckingAspire
    {
        get => _isCheckingAspire;
        private set
        {
            if (SetProperty(ref _isCheckingAspire, value))
                ((AsyncRelayCommand)CheckAspireCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsRefreshingRate
    {
        get => _isRefreshingRate;
        private set
        {
            if (SetProperty(ref _isRefreshingRate, value))
                ((AsyncRelayCommand)RefreshExchangeRateCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (SetProperty(ref _isLocked, value))
                ((AsyncRelayCommand)SaveTelemetrySettingsCommand).NotifyCanExecuteChanged();
        }
    }

    public ICommand CheckAspireCommand { get; }
    public ICommand OpenAspireDashboardCommand { get; }
    public ICommand OpenGlossatorFolderCommand { get; }
    public ICommand RefreshExchangeRateCommand { get; }
    public ICommand SaveTelemetrySettingsCommand { get; }

    public async Task InitializeAsync()
    {
        OtelEndpoint = _settingsService.OtelEndpoint;
        OtelServiceName = _settingsService.OtelServiceName;
        ApplyExchangeRateQuote(
            _settingsService.LastExchangeRateQuote ??
            new ExchangeRateQuote(_settingsService.UsdToTryRate, "Kayıtlı/Yedek", IsLive: false, DateTimeOffset.UtcNow));
        ApplySnapshot(_telemetryMonitor.GetSnapshot());

        await Task.WhenAll(
            CheckAspireConnectivityAsync(),
            RefreshExchangeRateAsync(forceRefresh: false, showBannerOnResult: false));
    }

    public async Task CheckAspireConnectivityAsync()
    {
        var endpoint = _settingsService.OtelEndpoint;
        var serviceName = _settingsService.OtelServiceName;
        var reachable = await StudioTelemetryMonitor.CheckOtlpReachableAsync(endpoint);

        AspireEndpointSummary = $"Endpoint: {endpoint} (gRPC) | Servis: {serviceName}";
        IsAspireConnected = reachable;
        AspireStatusText = reachable
            ? " ● ASPIRE OTLP BAĞLI"
            : " ● ASPIRE ÇEVRİMDIŞI (Trace'ler bekletilmeden atlanır)";
    }

    private async Task CheckAspireWithBannerAsync()
    {
        IsCheckingAspire = true;
        try
        {
            await CheckAspireConnectivityAsync();
            _bannerService.Show(
                IsAspireConnected ? "Aspire Bağlantısı Aktif" : "Aspire Ulaşılamıyor",
                IsAspireConnected
                    ? $"OpenTelemetry kolektörü ({_settingsService.OtelEndpoint}) yanıt veriyor."
                    : $"{_settingsService.OtelEndpoint} adresinde dinleyen bir Aspire konteyneri bulunamadı. Çeviriyi etkilemez.",
                IsAspireConnected ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            IsCheckingAspire = false;
        }
    }

    private async Task RefreshExchangeRateAsync(bool forceRefresh, bool showBannerOnResult)
    {
        IsRefreshingRate = true;
        try
        {
            var quote = await _settingsService.RefreshUsdToTryRateAsync(forceRefresh);
            ApplyExchangeRateQuote(quote);

            if (showBannerOnResult)
            {
                if (quote.IsLive)
                {
                    _bannerService.Show(
                        "Canlı Döviz Kuru Güncellendi",
                        $"1 USD = {quote.Rate:F4} ₺ ({quote.Source}) alındı ve yedek olarak kaydedildi.",
                        InfoBarSeverity.Success);
                }
                else
                {
                    _bannerService.Show(
                        "Canlı Kur Alınamadı",
                        $"Harici kur API'lerine erişilemedi; mevcut yedek kur ({quote.Rate:F2} ₺) kullanılıyor.",
                        InfoBarSeverity.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            if (showBannerOnResult)
            {
                _bannerService.Show("Kur Güncelleme Hatası", ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            IsRefreshingRate = false;
        }
    }

    private async Task SaveTelemetrySettingsAsync()
    {
        var endpoint = OtelEndpoint.Trim();
        var serviceName = OtelServiceName.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _bannerService.Show("Geçersiz OTLP Endpoint", "Geçerli bir http:// veya https:// adresi girin (ör: http://localhost:4317).", InfoBarSeverity.Warning);
            return;
        }

        if (!double.TryParse(UsdToTryRateText.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
        {
            _bannerService.Show("Geçersiz Kur Değeri", "Pozitif bir USD/TL kuru girin (ör: 42.50).", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await _settingsService.SaveStudioPreferencesAsync(endpoint, serviceName, rate);
            ExchangeRateStatusText = $"Manuel kur ayarlandı: 1 USD = {rate:F2} ₺ (Oturum için geçerli; 'Canlı Kur' ile API'den güncellenebilir).";
            await CheckAspireConnectivityAsync();
            _bannerService.Show(
                "Sistem Ayarları Kaydedildi",
                $"USD/TL kuru ({rate:F2}) hemen uygulanır. OTLP Endpoint/ServiceName yeniden başlatmada geçerli olur.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Kayıt Hatası", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ApplyExchangeRateQuote(ExchangeRateQuote quote)
    {
        UsdToTryRateText = quote.Rate.ToString("F2", CultureInfo.InvariantCulture);
        ExchangeRateStatusText = quote.IsLive
            ? $"Canlı kur aktif: 1 USD = {quote.Rate:F4} ₺ (Kaynak: {quote.Source}, {quote.FetchedAtUtc.ToLocalTime():HH:mm})"
            : $"Çevrimdışı / yedek kur aktif: 1 USD = {quote.Rate:F2} ₺ ({quote.Source})";
    }

    private void OnSnapshotChanged(TelemetrySnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
            return;
        }
        ApplySnapshot(snapshot);
    }

    public void ApplySnapshot(TelemetrySnapshot s)
    {
        var speedStr = s.LastTps > 0 ? $"{s.LastTps:F1} tok/s" : "- tok/s";
        var ttftStr = s.LastTtftMs > 0 ? $"{s.LastTtftMs:F0} ms" : "- ms";

        ActiveWorkersText = s.ActiveWorkers.ToString();
        SectionsTranslatedText = s.SectionsTranslated.ToString("N0");
        ChaptersTranslatedText = s.ChaptersTranslated.ToString("N0");
        SpeedAndTtftText = $"{speedStr} | {ttftStr}";
        QualityText = $"{s.PatchesApplied} / {s.Rejections} / {s.RecoveryAttempts}";
        StreamedTokensText = s.StreamedTokens.ToString("N0");
        LiveTelemetryStripText =
            $"Telemetri: Aktif İşçi: {s.ActiveWorkers}  |  Kesit: {s.SectionsTranslated}  |  Bölüm: {s.ChaptersTranslated}  |  " +
            $"Hız: {speedStr}  |  TTFT: {ttftStr}  |  Yama/Red: {s.PatchesApplied}/{s.Rejections}  |  Akış: {s.StreamedTokens:N0} token";
    }

    private void OpenAspireDashboard()
    {
        try
        {
            _dialogService.OpenUrlInBrowser("http://localhost:18888");
            _bannerService.Show("Aspire Açılıyor", "Varsayılan tarayıcınızda http://localhost:18888 adresi açılıyor.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Tarayıcı Açılamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenGlossatorFolder()
    {
        try
        {
            var dir = GlossatorPaths.GetGlossatorHomeDirectory();
            _dialogService.OpenFolderInExplorer(dir);
            _bannerService.Show("Klasör Açıldı", dir, InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Klasör Açılamadı", ex.Message, InfoBarSeverity.Error);
        }
    }

    public void Dispose()
    {
        _telemetryMonitor.SnapshotChanged -= OnSnapshotChanged;
    }
}
