using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Parsing;
using Glossator.Core.Packaging;
using Glossator.Core.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Spectre.Console;

AnsiConsole.Write(
    new FigletText("GLOSSATOR")
        .Color(Color.Cyan1));

AnsiConsole.MarkupLine("[bold cyan]Microsoft Agent Framework (.NET 10) Tabanlı Kitap Yerelleştirme Boru Hattı[/]\n");

// 1. Dinamik Kitap Yolu Alma
string bookPath;
const string defaultPath = @"C:\Books\Sample\OEBPS";

string? pathArg = null;
string? explicitOutputPath = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("-"))
    {
        if ((args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) ||
             args[i].Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
        {
            explicitOutputPath = args[++i].Trim('"', '\'');
            continue;
        }

        if (args[i].Equals("--count", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("--chapter", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-c", StringComparison.OrdinalIgnoreCase))
        {
            i++; // skip count or chapter argument value
        }
        continue;
    }
    if (pathArg == null)
    {
        pathArg = args[i];
    }
}

if (!string.IsNullOrWhiteSpace(pathArg))
{
    bookPath = pathArg.Trim('"', '\'');
}
else if (Directory.Exists(defaultPath))
{
    bookPath = defaultPath;
    AnsiConsole.MarkupLine($"[green]Varsayılan Kitap Yolu Seçildi:[/] [dim]{bookPath}[/]");
}
else
{
    bookPath = AnsiConsole.Ask<string>("[yellow]Lütfen Kitap klasörünü veya .epub yolunu girin:[/] ")
                          .Trim('"', '\'');
}

if (!Directory.Exists(bookPath) && !File.Exists(bookPath))
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Belirtilen yol bulunamadı: [dim]{bookPath}[/]");
    return;
}

try
{
    // 2. EPUB Paketini Oku & Bilgileri Göster
    var package = EpubManifestReader.Read(bookPath);

    var summaryTable = new Table().Border(TableBorder.Rounded);
    summaryTable.AddColumn("[bold]Özellik[/]");
    summaryTable.AddColumn("[bold]Değer[/]");
    summaryTable.AddRow("Kitap Başlığı", $"[cyan]{package.Title}[/]");
    summaryTable.AddRow("Yazar", $"[green]{package.Author ?? "Bilinmiyor"}[/]");
    summaryTable.AddRow("Bölüm Sayısı (Spine)", $"{package.Spine.Count} bölüm");
    summaryTable.AddRow("Sözlük (Glossary)", package.GlossaryItem != null ? $"[green]✔ {package.GlossaryItem.Href}[/]" : "[dim]Yok[/]");
    summaryTable.AddRow("Dizin (Index)", package.IndexItem != null ? $"[green]✔ {package.IndexItem.Href}[/]" : "[dim]Yok[/]");
    AnsiConsole.Write(summaryTable);

    // 3. Terimleri Çıkar & SQLite'a Aktar
    var glossaryTerms = EpubGlossaryReader.Read(package);
    var indexTerms = EpubIndexReader.Read(package);
    AnsiConsole.MarkupLine($"\n[yellow]Çıkarılan Terimler:[/] Sözlük: [green]{glossaryTerms.Count}[/] | Dizin: [green]{indexTerms.Count}[/]");

    var dbPath = Path.Combine(Environment.CurrentDirectory, "glossator.db");
    var repo = new SqliteGlossaryRepository(dbPath);
    repo.ImportTerms(glossaryTerms, package.Title);
    repo.ImportTerms(indexTerms, package.Title);
    repo.SeedDefaultTurkishTranslations();

    var allTerms = repo.GetAllTerms();
    AnsiConsole.MarkupLine($"[bold green]✔ SQLite ({dbPath}): Toplam {allTerms.Count} terim hazır (3-Tier Politika Aktif).[/]\n");

    // 4. OpenTelemetry (Aspire Dashboard & OTLP Uyumlu) Kurulumu
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

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
            opt.BatchExportProcessorOptions = new OpenTelemetry.BatchExportProcessorOptions<System.Diagnostics.Activity>
            {
                ScheduledDelayMilliseconds = 1000
            };
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

    // 5. API Ayarlarını Yükle ve Kontrol Et
    var aiSection = config.GetSection("GlossatorAi");
    var aiOptions = aiSection.Get<GlossatorAiOptions>() ?? new GlossatorAiOptions();

    foreach (var (providerName, providerConfig) in aiOptions.Providers)
    {
        if (string.IsNullOrWhiteSpace(providerConfig.ApiKey) || providerConfig.ApiKey.Contains("YOUR_"))
        {
            if (!Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine($"\n[bold yellow]⚠ appsettings.json dosyasında '{providerName}' için API Key bulunamadı.[/]");
                providerConfig.ApiKey = AnsiConsole.Prompt(
                    new TextPrompt<string>($"[green]Lütfen {providerName} API Anahtarınızı girin:[/] ")
                        .Secret());
            }
        }
    }

    // 6. DI ve BookLocalizationPipeline Kurulumu (.NET 10 Standartları)
    var firstChapterHref = package.Chapters.FirstOrDefault()?.Href
                           ?? package.Spine.FirstOrDefault(s => s.IsDocument)?.Href
                           ?? "first_chapter.html";
    var tracker = new TermUsageTracker(firstChapterHref);
    var postProcessor = new TermGlossingPostProcessor(repo, tracker);

    var services = new ServiceCollection();
    services.AddSingleton(repo);
    services.AddSingleton(tracker);
    services.AddSingleton(postProcessor);

    // Microsoft Learn IOptions<T> Standardı
    services.Configure<GlossatorAiOptions>(opts =>
    {
        opts.Providers = aiOptions.Providers;
        opts.Agents = aiOptions.Agents;
    });
    services.AddGlossatorAiClients();

    var serviceProvider = services.BuildServiceProvider();
    var pipeline = await serviceProvider.CreateBookLocalizationPipelineAsync();

    // 7. Canlı Ajan Akışı Event Dinleyicisi (Konsol Raporlama)
    pipeline.StepProgress += log =>
    {
        switch (log.Stage)
        {
            case "StyleAnalyst":
                AnsiConsole.Write(new Panel(
                    new Markup($"[bold cyan]{Markup.Escape(log.Summary)}[/]\n\n[dim]{Markup.Escape(log.Content ?? "")}[/]"))
                    .Header("[bold cyan]🎨 1. ADIM: YAZAR ÜSLUP & TON ANALİZİ (StyleAnalyst)[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Cyan1));
                break;

            case "PreProcessor":
                AnsiConsole.Write(new Panel(
                    new Markup($"[bold yellow]{Markup.Escape(log.Summary)}[/]\n\n[yellow]Dokunulmaz Terimler:[/] {Markup.Escape(log.Content ?? "")}"))
                    .Header("[bold yellow]🛡 2. ADIM: DOKUNULMAZ TERİM AYIKLAMA (SQLite Pre-Processor)[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
                break;

            case "Translator":
                AnsiConsole.Write(new Panel(
                    new Markup($"[bold blue]{Markup.Escape(log.Summary)}[/]\n\n{Markup.Escape(log.Content ?? "")}"))
                    .Header($"[bold blue]✍ 3. ADIM: ÇEVİRMEN AJAN (Translator) - Tur {log.Attempt}[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Blue));
                break;

            case "Reviewer":
                if (log.Summary.Contains("İhlali", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.Write(new Panel(
                        new Markup($"[bold red]{Markup.Escape(log.Summary)}[/]\n\n[red]{Markup.Escape(log.Content ?? "")}[/]"))
                        .Header($"[bold red]🔄 4. ADIM: DÖNGÜSEL İTİRAZ (Reviewer -> Translator Geri Bildirimi) - Tur {log.Attempt}[/]")
                        .Border(BoxBorder.Heavy)
                        .BorderColor(Color.Red));
                }
                else
                {
                    AnsiConsole.Write(new Panel(
                        new Markup($"[bold green]{Markup.Escape(log.Summary)}[/]\n\n{Markup.Escape(log.Content ?? "")}"))
                        .Header($"[bold green]✔ 4. ADIM: DENETÇİ AJAN (Reviewer Onaylandı)[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Green));
                }
                break;

            case "PostProcessor":
                AnsiConsole.Write(new Panel(
                    new Markup($"[bold magenta]{Markup.Escape(log.Summary)}[/]\n\n[bold white]{Markup.Escape(log.Content ?? "")}[/]"))
                    .Header("[bold magenta]✨ 5. ADIM: PEDAGOJİK SAYAÇ & TÜRKÇE PARANTEZ ENJEKSİYONU[/]")
                    .Border(BoxBorder.Double)
                    .BorderColor(Color.Magenta1));
                break;

            case "RateLimiter":
                AnsiConsole.MarkupLine($"\n[bold yellow]⏳ {Markup.Escape(log.Summary)}[/]\n");
                break;
        }
    };

    // Paketleme bayrağı kontrolü (--package veya -p)
    var isPackageOnly = args.Any(a => a.Equals("--package", StringComparison.OrdinalIgnoreCase) || a.Equals("-p", StringComparison.OrdinalIgnoreCase));
    if (isPackageOnly)
    {
        RunPackaging(package, explicitOutputPath);
        return;
    }

    // 8. Kitaptaki Çevrilebilir Belgeleri Evrensel Olarak Tespit Et
    // (Alfabetik Index ve Navigation dosyaları LLM çevirisinden muaf tutulur)
    var translatableDocuments = package.Spine
        .Where(s => s.IsDocument && s.Role != EpubDocumentRole.Index && s.Role != EpubDocumentRole.Navigation)
        .ToList();

    var mainChapters = translatableDocuments.Where(s => s.Role == EpubDocumentRole.Chapter).ToList();
    var auxDocuments = translatableDocuments.Where(s => s.Role == EpubDocumentRole.Auxiliary).ToList();

    var isAllChapters = args.Any(a => a.Equals("--all", StringComparison.OrdinalIgnoreCase) || a.Equals("--all-chapters", StringComparison.OrdinalIgnoreCase));
    var isAuxOnly = args.Any(a => a.Equals("--aux", StringComparison.OrdinalIgnoreCase));
    var chapterArgIndex = Array.FindIndex(args, a => a.Equals("--chapter", StringComparison.OrdinalIgnoreCase) || a.Equals("-c", StringComparison.OrdinalIgnoreCase));
    string? requestedChapter = null;
    if (chapterArgIndex >= 0 && chapterArgIndex + 1 < args.Length)
    {
        requestedChapter = args[chapterArgIndex + 1];
    }

    List<EpubItem> selectedChapters;

    if (isAllChapters)
    {
        selectedChapters = translatableDocuments;
        AnsiConsole.MarkupLine($"\n[bold green]📚 TÜM KİTAP MODU: Toplam {selectedChapters.Count} belge sırayla çevrilecek![/]");
    }
    else if (isAuxOnly)
    {
        selectedChapters = auxDocuments;
        AnsiConsole.MarkupLine($"\n[bold yellow]📚 EKLER MODU: Toplam {selectedChapters.Count} ek belge (Sözlük, Önsöz vb.) çevrilecek![/]");
    }
    else if (!string.IsNullOrWhiteSpace(requestedChapter))
    {
        var matched = FindChapter(translatableDocuments, requestedChapter);
        if (matched == null)
        {
            AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] '{requestedChapter}' ile eşleşen bir belge bulunamadı!");
            AnsiConsole.MarkupLine("[yellow]Mevcut Belgeler:[/] " + string.Join(", ", translatableDocuments.Select(c => c.Href)));
            return;
        }
        selectedChapters = [matched];
        AnsiConsole.MarkupLine($"\n[bold green]🎯 Seçilen Belge:[/] [cyan]{Markup.Escape(matched.Href)}[/] ({Markup.Escape(GetChapterFriendlyName(matched))})");
    }
    else if (!Console.IsInputRedirected)
    {
        // İnteraktif Menü (Spectre.Console SelectionPrompt)
        var menuMap = new Dictionary<string, List<EpubItem>>();
        const string packageKey = "📦 [bold green]ÇEVRİLEN KİTABI EPUB OLARAK PAKETLE[/] (Tablete Hazır .epub Üret)";
        const string allKey = "🚀 [bold cyan]TÜM KİTABI SIRAYLA ÇEVİR[/] (Bölümler + Ekler)";
        const string mainKey = "📖 [bold blue]SADECE ANA BÖLÜMLERİ ÇEVİR[/]";
        const string auxKey = "📚 [bold yellow]SÖZLÜK VE EKLERİ ÇEVİR[/] (Glossary, Colophon, Önsöz vb.)";

        menuMap[packageKey] = [];
        menuMap[allKey] = translatableDocuments;
        if (mainChapters.Count > 0) menuMap[mainKey] = mainChapters;
        if (auxDocuments.Count > 0) menuMap[auxKey] = auxDocuments;

        var prompt = new SelectionPrompt<string>()
            .Title("\n[bold yellow]Lütfen gerçekleştirmek istediğiniz işlemi seçin:[/]")
            .PageSize(20)
            .AddChoices(packageKey, allKey);

        if (mainChapters.Count > 0) prompt.AddChoice(mainKey);
        if (auxDocuments.Count > 0) prompt.AddChoice(auxKey);

        // Her bir belgeyi menüye tekil olarak da ekle
        foreach (var docItem in translatableDocuments)
        {
            var fileName = Path.GetFileName(docItem.Href);
            var cps = await repo.GetCompletedCheckpointsAsync(fileName);
            var completedCount = cps.Count(kv => kv.Key >= 0);
            var badge = completedCount > 0
                ? $"[green]({completedCount} blok önbellekte)[/]"
                : "[dim](Başlanmadı)[/]";
            var typeTag = docItem.Role == EpubDocumentRole.Auxiliary ? "[yellow](EK)[/]" : "[blue](BÖLÜM)[/]";
            var friendlyTitle = Markup.Escape(GetChapterFriendlyName(docItem));
            var safeHref = Markup.Escape(docItem.Href);
            var label = $"{typeTag} {safeHref,-16} {friendlyTitle,-35} {badge}";
            menuMap[label] = [docItem];
            prompt.AddChoice(label);
        }

        var selectedChoice = AnsiConsole.Prompt(prompt);
        if (selectedChoice == packageKey)
        {
            RunPackaging(package, explicitOutputPath);
            return;
        }

        selectedChapters = menuMap[selectedChoice];
    }
    else
    {
        // Otomasyon / Redirected console
        var def = translatableDocuments.FirstOrDefault() ?? package.Spine.First();
        selectedChapters = [def];
    }

    var isFresh = args.Any(a => a.Equals("--fresh", StringComparison.OrdinalIgnoreCase));
    var isTestMode = args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase));
    var countArgIndex = Array.FindIndex(args, a => a.Equals("--count", StringComparison.OrdinalIgnoreCase));
    int? requestedSectionCount = null;
    if (countArgIndex >= 0 && countArgIndex + 1 < args.Length && int.TryParse(args[countArgIndex + 1], out var parsedCount))
    {
        requestedSectionCount = parsedCount;
    }

    // 9. Seçilen Bölümleri Sırayla İşle
    for (int chIndex = 0; chIndex < selectedChapters.Count; chIndex++)
    {
        var chapterItem = selectedChapters[chIndex];
        var chapterFullPath = Path.Combine(package.OpfDirectory, chapterItem.Href);
        var chapterFileName = Path.GetFileName(chapterFullPath);
        var chapterKey = Path.GetFileNameWithoutExtension(chapterFileName);
        var outputFileName = $"{chapterKey}_tr.html";
        var outputFilePath = Path.Combine(package.OpfDirectory, outputFileName);

        AnsiConsole.MarkupLine($"\n[bold white on darkblue] ══════════════════════════════════════════════════════════════════════ [/]");
        AnsiConsole.MarkupLine($"[bold white on darkblue] 📖 BÖLÜM {chIndex + 1} / {selectedChapters.Count}: {Markup.Escape(chapterFileName)} ({Markup.Escape(GetChapterFriendlyName(chapterItem))}) [/]");
        AnsiConsole.MarkupLine($"[bold white on darkblue] ══════════════════════════════════════════════════════════════════════ [/]");

        var (document, segments) = HtmlSegmenter.SegmentFile(chapterFullPath);
        AnsiConsole.MarkupLine($"[green]✔ Ayrıştırılan Metin Bloğu:[/] [bold cyan]{segments.Count}[/] adet");

        var allSections = HtmlSectionGrouper.Group(segments, chapterKey);
        AnsiConsole.MarkupLine($"[cyan]📑 Konu Başlığı Mimarisi:[/] {segments.Count} blok toplam [bold green]{allSections.Count}[/] mantıksal bölüme (Section) gruplandı.");

        List<HtmlSection> targetSections;
        if (requestedSectionCount.HasValue)
        {
            targetSections = allSections.Take(requestedSectionCount.Value).ToList();
        }
        else if (isTestMode)
        {
            // Yalnızca açıkça --test parametresi verilirse ilk 2 section ile sınırla
            targetSections = allSections.Take(2).ToList();
        }
        else
        {
            // Varsayılan: Seçilen bölümün TAMAMI çevrilir!
            targetSections = allSections;
        }

        var targetBlockCount = targetSections.Sum(s => s.Segments.Count);
        if (targetSections.Count >= allSections.Count)
        {
            AnsiConsole.MarkupLine($"[bold green]🚀 TAM BÖLÜM MODU: TÜM {allSections.Count} KONU BÖLÜMÜ ({segments.Count} BLOK) İŞLENİYOR...[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold yellow]🧪 TEST MODU: İLK {targetSections.Count} KONU BÖLÜMÜ ({targetBlockCount} BLOK) İŞLENİYOR... (Tamamı için varsayılan modu kullanın)[/]");
        }

        if (isFresh)
        {
            await repo.ClearCheckpointsAsync(chapterFileName);
            AnsiConsole.MarkupLine($"[bold yellow]⚡ --fresh: {chapterFileName} için önceki checkpointler temizlendi.[/]");
        }

        var existingCheckpoints = await repo.GetCompletedCheckpointsAsync(chapterFileName);
        var cachedCount = existingCheckpoints.Count(kv => kv.Key >= 0);
        AnsiConsole.MarkupLine($"[cyan]💾 Checkpoint / Önbellek:[/] {cachedCount} blok önceden tamamlanmış.");

        // Style Analyst: Bu bölümün üslup parmak izini çıkar/yükle
        string chapterStyleGuide;
        if (!isFresh && existingCheckpoints.TryGetValue(-1, out var styleCp) && !string.IsNullOrWhiteSpace(styleCp.TranslatedText))
        {
            chapterStyleGuide = styleCp.TranslatedText;
            pipeline.SetStyleGuide(chapterStyleGuide);
            AnsiConsole.MarkupLine("[bold green]✔ Yazar Üslup & Ton Kimliği Önbellekten Yüklendi (0 ms, 0 Token)[/]");
        }
        else
        {
            var substantiveParagraphs = segments
                .Where(s => s.TagName.Equals("p", StringComparison.OrdinalIgnoreCase) && s.MaskedText.Length > 60)
                .Take(12)
                .Select(s => s.MaskedText)
                .ToList();

            if (substantiveParagraphs.Count == 0)
            {
                substantiveParagraphs = segments.Take(5).Select(s => s.MaskedText).ToList();
            }

            var chapterSampleText = string.Join("\n\n---\n\n", substantiveParagraphs);
            AnsiConsole.MarkupLine($"[cyan]🔍 Style Analyst {chapterFileName} üslup ve ton kimliğini inceliyor ({substantiveParagraphs.Count} paragraf)...[/]");
            chapterStyleGuide = await pipeline.AnalyzeStyleAsync(chapterSampleText);
            await repo.SaveCheckpointAsync(new SegmentCheckpoint
            {
                ChapterFile = chapterFileName,
                SegmentIndex = -1,
                OriginalHash = SqliteGlossaryRepository.ComputeHash(chapterSampleText),
                TagName = "styleguide",
                OriginalText = "StyleGuide",
                TranslatedText = chapterStyleGuide,
                StageStatus = "Completed",
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Section Bazlı Çeviri Döngüsü
        for (int s = 0; s < targetSections.Count; s++)
        {
            var sec = targetSections[s];

            bool allCached = sec.Segments.All(seg =>
                existingCheckpoints.TryGetValue(seg.Index, out var cp) &&
                cp.OriginalHash == SqliteGlossaryRepository.ComputeHash(seg.MaskedText) &&
                !string.IsNullOrWhiteSpace(cp.TranslatedText));

            if (allCached)
            {
                foreach (var seg in sec.Segments)
                {
                    seg.TranslatedText = existingCheckpoints[seg.Index].TranslatedText;
                    await pipeline.RegisterCachedSectionAsync(seg.MaskedText);
                }

                AnsiConsole.MarkupLine($"\n[bold white on darkgreen] === SECTION {s + 1} / {targetSections.Count} ({sec.Title}) - ÖNBELLEKTEN YÜKLENDİ ({sec.Segments.Count} Blok - 0 ms, 0 Token) === [/]");
                HtmlSegmenter.ApplyTranslations(segments);
                await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml());
                continue;
            }

            AnsiConsole.MarkupLine($"\n[bold white on blue] === SECTION {s + 1} / {targetSections.Count}: {sec.Title} ({sec.Segments.Count} Blok) === [/]");
            await pipeline.TranslateSectionAsync(sec, chapterStyleGuide);

            foreach (var seg in sec.Segments)
            {
                var currentHash = SqliteGlossaryRepository.ComputeHash(seg.MaskedText);
                var isTranslated = !string.IsNullOrWhiteSpace(seg.TranslatedText);
                if (!isTranslated)
                {
                    AnsiConsole.MarkupLine($"[bold yellow]⚠ Uyarı: Blok {seg.Index} ({seg.TagName}) model çıktısında eksik kaldı, orijinal metinle geçildi.[/]");
                }

                await repo.SaveCheckpointAsync(new SegmentCheckpoint
                {
                    ChapterFile = chapterFileName,
                    SegmentIndex = seg.Index,
                    OriginalHash = currentHash,
                    TagName = seg.TagName,
                    OriginalText = seg.MaskedText,
                    TranslatedText = isTranslated ? seg.TranslatedText! : seg.MaskedText,
                    StageStatus = isTranslated ? "Completed" : "MissingFallback",
                    UpdatedAt = DateTime.UtcNow
                });
            }

            tracerProvider.ForceFlush();
            meterProvider.ForceFlush();

            HtmlSegmenter.ApplyTranslations(segments);
            await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml());

            await Task.Delay(2000);
        }

        HtmlSegmenter.ApplyTranslations(segments);
        await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml());
        AnsiConsole.MarkupLine($"\n[bold green]✔ {chapterFileName} Yerelleştirildi ve Kaydedildi:[/] [bold cyan]{outputFilePath}[/]\n");
    }

    // 10. Bitiş Özeti
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
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] {ex.Message}");
    AnsiConsole.WriteException(ex);
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

static EpubItem? FindChapter(List<EpubItem> documents, string input)
{
    var trimmed = input.Trim().ToLowerInvariant();
    var match = documents.FirstOrDefault(c => c.Href.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    if (match != null) return match;

    match = documents.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c.Href).Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    if (match != null) return match;

    if (int.TryParse(trimmed, out var num) && num >= 1 && num <= documents.Count)
    {
        return documents[num - 1];
    }

    match = documents.FirstOrDefault(c => c.Title?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true);
    if (match != null) return match;

    return documents.FirstOrDefault(c => c.Href.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
}

static string GetChapterFriendlyName(EpubItem item)
{
    if (!string.IsNullOrWhiteSpace(item.Title))
        return item.Title;

    return Path.GetFileNameWithoutExtension(item.Href);
}
