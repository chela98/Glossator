using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Glossator.AI.Diagnostics;

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
}
