using System.Text;
using System.Text.RegularExpressions;

namespace Glossator.Core.Parsing;

public partial class HtmlSection
{
    public int SectionIndex { get; init; }
    public string SectionKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<HtmlSegment> Segments { get; init; } = [];

    // Aşama 1: Kanonik çift etiket [BLOK_001] ... [/BLOK_001] veya [BLOK_001 | TAG: p] ... [/BLOK_001]
    [GeneratedRegex(@"\[BLOK_0*(\d+)(?:\s*\|[^\]]*)?\]([\s\S]*?)\[/BLOK_0*\1\]", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalBlockRegex();

    // Aşama 2: Kapanış etiketi unutulmuş lookahead bloklar (sonraki bloğa veya metin sonuna kadar)
    [GeneratedRegex(@"(?:\n|^)\[BLOK_0*(\d+)[^\]]*\]\s*([\s\S]*?)(?=(?:\n\[BLOK_0*\d+[^\]]*\]|\Z))", RegexOptions.IgnoreCase)]
    private static partial Regex LookaheadBlockRegex();

    // Aşama 3: Markdown başlık fallback (### BLOK 001, **BLOK_001**:)
    [GeneratedRegex(@"(?:\n|^)(?:#{1,4}\s*|\*{1,2})BLOK[_\s]*0*(\d+)[^:\n]*:?\*{0,2}\s*([\s\S]*?)(?=(?:\n(?:#{1,4}\s*|\*{1,2})BLOK[_\s]*\d+|\Z))", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownBlockRegex();

    /// <summary>
    /// Bu Section içindeki tüm metinlerin birleşimi (Terim taraması ve bağlam için).
    /// </summary>
    public string GetCombinedText() => string.Join("\n\n", Segments.Select(s => s.MaskedText));

    /// <summary>
    /// Translator'a ve Reviewer'a gidecek yapılandırılmış blok formatı.
    /// Her blok ID ve HTML etiket türünü açıkça taşır.
    /// </summary>
    public string ToPromptFormat()
    {
        var sb = new StringBuilder();
        foreach (var seg in Segments)
        {
            sb.AppendLine($"[BLOK_{seg.Index:D3} | TAG: {seg.TagName}]");
            sb.AppendLine(seg.MaskedText);
            sb.AppendLine($"[/BLOK_{seg.Index:D3}]");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Eksik kalan indeksler için mikro kurtarma (Delta Recovery) section'ı oluşturur.
    /// </summary>
    public HtmlSection CreateDeltaSection(IReadOnlyList<int> missingIndices)
    {
        var missingSet = missingIndices.ToHashSet();
        var deltaSegments = Segments.Where(s => missingSet.Contains(s.Index)).ToList();

        return new HtmlSection
        {
            SectionIndex = SectionIndex,
            SectionKey = $"{SectionKey}_delta",
            Title = $"{Title} (Kurtarma)",
            Segments = deltaSegments
        };
    }

    /// <summary>
    /// Modelden dönen yapılandırılmış metni 3 aşamalı (Kanonik -> Lookahead -> Markdown)
    /// dayanıklılık motoruyla ayrıştırır ve segmentlere yerleştirir.
    /// </summary>
    public bool TryUnpackTranslations(string responseText, out List<int> missingIndices)
    {
        missingIndices = [];
        if (string.IsNullOrWhiteSpace(responseText))
        {
            missingIndices.AddRange(Segments.Select(s => s.Index));
            return false;
        }

        var foundMap = new Dictionary<int, string>();

        // Aşama 1: Kanonik çift etiket ayrıştırması
        foreach (Match match in CanonicalBlockRegex().Matches(responseText))
        {
            if (int.TryParse(match.Groups[1].Value, out var idx) && !foundMap.ContainsKey(idx))
            {
                foundMap[idx] = match.Groups[2].Value.Trim();
            }
        }

        // Aşama 2: Kapanış etiketi eksik olan bloklar için lookahead ayrıştırması
        if (foundMap.Count < Segments.Count)
        {
            foreach (Match match in LookaheadBlockRegex().Matches(responseText))
            {
                if (int.TryParse(match.Groups[1].Value, out var idx) && !foundMap.ContainsKey(idx))
                {
                    var content = match.Groups[2].Value.Trim();
                    content = Regex.Replace(content, @"\[/BLOK_0*\d+\]\s*$", "", RegexOptions.IgnoreCase).Trim();
                    foundMap[idx] = content;
                }
            }
        }

        // Aşama 3: Markdown veya biçim bozulması fallback'i
        if (foundMap.Count < Segments.Count)
        {
            foreach (Match match in MarkdownBlockRegex().Matches(responseText))
            {
                if (int.TryParse(match.Groups[1].Value, out var idx) && !foundMap.ContainsKey(idx))
                {
                    foundMap[idx] = match.Groups[2].Value.Trim();
                }
            }
        }

        foreach (var seg in Segments)
        {
            if (foundMap.TryGetValue(seg.Index, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                seg.TranslatedText = translated;
            }
            else
            {
                missingIndices.Add(seg.Index);
            }
        }

        return missingIndices.Count == 0;
    }
}
