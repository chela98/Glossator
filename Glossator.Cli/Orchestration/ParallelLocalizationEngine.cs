using System.Collections.Concurrent;
using System.Diagnostics;
using Glossator.AI.Diagnostics;
using Glossator.AI.Extensions;
using Glossator.AI.Workflows;
using Glossator.Core.Data;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Glossator.Cli.Orchestration;

/// <summary>
/// Çoklu iş parçacıklı bölüm yerelleştirme ve işçi havuzu orkestratörü.
/// Parallel.ForEachAsync ve izole IServiceScope mekanizmasıyla race condition'ları önler,
/// Spectre.Console canlı ilerleme çubuğuyla durumu raporlar.
/// </summary>
public sealed class ParallelLocalizationEngine(
    IServiceProvider rootServiceProvider,
    ILogger<ParallelLocalizationEngine>? logger = null)
{
    private readonly ILogger<ParallelLocalizationEngine> _logger = logger ?? NullLogger<ParallelLocalizationEngine>.Instance;
    public async Task RunAsync(
        EpubPackage package,
        List<EpubItem> selectedChapters,
        int concurrency,
        bool isFresh,
        int? requestedSectionCount,
        bool isTestMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(selectedChapters);

        if (selectedChapters.Count == 0)
        {
            return;
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        };

        _logger.LogInformation("Starting ParallelLocalizationEngine with {ChapterCount} chapters (Concurrency={Concurrency}, Fresh={IsFresh}, TestMode={IsTestMode})",
            selectedChapters.Count, concurrency, isFresh, isTestMode);

        GlossatorDiagnostics.ResetSessionStats();
        var sessionStopwatch = Stopwatch.StartNew();

        try
        {
        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var mainProgress = ctx.AddTask(
                    $"[bold cyan]Kitap Toplamı ({selectedChapters.Count} Bölüm - Concurrency: {concurrency})[/]",
                    maxValue: selectedChapters.Count);
                var activeTasks = new ConcurrentDictionary<string, ProgressTask>();

                await Parallel.ForEachAsync(selectedChapters, parallelOptions, async (chapterItem, ct) =>
                {
                    Glossator.AI.Diagnostics.GlossatorDiagnostics.ActiveWorkersCounter.Add(1);
                    var chapterFullPath = Path.Combine(package.OpfDirectory, chapterItem.Href);
                    var chapterDir = Path.GetDirectoryName(chapterFullPath) ?? package.OpfDirectory;
                    var chapterFileName = Path.GetFileName(chapterFullPath);
                    var chapterKey = Path.GetFileNameWithoutExtension(chapterFileName);
                    var ext = Path.GetExtension(chapterFileName);
                    if (string.IsNullOrWhiteSpace(ext)) ext = ".html";
                    var outputFilePath = Path.Combine(chapterDir, $"{chapterKey}_tr{ext}");
                    var bookId = package.Title.Trim();

                    _logger.LogInformation("Worker picked up chapter '{Chapter}'", chapterFileName);

                    // Bölüme özel UI İlerleme Görevi
                    var task = ctx.AddTask($"[green]{Markup.Escape(chapterFileName)}[/] (Başlatılıyor...)", maxValue: 100);
                    activeTasks[chapterFileName] = task;

                    try
                    {

                    // 1. Her bölüm için izole DI Kapsamı (IServiceScope)
                    await using var scope = rootServiceProvider.CreateAsyncScope();
                    var checkpointStore = scope.ServiceProvider.GetRequiredService<CheckpointStore>();
                    var tracker = scope.ServiceProvider.GetRequiredService<TermUsageTracker>();
                    tracker.BeginNewScope(chapterFileName);

                    var pipeline = await scope.ServiceProvider.CreateBookLocalizationPipelineAsync(ct);

                    // Bölüm olaylarını alt task açıklamasına yansıt (Terminal genişliğini aşmayacak şekilde güvenli özet)
                    pipeline.StepProgress += log =>
                    {
                        var shortSummary = log.Summary.Length > 55 ? log.Summary[..52] + "..." : log.Summary;
                        task.Description = $"[yellow]{Markup.Escape(chapterFileName)}[/] | [dim]{Markup.Escape(log.Stage)}[/] {Markup.Escape(shortSummary)}";
                    };

                    // 2. Belge Segmentasyonu ve Gruplama
                    var (document, segments) = HtmlSegmenter.SegmentFile(chapterFullPath);
                    var allSections = HtmlSectionGrouper.Group(segments, chapterKey);
                    var targetSections = requestedSectionCount.HasValue
                        ? allSections.Take(requestedSectionCount.Value).ToList()
                        : (isTestMode ? allSections.Take(2).ToList() : allSections);

                    task.MaxValue = targetSections.Count == 0 ? 1 : targetSections.Count;
                    task.Value = 0;

                    if (isFresh)
                    {
                        await checkpointStore.ClearCheckpointsAsync(bookId, chapterFileName, ct);
                        var oldDone = outputFilePath + ".done";
                        if (File.Exists(oldDone)) { try { File.Delete(oldDone); } catch { } }
                        var oldPart = outputFilePath + ".part";
                        if (File.Exists(oldPart)) { try { File.Delete(oldPart); } catch { } }
                    }

                    var existingCheckpoints = await checkpointStore.GetCompletedCheckpointsAsync(bookId, chapterFileName, ct);

                    // 2.1. Önceden tamamlanmış checkpoint'leri hemen DOM'a ve diske uygula (canlı izleme)
                    if (!isFresh && existingCheckpoints.Count > 0)
                    {
                        var hasCachedSegments = false;
                        foreach (var seg in segments)
                        {
                            if (existingCheckpoints.TryGetValue(seg.Index, out var cp) &&
                                cp.OriginalHash == SegmentCheckpoint.ComputeHash(seg.MaskedText) &&
                                !string.IsNullOrWhiteSpace(cp.TranslatedText))
                            {
                                seg.TranslatedText = cp.TranslatedText;
                                hasCachedSegments = true;
                            }
                        }

                        if (hasCachedSegments)
                        {
                            HtmlSegmenter.ApplyTranslations(segments);
                            await File.WriteAllTextAsync(outputFilePath + ".part", document.ToStrictXhtml(), ct);
                        }
                    }

                    // 3. Style Guide Çözümleme
                    string chapterStyleGuide;
                    if (!isFresh && existingCheckpoints.TryGetValue(-1, out var styleCp) && !string.IsNullOrWhiteSpace(styleCp.TranslatedText))
                    {
                        chapterStyleGuide = styleCp.TranslatedText;
                        pipeline.SetStyleGuide(chapterStyleGuide);
                    }
                    else
                    {
                        task.Description = $"[cyan]{Markup.Escape(chapterFileName)}[/] | Style Analyst Analiz Ediyor...";
                        var sampleParagraphs = segments
                            .Where(s => s.TagName.Equals("p", StringComparison.OrdinalIgnoreCase) && s.MaskedText.Length > 60)
                            .Take(12)
                            .Select(s => s.MaskedText)
                            .ToList();

                        if (sampleParagraphs.Count == 0)
                        {
                            sampleParagraphs = segments.Take(5).Select(s => s.MaskedText).ToList();
                        }

                        var sample = string.Join("\n\n---\n\n", sampleParagraphs);
                        chapterStyleGuide = await pipeline.AnalyzeStyleAsync(sample, ct);

                        await checkpointStore.EnqueueAsync(new SegmentCheckpoint
                        {
                            BookId = bookId,
                            ChapterFile = chapterFileName,
                            SegmentIndex = -1,
                            OriginalHash = SegmentCheckpoint.ComputeHash(sample),
                            TagName = "styleguide",
                            OriginalText = "StyleGuide",
                            TranslatedText = chapterStyleGuide,
                            StageStatus = "Completed",
                            UpdatedAt = DateTime.UtcNow
                        }, ct);
                    }

                    // 4. Section Döngüsü
                    for (int s = 0; s < targetSections.Count; s++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var sec = targetSections[s];

                        bool allCached = sec.Segments.All(seg =>
                            existingCheckpoints.TryGetValue(seg.Index, out var cp) &&
                            cp.OriginalHash == SegmentCheckpoint.ComputeHash(seg.MaskedText) &&
                            !string.IsNullOrWhiteSpace(cp.TranslatedText));

                        if (allCached)
                        {
                            foreach (var seg in sec.Segments)
                            {
                                seg.TranslatedText = existingCheckpoints[seg.Index].TranslatedText;
                                await pipeline.RegisterCachedSectionAsync(seg.MaskedText);
                            }
                        }
                        else
                        {
                            var shortTitle = sec.Title.Length > 45 ? sec.Title[..42] + "..." : sec.Title;
                            task.Description = $"[blue]{Markup.Escape(chapterFileName)}[/] | Sec {s + 1}/{targetSections.Count}: {Markup.Escape(shortTitle)}";
                            await pipeline.TranslateSectionAsync(sec, chapterStyleGuide, ct);
                            GlossatorDiagnostics.RecordSectionCompleted();

                            // Segmentleri kuyruk üzerinden arka plan SQLite yazıcısına aktar
                            foreach (var seg in sec.Segments)
                            {
                                var isTranslated = !string.IsNullOrWhiteSpace(seg.TranslatedText) &&
                                                   (seg.IsPureTechnicalOrPlaceholder() ||
                                                    !string.Equals(seg.TranslatedText.Trim(), seg.MaskedText.Trim(), StringComparison.OrdinalIgnoreCase));

                                await checkpointStore.EnqueueAsync(new SegmentCheckpoint
                                {
                                    BookId = bookId,
                                    ChapterFile = chapterFileName,
                                    SegmentIndex = seg.Index,
                                    OriginalHash = SegmentCheckpoint.ComputeHash(seg.MaskedText),
                                    TagName = seg.TagName,
                                    OriginalText = seg.MaskedText,
                                    TranslatedText = isTranslated ? seg.TranslatedText! : seg.MaskedText,
                                    StageStatus = isTranslated ? "Completed" : "MissingFallback",
                                    UpdatedAt = DateTime.UtcNow
                                }, ct);
                            }

                            // Canlı Takip: Bölüm henüz bitmedi; ara çıktıyı .part dosyasına yaz (kullanıcı canlı izleyebilsin fakat yarım dosya tamamlandı sayılmasın)
                            HtmlSegmenter.ApplyTranslations(segments);
                            await File.WriteAllTextAsync(outputFilePath + ".part", document.ToStrictXhtml(), ct);
                        }

                        task.Increment(1);
                    }

                    // 5. Nihai HTML Çıktısını Diske Yaz (W3C Katı XHTML)
                    HtmlSegmenter.ApplyTranslations(segments);
                    await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml(), ct);

                    // KONTROL: Bölümdeki tüm segmentler eksiksiz çevrildi mi? (İngilizce kalan veya boş olan var mı?)
                    // Saf yer tutucu/teknik bloklarda Translated == Masked olması normaldir; doğal dil içeren bloklar denetlenir.
                    var untranslatedCount = segments.Count(s =>
                        string.IsNullOrWhiteSpace(s.TranslatedText) ||
                        (!s.IsPureTechnicalOrPlaceholder() &&
                         string.Equals(s.TranslatedText.Trim(), s.MaskedText.Trim(), StringComparison.OrdinalIgnoreCase)));

                    if (untranslatedCount == 0)
                    {
                        // Kuyruktaki tüm checkpoint kayıtlarının SQLite'a yazıldığını garanti et
                        await checkpointStore.FlushAsync(ct);

                        // Tamamlanma onay dosyası oluştur ve ara .part dosyasını temizle
                        var doneFilePath = outputFilePath + ".done";
                        await File.WriteAllTextAsync(doneFilePath, DateTime.UtcNow.ToString("o"), ct);

                        var partFilePath = outputFilePath + ".part";
                        if (File.Exists(partFilePath))
                        {
                            try { File.Delete(partFilePath); } catch { /* best-effort cleanup */ }
                        }

                        Glossator.AI.Diagnostics.GlossatorDiagnostics.ChaptersTranslatedCounter.Add(1);
                        GlossatorDiagnostics.RecordChapterCompleted();
                        _logger.LogInformation("OK: Chapter '{Chapter}' completed successfully ({SectionCount} sections).",
                            chapterFileName, targetSections.Count);

                        task.Description = $"[bold green]OK: {Markup.Escape(chapterFileName)} Tamamlandı[/]";
                    }
                    else
                    {
                        _logger.LogWarning("Bölüm '{Chapter}' kısmi çevrildi ({Count} segment eksik veya İngilizce kaldı). .done onayı verilmedi; sonraki çalıştırmada kaldığı yerden devam edilecek.",
                            chapterFileName, untranslatedCount);

                        task.Description = $"[bold yellow]UYARI: {Markup.Escape(chapterFileName)} ({untranslatedCount} blok eksik - .part korundu)[/]";
                    }

                    task.Value = task.MaxValue;
                    lock (activeTasks)
                    {
                        mainProgress.Increment(1);
                    }
                    }
                    catch (OperationCanceledException)
                    {
                        task.Description = $"[bold red]İPTAL: {Markup.Escape(chapterFileName)}[/]";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Worker failed on chapter '{Chapter}'", chapterFileName);
                        task.Description = $"[bold red]HATA: {Markup.Escape(chapterFileName)}: {Markup.Escape(ex.Message)}[/]";
                        throw;
                    }
                    finally
                    {
                        Glossator.AI.Diagnostics.GlossatorDiagnostics.ActiveWorkersCounter.Add(-1);
                    }
                });
            });
        }
        finally
        {
            sessionStopwatch.Stop();
            var aiOpts = rootServiceProvider.GetService<Microsoft.Extensions.Options.IOptions<Glossator.AI.Configuration.GlossatorAiOptions>>()?.Value;
            RenderSessionReport(sessionStopwatch.Elapsed, aiOpts);
        }
    }

    private static void RenderSessionReport(TimeSpan elapsed, Glossator.AI.Configuration.GlossatorAiOptions? aiOptions)
    {
        var snapshot = GlossatorDiagnostics.GetStageStatsSnapshot();
        if (snapshot.Count == 0 && GlossatorDiagnostics.SessionSectionsCompleted == 0)
        {
            return;
        }

        const double usdToTry = 38.00;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Title("[bold yellow]ÇEVİRİ SONU TOKEN VE MALİYET KARNESİ[/]")
            .AddColumn(new TableColumn("[bold white]Ajan / Model[/]"))
            .AddColumn(new TableColumn("[bold cyan]Çağrı[/]").RightAligned())
            .AddColumn(new TableColumn("[bold green]Girdi (Input)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold blue]Görünür Çıktı[/]").RightAligned())
            .AddColumn(new TableColumn("[bold magenta1]Düşünme (Reasoning)[/]").RightAligned())
            .AddColumn(new TableColumn("[bold yellow]Fatura Çıktı[/]").RightAligned())
            .AddColumn(new TableColumn("[bold white]Maliyet (Gemini)[/]").RightAligned());

        long totalCalls = 0;
        long totalIn = 0;
        long totalVisibleOut = 0;
        long totalReasoning = 0;
        long totalBilledOut = 0;
        double grandTotalUsd = 0.0;

        string[] orderedStages = ["StyleAnalyst", "Translator", "Reviewer"];
        var allKeys = orderedStages
            .Where(k => snapshot.ContainsKey(k))
            .Concat(snapshot.Keys.Where(k => !orderedStages.Contains(k, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var stage in allKeys)
        {
            var s = snapshot[stage];
            var calls = Interlocked.Read(ref s.Calls);
            var inTok = Interlocked.Read(ref s.InputTokens);
            var visOut = Interlocked.Read(ref s.VisibleOutputTokens);
            var billedOut = Interlocked.Read(ref s.BilledOutputTokens);
            var reasonTok = Math.Max(0, billedOut - visOut);

            totalCalls += calls;
            totalIn += inTok;
            totalVisibleOut += visOut;
            totalReasoning += reasonTok;
            totalBilledOut += billedOut;

            var (provName, modelId, inRateUsd, outRateUsd, isFree) = aiOptions != null
                ? aiOptions.ResolveStagePricing(stage)
                : ("Gemini", "gemini-2.5-flash", 0.30, 2.50, false);

            var stageUsd = isFree
                ? 0.0
                : (inTok / 1_000_000.0 * inRateUsd) + (billedOut / 1_000_000.0 * outRateUsd);
            var stageTry = stageUsd * usdToTry;
            grandTotalUsd += stageUsd;

            var stageLabel = $"[bold]{Markup.Escape(stage)}[/] [dim]({Markup.Escape(provName)}: {Markup.Escape(modelId)})[/]";
            var costLabel = isFree
                ? $"[green]$0.0000[/] [dim](Ücretsiz)[/]"
                : $"${stageUsd:F4} [dim](~{stageTry:F2} ₺)[/]";

            table.AddRow(
                stageLabel,
                $"{calls:N0}",
                $"{inTok:N0}",
                $"{visOut:N0}",
                reasonTok > 0 ? $"{reasonTok:N0}" : "[dim]0[/]",
                $"{billedOut:N0}",
                costLabel);
        }

        var grandTotalTry = grandTotalUsd * usdToTry;

        table.AddEmptyRow();
        table.AddRow(
            "[bold yellow]TOPLAM[/]",
            $"[bold cyan]{totalCalls:N0}[/]",
            $"[bold green]{totalIn:N0}[/]",
            $"[bold blue]{totalVisibleOut:N0}[/]",
            $"[bold magenta1]{totalReasoning:N0}[/]",
            $"[bold yellow]{totalBilledOut:N0}[/]",
            $"[bold green]${grandTotalUsd:F4} (~{grandTotalTry:F2} ₺)[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        var elapsedFormatted = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed:mm\\:ss}"
            : $"{elapsed:mm\\:ss}";

        var summaryPanel = new Panel(
            new Markup(
                $"[bold white]Tamamlanan Bölüm:[/] [green]{GlossatorDiagnostics.SessionChaptersCompleted}[/]  |  " +
                $"[bold white]Çevrilen Kesit (Section):[/] [cyan]{GlossatorDiagnostics.SessionSectionsCompleted}[/]  |  " +
                $"[bold white]Reviewer Nokta Atışı Yama:[/] [yellow]{GlossatorDiagnostics.SessionPatchesApplied} blok[/]  |  " +
                $"[bold white]Tam İade (Ret):[/] [red]{GlossatorDiagnostics.SessionRejections}[/]  |  " +
                $"[bold white]Süre:[/] [white]{elapsedFormatted}[/]"))
            .Header("[bold cyan]Redaksiyon ve Performans Özeti[/]")
            .BorderColor(Color.Grey);

        AnsiConsole.Write(summaryPanel);
    }
}
