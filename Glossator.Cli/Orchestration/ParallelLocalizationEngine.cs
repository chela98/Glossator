using System.Collections.Concurrent;
using Glossator.AI.Extensions;
using Glossator.AI.Workflows;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Glossator.Cli.Orchestration;

/// <summary>
/// Çoklu iş parçacıklı bölüm yerelleştirme ve işçi havuzu orkestratörü.
/// Parallel.ForEachAsync ve izole IServiceScope mekanizmasıyla race condition'ları önler,
/// Spectre.Console canlı ilerleme çubuğuyla durumu raporlar.
/// </summary>
public sealed class ParallelLocalizationEngine(IServiceProvider rootServiceProvider)
{
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
                    var chapterFullPath = Path.Combine(package.OpfDirectory, chapterItem.Href);
                    var chapterFileName = Path.GetFileName(chapterFullPath);
                    var chapterKey = Path.GetFileNameWithoutExtension(chapterFileName);
                    var outputFilePath = Path.Combine(package.OpfDirectory, $"{chapterKey}_tr.html");

                    // Bölüme özel UI İlerleme Görevi
                    var task = ctx.AddTask($"[green]{Markup.Escape(chapterFileName)}[/] (Başlatılıyor...)", maxValue: 100);
                    activeTasks[chapterFileName] = task;

                    // 1. Her bölüm için izole DI Kapsamı (IServiceScope)
                    await using var scope = rootServiceProvider.CreateAsyncScope();
                    var repo = scope.ServiceProvider.GetRequiredService<SqliteGlossaryRepository>();
                    var queue = scope.ServiceProvider.GetRequiredService<ICheckpointQueue>();
                    var tracker = scope.ServiceProvider.GetRequiredService<TermUsageTracker>();
                    tracker.BeginNewScope(chapterFileName);

                    var pipeline = await scope.ServiceProvider.CreateBookLocalizationPipelineAsync(ct);

                    // Bölüm olaylarını alt task açıklamasına yansıt
                    pipeline.StepProgress += log =>
                    {
                        task.Description = $"[yellow]{Markup.Escape(chapterFileName)}[/] | [dim]{Markup.Escape(log.Stage)}[/] {Markup.Escape(log.Summary)}";
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
                        await repo.ClearCheckpointsAsync(chapterFileName, ct);
                    }

                    var existingCheckpoints = await repo.GetCompletedCheckpointsAsync(chapterFileName, ct);

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

                        await queue.EnqueueAsync(new SegmentCheckpoint
                        {
                            ChapterFile = chapterFileName,
                            SegmentIndex = -1,
                            OriginalHash = SqliteGlossaryRepository.ComputeHash(sample),
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
                            cp.OriginalHash == SqliteGlossaryRepository.ComputeHash(seg.MaskedText) &&
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
                            task.Description = $"[blue]{Markup.Escape(chapterFileName)}[/] | Sec {s + 1}/{targetSections.Count}: {Markup.Escape(sec.Title)}";
                            await pipeline.TranslateSectionAsync(sec, chapterStyleGuide, ct);

                            // Segmentleri kuyruk üzerinden arka plan SQLite yazıcısına aktar
                            foreach (var seg in sec.Segments)
                            {
                                var isTranslated = !string.IsNullOrWhiteSpace(seg.TranslatedText);
                                await queue.EnqueueAsync(new SegmentCheckpoint
                                {
                                    ChapterFile = chapterFileName,
                                    SegmentIndex = seg.Index,
                                    OriginalHash = SqliteGlossaryRepository.ComputeHash(seg.MaskedText),
                                    TagName = seg.TagName,
                                    OriginalText = seg.MaskedText,
                                    TranslatedText = isTranslated ? seg.TranslatedText! : seg.MaskedText,
                                    StageStatus = isTranslated ? "Completed" : "MissingFallback",
                                    UpdatedAt = DateTime.UtcNow
                                }, ct);
                            }
                        }

                        // Katı W3C XHTML olarak ara kaydet
                        HtmlSegmenter.ApplyTranslations(segments);
                        await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml(), ct);

                        task.Increment(1);
                    }

                    // 5. Nihai HTML Çıktısını Diske Yaz (W3C Katı XHTML)
                    HtmlSegmenter.ApplyTranslations(segments);
                    await File.WriteAllTextAsync(outputFilePath, document.ToStrictXhtml(), ct);

                    task.Description = $"[bold green]✔ {Markup.Escape(chapterFileName)} Tamamlandı[/]";
                    task.Value = task.MaxValue;
                    mainProgress.Increment(1);
                });
            });
    }
}
