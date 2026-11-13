using System.Windows.Input;
using Glossator.App.Services;

namespace Glossator.App.ViewModels;

/// <summary>
/// Hakkında ve Geliştirici Portfolyosu sayfasının görünüm modelidir.
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    public const string LinkedInProfileUrl = "https://www.linkedin.com/in/emre-dalbudak/";
    public const string GitHubProfileUrl = "https://github.com/chela98";
    public const string GitHubRepositoryUrl = "https://github.com/chela98/Glossator";

    private readonly IStudioDialogService _dialogService;
    private readonly BannerService _bannerService;

    public AboutViewModel(IStudioDialogService dialogService, BannerService bannerService)
    {
        _dialogService = dialogService;
        _bannerService = bannerService;

        OpenLinkedInCommand = new RelayCommand(() => OpenUrl(LinkedInProfileUrl, "LinkedIn Profili"));
        OpenGitHubCommand = new RelayCommand(() => OpenUrl(GitHubProfileUrl, "GitHub Profili"));
        OpenRepoCommand = new RelayCommand(() => OpenUrl(GitHubRepositoryUrl, "GitHub Deposu"));
        CopyUrlCommand = new RelayCommand(param =>
        {
            if (param is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    _dialogService.CopyTextToClipboard(url);
                    _bannerService.Show("Bağlantı Kopyalandı", url, Wpf.Ui.Controls.InfoBarSeverity.Success);
                }
                catch (Exception ex)
                {
                    _bannerService.Show("Panoya Kopyalanamadı", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Warning);
                }
            }
        });
    }

    public string DeveloperName => "Emre Dalbudak";
    public string DeveloperHandle => "@chela98";
    public string RoleSummary => ".NET & AI Developer";
    public string ProjectDescription =>
        "AST/DOM maskeleme, çoklu ajan orkestrasyonu ve pedagojik terim yönetimiyle teknik EPUB kitaplarını " +
        "Strict XHTML omurgasını, kod bloklarını ve alan terminolojisini koruyarak Türkçeye kazandıran " +
        "açık kaynak .NET 10 yerelleştirme motoru.";
    public string LinkedInDisplay => "linkedin.com/in/emre-dalbudak";
    public string GitHubDisplay => "github.com/chela98";
    public string RepoDisplay => "github.com/chela98/Glossator";

    public IReadOnlyList<string> ArchitectureBadges { get; } =
    [
        ".NET 10 · C# 14",
        "Microsoft Agent Framework 1.24",
        "Microsoft.Extensions.AI 10.10",
        "WPF-UI Fluent (Koyu / Aydınlık + Mica)",
        "AngleSharp AST & Strict XHTML",
        "SQLite WAL + BoundedChannel",
        "Multi-Provider Discovery & Reasoning",
        "OpenTelemetry + .NET Aspire",
        "xUnit Test Paketi & GitHub CI"
    ];

    public string PipelineTitle => "4-Aşamalı Çeviri ve Doğrulama Hattı";

    public IReadOnlyList<PipelineStageItem> PipelineStages { get; } =
    [
        new(
            "1",
            "AST Maskeleme ve Kesitleme (Glossator.Core)",
            "HtmlSegmenter ve DomTreeWalkerMasker ile <pre>, kod, SVG ve matematik düğümleri ⟦PH_xxxxxx⟧ yer tutucularına kilitlenir; HtmlSectionGrouper metni token bütçeli (1200–2800) mantıksal kesitlere ayırır."),
        new(
            "2",
            "Çoklu Ajan Orkestrasyonu (StyleAnalyst → Translator ⇄ Reviewer)",
            "Microsoft Agent Framework iş akışı KV-cache dostu üslup kılavuzu çıkarır; Translator kesitleri çevirir, Reviewer ise C# ön filtresi, hedefli JSON Patch, tek seferlik JSON Self-Repair ve C# Gatekeeper ile doğruluk sağlar."),
        new(
            "3",
            "Pedagojik Sözlük ve Mikro Kurtarma",
            "TermGlossingPostProcessor ve TermUsageTracker (AlwaysEnglish, AlwaysTurkish, ProgressiveGloss: 1. geçişte hibrit, 2+ geçişte yalın Türkçe) uygulanır; eksik bloklarda DirectTranslatorFallback ve MicroRecovery devreye girer."),
        new(
            "4",
            "Checkpoint İzolasyonu ve Strict EPUB Paketleme",
            "CheckpointStore (BoundedChannel + SQLite WAL) ilerlemeyi güvenle yazar; eksik bloklu bölümler .part olarak izole edilir, tamamlanan bölümler (.done) StrictXhtmlMarkupFormatter ile atomik *_TR.epub paketine dönüştürülür.")
    ];

    public ICommand OpenLinkedInCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand CopyUrlCommand { get; }

    private void OpenUrl(string url, string description)
    {
        try
        {
            _dialogService.OpenUrlInBrowser(url);
        }
        catch (Exception ex)
        {
            _bannerService.Show("Bağlantı Açılamadı", $"{description} açılamadı: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    public sealed record PipelineStageItem(string StepNumber, string Title, string Description);
}
