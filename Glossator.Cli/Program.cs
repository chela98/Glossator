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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Spectre.Console;
using Spectre.Console.Cli;

// 0. Eğer 'glossary' alt komutu veya '--help / -h' çağrıldıysa Spectre.Console.Cli motorunu çalıştır
if (args.Length > 0 && args[0].Equals("glossary", StringComparison.OrdinalIgnoreCase))
{
    var glossaryServices = new ServiceCollection();
    var defaultDbPath = Path.Combine(Environment.CurrentDirectory, "glossator.db");
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

    AnsiConsole.MarkupLine("[bold cyan]GLOSSATOR - Kitap Yerelleştirme ve Terim Yönetim Sistemi (.NET 10 / MAF)[/]\n");
    AnsiConsole.MarkupLine("[bold yellow]Kullanım Standartları:[/]\n");
    AnsiConsole.MarkupLine("  [green]1. Kitap Çeviri Hattı (Worker Pool & Single-Writer):[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]<kitap-klasörü-veya-.epub>[/] [yellow][[-o çıktı.epub]] [[-j 3]] [[--fresh]] [[--test]][/]");
    AnsiConsole.MarkupLine("\n  [green]2. Sözlük & Terim Yönetimi (CLI & Git Entegrasyonu):[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary list[/] [yellow][[--unreviewed]] [[--policy <pol>]] [[--search <q>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary add[/] [yellow]-e <en> -t <tr> [[-p <pol>]] [[-d <tanım>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary edit[/] [yellow][[-i <id>]] [[-e <en>]] [[-t <tr>]] [[-p <pol>]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary search[/] [yellow]<kelime>[/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary export[/] [yellow][[glossary.yaml]] [[-f yaml|json]][/]");
    AnsiConsole.MarkupLine("     [dim]glossator[/] [cyan]glossary import[/] [yellow]<glossary.yaml> [[-s kaynak]][/]");
    return 0;
}

AnsiConsole.Write(
    new FigletText("GLOSSATOR")
        .Color(Color.Cyan1));

AnsiConsole.MarkupLine("[bold cyan]Microsoft Agent Framework (.NET 10) Tabanlı Kitap Yerelleştirme Boru Hattı[/]\n");

// 0. İnteraktif Ana Menü (Uygulama argümansız başlatıldığında)
if (args.Length == 0 && !Console.IsInputRedirected)
{
    var mainChoice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[bold green]Lütfen yapmak istediğiniz işlemi seçin:[/]")
            .PageSize(10)
            .AddChoices(
                "🚀 1. Kitap Yerelleştirme / Çeviri Boru Hattı",
                "📚 2. Sözlük ve Terim Yönetim Merkezi (TUI Hub)",
                "📦 3. EPUB Paketleme Motoru",
                "🚪 0. Çıkış"));

    if (mainChoice.StartsWith("🚪") || mainChoice.StartsWith("0"))
    {
        return 0;
    }

    if (mainChoice.StartsWith("📚") || mainChoice.StartsWith("2"))
    {
        var defaultDb = Path.Combine(Environment.CurrentDirectory, "glossator.db");
        var hubConnFactory = new SqliteConnectionFactory(defaultDb);
        var hubRepo = new SqliteGlossaryRepository(hubConnFactory);
        var hubSerialization = new GlossarySerializationService();
        await GlossaryInteractiveHub.RunAsync(hubRepo, hubSerialization);

        var continueWithTranslation = AnsiConsole.Confirm("\n[bold yellow]Sözlük yönetiminden sonra kitap çeviri işlemine geçmek ister misiniz?[/]", defaultValue: false);
        if (!continueWithTranslation)
        {
            return 0;
        }
    }
    else if (mainChoice.StartsWith("📦") || mainChoice.StartsWith("3"))
    {
        var targetBook = ResolveBookPath(null);
        if (!string.IsNullOrWhiteSpace(targetBook) && (Directory.Exists(targetBook) || File.Exists(targetBook)))
        {
            var epubPackage = EpubManifestReader.Read(targetBook);
            RunPackaging(epubPackage);
        }
        return 0;
    }
}

// 1. Dinamik Kitap Yolu ve CLI Parametreleri
string? pathArg = null;
string? explicitOutputPath = null;
var isFresh = false;
var isTestMode = false;
int? requestedSectionCount = null;
int concurrency = 3;
bool hasExplicitConcurrency = false;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.StartsWith("-"))
    {
        if ((arg.Equals("--concurrency", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("-j", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
        {
            hasExplicitConcurrency = true;
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
    }
    else if (pathArg == null)
    {
        pathArg = arg.Trim('"', '\'');
    }
}

string bookPath = ResolveBookPath(pathArg);

if (string.IsNullOrWhiteSpace(bookPath) || (!Directory.Exists(bookPath) && !File.Exists(bookPath)))
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Geçerli bir kitap yolu belirtilmedi veya bulunamadı: [dim]{bookPath}[/]");
    AnsiConsole.MarkupLine("[yellow]Kullanım:[/] glossator <kitap-klasörü-veya-.epub> [-o çıktı.epub] [--fresh] [--test]");
    return 1;
}

// 2. Graceful Shutdown & Cancellation Yönetimi (Ctrl+C / SIGINT)
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    AnsiConsole.MarkupLine("\n[bold yellow]⚠ İptal sinyali (Ctrl+C) alındı. Devam eden istekler güvenle sonlandırılıyor...[/]");
    cts.Cancel();
};

try
{
    // 3. EPUB Paketini Oku & Bilgileri Göster
    var package = EpubManifestReader.Read(bookPath);

    var summaryTable = new Table().Border(TableBorder.Rounded);
    summaryTable.AddColumn("[bold]Özellik[/]");
    summaryTable.AddColumn("[bold]Değer[/]");
    summaryTable.AddRow("Kitap Başlığı", $"[cyan]{Markup.Escape(package.Title)}[/]");
    summaryTable.AddRow("Yazar", $"[green]{Markup.Escape(package.Author ?? "Bilinmiyor")}[/]");
    summaryTable.AddRow("Bölüm Sayısı (Spine)", $"{package.Spine.Count} bölüm");
    summaryTable.AddRow("Sözlük (Glossary)", package.GlossaryItem != null ? $"[green]✔ {package.GlossaryItem.Href}[/]" : "[dim]Yok[/]");
    summaryTable.AddRow("Dizin (Index)", package.IndexItem != null ? $"[green]✔ {package.IndexItem.Href}[/]" : "[dim]Yok[/]");
    summaryTable.AddRow("Eşzamanlılık (Worker)", hasExplicitConcurrency ? $"[yellow]{concurrency}[/] paralel iş parçacığı" : "[dim]Bölüm seçiminde belirlenecek[/]");
    AnsiConsole.Write(summaryTable);

    // 4. Modern .NET 10 HostApplicationBuilder ve DI Altyapısı
    var hostBuilder = Host.CreateApplicationBuilder(args);
    hostBuilder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables();

    var config = hostBuilder.Configuration;

    // OpenTelemetry Kurulumu
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    Environment.SetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");

    var otelEndpoint = config["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";
    var serviceName = config["OpenTelemetry:ServiceName"] ?? "Glossator.Cli";

    using var tracerProvider = Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
        .AddSource(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
        .AddSource("Microsoft.Agents.*")
        .AddSource("Microsoft.Agents.AI.Workflows*")
        .AddSource("Experimental.Microsoft.Extensions.AI*")
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri(otelEndpoint);
            opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        })
        .Build();

    using var meterProvider = Sdk.CreateMeterProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
        .AddMeter(Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName)
        .AddMeter("Microsoft.Agents.*")
        .AddMeter("Microsoft.Agents.AI.Workflows*")
        .AddMeter("Experimental.Microsoft.Extensions.AI*")
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri(otelEndpoint);
            opt.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        })
        .Build();

    AnsiConsole.MarkupLine($"[cyan]📡 OpenTelemetry Aktif:[/] OTLP Hedefi: [dim]{otelEndpoint}[/] (Aspire Dashboard: http://localhost:18888)");

    // API Yapılandırması ve Güvenlik Denetimi
    var aiSection = config.GetSection("GlossatorAi");
    var aiOptions = aiSection.Get<GlossatorAiOptions>() ?? new GlossatorAiOptions();

    foreach (var (providerName, providerConfig) in aiOptions.Providers)
    {
        if (string.IsNullOrWhiteSpace(providerConfig.ApiKey) || providerConfig.ApiKey.Contains("YOUR_"))
        {
            if (!Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine($"\n[bold yellow]⚠ '{providerName}' için API Key bulunamadı veya yer tutucu durumunda.[/]");
                providerConfig.ApiKey = AnsiConsole.Prompt(
                    new TextPrompt<string>($"[green]Lütfen {providerName} API Anahtarınızı girin:[/] ")
                        .Secret());
            }
        }
    }

    // SQLite Veri Tabanı ve Sözlük Başlatma
    var dbPath = Path.Combine(Environment.CurrentDirectory, "glossator.db");
    var connectionFactory = new SqliteConnectionFactory(dbPath);
    var repo = new SqliteGlossaryRepository(connectionFactory);

    var glossaryTerms = EpubGlossaryReader.Read(package);
    var indexTerms = EpubIndexReader.Read(package);
    AnsiConsole.MarkupLine($"\n[yellow]EPUB Kaynaklarından Çıkarılan Terimler:[/] Sözlük: [green]{glossaryTerms.Count}[/] | Dizin: [green]{indexTerms.Count}[/]");

    await repo.ImportTermsAsync(glossaryTerms, package.Title, cts.Token);
    await repo.ImportTermsAsync(indexTerms, package.Title, cts.Token);
    await repo.SeedDefaultTurkishTranslationsAsync(cts.Token);

    var allTerms = repo.GetAllTerms();
    RenderGlossaryInventory(allTerms);

    if (!Console.IsInputRedirected)
    {
        var openGlossaryHub = AnsiConsole.Confirm(
            "\n[bold yellow]Çeviriye başlamadan önce sözlüğü incelemek, yeni terim eklemek veya politikaları düzenlemek ister misiniz?[/]",
            defaultValue: false);

        if (openGlossaryHub)
        {
            await GlossaryInteractiveHub.RunAsync(repo, new GlossarySerializationService(), cts.Token);
            allTerms = repo.GetAllTerms();
            AnsiConsole.MarkupLine($"\n[bold green]✔ Sözlük güncellendi. Toplam {allTerms.Count} terim ile işleme devam ediliyor.[/]\n");
        }
    }

    // Checkpoint Channel Kuyruğu & Tekil Arka Plan Yazıcısı (Single-Writer Micro-Batching)
    var checkpointQueue = new CheckpointQueue();
    await using var checkpointWriter = new CheckpointBackgroundWriter(checkpointQueue, connectionFactory);

    // Servis Bağımlılıkları (.NET 10 DI)
    hostBuilder.Services.AddSingleton<ISqliteConnectionFactory>(connectionFactory);
    hostBuilder.Services.AddSingleton(repo);
    hostBuilder.Services.AddSingleton<ICheckpointQueue>(checkpointQueue);
    hostBuilder.Services.AddSingleton(checkpointWriter);

    // Per-chapter Scoped dependencies
    hostBuilder.Services.AddScoped(_ => new TermUsageTracker("default"));
    hostBuilder.Services.AddScoped<TermGlossingPostProcessor>();
    hostBuilder.Services.AddSingleton<ParallelLocalizationEngine>();

    hostBuilder.Services.Configure<GlossatorAiOptions>(opts =>
    {
        opts.Providers = aiOptions.Providers;
        opts.Agents = aiOptions.Agents;
    });
    hostBuilder.Services.AddGlossatorAiClients();

    using var host = hostBuilder.Build();

    // 5. Bölüm Seçim Menüsü (Çoklu Seçim Checkbox, Aralık, Tek veya Tümü)
    var translatableDocuments = package.Spine.Where(s => s.IsDocument).ToList();
    List<EpubItem> selectedChapters = [];

    if (!Console.IsInputRedirected)
    {
        var selectionMode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold green]Lütfen çevirmek istediğiniz bölüm seçim modunu belirleyin:[/]")
                .PageSize(10)
                .AddChoices(
                    "☑️  1. Çoklu Bölüm Seçimi (Boşluk tuşu ile işaretleyin / kaldırın)",
                    $"📚 2. Tüm Kitabı Çevir (Tüm {translatableDocuments.Count} Bölüm)",
                    "🎯 3. Tek Bir Bölüm Seç (Hızlı Test)",
                    "🔢 4. Bölüm Numarası veya Aralığı Gir (Örn: 1-5 veya 2,4,6)"));

        if (selectionMode.StartsWith("☑️") || selectionMode.Contains("1."))
        {
            while (selectedChapters.Count == 0)
            {
                var multiPrompt = new MultiSelectionPrompt<EpubItem>()
                    .Title("[bold green]Çevirmek istediğiniz bölümleri seçin:[/] [dim](Boşluk: Seç/Kaldır, Enter: Onayla)[/]")
                    .PageSize(15)
                    .UseConverter(item => $"{Path.GetFileName(item.Href)} - {GetChapterFriendlyName(item)}")
                    .AddChoices(translatableDocuments);

                selectedChapters = AnsiConsole.Prompt(multiPrompt);

                if (selectedChapters.Count == 0)
                {
                    AnsiConsole.MarkupLine("[bold red]⚠ En az bir bölüm seçmelisiniz![/]");
                }
            }
        }
        else if (selectionMode.StartsWith("📚") || selectionMode.Contains("2."))
        {
            selectedChapters = translatableDocuments;
        }
        else if (selectionMode.StartsWith("🎯") || selectionMode.Contains("3."))
        {
            var singlePrompt = new SelectionPrompt<EpubItem>()
                .Title("[bold green]Çevirmek istediğiniz tek bölümü seçin:[/]")
                .PageSize(15)
                .UseConverter(item => $"{Path.GetFileName(item.Href)} - {GetChapterFriendlyName(item)}")
                .AddChoices(translatableDocuments);

            selectedChapters = [AnsiConsole.Prompt(singlePrompt)];
        }
        else if (selectionMode.StartsWith("🔢") || selectionMode.Contains("4."))
        {
            selectedChapters = PromptChapterRange(translatableDocuments);
        }
    }
    else
    {
        var def = translatableDocuments.FirstOrDefault() ?? package.Spine.First();
        selectedChapters = [def];
    }

    // 6. Dinamik Eşzamanlılık (Worker Pool Boyutu) Belirleme
    if (!hasExplicitConcurrency && !Console.IsInputRedirected)
    {
        if (selectedChapters.Count == 1)
        {
            concurrency = 1;
        }
        else
        {
            var maxConcurrency = Math.Min(selectedChapters.Count, Environment.ProcessorCount * 2);
            var defaultConcurrency = Math.Clamp(Math.Min(selectedChapters.Count, 3), 1, Environment.ProcessorCount);

            concurrency = AnsiConsole.Prompt(
                new TextPrompt<int>($"[bold yellow]Kaç paralel işçi (worker thread) çalışsın?[/] [dim](1 ile {maxConcurrency} arası, Varsayılan: {defaultConcurrency}):[/] ")
                    .DefaultValue(defaultConcurrency)
                    .Validate(val => val >= 1 && val <= Environment.ProcessorCount * 2
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Lütfen 1 ile {Environment.ProcessorCount * 2} arasında bir değer girin.")));

            concurrency = Math.Clamp(concurrency, 1, selectedChapters.Count);
        }
    }
    else
    {
        concurrency = Math.Clamp(concurrency, 1, Math.Max(1, selectedChapters.Count));
    }

    // Ön-Başlatma Doğrulama ve Onay Tablosu
    var preLaunchTable = new Table().Border(TableBorder.DoubleEdge);
    preLaunchTable.Title("[bold green]🚀 Yerelleştirme Boru Hattı Başlatma Özeti[/]");
    preLaunchTable.AddColumn(new TableColumn("[bold]Parametre[/]"));
    preLaunchTable.AddColumn(new TableColumn("[bold]Değer[/]"));

    preLaunchTable.AddRow("Kitap", $"[bold cyan]{Markup.Escape(package.Title)}[/]");
    preLaunchTable.AddRow("Seçilen Bölüm Sayısı", $"[bold yellow]{selectedChapters.Count}[/] adet");
    preLaunchTable.AddRow("Bölümler", $"[dim]{string.Join(", ", selectedChapters.Take(4).Select(c => Path.GetFileName(c.Href)))}{(selectedChapters.Count > 4 ? $" ... (+{selectedChapters.Count - 4} daha)" : "")}[/]");
    preLaunchTable.AddRow("Paralel İşçi (Worker)", $"[bold green]{concurrency}[/] iş parçacığı");
    preLaunchTable.AddRow("Sözlük Durumu", $"[bold cyan]{allTerms.Count}[/] terim (3-Tier Politika)");
    preLaunchTable.AddRow("Çalışma Modu", isTestMode ? "[yellow]Test Modu (Kısıtlı Bölüm)[/]" : (isFresh ? "[magenta]Fresh Modu (Sıfırdan)[/]" : "[green]Checkpoint'ten Devam Eden Mod[/]"));

    AnsiConsole.Write(preLaunchTable);

    if (!Console.IsInputRedirected)
    {
        var proceed = AnsiConsole.Confirm("\n[bold green]Yerelleştirme motoru bu ayarlarla başlatılsın mı?[/]", defaultValue: true);
        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]İşlem kullanıcı tarafından iptal edildi.[/]");
            return 0;
        }
    }

    // 7. Seçilen Bölümleri Paralel İşçi Havuzu ile İşle
    AnsiConsole.MarkupLine($"\n[bold cyan]🚀 Paralel İşçi Havuzu Başlatılıyor: [yellow]{selectedChapters.Count}[/] Bölüm, [green]{concurrency}[/] Worker...[/]\n");
    var engine = host.Services.GetRequiredService<ParallelLocalizationEngine>();
    await engine.RunAsync(package, selectedChapters, concurrency, isFresh, requestedSectionCount, isTestMode, cts.Token);

    tracerProvider.ForceFlush();
    meterProvider.ForceFlush();

    // 7. Bitiş Özeti ve Paketleme
    AnsiConsole.MarkupLine("\n[bold green]🎉 Hedeflenen tüm belgeler başarıyla işlendi ve kaydedildi![/]");
    if (!Console.IsInputRedirected)
    {
        var packageConfirmation = AnsiConsole.Confirm("\n[bold yellow]Çevrilen kitabı şimdi standart EPUB olarak paketlemek ister misiniz?[/]", defaultValue: true);
        if (packageConfirmation)
        {
            RunPackaging(package, explicitOutputPath);
        }

        AnsiConsole.MarkupLine("[dim]Çıkmak için Enter'a basın...[/]");
        Console.ReadLine();
    }

    return 0;
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("\n[bold red]İşlem kullanıcı tarafından durduruldu (Ctrl+C).[/]");
    AnsiConsole.MarkupLine("[dim]In-flight LLM HTTP istekleri derhal kesildi, token yakılması önlendi, veritabanı tutarlılığı korundu.[/]\n");
    return 130;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] {ex.Message}");
    AnsiConsole.WriteException(ex);
    return 1;
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
    AnsiConsole.MarkupLine("\n[bold cyan]📦 EPUB Paketleme Motoru Çalıştırılıyor...[/]");
    var translations = EpubPackager.DiscoverTranslations(package);
    AnsiConsole.MarkupLine($"[green]✔ Bulunan Çevrilmiş Dosya:[/] [bold cyan]{translations.Count}[/] adet");

    if (translations.Count == 0)
    {
        AnsiConsole.MarkupLine("[bold yellow]⚠ Uyarı:[/] Henüz çevrilmiş (*_tr.html) dosya bulunamadı! Lütfen önce bölümleri çevirin.");
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
        .Header("[bold green]🎉 EPUB PAKETLEME BAŞARIYLA TAMAMLANDI[/]")
        .BorderColor(Color.Green));

    AnsiConsole.MarkupLine($"\n[bold green]✔ Kitabınız tabletinizde (Apple Books / Kindle / Kobo / Calibre) okunmaya hazır![/]\n");
}

static string GetChapterFriendlyName(EpubItem item)
{
    if (!string.IsNullOrWhiteSpace(item.Title))
        return item.Title;

    return Path.GetFileNameWithoutExtension(item.Href);
}

static void RenderGlossaryInventory(List<GlossaryEntry> terms)
{
    var total = terms.Count;
    var progressive = terms.Count(t => t.Policy == GlossingPolicy.ProgressiveGloss);
    var alwaysEn = terms.Count(t => t.Policy == GlossingPolicy.AlwaysEnglish);
    var alwaysTr = terms.Count(t => t.Policy == GlossingPolicy.AlwaysTurkish);
    var reviewed = terms.Count(t => t.IsReviewed);

    var statsTable = new Table().Border(TableBorder.Rounded);
    statsTable.Title($"[bold cyan]📚 Sözlük ve Terim Envanteri (Toplam: {total} Terim - SQLite)[/]");
    statsTable.AddColumn(new TableColumn("[bold]Politika / Kategori[/]"));
    statsTable.AddColumn(new TableColumn("[bold]Terim Sayısı[/]").RightAligned());
    statsTable.AddColumn(new TableColumn("[bold]Dağılım (%)[/]").RightAligned());
    statsTable.AddColumn(new TableColumn("[bold]Yerelleştirme Davranışı[/]"));

    statsTable.AddRow(
        "[cyan]ProgressiveGloss[/]",
        $"[bold cyan]{progressive}[/]",
        total > 0 ? $"{progressive * 100.0 / total:F1}%" : "0%",
        "İlk geçişte çift dilli [[EN (TR)]], sonraki geçişlerde Türkçe");
    statsTable.AddRow(
        "[magenta]AlwaysEnglish[/]",
        $"[bold magenta]{alwaysEn}[/]",
        total > 0 ? $"{alwaysEn * 100.0 / total:F1}%" : "0%",
        "Kod, API veya endüstri standardı (orijinal dilde korunur)");
    statsTable.AddRow(
        "[green]AlwaysTurkish[/]",
        $"[bold green]{alwaysTr}[/]",
        total > 0 ? $"{alwaysTr * 100.0 / total:F1}%" : "0%",
        "Yerleşik Türkçe karşılığı kullanılır (çekim eki uyumlu)");
    statsTable.AddRow(
        "[dim]İncelenmiş / Onaylı[/]",
        $"[dim]{reviewed}[/]",
        total > 0 ? $"{reviewed * 100.0 / total:F1}%" : "0%",
        "Kullanıcı veya sözlük kurallarınca onaylanmış terimler");

    AnsiConsole.Write(statsTable);

    var sampleTerms = terms.Take(10).ToList();
    if (sampleTerms.Count > 0)
    {
        var sampleTable = new Table().Border(TableBorder.SimpleHeavy);
        sampleTable.Title("[bold yellow]🔍 Örnek Terimler ve Politika Eşleşmeleri (İlk 10 Kayıt)[/]");
        sampleTable.AddColumn(new TableColumn("[dim]ID[/]").RightAligned());
        sampleTable.AddColumn(new TableColumn("[bold]İngilizce Terim[/]"));
        sampleTable.AddColumn(new TableColumn("[bold]Türkçe Karşılık[/]"));
        sampleTable.AddColumn(new TableColumn("[bold]Politika[/]"));
        sampleTable.AddColumn(new TableColumn("[bold]Durum[/]").Centered());

        foreach (var t in sampleTerms)
        {
            var pColor = t.Policy switch
            {
                GlossingPolicy.AlwaysEnglish => "magenta",
                GlossingPolicy.AlwaysTurkish => "green",
                _ => "cyan"
            };

            sampleTable.AddRow(
                $"[dim]#{t.Id}[/]",
                $"[bold white]{Markup.Escape(t.EnglishTerm)}[/]",
                t.TurkishTerm != null ? $"[{pColor}]{Markup.Escape(t.TurkishTerm)}[/]" : "[dim]- (Kural Bekliyor)[/]",
                $"[{pColor}]{t.Policy}[/]",
                t.IsReviewed ? "[green]✔ Onaylı[/]" : "[yellow]⚠ Ham[/]"
            );
        }

        AnsiConsole.Write(sampleTable);
    }
}

static List<EpubItem> PromptChapterRange(List<EpubItem> documents)
{
    while (true)
    {
        AnsiConsole.MarkupLine("\n[dim]Mevcut Bölümler:[/]");
        for (int idx = 0; idx < documents.Count; idx++)
        {
            var doc = documents[idx];
            AnsiConsole.MarkupLine($"  [yellow]{idx + 1:D2}.[/] [cyan]{Path.GetFileName(doc.Href)}[/] - [dim]{GetChapterFriendlyName(doc)}[/]");
        }

        var input = AnsiConsole.Ask<string>("\n[bold yellow]Bölüm aralığı veya numaraları girin (Örn: 1-3 veya 1,3,5):[/] ").Trim();
        var selectedIndices = new HashSet<int>();

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        AnsiConsole.MarkupLine("[bold red]⚠ Geçersiz aralık veya numara formatı. Lütfen tekrar deneyin.[/]");
    }
}
