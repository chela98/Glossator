using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

public class HtmlSegment
{
    public int Index { get; init; }
    public string TagName { get; init; } = string.Empty;
    public string OriginalInnerHtml { get; init; } = string.Empty;

    /// <summary>
    /// Kod, formül ve dizin çapaları ⟦PH_000001⟧ şeklinde maskelenmiş, çeviriye gidecek metin.
    /// </summary>
    public string MaskedText { get; init; } = string.Empty;

    /// <summary>
    /// DOM TreeWalker tarafından üretilen AST maskeleme belirteçleri.
    /// </summary>
    public IReadOnlyList<MaskingToken> Tokens { get; init; } = [];

    /// <summary>
    /// Yer tutucu -> Orijinal HTML parçası eşleştirmesi (geriye dönük uyumluluk).
    /// </summary>
    public Dictionary<string, string> Placeholders { get; init; } = [];

    /// <summary>
    /// AngleSharp DOM ağacındaki orijinal element referansı (çeviri bitince doğrudan güncellenir).
    /// </summary>
    public IElement DomElement { get; init; } = null!;

    public string? TranslatedText { get; set; }

    /// <summary>
    /// Çevrilen metindeki belirteçleri orijinal HTML parçalarıyla deterministik olarak geri takar.
    /// </summary>
    public string ReconstructHtml()
    {
        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return OriginalInnerHtml;
        }

        var text = DomTreeWalkerMasker.NormalizeTokens(TranslatedText);

        if (Tokens != null && Tokens.Count > 0)
        {
            return DomTreeWalkerMasker.RestorePlaceholders(text, Tokens);
        }

        if (Placeholders != null && Placeholders.Count > 0)
        {
            var ordered = Placeholders.OrderByDescending(p => p.Key.Length).ThenByDescending(p => p.Key);
            foreach (var (placeholder, originalHtml) in ordered)
            {
                text = text.Replace(placeholder, originalHtml, StringComparison.Ordinal);
            }
        }

        return text;
    }
}