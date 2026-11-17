using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glossator.AI.Diagnostics;

public sealed class StageUsageStats
{
    public long Calls;
    public long InputTokens;
    public long CachedInputTokens;
    public long BilledOutputTokens;
    public long VisibleOutputTokens;
    public long RecordedReasoningTokens;
    internal long ExplicitReasoningCalls;

    public long ReasoningTokens => Interlocked.Read(ref ExplicitReasoningCalls) > 0
        ? Interlocked.Read(ref RecordedReasoningTokens)
        : Math.Max(0, Interlocked.Read(ref BilledOutputTokens) - Interlocked.Read(ref VisibleOutputTokens));

    public double CacheHitRatio
    {
        get
        {
            var input = Interlocked.Read(ref InputTokens);
            var cached = Interlocked.Read(ref CachedInputTokens);
            return input > 0 ? Math.Min(1.0, (double)cached / input) : 0.0;
        }
    }
}

public static class GlossatorDiagnostics
{
    public const string SourceName = "Glossator";
    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    // Canlı İlerleme ve Kalite Metrikleri (Aspire Dashboard Canlı Grafikleri)
    public static readonly Counter<long> SectionsTranslatedCounter = 
        Meter.CreateCounter<long>("glossator.sections.translated", "sections", "Başarıyla yerelleştirilen mantıksal konu (HtmlSection) sayısı");

    public static readonly Counter<long> ChaptersTranslatedCounter = 
        Meter.CreateCounter<long>("glossator.chapters.translated", "chapters", "Tamamlanan toplam bölüm (XHTML) sayısı");

    public static readonly Counter<long> ReviewerPatchesCounter = 
        Meter.CreateCounter<long>("glossator.reviewer.patches_applied", "patches", "Denetçi (Reviewer) tarafından uygulanan JSON yaması sayısı");

    public static readonly Counter<long> ReviewerRejectionsCounter = 
        Meter.CreateCounter<long>("glossator.reviewer.rejections", "rejections", "Denetçi tarafından reddedilen ve yeniden çeviriye giren taslak sayısı");

    public static readonly Counter<long> RecoveryAttemptsCounter = 
        Meter.CreateCounter<long>("glossator.recovery.attempts", "attempts", "Eksik blok mikro kurtarma döngüsü sayısı");

    public static readonly UpDownCounter<int> ActiveWorkersCounter = 
        Meter.CreateUpDownCounter<int>("glossator.workers.active", "workers", "Şu an aktif olarak çalışan eşzamanlı işçi (worker) sayısı");

    // SSE Canlı Akış (Streaming), TTFT ve TPS Metrikleri
    public static readonly Histogram<double> TimeToFirstTokenHistogram = 
        Meter.CreateHistogram<double>("glossator.genai.ttft", "ms", "İlk token varış süresi (Time to First Token - TTFT)");

    public static readonly Histogram<double> TokensPerSecondHistogram = 
        Meter.CreateHistogram<double>("glossator.genai.tps", "tokens/sec", "Token akış üretim hızı (Tokens Per Second - TPS)");

    public static readonly Counter<long> StreamedTokensCounter = 
        Meter.CreateCounter<long>("glossator.genai.tokens_streamed", "tokens", "Canlı akışla (SSE) başarıyla üretilen toplam token sayısı");

    // Oturum Bazlı (Bölüm/Kitap Sonu Karnesi İçin) Thread-Safe Sayaçlar
    private static readonly ConcurrentDictionary<string, StageUsageStats> _stageStats = new(StringComparer.OrdinalIgnoreCase);
    private static long _sessionPatchesApplied;
    private static long _sessionRejections;
    private static long _sessionSectionsCompleted;
    private static long _sessionChaptersCompleted;

    public static long SessionPatchesApplied => Interlocked.Read(ref _sessionPatchesApplied);
    public static long SessionRejections => Interlocked.Read(ref _sessionRejections);
    public static long SessionSectionsCompleted => Interlocked.Read(ref _sessionSectionsCompleted);
    public static long SessionChaptersCompleted => Interlocked.Read(ref _sessionChaptersCompleted);

    public static void ResetSessionStats()
    {
        _stageStats.Clear();
        Interlocked.Exchange(ref _sessionPatchesApplied, 0);
        Interlocked.Exchange(ref _sessionRejections, 0);
        Interlocked.Exchange(ref _sessionSectionsCompleted, 0);
        Interlocked.Exchange(ref _sessionChaptersCompleted, 0);
    }

    public static void RecordStageUsage(
        string stageName,
        long inputTokens,
        long billedOutputTokens,
        long visibleOutputTokens,
        long cachedInputTokens = 0,
        long? reasoningTokens = null)
    {
        var key = string.IsNullOrWhiteSpace(stageName) ? "Other" : stageName;
        var stats = _stageStats.GetOrAdd(key, _ => new StageUsageStats());
        var safeInput = Math.Max(0, inputTokens);
        var safeCached = Math.Clamp(cachedInputTokens, 0, safeInput);
        var safeBilled = Math.Max(0, billedOutputTokens);
        var safeVisible = Math.Max(0, visibleOutputTokens);
        var resolvedReasoning = reasoningTokens.HasValue
            ? Math.Max(0, reasoningTokens.Value)
            : Math.Max(0, safeBilled - safeVisible);

        Interlocked.Increment(ref stats.Calls);
        Interlocked.Add(ref stats.InputTokens, safeInput);
        Interlocked.Add(ref stats.CachedInputTokens, safeCached);
        Interlocked.Add(ref stats.BilledOutputTokens, safeBilled);
        Interlocked.Add(ref stats.VisibleOutputTokens, safeVisible);
        Interlocked.Add(ref stats.RecordedReasoningTokens, resolvedReasoning);
        Interlocked.Increment(ref stats.ExplicitReasoningCalls);
    }

    public static void RecordPatchesApplied(int count) => Interlocked.Add(ref _sessionPatchesApplied, count);
    public static void RecordRejection() => Interlocked.Increment(ref _sessionRejections);
    public static void RecordSectionCompleted() => Interlocked.Increment(ref _sessionSectionsCompleted);
    public static void RecordChapterCompleted() => Interlocked.Increment(ref _sessionChaptersCompleted);

    public static IReadOnlyDictionary<string, StageUsageStats> GetStageStatsSnapshot() => _stageStats;
}
