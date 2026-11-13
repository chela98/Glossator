using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Glossator.AI.Diagnostics;
using Glossator.AI.Extensions;
using Glossator.AI.Workflows;
using Glossator.App.Infrastructure;
using Glossator.Core.Data;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glossator.App.Engine;

public sealed record StudioProgressUpdate(
    string ChapterHref,
    string ChapterFileName,
    string Stage,
    string Message,
    int CompletedSections,
    int TotalSections,
    int CompletedChapters,
    int TotalChapters,
    double OverallPercentage,
    ChapterTranslationState? ChapterState = null);

public sealed record StudioSessionSummary(
    TimeSpan Elapsed,
    long ChaptersCompleted,
    long SectionsCompleted,
    long PatchesApplied,
    long Rejections,
    IReadOnlyDictionary<string, StageUsageStats> StageStats);

public sealed class StudioLocalizationEngine(
    IServiceProvider rootServiceProvider,
    ILogger<StudioLocalizationEngine>? logger = null)
{
    private readonly ILogger<StudioLocalizationEngine> _logger = logger ?? NullLogger<StudioLocalizationEngine>.Instance;

    public event Action<StudioProgressUpdate>? ProgressChanged;
    public event Action<string>? LogMessageReceived;

    public async Task<StudioSessionSummary> RunAsync(
        EpubPackage package,
        List<EpubItem> selectedChapters,
        int concurrency,
        bool isFresh,
        int? requestedSectionCount = null,
        bool isTestMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(selectedChapters);

        var sessionStopwatch = Stopwatch.StartNew();
        GlossatorDiagnostics.ResetSessionStats();

        if (selectedChapters.Count == 0)
        {
            return BuildSummary(sessionStopwatch.Elapsed);
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        };

        _logger.LogInformation("Starting StudioLocalizationEngine with {ChapterCount} chapters (Concurrency={Concurrency}, Fresh={IsFresh}, TestMode={IsTestMode}, SectionLimit={SectionLimit})",
            selectedChapters.Count, concurrency, isFresh, isTestMode, requestedSectionCount);

        int completedChaptersCount = 0;
        var chapterFractions = new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        void Report(string chapterHref, string chapterFileName, string stage, string msg, int secDone, int secTotal, ChapterTranslationState? chapterState = null)
        {
            if (secTotal > 0)
            {
                chapterFractions[chapterHref] = Math.Clamp((double)secDone / secTotal, 0.0, 1.0);
            }

            var totalFraction = chapterFractions.Values.Sum();
            var pct = selectedChapters.Count == 0
                ? 0
                : Math.Clamp(Math.Round(totalFraction / selectedChapters.Count * 100, 1), 0, 100);

            ProgressChanged?.Invoke(new StudioProgressUpdate(
                chapterHref, chapterFileName, stage, msg, secDone, secTotal, Volatile.Read(ref completedChaptersCount), selectedChapters.Count, pct, chapterState));
        }

        await Parallel.ForEachAsync(selectedChapters, parallelOptions, async (chapterItem, ct) =>
        {
            GlossatorDiagnostics.ActiveWorkersCounter.Add(1);
            var chapterHref = chapterItem.Href;
            var chapterFullPath = Path.Combine(package.OpfDirectory, chapterHref);
            var chapterDir = Path.GetDirectoryName(chapterFullPath) ?? package.OpfDirectory;
            var chapterFileName = Path.GetFileName(chapterFullPath);
            var chapterKey = Path.GetFileNameWithoutExtension(chapterFileName);
            var ext = Path.GetExtension(chapterFileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".html";
            var outputFilePath = Path.Combine(chapterDir, $"{chapterKey}_tr{ext}");
            var bookId = package.Title.Trim();

            Report(chapterHref, chapterFileName, "Başlatılıyor", $"{chapterFileName} işleme alındı.", 0, 1);
            LogMessageReceived?.Invoke($"[Worker] '{chapterFileName}' çeviri kuyruğuna alındı.");

            await using var scope = rootServiceProvider.CreateAsyncScope();
            var checkpointStore = scope.ServiceProvider.GetRequiredService<CheckpointStore>();

            try
            {
                var tracker = scope.ServiceProvider.GetRequiredService<TermUsageTracker>();
                tracker.BeginNewScope(chapterFileName);

                var pipeline = await scope.ServiceProvider.CreateBookLocalizationPipelineAsync(ct);
                int currentSectionIdx = 0;
                int totalSectionCount = 1;
                var lastTransientUiReportTicks = 0L;

                pipeline.StepProgress += log =>
                {
                    if (!log.IsTransient)
                    {
                        LogMessageReceived?.Invoke($"[{chapterFileName}] {log.Stage}: {log.Summary}");
                    }

                    // UI thread boğulmasını engellemek için transient akış olaylarını ~250ms throttle et
                    var nowTicks = Environment.TickCount64;
                    var prevTicks = Interlocked.Read(ref lastTransientUiReportTicks);
                    if (log.IsTransient && nowTicks - prevTicks < 250)
                    {
                        return;
                    }
                    Interlocked.Exchange(ref lastTransientUiReportTicks, nowTicks);

                    Report(chapterHref, chapterFileName, log.Stage, log.Summary,
                        Volatile.Read(ref currentSectionIdx), Volatile.Read(ref totalSectionCount));
                };

                var (document, segments) = HtmlSegmenter.SegmentFile(chapterFullPath);
                var allSections = HtmlSectionGrouper.Group(segments, chapterKey);
                var targetSections = requestedSectionCount.HasValue && requestedSectionCount.Value > 0
                    ? allSections.Take(requestedSectionCount.Value).ToList()
                    : (isTestMode ? allSections.Take(2).ToList() : allSections);

                Volatile.Write(ref totalSectionCount, Math.Max(1, targetSections.Count));

                if (isFresh)
                {
                    await checkpointStore.ClearCheckpointsAsync(bookId, chapterFileName, ct);
                    foreach (var suffix in new[] { ".done", ".part", "" })
                    {
                        var staleFile = outputFilePath + suffix;
                        if (File.Exists(staleFile)) { try { File.Delete(staleFile); } catch { } }
                    }
                }

                var existingCheckpoints = await checkpointStore.GetCompletedCheckpointsAsync(bookId, chapterFileName, ct);

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

                string chapterStyleGuide;
                if (!isFresh && existingCheckpoints.TryGetValue(-1, out var styleCp) && !string.IsNullOrWhiteSpace(styleCp.TranslatedText))
                {
                    chapterStyleGuide = styleCp.TranslatedText;
                    pipeline.SetStyleGuide(chapterStyleGuide);
                }
                else
                {
                    Report(chapterHref, chapterFileName, "StyleAnalyst", "Yazar üslubu analiz ediliyor...", 0, Volatile.Read(ref totalSectionCount), ChapterTranslationState.Partial);
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

                for (int s = 0; s < targetSections.Count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sec = targetSections[s];
                    Volatile.Write(ref currentSectionIdx, s);

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
                        Report(chapterHref, chapterFileName, "Önbellek", $"Kesit {s + 1}/{targetSections.Count} checkpoint'ten yüklendi.", s + 1, Volatile.Read(ref totalSectionCount), ChapterTranslationState.Partial);
                    }
                    else
                    {
                        Report(chapterHref, chapterFileName, "Translator", $"Kesit {s + 1}/{targetSections.Count}: {sec.Title}", s, Volatile.Read(ref totalSectionCount), ChapterTranslationState.Partial);
                        await pipeline.TranslateSectionAsync(sec, chapterStyleGuide, ct);
                        GlossatorDiagnostics.RecordSectionCompleted();

                        foreach (var seg in sec.Segments)
                        {
                            var isTranslated = seg.IsTranslationComplete(package.Title, package.Author);

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

                        HtmlSegmenter.ApplyTranslations(segments);
                        await File.WriteAllTextAsync(outputFilePath + ".part", document.ToStrictXhtml(), ct);
                        Report(chapterHref, chapterFileName, "Translator", $"Kesit {s + 1}/{targetSections.Count} kaydedildi.", s + 1, Volatile.Read(ref totalSectionCount), ChapterTranslationState.Partial);
                    }
                }

                HtmlSegmenter.ApplyTranslations(segments);
                var finalXhtml = document.ToStrictXhtml();

                var untranslatedCount = segments.Count(s => !s.IsTranslationComplete(package.Title, package.Author));

                bool isPartialLimit = isTestMode || (requestedSectionCount.HasValue && requestedSectionCount.Value > 0 && requestedSectionCount.Value < allSections.Count);

                if (untranslatedCount == 0 && !isPartialLimit)
                {
                    await checkpointStore.FlushAsync(ct);
                    await File.WriteAllTextAsync(outputFilePath, finalXhtml, ct);
                    await File.WriteAllTextAsync(outputFilePath + ".done", DateTime.UtcNow.ToString("O"), ct);
                    var partFile = outputFilePath + ".part";
                    if (File.Exists(partFile)) { try { File.Delete(partFile); } catch { } }

                    GlossatorDiagnostics.ChaptersTranslatedCounter.Add(1);
                    GlossatorDiagnostics.RecordChapterCompleted();

                    Interlocked.Increment(ref completedChaptersCount);
                    Report(chapterHref, chapterFileName, "Tamamlandı", $"{chapterFileName} başarıyla yerelleştirildi.", Volatile.Read(ref totalSectionCount), Volatile.Read(ref totalSectionCount), ChapterTranslationState.Completed);
                    LogMessageReceived?.Invoke($"[Başarılı] '{chapterFileName}' tamamlandı.");
                }
                else
                {
                    // Kısmi / test modunda yalnızca .part yazılır, nihai EPUB'a girmemesi sağlanır
                    await File.WriteAllTextAsync(outputFilePath + ".part", finalXhtml, ct);
                    Interlocked.Increment(ref completedChaptersCount);
                    Report(chapterHref, chapterFileName, "Kısmi (.part)", $"{chapterFileName} kısmi olarak kaydedildi.", Volatile.Read(ref totalSectionCount), Volatile.Read(ref totalSectionCount), ChapterTranslationState.Partial);
                    LogMessageReceived?.Invoke($"[Uyarı] '{chapterFileName}' kısmi (.part) kaydedildi ({untranslatedCount} çevrilmemiş blok veya kesit/test limiti).");
                }
            }
            catch (OperationCanceledException)
            {
                LogMessageReceived?.Invoke($"[İptal] '{chapterFileName}' durduruldu.");
                var currentState = DocumentTranslationStatus.GetState(package.OpfDirectory, chapterHref);
                Report(chapterHref, chapterFileName, "Durduruldu", $"{chapterFileName} iptal edildi.", 0, 1, currentState);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chapter '{Chapter}' failed during localization.", chapterFileName);
                LogMessageReceived?.Invoke($"[Hata] '{chapterFileName}' başarısız: {ex.Message}");
                var currentState = DocumentTranslationStatus.GetState(package.OpfDirectory, chapterHref);
                Report(chapterHref, chapterFileName, "Hata", ex.Message, 0, 1, currentState);
                throw;
            }
            finally
            {
                try
                {
                    await checkpointStore.FlushAsync(CancellationToken.None);
                }
                catch (Exception flushEx)
                {
                    _logger.LogWarning(flushEx, "Checkpoint flush failed in finally for '{Chapter}'.", chapterFileName);
                }
                GlossatorDiagnostics.ActiveWorkersCounter.Add(-1);
            }
        });

        sessionStopwatch.Stop();
        return BuildSummary(sessionStopwatch.Elapsed);
    }

    public static StudioSessionSummary BuildCurrentSessionSummary(TimeSpan elapsed) => BuildSummary(elapsed);

    private static StudioSessionSummary BuildSummary(TimeSpan elapsed) => new(
        elapsed,
        GlossatorDiagnostics.SessionChaptersCompleted,
        GlossatorDiagnostics.SessionSectionsCompleted,
        GlossatorDiagnostics.SessionPatchesApplied,
        GlossatorDiagnostics.SessionRejections,
        GlossatorDiagnostics.GetStageStatsSnapshot());
}
