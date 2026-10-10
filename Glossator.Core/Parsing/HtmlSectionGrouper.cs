using System.Text.RegularExpressions;

namespace Glossator.Core.Parsing;

/// <summary>
/// HTML segmentlerini LLM bağlam penceresini (context window) ve semantik başlıkları gözeterek
/// dinamik, token duyarlı Section bloklarına gruplayan motor.
/// </summary>
public static class HtmlSectionGrouper
{
    /// <summary>
    /// LLM prompt'u için hedeflenen ideal token alt eşiği (soft limit).
    /// </summary>
    public const int SoftTokenLimit = 1800;

    /// <summary>
    /// LLM yanıt kesilmesini ve context penceresi taşmasını önleyen zorunlu üst token eşiği (hard limit).
    /// </summary>
    public const int HardTokenLimit = 2500;

    /// <summary>
    /// Bir section içinde tutulabilecek maksimum segment sayısı.
    /// </summary>
    public const int MaxSegmentsPerSection = 20;

    private static readonly HashSet<string> MajorHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3"
    };

    private static readonly HashSet<string> SemanticSplitTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h4", "h5", "h6", "dt", "blockquote", "hr"
    };

    /// <summary>
    /// Verilen HTML segmentlerini semantik başlık hiyerarşisi ve token sınırlarına göre gruplar.
    /// Başlıksız uzun metinlerde dahi 2.500 token üst sınırını aşmayacak şekilde güvenli alt bölümler oluşturur.
    /// </summary>
    public static List<HtmlSection> Group(List<HtmlSegment> segments, string chapterKey = "ch01")
    {
        if (segments == null || segments.Count == 0) return [];

        var sections = new List<HtmlSection>();
        var currentBatch = new List<HtmlSegment>();
        var currentBatchTokens = 0;
        var currentTitle = "Giriş";
        var sectionIndex = 0;

        foreach (var seg in segments)
        {
            var segTokens = EstimateTokens(seg);
            var isMajorHeading = MajorHeadings.Contains(seg.TagName);
            var isSemanticSplitTag = SemanticSplitTags.Contains(seg.TagName);

            // Kural 1: Ana başlık geldiğinde ve mevcut grupta içerik varsa hemen yeni bölüme geç
            if (isMajorHeading && currentBatch.Count > 0)
            {
                sections.Add(CreateSection(sectionIndex++, chapterKey, currentTitle, currentBatch));
                currentBatch = [];
                currentBatchTokens = 0;
                currentTitle = ExtractCleanTitle(seg.MaskedText);
            }
            // Kural 2 (Hard Limit): Mevcut grup + yeni segment 2.500 token'ı aşıyorsa etiket ne olursa olsun derhal böl
            else if (currentBatch.Count > 0 && (currentBatchTokens + segTokens > HardTokenLimit))
            {
                sections.Add(CreateSection(sectionIndex++, chapterKey, $"{currentTitle} (Devam)", currentBatch));
                currentBatch = [];
                currentBatchTokens = 0;
            }
            // Kural 3 (Soft Limit): Mevcut grup 1.800 token'ı aştıysa ve gelen eleman ikincil bir başlık/sınır ise böl
            else if (currentBatch.Count > 0 && currentBatchTokens >= SoftTokenLimit && isSemanticSplitTag)
            {
                sections.Add(CreateSection(sectionIndex++, chapterKey, $"{currentTitle} (Devam)", currentBatch));
                currentBatch = [];
                currentBatchTokens = 0;
            }
            // Kural 4: Maksimum segment sınırına ulaşıldıysa böl
            else if (currentBatch.Count >= MaxSegmentsPerSection)
            {
                sections.Add(CreateSection(sectionIndex++, chapterKey, $"{currentTitle} (Devam)", currentBatch));
                currentBatch = [];
                currentBatchTokens = 0;
            }

            if (isMajorHeading && currentBatch.Count == 0)
            {
                currentTitle = ExtractCleanTitle(seg.MaskedText);
            }

            currentBatch.Add(seg);
            currentBatchTokens += segTokens;
        }

        if (currentBatch.Count > 0)
        {
            sections.Add(CreateSection(sectionIndex++, chapterKey, currentTitle, currentBatch));
        }

        return sections;
    }

    /// <summary>
    /// Bir segmentin yaklaşık LLM token tüketimini hesaplar.
    /// Token Yaklaşımı: (Kelime Sayısı * 1.35) + (Belirteç Sayısı * 1).
    /// </summary>
    public static int EstimateTokens(HtmlSegment segment)
    {
        if (segment == null || string.IsNullOrWhiteSpace(segment.MaskedText))
        {
            return 0;
        }

        var wordCount = segment.MaskedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var placeholderCount = segment.Tokens?.Count ?? 0;
        if (placeholderCount == 0 && segment.Placeholders != null)
        {
            placeholderCount = segment.Placeholders.Count;
        }

        return Math.Max(1, (int)Math.Ceiling(wordCount * 1.35) + placeholderCount);
    }

    private static HtmlSection CreateSection(int index, string chapterKey, string title, List<HtmlSegment> segments)
    {
        return new HtmlSection
        {
            SectionIndex = index,
            SectionKey = $"{chapterKey}_sec{index:D2}",
            Title = string.IsNullOrWhiteSpace(title) ? $"Bölüm {index + 1}" : title,
            Segments = new List<HtmlSegment>(segments)
        };
    }

    private static string ExtractCleanTitle(string maskedText)
    {
        if (string.IsNullOrWhiteSpace(maskedText)) return "Başlıksız Bölüm";

        var clean = Regex.Replace(maskedText, @"⟦PH_\d+⟧|\[\[(?:CODE|INDEX)_\d+\]\]", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        if (clean.Length > 60)
        {
            clean = clean[..57] + "...";
        }

        return string.IsNullOrWhiteSpace(clean) ? "Başlıksız Bölüm" : clean;
    }
}
