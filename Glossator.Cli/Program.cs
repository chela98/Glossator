using System.Runtime.InteropServices;
using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Glossator.Cli.Commands.Glossary;
using Glossator.Cli.Infrastructure;
using Glossator.Cli.Orchestration;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Glossator.Core.Serialization;
using Glossator.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Spectre.Console;
using Spectre.Console.Cli;

// UTF-8 Konsol Desteği (Windows conhost / Terminal karakter desteği)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// 0. Eğer 'glossary' alt komutu çağrıldıysa Spectre.Console.Cli motorunu çalıştır
if (args.Length > 0 && args[0].Equals("glossary", StringComparison.OrdinalIgnoreCase))
{
    var glossaryServices = new ServiceCollection();
    var defaultDbPath = GlossatorPaths.GetMasterDatabasePath();
    var defaultConnFactory = new SqliteConnectionFactory(defaultDbPath);
    glossaryServices.AddSingleton<ISqliteConnectionFactory>(defaultConnFactory);
    glossaryServices.AddSingleton(new SqliteGlossaryRepository(defaultConnFactory));
    glossaryServices.AddSingleton<IGlossarySerializationService, GlossarySerializationService>();

    var app = new CommandApp(new TypeRegistrar(glossaryServices));
    app.Configure(config =>
    {
        config.SetApplicationName("glossator");
        config.AddBranch("glossary", glossary =>
        {
            glossary.SetDescription("Sözlük ve terim yönetimi komutları (SQLite Browser ihtiyacını ortadan kaldırır).");
            glossary.AddCommand<GlossaryListCommand>("list")
                .WithDescription("Sözlükteki terimleri listeler ve filtreler.");
            glossary.AddCommand<GlossaryAddCommand>("add")
                .WithDescription("Sözlüğe yeni bir terim ekler.");
            glossary.AddCommand<GlossaryEditCommand>("edit")
                .WithDescription("Mevcut bir terimi günceller.");
            glossary.AddCommand<GlossaryDeleteCommand>("delete")
                .WithDescription("Sözlükten bir terimi siler.");
            glossary.AddCommand<GlossarySearchCommand>("search")
                .WithDescription("Sözlükte terim arar.");
            glossary.AddCommand<GlossaryExportCommand>("export")
                .WithDescription("Sözlüğü glossary.yaml veya json dosyasına aktarır.");
            glossary.AddCommand<GlossaryImportCommand>("import")
                .WithDescription("YAML veya JSON dosyasından terimleri içe aktarır.");
        });
    });

    return await app.RunAsync(args);
}

if (args.Length == 1 && (args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) || args[0].Equals("help", StringComparison.OrdinalIgnoreCase)))
{
    AnsiConsole.Write(
        new FigletText("GLOSSATOR")
            .Color(Color.Cyan1));

    AnsiConsole.MarkupLine("[bold cyan]GLOSSATOR - Kitap Yerelleştirme ve Terim Yönetim Sistemi (.NET 10 / MAF)[/]");
    AnsiConsole.MarkupLine("[dim]Geliştirici: Emre Dalbudak  •  GitHub: https://github.com/chela98  •  LinkedIn: https://www.linkedin.com/in/emre-dalbudak/[/]\n");
    AnsiConsole.MarkupLine("[bold yellow]Kullanım Standartları:[/]\n");
    AnsiConsole.MarkupLine("  [green]1. Kitap Çeviri Hattı (Worker Pool & Single-Writer):[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]<kitap-klasörü-veya-.epub>[/] [yellow][[-o çıktı.epub]] [[-j 3]] [[--fresh]] [[--test]][/]");
    AnsiConsole.MarkupLine("\n  [green]2. Sözlük & Terim Yönetimi (CLI & Git Entegrasyonu):[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary list[/] [yellow][[--unreviewed]] [[--policy <pol>]] [[--search <q>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary add[/] [yellow]-e <en> -t <tr> [[-p <pol>]] [[-d <tanım>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary edit[/] [yellow][[-i <id>]] [[-e <en>]] [[-t <tr>]] [[-p <pol>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary delete[/] [yellow]<terim-veya-id>[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary search[/] [yellow]<kelime>[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary export[/] [yellow][[glossary.yaml]] [[-f yaml|json]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary import[/] [yellow]<glossary.yaml> [[-s kaynak]][/]");
    return 0;
}

AnsiConsole.Write(
    new FigletText("GLOSSATOR")
        .Color(Color.Cyan1));

AnsiConsole.MarkupLine("[bold cyan]Microsoft Agent Framework (.NET 10) Tabanlı Kitap Yerelleştirme Boru Hattı[/]");
AnsiConsole.MarkupLine("[dim]Geliştirici: Emre Dalbudak  •  GitHub: https://github.com/chela98  •  LinkedIn: https://www.linkedin.com/in/emre-dalbudak/[/]\n");

// Graceful Shutdown & Cancellation Yönetimi (Ctrl+C / SIGINT / SIGTERM / SIGHUP)
using var cts = new CancellationTokenSource();
CancellationTokenSource? currentRunCts = null;

void RequestGracefulCancel(string source)
{
    var activeRun = Volatile.Read(ref currentRunCts);
    if (activeRun != null && !activeRun.IsCancellationRequested)
    {
        AnsiConsole.MarkupLine($"\n[bold yellow]İptal ({Markup.Escape(source)}):[/] Devam eden çeviri işlemi durduruluyor...");
        activeRun.Cancel();
    }
    else if (!cts.IsCancellationRequested)
    {
        AnsiConsole.MarkupLine($"\n[bold yellow]Çıkış ({Markup.Escape(source)}):[/] Uygulama kapatılıyor...");
        cts.Cancel();
    }
}

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    RequestGracefulCancel("SIGINT");
};

using var sigTermRegistration = TryRegisterPosixSignal(PosixSignal.SIGTERM, () => RequestGracefulCancel("SIGTERM"));
using var sigHupRegistration = TryRegisterPosixSignal(PosixSignal.SIGHUP, () => RequestGracefulCancel("SIGHUP"));

IHost? host = null;

try
{
    // 1. Dinamik Kitap Yolu ve CLI Parametreleri Ayrıştırma
    string? pathArg = null;
    string? explicitOutputPath = null;
    var isFresh = false;
    var isTestMode = false;
    int? requestedSectionCount = null;
    int concurrency = 3;
    var autoExtractTerms = false;

    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("-"))
        {
            if ((arg.Equals("--concurrency", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-j", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var parsedConcurrency))
                {
                    concurrency = Math.Clamp(parsedConcurrency, 1, Environment.ProcessorCount * 2);
                }
                continue;
            }

            if ((arg.Equals("--output", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                explicitOutputPath = args[++i].Trim('"', '\'');
                continue;
            }

            if ((arg.Equals("--input", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-i", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                pathArg = args[++i].Trim('"', '\'');
                continue;
            }

            if ((arg.Equals("--count", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var count))
                {
                    requestedSectionCount = count;
                }
                continue;
            }

            if (arg.Equals("--fresh", StringComparison.OrdinalIgnoreCase))
            {
                isFresh = true;
                continue;
            }

            if (arg.Equals("--test", StringComparison.OrdinalIgnoreCase))
            {
                isTestMode = true;
                continue;
            }

            if (arg.Equals("--extract-terms", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--auto-extract", StringComparison.OrdinalIgnoreCase))
            {
                autoExtractTerms = true;
                continue;
            }
        }
        else if (pathArg == null)
        {
            pathArg = arg.Trim('"', '\'');
        }
    }

    // 2. Kütüphane / Son Açılan Kitaplar Yöneticisi ile Kitap Yolunu Çöz
    var recentBooks = await RecentBooksManager.LoadRecentBooksAsync(cts.Token);
    string bookPath;

    if (!string.IsNullOrWhiteSpace(pathArg))
    {
        bookPath = pathArg;
    }
    else if (recentBooks.Count > 0 && (Directory.Exists(recentBooks[0].Path) || File.Exists(recentBooks[0].Path)))
    {
        bookPath = recentBooks[0].Path;
    }
    else
    {
        bookPath = ResolveBookPath(null);
    }

    if (string.IsNullOrWhiteSpace(bookPath) || (!Directory.Exists(bookPath) && !File.Exists(bookPath)))
    {
        AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Geçerli bir kitap yolu belirtilmedi veya bulunamadı: [dim]{Markup.Escape(bookPath ?? "")}[/]");
        AnsiConsole.MarkupLine("[yellow]Kullanım:[/] glossator <kitap-klasörü-veya-.epub> [[-o çıktı.epub]] [[--fresh]] [[--test]]");
        return 1;
    }

    // 3. EPUB Paketini Yükle ve Kütüphaneye Kaydet
    var package = EpubManifestReader.Read(bookPath);
    var activeBookEntry = await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package, cts.Token);

    // 4. Modern .NET 10 HostApplicationBuilder ve DI Altyapısı
    var hostBuilder = Host.CreateApplicationBuilder();
    CliHostConfiguration.Configure(
        hostBuilder.Configuration,
        hostBuilder.Environment.EnvironmentName,
        args);

    var config = hostBuilder.Configuration;

    // OpenTelemetry Kurulumu
    // Gizlilik gereği ham metin/prompt içerikleri varsayılan olarak kapalıdır; yalnızca yapılandırmayla açılır.
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    var captureSensitive = config.GetValue<bool>("OpenTelemetry:CaptureMessageContent")
                           || config.GetValue<bool>("GlossatorAi:EnableSensitiveTelemetry");
    if (captureSensitive)
    {
        Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");
    }

    var otelEndpoint = config["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";
    var serviceName = config["OpenTelemetry:ServiceName"] ?? "Glossator.Cli";

    // 1. ILogger -> OpenTelemetry Logging Entegrasyonu (Aspire Dashboard "Structured Logs" Canlı Akışı)
    hostBuilder.Logging.ClearProviders(); // TUI konsol ekranının bozulmaması için ham konsol loglarını temizle
    hostBuilder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
        logging.AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri(otelEndpoint);
            opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            opt.TimeoutMilliseconds = 1500;
        });
    });

    // 2. OpenTelemetry Tracing & Metrics (DI Host Entegrasyonu)
    hostBuilder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName))
        .WithTracing(tracing => tracing
            .AddSource(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
            .AddSource("Microsoft.Agents.*")
            .AddSource("Microsoft.Agents.AI.Workflows*")
            .AddSource("Experimental.Microsoft.Extensions.AI*")
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(otelEndpoint);
                opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                opt.TimeoutMilliseconds = 1500;
            }))
        .WithMetrics(metrics => metrics
            .AddMeter(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
            .AddMeter("Microsoft.Agents.*")
            .AddMeter("Microsoft.Agents.AI.Workflows*")
            .AddMeter("Experimental.Microsoft.Extensions.AI*")
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(otelEndpoint);
                opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                opt.TimeoutMilliseconds = 1500;
            }));

    // API Yapılandırması ve Güvenlik Denetimi
    var aiSection = config.GetSection("GlossatorAi");
    var aiOptions = aiSection.Get<GlossatorAiOptions>() ?? new GlossatorAiOptions();

    // Sadece aktif olarak görev alan ajanların (StyleAnalyst, Translator, Reviewer) fiilen çözümlediği sağlayıcıları denetle
    var activeProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var agentRoles = new[]
    {
        Glossator.AI.Extensions.AiServiceExtensions.KeyStyleAnalyst,
        Glossator.AI.Extensions.AiServiceExtensions.KeyTranslator,
        Glossator.AI.Extensions.AiServiceExtensions.KeyReviewer
    };

    foreach (var role in agentRoles)
    {
        var (providerName, _, _) = aiOptions.ResolveAgent(role);
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            activeProviders.Add(providerName);
        }
    }

    var sessionApiKeyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var providerName in activeProviders)
    {
        if (aiOptions.Providers.TryGetValue(providerName, out var providerConfig))
        {
            // Localhost / 127.0.0.1 (Ollama, LM Studio vb.) için API Key zorunlu değildir
            bool isLocal = Glossator.AI.Configuration.GlossatorAiOptions.IsLocalEndpoint(providerConfig.Endpoint);

            if (!isLocal && (string.IsNullOrWhiteSpace(providerConfig.ApiKey) || providerConfig.ApiKey.Contains("YOUR_")))
            {
                if (!Console.IsInputRedirected)
                {
                    AnsiConsole.MarkupLine($"\n[bold yellow]Uyarı:[/] Aktif sağlayıcı '[cyan]{Markup.Escape(providerName)}[/]' için API Key bulunamadı veya yer tutucu durumunda.");
                    var enteredKey = AnsiConsole.Prompt(
                        new TextPrompt<string>($"[green]Lütfen {Markup.Escape(providerName)} API Anahtarınızı girin:[/] ")
                            .Secret());

                    if (!string.IsNullOrWhiteSpace(enteredKey))
                    {
                        providerConfig.ApiKey = enteredKey.Trim();
                        sessionApiKeyOverrides[providerName] = providerConfig.ApiKey;
                        var shouldSave = AnsiConsole.Confirm($"[dim]Bu anahtarı bir sonraki çalıştırmalar için kullanıcı profiline (~/.glossator/user_settings.json) kaydetmek ister misiniz?[/]", defaultValue: true);
                        if (shouldSave)
                        {
                            await TrySaveApiKeyToUserSettingsAsync(providerName, providerConfig.ApiKey);
                        }
                    }
                }
            }
        }
    }

    // Merkezi SQLite Veri Tabanı ve Sözlük Başlatma (~/.glossator/glossator.db)
    var dbPath = GlossatorPaths.GetMasterDatabasePath();
    var connectionFactory = new SqliteConnectionFactory(dbPath);
    var repo = new SqliteGlossaryRepository(connectionFactory);
    var serializationService = new GlossarySerializationService();

    // 1. Tek Doğruluk Kaynağı (Single Source of Truth): SQLite Veritabanı (~/.glossator/glossator.db)
    // Eski ~/.glossator/master_glossary.yaml kalıntısı varsa temizle; YAML dosyaları yalnızca
    // açıkça 'glossator glossary import' ve 'export' komutlarıyla kullanılır, DB'yi otomatik ezmez.
    var legacyMasterYaml = Path.Combine(GlossatorPaths.GetGlossatorHomeDirectory(), "master_glossary.yaml");
    if (File.Exists(legacyMasterYaml))
    {
        try { File.Delete(legacyMasterYaml); } catch { /* ignore */ }
    }

    // Kitabın fiziksel konumunu belirle (.epub arşiviyse bulunduğu gerçek dizin, klasörse kendisi)
    var physicalBookDir = !string.IsNullOrWhiteSpace(package.SourceArchivePath) && File.Exists(package.SourceArchivePath)
        ? (Path.GetDirectoryName(Path.GetFullPath(package.SourceArchivePath)) ?? Environment.CurrentDirectory)
        : (!string.IsNullOrWhiteSpace(bookPath) && File.Exists(bookPath)
            ? (Path.GetDirectoryName(Path.GetFullPath(bookPath)) ?? Environment.CurrentDirectory)
            : (!string.IsNullOrWhiteSpace(package.RootDirectory) ? package.RootDirectory : Environment.CurrentDirectory));

    // Veritabanı boşsa çekirdek terimleri yükler ve hatalı artık kayıtları temizler (mevcut onaylı terimlere dokunmaz)
    await repo.SeedDefaultTurkishTranslationsAsync(cts.Token);

    // 2. Kitaba Özgü Yeni Terim Çıkarımı (Karantina Mimarisi: Master Sözlüğü korumak için ham terimleri kitaba özel dosyaya yazar)
    if (autoExtractTerms)
    {
        var glossaryTerms = EpubGlossaryReader.Read(package);
        var indexTerms = EpubIndexReader.Read(package);

        var extractedTerms = glossaryTerms.Concat(indexTerms)
            .DistinctBy(t => t.EnglishTerm, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extractedTerms.Count > 0)
        {
            var quarantineYamlPath = Path.Combine(physicalBookDir, "glossary.extracted.yaml");

            await serializationService.ExportAsync(extractedTerms, quarantineYamlPath, SerializationFormat.Yaml, cts.Token);

            AnsiConsole.MarkupLine($"\n[bold yellow]Karantina Sözlüğü:[/] Kitap dizininden [bold cyan]{extractedTerms.Count}[/] ham terim çıkarıldı ve '[green]{Markup.Escape(quarantineYamlPath)}[/]' dosyasına kaydedildi.");
            AnsiConsole.MarkupLine("[dim]İpucu: Bu terimleri inceleyip onayladıktan sonra ana sözlüğe aktarmak için:[/]");
            AnsiConsole.MarkupLine($"[cyan]  glossator glossary import \"{Markup.Escape(quarantineYamlPath)}\"[/]\n");
        }
    }

    // Servis Bağımlılıkları (.NET 10 DI)
    hostBuilder.Services.AddSingleton<ISqliteConnectionFactory>(connectionFactory);
    hostBuilder.Services.AddSingleton(repo);
    hostBuilder.Services.AddSingleton<CheckpointStore>();

    // Per-chapter Scoped dependencies
    hostBuilder.Services.AddScoped(_ => new TermUsageTracker("default"));
    hostBuilder.Services.AddScoped<TermGlossingPostProcessor>();
    hostBuilder.Services.AddGlossatorExchangeRateService();
    hostBuilder.Services.AddSingleton<ParallelLocalizationEngine>();

    hostBuilder.Services.Configure<GlossatorAiOptions>(aiSection);
    hostBuilder.Services.PostConfigure<GlossatorAiOptions>(opts =>
    {
        foreach (var (providerName, overrideKey) in sessionApiKeyOverrides)
        {
            if (opts.Providers.TryGetValue(providerName, out var targetProvider))
            {
                targetProvider.ApiKey = overrideKey;
            }
        }
    });
    hostBuilder.Services.AddGlossatorAiClients();

    host = hostBuilder.Build();
    await host.StartAsync(cts.Token);
    var engine = host.Services.GetRequiredService<ParallelLocalizationEngine>();

    // Eğer doğrudan CLI bayrakları ile çağrıldıysa (Headless / Script Modu)
    if (isTestMode || requestedSectionCount.HasValue || (Console.IsInputRedirected && !string.IsNullOrWhiteSpace(pathArg)))
    {
        var translatableDocs = GetTranslatableDocuments(package);
        var targetChapters = isTestMode ? translatableDocs.Take(1).ToList() : translatableDocs;
        try
        {
            await engine.RunAsync(package, targetChapters, concurrency, isFresh, requestedSectionCount, isTestMode, cts.Token);
            return engine.LastFailedChapters.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]Headless Mod Hatası:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // PERSISTENT TUI DASHBOARD & WORKSPACE HUB (ANA UYGULAMA DÖNGÜSÜ)
    // ─────────────────────────────────────────────────────────────────────────────
    while (!cts.IsCancellationRequested)
    {
        var translatableDocuments = GetTranslatableDocuments(package);
        activeBookEntry = await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package, cts.Token);
        var allTerms = repo.GetAllTerms();

        // 1. Şık ve Bilgilendirici Dashboard Paneli
        var dashboardTable = new Table().Border(TableBorder.Rounded);
        dashboardTable.AddColumn(new TableColumn("[bold]Alan[/]"));
        dashboardTable.AddColumn(new TableColumn("[bold]Bilgi[/]"));

        dashboardTable.AddRow("Aktif Kitap", $"[bold cyan]{Markup.Escape(package.Title)}[/]");
        dashboardTable.AddRow("Yazar & Yayıncı", $"[green]{Markup.Escape(package.Author ?? "Bilinmiyor")}[/] | [yellow]{Markup.Escape(activeBookEntry.Publisher ?? "Bilinmiyor")}[/]");
        dashboardTable.AddRow("İlerleme Durumu", $"[bold green]{activeBookEntry.TranslatedChapters} / {activeBookEntry.TotalChapters}[/] Bölüm (%{activeBookEntry.ProgressPercentage:F1})");
        dashboardTable.AddRow("Master Sözlük", $"[bold cyan]{allTerms.Count}[/] Onaylı Terim (SSOT)");
        dashboardTable.AddRow("Varsayılan Worker", $"[bold yellow]{concurrency}[/] Paralel İşçi (Worker)");

        AnsiConsole.Clear();
        AnsiConsole.Write(new Panel(dashboardTable)
            .Header("[bold cyan]GLOSSATOR TUI & WORKSPACE HUD[/]")
            .BorderColor(Color.Cyan1));

        DrainKeyBuffer();

        // 2. Ana Menü Seçenekleri (Sıfır Onay Yorgunluğu)
        var menuChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold green]Lütfen yapmak istediğiniz işlemi seçin:[/]")
                .PageSize(10)
                .AddChoices(
                    $"1. Çeviriye Başla (Kaldığı Yerden Devam Et - {concurrency} Worker)",
                    "2. Özelleştirilmiş Başlatma (Bölüm Seçimi & Worker Sayısı)",
                    "3. Sözlük ve Terim Yönetim Merkezi (TUI Hub)",
                    "4. Kitap Değiştir / Kütüphane (Kayıtlı Kitaplar)",
                    "5. Nihai EPUB'ı Derle ve Paketle",
                    "0. Çıkış"));

        if (menuChoice.StartsWith("0") || menuChoice.Contains("Çıkış"))
        {
            AnsiConsole.MarkupLine("\n[cyan]Glossator oturumu güvenle kapatıldı. İyi okumalar![/]");
            return 0;
        }

        if (menuChoice.StartsWith("1"))
        {
            // Kaldığı Yerden Devam Et (Akıllı Bulucu)
            var pendingChapters = translatableDocuments
                .Where(doc => !IsDocumentTranslated(package.OpfDirectory, doc.Href))
                .ToList();

            if (pendingChapters.Count == 0)
            {
                AnsiConsole.MarkupLine("\n[bold green]Tebrikler![/] Bu kitaptaki tüm bölümler (%100) zaten tamamlanmış.");
                AnsiConsole.MarkupLine("[dim]Belirli bir bölümü baştan çalıştırmak için [2] Özelleştirilmiş Başlatma'yı, EPUB derlemek için [5]'i kullanabilirsiniz.[/]");
                AnsiConsole.MarkupLine("\n[dim]Devam etmek için Enter'a basın...[/]");
                Console.ReadLine();
                continue;
            }

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold cyan]> Kaldığı yerden devam ediliyor:[/] [yellow]{pendingChapters.Count}[/] çevrilmemiş bölüm bulundu. [green]{concurrency}[/] Worker başlatılıyor...\n");
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            currentRunCts = runCts;
            try
            {
                await engine.RunAsync(package, pendingChapters, concurrency, isFresh: false, null, isTestMode: false, runCts.Token);
                host.Services.GetService<TracerProvider>()?.ForceFlush();
                host.Services.GetService<MeterProvider>()?.ForceFlush();
                await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package, cts.Token);

                AnsiConsole.MarkupLine("\n[bold green]İşlem tamamlandı.[/] Ana menüye dönmek için Enter'a basın...");
                Console.ReadLine();
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[bold yellow]İşlem durduruldu (Ctrl+C).[/] O ana kadar tamamlanan bölümler güvenle kaydedildi.");
                AnsiConsole.MarkupLine("[dim]Ana menüye dönmek için Enter'a basın...[/]");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                await HandleTranslationExceptionAsync(ex, host.Services, aiOptions);
            }
            finally
            {
                currentRunCts = null;
            }
        }
        else if (menuChoice.StartsWith("2"))
        {
            // Özelleştirilmiş Bölüm ve Worker Seçimi
            var chapterChoiceMode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold green]Bölüm seçim modunu belirleyin:[/]")
                    .PageSize(10)
                    .AddChoices(
                        "1. Çoklu Bölüm Seçimi (Durum rozetleriyle işaretleyin)",
                        $"2. Tüm Kitap ({translatableDocuments.Count} Bölüm)",
                        "3. Tek Bir Bölüm Seç (Hızlı Test)",
                        "4. Bölüm Numarası veya Aralığı Gir (Örn: 1-5 veya 2,4,6)",
                        "0. Geri Dön"));

            if (chapterChoiceMode.StartsWith("0") || chapterChoiceMode.Contains("Geri Dön"))
            {
                continue;
            }

            List<EpubItem> selectedChapters = [];

            if (chapterChoiceMode.StartsWith("1"))
            {
                var multiPrompt = new MultiSelectionPrompt<EpubItem>()
                    .Title("[bold green]Çevirmek istediğiniz bölümleri seçin:[/] [dim](Boşluk: Seç/Kaldır, Enter: Onayla)[/]")
                    .PageSize(15)
                    .UseConverter(item =>
                    {
                        var isDone = IsDocumentTranslated(package.OpfDirectory, item.Href);
                        var statusBadge = isDone ? "[green]TAMAM[/]" : "[yellow]BEKLİYOR[/]";
                        return $"{statusBadge} {Markup.Escape(Path.GetFileName(item.Href))} - {Markup.Escape(GetChapterFriendlyName(item))}";
                    })
                    .AddChoices(translatableDocuments);

                selectedChapters = AnsiConsole.Prompt(multiPrompt);
            }
            else if (chapterChoiceMode.StartsWith("2"))
            {
                selectedChapters = translatableDocuments;
            }
            else if (chapterChoiceMode.StartsWith("3"))
            {
                var singlePrompt = new SelectionPrompt<EpubItem>()
                    .Title("[bold green]Çevirmek istediğiniz tek bölümü seçin:[/]")
                    .PageSize(15)
                    .UseConverter(item =>
                    {
                        var isDone = IsDocumentTranslated(package.OpfDirectory, item.Href);
                        var statusBadge = isDone ? "[green]TAMAM[/]" : "[yellow]BEKLİYOR[/]";
                        return $"{statusBadge} {Markup.Escape(Path.GetFileName(item.Href))} - {Markup.Escape(GetChapterFriendlyName(item))}";
                    })
                    .AddChoices(translatableDocuments);

                selectedChapters = [AnsiConsole.Prompt(singlePrompt)];
            }
            else if (chapterChoiceMode.StartsWith("4"))
            {
                selectedChapters = PromptChapterRange(package.OpfDirectory, translatableDocuments);
            }

            if (selectedChapters.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Hiçbir bölüm seçilmedi, ana menüye dönülüyor.[/]");
                await Task.Delay(1000);
                continue;
            }

            // Worker sayısı ayarı
            var runConcurrency = AnsiConsole.Prompt(
                new TextPrompt<int>($"[bold yellow]Kaç paralel işçi (worker thread) çalışsın?[/] [dim](Varsayılan: {concurrency}):[/] ")
                    .DefaultValue(concurrency)
                    .Validate(val => val >= 1 && val <= Environment.ProcessorCount * 2
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"1 ile {Environment.ProcessorCount * 2} arasında bir değer girin.")));

            var forceFresh = AnsiConsole.Confirm("[bold yellow]Bu bölümler için mevcut çeviri önbelleği temizlensin mi (Sıfırdan çevir)?[/]", defaultValue: false);

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold cyan]> Özel başlatma:[/] [yellow]{selectedChapters.Count}[/] Bölüm, [green]{runConcurrency}[/] Worker...\n");
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            currentRunCts = runCts;
            try
            {
                await engine.RunAsync(package, selectedChapters, runConcurrency, isFresh: forceFresh, requestedSectionCount, isTestMode, runCts.Token);
                host.Services.GetService<TracerProvider>()?.ForceFlush();
                host.Services.GetService<MeterProvider>()?.ForceFlush();
                await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package, cts.Token);

                AnsiConsole.MarkupLine("\n[bold green]İşlem tamamlandı.[/] Devam etmek için Enter'a basın...");
                Console.ReadLine();
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[bold yellow]İşlem durduruldu (Ctrl+C).[/] O ana kadar tamamlanan bölümler güvenle kaydedildi.");
                AnsiConsole.MarkupLine("[dim]Ana menüye dönmek için Enter'a basın...[/]");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                await HandleTranslationExceptionAsync(ex, host.Services, aiOptions);
            }
            finally
            {
                currentRunCts = null;
            }
        }
        else if (menuChoice.StartsWith("3"))
        {
            // Sözlük ve Terim Yönetim Merkezi (TUI Hub)
            await GlossaryInteractiveHub.RunAsync(repo, serializationService, cts.Token);
        }
        else if (menuChoice.StartsWith("4"))
        {
            // Kitap Değiştir / Kütüphane Yöneticisi
            while (!cts.IsCancellationRequested)
            {
                var recents = await RecentBooksManager.LoadRecentBooksAsync(cts.Token);
                var choices = new List<string>();

                foreach (var b in recents)
                {
                    var exists = File.Exists(b.Path) || Directory.Exists(b.Path);
                    var status = exists ? $"%{b.ProgressPercentage:F1}" : "[red]KAYIP DOSYA[/]";
                    choices.Add($"* {b.Title} ({b.Publisher}) ({status}) - {b.Path}");
                }

                choices.Add("+ Yeni Kitap Yolu Gir / Sürükle Bırak (.epub veya klasör)");
                if (recents.Count > 0)
                {
                    choices.Add("- Listeden Kitap Kaldır");
                    choices.Add("- Bulunamayan / Silinmiş Kitapları Temizle");
                }
                choices.Add("< Geri Dön");

                var bookPick = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold green]Lütfen çalışmak istediğiniz kitabı seçin veya yönetin:[/]")
                        .PageSize(14)
                        .UseConverter(Markup.Escape)
                        .AddChoices(choices));

                if (bookPick.StartsWith("<") || bookPick.Contains("Geri Dön"))
                {
                    break;
                }

                if (bookPick.StartsWith("- Listeden Kitap Kaldır"))
                {
                    var removeChoices = recents
                        .Select(r => $"{r.Title} - {r.Path}")
                        .Concat(["< İptal"])
                        .ToList();

                    var toRemove = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold yellow]Kütüphane geçmişinden kaldırmak istediğiniz kitabı seçin (diskteki dosyanız silinmez):[/]")
                            .PageSize(10)
                            .UseConverter(Markup.Escape)
                            .AddChoices(removeChoices));

                    if (!toRemove.StartsWith("<") && !toRemove.Contains("İptal"))
                    {
                        var matchedEntry = recents.FirstOrDefault(r => toRemove.Contains(r.Path));
                        if (matchedEntry != null)
                        {
                            await RecentBooksManager.RemoveBookAsync(matchedEntry.Path, cts.Token);
                            AnsiConsole.MarkupLine($"\n[bold green]OK:[/] '[cyan]{Markup.Escape(matchedEntry.Title)}[/]' kütüphane geçmişinden kaldırıldı.");
                            await Task.Delay(1000);
                        }
                    }
                    continue;
                }

                if (bookPick.StartsWith("- Bulunamayan"))
                {
                    var cleanedCount = await RecentBooksManager.CleanMissingBooksAsync(cts.Token);
                    if (cleanedCount > 0)
                    {
                        AnsiConsole.MarkupLine($"\n[bold green]OK:[/] Diskte artık bulunamayan [cyan]{cleanedCount}[/] kitap kütüphane geçmişinden temizlendi.");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("\n[dim]Tüm kayıtlı kitaplar diskte mevcut, temizlenecek kayıp dosya yok.[/]");
                    }
                    await Task.Delay(1200);
                    continue;
                }

                string targetNewPath;
                if (bookPick.StartsWith("+") || bookPick.Contains("Yeni Kitap"))
                {
                    targetNewPath = AnsiConsole.Ask<string>("[yellow]Lütfen .epub dosyasını veya klasör yolunu sürükleyip bırakın:[/] ").Trim('"', '\'');
                }
                else
                {
                    var matched = recents.FirstOrDefault(r => bookPick.Contains(r.Path));
                    targetNewPath = matched?.Path ?? bookPath;
                }

                if (Directory.Exists(targetNewPath) || File.Exists(targetNewPath))
                {
                    bookPath = targetNewPath;
                    package = EpubManifestReader.Read(bookPath);
                    activeBookEntry = await RecentBooksManager.RegisterOrUpdateAsync(bookPath, package, cts.Token);

                    // Yeni kitaptan terimleri karantinaya çıkarma (Master Sözlüğü korur)
                    if (autoExtractTerms)
                    {
                        var newGlossaryTerms = EpubGlossaryReader.Read(package);
                        var newIndexTerms = EpubIndexReader.Read(package);
                        var extractedTerms = newGlossaryTerms.Concat(newIndexTerms)
                            .DistinctBy(t => t.EnglishTerm, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (extractedTerms.Count > 0)
                        {
                            var bookDirectory = Path.GetDirectoryName(Path.GetFullPath(bookPath)) ?? AppContext.BaseDirectory;
                            var quarantineYamlPath = Path.Combine(bookDirectory, "glossary.extracted.yaml");
                            await serializationService.ExportAsync(extractedTerms, quarantineYamlPath, SerializationFormat.Yaml, cts.Token);
                            AnsiConsole.MarkupLine($"[dim]Kitap dizininden {extractedTerms.Count} ham terim 'glossary.extracted.yaml' dosyasına yazıldı.[/]");
                        }
                    }

                    AnsiConsole.MarkupLine($"\n[bold green]OK:[/] Aktif kitap başarıyla seçildi: [bold cyan]{Markup.Escape(package.Title)}[/]");
                    AnsiConsole.MarkupLine($"[dim]Durum: {activeBookEntry.TranslatedChapters} / {activeBookEntry.TotalChapters} Bölüm (%{activeBookEntry.ProgressPercentage:F1})[/]\n");

                    DrainKeyBuffer();

                    var postSwitchAction = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold green]Seçilen kitap ile ne yapmak istersiniz?[/]")
                            .AddChoices(
                                "1. Ana Dashboard'a Dön (İncele & Ayarları Gör)",
                                "2. Başka Bir Kitap Seç"));

                    if (postSwitchAction.StartsWith("1"))
                    {
                        break;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[bold red]Hata:[/] Belirtilen yol bulunamadı: {Markup.Escape(targetNewPath)}");
                    await Task.Delay(2000);
                }
            }
        }
        else if (menuChoice.StartsWith("5"))
        {
            // EPUB Paketleme Motoru
            RunPackaging(package, explicitOutputPath);
            AnsiConsole.MarkupLine("\n[dim]Ana menüye dönmek için Enter'a basın...[/]");
            Console.ReadLine();
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[bold red]İşlem kullanıcı tarafından durduruldu (Ctrl+C / SIGTERM).[/]");
    AnsiConsole.MarkupLine("[dim]In-flight LLM HTTP istekleri derhal kesildi, token yakılması önlendi, veritabanı tutarlılığı korundu.[/]\n");
    return 130;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.WriteException(ex);
    return 1;
}
finally
{
    await ShutdownCliHostAsync(host);
}

static PosixSignalRegistration? TryRegisterPosixSignal(PosixSignal signal, Action onSignal)
{
    try
    {
        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            onSignal();
        });
    }
    catch (PlatformNotSupportedException)
    {
        return null;
    }
}

static async Task ShutdownCliHostAsync(IHost? host)
{
    if (host == null)
    {
        return;
    }

    try
    {
        if (host.Services.GetService<CheckpointStore>() is { } checkpointStore)
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await checkpointStore.DisposeAsync().AsTask().WaitAsync(flushCts.Token).ConfigureAwait(false);
        }
    }
    catch { }

    try
    {
        host.Services.GetService<TracerProvider>()?.ForceFlush(1000);
        host.Services.GetService<MeterProvider>()?.ForceFlush(1000);
    }
    catch { }

    try
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await host.StopAsync(stopCts.Token).ConfigureAwait(false);
    }
    catch { }

    try
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            host.Dispose();
        }
    }
    catch { }
}

static List<EpubItem> GetTranslatableDocuments(EpubPackage package)
{
    return package.Spine
        .Where(s => s.IsDocument &&
                    s.Role != EpubDocumentRole.Index &&
                    s.Role != EpubDocumentRole.Cover &&
                    s.Role != EpubDocumentRole.Navigation)
        .ToList();
}

static string ResolveBookPath(string? candidate)
{
    if (!string.IsNullOrWhiteSpace(candidate))
    {
        return candidate;
    }

    var envPath = Environment.GetEnvironmentVariable("GLOSSATOR_BOOK_PATH");
    if (!string.IsNullOrWhiteSpace(envPath) && (Directory.Exists(envPath) || File.Exists(envPath)))
    {
        return envPath;
    }

    var currentDir = Directory.GetCurrentDirectory();
    var oebpsInCurrent = Path.Combine(currentDir, "OEBPS");
    if (Directory.Exists(oebpsInCurrent))
    {
        return oebpsInCurrent;
    }

    var epubs = Directory.GetFiles(currentDir, "*.epub");
    if (epubs.Length > 0)
    {
        return epubs[0];
    }

    if (!Console.IsInputRedirected)
    {
        return AnsiConsole.Ask<string>("[yellow]Lütfen Kitap klasörünü veya .epub dosya yolunu girin:[/] ")
            .Trim('"', '\'');
    }

    return string.Empty;
}

static void RunPackaging(EpubPackage package, string? customOutputPath = null)
{
    AnsiConsole.MarkupLine("\n[bold cyan]> EPUB Paketleme Motoru Çalıştırılıyor...[/]");
    var translations = EpubPackager.DiscoverTranslations(package);
    AnsiConsole.MarkupLine($"[green]+ Bulunan Çevrilmiş Dosya:[/] [bold cyan]{translations.Count}[/] adet");

    if (translations.Count == 0)
    {
        AnsiConsole.MarkupLine("[bold yellow]Uyarı:[/] Henüz çevrilmiş (*_tr.xhtml / *_tr.html) dosya bulunamadı! Lütfen önce bölümleri çevirin.");
        return;
    }

    var outputPath = EpubPackager.Package(package, customOutputPath);
    var fileInfo = new FileInfo(outputPath);

    var table = new Table().Border(TableBorder.DoubleEdge);
    table.AddColumn("[bold]Alan[/]");
    table.AddColumn("[bold]Bilgi[/]");
    table.AddRow("Kitap", $"[cyan]{Markup.Escape(package.Title)} (Türkçe)[/]");
    table.AddRow("Çevrilen Dosyalar", $"[green]{translations.Count} dosya entegre edildi[/]");
    table.AddRow("Çıktı Dosyası", $"[bold yellow]{Markup.Escape(outputPath)}[/]");
    table.AddRow("Boyut", $"[bold green]{fileInfo.Length / (1024.0 * 1024.0):F2} MB ({fileInfo.Length / 1024:N0} KB)[/]");
    table.AddRow("Standart", "[green]IDPF EPUB 3 / EPUB 2 Uyumlu (mimetype uncompressed ilk sıra)[/]");

    AnsiConsole.Write(new Panel(table)
        .Header("[bold green]EPUB PAKETLEME TAMAMLANDI[/]")
        .BorderColor(Color.Green));

    AnsiConsole.MarkupLine($"\n[bold green]OK:[/] Kitabınız tabletinizde (Apple Books / Kindle / Kobo / Calibre) okunmaya hazır!\n");
}

static string GetChapterFriendlyName(EpubItem item)
{
    if (!string.IsNullOrWhiteSpace(item.Title))
        return item.Title;

    return Path.GetFileNameWithoutExtension(item.Href);
}

static List<EpubItem> PromptChapterRange(string opfDirectory, List<EpubItem> documents)
{
    while (true)
    {
        AnsiConsole.MarkupLine("\n[dim]Mevcut Bölümler:[/]");
        for (int idx = 0; idx < documents.Count; idx++)
        {
            var doc = documents[idx];
            var isDone = IsDocumentTranslated(opfDirectory, doc.Href);
            var badge = isDone ? "[green]TAMAM[/]" : "[dim]--[/]";
            AnsiConsole.MarkupLine($"  [yellow]{idx + 1:D2}.[/] {badge} [cyan]{Markup.Escape(Path.GetFileName(doc.Href))}[/] - [dim]{Markup.Escape(GetChapterFriendlyName(doc))}[/]");
        }

        var promptText = "\n[bold yellow]Bölüm aralığı veya numaraları girin (Örn: 1-3 veya 1,3,5) [dim][[Ana menü için 0 veya q]][/]:[/] ";
        var rawInput = AnsiConsole.Prompt(
            new TextPrompt<string>(promptText)
                .AllowEmpty())?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(rawInput) ||
            rawInput == "0" ||
            rawInput.Equals("q", StringComparison.OrdinalIgnoreCase) ||
            rawInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            rawInput.Equals("iptal", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var selectedIndices = new HashSet<int>();
        var parts = rawInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool parseSuccess = true;

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (rangeParts.Length == 2 && int.TryParse(rangeParts[0], out var start) && int.TryParse(rangeParts[1], out var end))
                {
                    for (int k = Math.Min(start, end); k <= Math.Max(start, end); k++)
                    {
                        if (k >= 1 && k <= documents.Count)
                            selectedIndices.Add(k - 1);
                    }
                }
                else
                {
                    parseSuccess = false;
                }
            }
            else if (int.TryParse(part, out var num))
            {
                if (num >= 1 && num <= documents.Count)
                    selectedIndices.Add(num - 1);
            }
            else
            {
                parseSuccess = false;
            }
        }

        if (parseSuccess && selectedIndices.Count > 0)
        {
            return selectedIndices.OrderBy(i => i).Select(i => documents[i]).ToList();
        }

        AnsiConsole.MarkupLine("[bold red]HATA:[/] Geçersiz aralık veya numara formatı. Lütfen tekrar deneyin.");
    }
}

static bool IsDocumentTranslated(string opfDirectory, string docHref)
    => DocumentTranslationStatus.IsDocumentTranslated(opfDirectory, docHref);

static void DrainKeyBuffer()
{
    try
    {
        while (Console.KeyAvailable)
        {
            Console.ReadKey(intercept: true);
        }
    }
    catch
    {
        // Standart girdi yeniden yönlendirildiyse veya konsol erişimi yoksa yoksay
    }
}

static async Task HandleTranslationExceptionAsync(
    Exception ex,
    IServiceProvider services,
    GlossatorAiOptions aiOptions)
{
    bool isAuthError = false;
    string? authDetail = null;

    var current = ex;
    while (current != null)
    {
        if (current is System.ClientModel.ClientResultException cre && cre.Status == 401)
        {
            isAuthError = true;
            authDetail = cre.Message;
            break;
        }
        if (current.Message.Contains("401") ||
            current.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            current.Message.Contains("Geçersiz API anahtarı", StringComparison.OrdinalIgnoreCase) ||
            current.Message.Contains("authentication_error", StringComparison.OrdinalIgnoreCase))
        {
            isAuthError = true;
            authDetail = current.Message;
            break;
        }
        current = current.InnerException;
    }

    if (isAuthError)
    {
        var (activeProviderName, activeConfig, _) = aiOptions.ResolveAgent(AiServiceExtensions.KeyStyleAnalyst);

        var panel = new Panel(
            new Markup($"[bold red]HATA: HTTP 401 - Yetkilendirme / API Anahtarı Reddedildi![/]\n\n" +
                       $"Aktif Sağlayıcı : [cyan]{Markup.Escape(activeProviderName)}[/]\n" +
                       $"Endpoint        : [yellow]{Markup.Escape(activeConfig?.Endpoint ?? "Bilinmiyor")}[/]\n" +
                       $"Sunucu Yanıtı   : [red]{Markup.Escape(authDetail ?? "Geçersiz API anahtarı")}[/]\n\n" +
                       $"[dim]Not: O ana kadar çevrilen tüm bölümler ve ara checkpoint kayıtları SQLite ve diskte güvenle saklandı.[/]"))
            .Header("[bold red]API KİMLİK DOĞRULAMA HATASI[/]")
            .BorderColor(Color.Red);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        DrainKeyBuffer();

        var updateKey = AnsiConsole.Confirm($"[bold yellow]'{Markup.Escape(activeProviderName)}' için yeni bir API anahtarı girmek ister misiniz?[/]", defaultValue: true);
        if (updateKey)
        {
            var newKey = AnsiConsole.Prompt(
                new TextPrompt<string>($"[bold green]Lütfen {Markup.Escape(activeProviderName)} API Anahtarınızı girin:[/] ")
                    .Secret());

            if (!string.IsNullOrWhiteSpace(newKey))
            {
                var trimmed = newKey.Trim();
                if (aiOptions.Providers.TryGetValue(activeProviderName, out var targetProv))
                {
                    targetProv.ApiKey = trimmed;
                }

                var optionsInstance = services.GetService<IOptionsMonitor<GlossatorAiOptions>>()?.CurrentValue
                    ?? services.GetService<IOptions<GlossatorAiOptions>>()?.Value;
                if (optionsInstance != null && optionsInstance.Providers.TryGetValue(activeProviderName, out var optProv))
                {
                    optProv.ApiKey = trimmed;
                }

                AnsiConsole.MarkupLine($"\n[bold green]OK:[/] '[cyan]{Markup.Escape(activeProviderName)}[/]' API anahtarı çalışma zamanında güncellendi.");

                var saveToDisk = AnsiConsole.Confirm("[dim]Bu API anahtarını kalıcı olması için kullanıcı profiline (~/.glossator/user_settings.json) kaydetmek ister misiniz?[/]", defaultValue: true);
                if (saveToDisk)
                {
                    await TrySaveApiKeyToUserSettingsAsync(activeProviderName, trimmed);
                }

                AnsiConsole.MarkupLine("[bold cyan]Şimdi ana menüden '1. Çeviriye Başla' diyerek kaldığınız yerden devam edebilirsiniz.[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]İşlem iptal edildi. Ana menüye dönülüyor...[/]");
        }

        AnsiConsole.MarkupLine("\n[dim]Devam etmek için Enter'a basın...[/]");
        Console.ReadLine();
    }
    else
    {
        var errorPanel = new Panel(
            new Markup($"[bold red]Çeviri sırasında bir hata oluştu:[/] {Markup.Escape(ex.Message)}\n\n" +
                       $"[dim]Ayrıntı: {Markup.Escape(ex.InnerException?.Message ?? "Yok")}[/]\n\n" +
                       $"[dim]Veritabanı tutarlılığı korunmuştur. Yapılan ilerleme güvenle saklandı.[/]"))
            .Header("[bold red]HATA BİLDİRİMİ[/]")
            .BorderColor(Color.Red);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(errorPanel);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Ana menüye dönmek için Enter'a basın...[/]");
        Console.ReadLine();
    }
}

static async Task TrySaveApiKeyToUserSettingsAsync(string providerName, string newKey)
{
    var settingsPath = GlossatorPaths.GetUserSettingsPath();
    try
    {
        System.Text.Json.Nodes.JsonNode node;
        if (File.Exists(settingsPath))
        {
            var existingJson = await File.ReadAllTextAsync(settingsPath);
            node = System.Text.Json.Nodes.JsonNode.Parse(existingJson) ?? new System.Text.Json.Nodes.JsonObject();
        }
        else
        {
            node = new System.Text.Json.Nodes.JsonObject();
        }

        var glossatorAi = node["GlossatorAi"] as System.Text.Json.Nodes.JsonObject;
        if (glossatorAi == null)
        {
            glossatorAi = new System.Text.Json.Nodes.JsonObject();
            node["GlossatorAi"] = glossatorAi;
        }

        var providers = glossatorAi["Providers"] as System.Text.Json.Nodes.JsonObject;
        if (providers == null)
        {
            providers = new System.Text.Json.Nodes.JsonObject();
            glossatorAi["Providers"] = providers;
        }

        var provider = providers[providerName] as System.Text.Json.Nodes.JsonObject;
        if (provider == null)
        {
            provider = new System.Text.Json.Nodes.JsonObject();
            providers[providerName] = provider;
        }

        provider["ApiKey"] = newKey;

        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await Glossator.Core.Services.AtomicFileWriter.WriteAllTextAsync(
            settingsPath,
            node.ToJsonString(opts));

        AnsiConsole.MarkupLine($"[bold green]OK:[/] '[cyan]{providerName}[/]' API anahtarı güvenle kullanıcı profiline (~/.glossator/user_settings.json) kaydedildi. [dim](Git'e asla sızmaz)[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[dim red]Kullanıcı ayarları kaydedilirken hata oluştu: {Markup.Escape(ex.Message)}[/]");
    }
}

