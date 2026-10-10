using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

/// <summary>
/// AngleSharp DOM ağacını dolaşarak teknik düğümleri (kod, formül, SVG, dizin etiketleri)
/// deterministik AST seviyesinde yer tutucularla takas eden ve çeviri sonrasında geri yükleyen sınıf.
/// </summary>
public sealed partial class DomTreeWalkerMasker : IDomMasker
{
    public static readonly DomTreeWalkerMasker Instance = new();

    private static readonly HashSet<string> ProtectedTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "kbd", "samp", "var", "math", "svg", "tt"
    };

    [GeneratedRegex(@"[\[⟦]{1,2}\s*(?:PH|CODE|INDEX|VAR|MATH|SVG)[_\-\s]*(\d+)\s*[\]⟧]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex FuzzyPlaceholderRegex();

    /// <summary>
    /// LLM tarafından biçimi bozulmuş veya eski formatta dönen belirteçleri kanonik ⟦PH_000001⟧ formatına normalize eder.
    /// </summary>
    public static string NormalizeTokens(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        return FuzzyPlaceholderRegex().Replace(input, match =>
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
            {
                return $"⟦PH_{id:D6}⟧";
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Verilen metindeki ⟦PH_xxxxxx⟧ belirteçlerini deterministik azalan uzunluk sırasıyla
    /// orijinal HTML/XML parçalarıyla takas eder.
    /// </summary>
    public static string RestorePlaceholders(string text, IReadOnlyList<MaskingToken> tokens)
    {
        if (string.IsNullOrWhiteSpace(text) || tokens == null || tokens.Count == 0)
        {
            return text;
        }

        var normalized = NormalizeTokens(text);
        var ordered = tokens.OrderByDescending(t => t.Token.Length).ThenByDescending(t => t.Token);

        foreach (var t in ordered)
        {
            normalized = normalized.Replace(t.Token, t.OriginalMarkup, StringComparison.Ordinal);
        }

        return normalized;
    }

    /// <inheritdoc />
    public (string MaskedInnerHtml, IReadOnlyList<MaskingToken> Tokens) MaskElement(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var tokens = new List<MaskingToken>();
        var tokenCounter = 1;

        // Orijinal DOM'u korumak için derin klon al
        var clone = (IElement)element.Clone(deep: true);

        // Korumaya aday elemanları topla
        var allDescendants = clone.QuerySelectorAll("*").ToList();

        foreach (var target in allDescendants)
        {
            if (!IsProtected(target))
            {
                continue;
            }

            // Eğer hedef elemanın üst soyunda zaten korunan bir eleman varsa içteki elemanı ayrıca maskeleme
            if (HasProtectedAncestor(target, clone))
            {
                continue;
            }

            var tokenId = tokenCounter++;
            var tokenString = $"⟦PH_{tokenId:D6}⟧";
            var originalMarkup = target.OuterHtml;

            tokens.Add(new MaskingToken(tokenString, tokenId, originalMarkup, target.TagName.ToLowerInvariant()));

            // DOM Düğüm Takası: Hedef elemanı kaldır ve yerine TextNode yerleştir
            var placeholderNode = clone.Owner!.CreateTextNode(tokenString);
            target.ReplaceWith(placeholderNode);
        }

        return (clone.InnerHtml.Trim(), tokens);
    }

    /// <inheritdoc />
    public void UnmaskElement(IElement element, string translatedInnerHtml, IReadOnlyList<MaskingToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (string.IsNullOrWhiteSpace(translatedInnerHtml))
        {
            return;
        }

        // 1. LLM belirteç mutasyonlarını kanonik formata çek
        var normalized = NormalizeTokens(translatedInnerHtml);

        // 2. Token parite kontrolü: Eksik belirteç varsa veri bütünlüğü ihlal edilmiştir
        if (tokens != null && tokens.Count > 0)
        {
            foreach (var t in tokens)
            {
                if (!normalized.Contains(t.Token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Kritik Token Kaybı: '{t.Token}' ({t.TagName}) çeviri metninde bulunamadı! " +
                        $"Orijinal işaretleme: {t.OriginalMarkup}");
                }
            }

            // 3. Deterministik azalan uzunluk sırasıyla geri takas
            var ordered = tokens.OrderByDescending(t => t.Token.Length).ThenByDescending(t => t.Token);
            foreach (var t in ordered)
            {
                normalized = normalized.Replace(t.Token, t.OriginalMarkup, StringComparison.Ordinal);
            }
        }

        element.InnerHtml = normalized;
    }

    private static bool HasProtectedAncestor(IElement element, IElement root)
    {
        var current = element.ParentElement;
        while (current != null && current != root)
        {
            if (IsProtected(current))
            {
                return true;
            }
            current = current.ParentElement;
        }
        return false;
    }

    private static bool IsProtected(IElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        if (ProtectedTagNames.Contains(tag))
        {
            return true;
        }

        // Dizin çapaları ve dipnot referansları
        if (tag == "a" && (
            element.GetAttribute("data-type") == "indexterm" ||
            element.GetAttribute("epub:type") == "indexterm" ||
            element.GetAttribute("epub:type") == "noteref" ||
            element.ClassList.Contains("indexterm")))
        {
            return true;
        }

        // Dipnot üst simgeleri: <sup class="footnote"> veya <sup id="fn*">
        if (tag == "sup" && (
            element.ClassList.Contains("footnote") ||
            (element.Id != null && element.Id.StartsWith("fn", StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return false;
    }
}
