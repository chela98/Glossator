using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

public class HtmlSegment
{
    public int Index { get; init; }
    public string TagName { get; init; } = string.Empty;
    public string OriginalInnerHtml { get; init; } = string.Empty;

    /// <summary>
    /// Kod ve dizin çapaları [[CODE_0]], [[INDEX_0]] şeklinde maskelenmiş, çeviriye gidecek metin.
    /// </summary>
    public string MaskedText { get; init; } = string.Empty;

    /// <summary>
    /// Yer tutucu -> Orijinal HTML parçası eşleştirmesi
    /// </summary>
    public Dictionary<string, string> Placeholders { get; init; } = [];

    /// <summary>
    /// AngleSharp DOM ağacındaki orijinal element referansı (çeviri bitince doğrudan güncellenir)
    /// </summary>
    public IElement DomElement { get; init; } = null!;

    public string? TranslatedText { get; set; }

    /// <summary>
    /// Çevrilen metindeki [[CODE_0]] etiketlerini orijinal HTML parçalarıyla geri takar.
    /// </summary>
    public string ReconstructHtml()
    {
        if (string.IsNullOrWhiteSpace(TranslatedText))
        {
            return OriginalInnerHtml;
        }

        var result = TranslatedText;
        foreach (var (placeholder, originalHtml) in Placeholders)
        {
            result = result.Replace(placeholder, originalHtml);
        }

        return result;
    }
}