using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Glossator.Core.Parsing;

/// <summary>
/// AngleSharp DOM ağacını dolaşarak teknik düğümleri (kod, formül, SVG, dizin etiketleri)
/// deterministik AST seviyesinde yer tutucularla takas eden ve çeviri sonrasında geri yükleyen sınıf.
/// </summary>
public sealed partial class DomTreeWalkerMasker
{
    public static readonly DomTreeWalkerMasker Instance = new();

    private static readonly HashSet<string> ProtectedTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "kbd", "samp", "var", "math", "svg", "tt"
    };

    [GeneratedRegex(@"[\[⟦{(]{1,2}\s*(?:PH|CODE|INDEX|VAR|MATH|SVG)[_\-\s]*(\d+)\s*[\]⟧})]{1,2}", RegexOptions.IgnoreCase)]
    private static partial Regex FuzzyPlaceholderRegex();

    [GeneratedRegex(@"<[^>]+>|&[a-zA-Z0-9#]+;", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagOrEntityRegex();

    // Tek kelimelik olduğunda dahi mutlaka Türkçeye çevrilmesi gereken genel İngilizce yapısal başlık ve terimler
    private static readonly HashSet<string> TranslatableSingleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Note", "Notes", "Tip", "Tips", "Warning", "Warnings", "Caution", "Important",
        "Summary", "Introduction", "Preface", "Foreword", "Conclusion", "Conclusions", "Appendix", "Glossary", "Index", "References", "Bibliography",
        "Overview", "Background", "Prerequisites", "Requirements", "Objectives", "Goals",
        "Performance", "Cost", "Costs", "Modularity", "Flexibility", "Scalability", "Security", "Reliability", "Availability", "Maintainability",
        "Architecture", "Design", "Implementation", "Evaluation", "Testing", "Validation", "Verification", "Optimization",
        "Example", "Examples", "Figure", "Figures", "Table", "Tables", "Chapter", "Chapters", "Part", "Parts", "Section", "Sections",
        "Problem", "Problems", "Solution", "Solutions", "Challenge", "Challenges", "Limitation", "Limitations",
        "Advantage", "Advantages", "Disadvantage", "Disadvantages", "Benefit", "Benefits", "Strengths", "Weaknesses",
        "Context", "Memory", "Tools", "Nodes", "Edges", "Properties", "Graph", "Graphs", "Hypergraph", "Hypergraphs", "Broadcast"
    };

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
    /// Verilen metnin yalnızca yer tutuculardan (⟦PH_xxxxxx⟧), HTML etiketlerinden, sayılardan, boşluklardan,
    /// noktalama işaretlerinden veya çevrilmemesi doğal olan tek kelimelik özel ürün/teknoloji adlarından (örn: Cognee, Mem0, Zep, Neo4j)
    /// oluşup oluşmadığını kontrol eder.
    /// </summary>
    public static bool IsPureTechnicalOrPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var withoutPlaceholders = FuzzyPlaceholderRegex().Replace(text, " ");
        var visibleText = HtmlTagOrEntityRegex().Replace(withoutPlaceholders, " ").Trim();

        if (string.IsNullOrWhiteSpace(visibleText)) return true;

        var letterCount = 0;
        foreach (var c in visibleText)
        {
            if (char.IsLetter(c))
            {
                letterCount++;
            }
        }

        if (letterCount < 2) return true;

        // Eğer görünür metin boşluk içermeyen tek bir kelimeyse (örn: "Cognee", "Mem0", "Zep", "Neo4j", "SPARQL"):
        // Genel İngilizce başlık kelimelerinden biri değilse ve büyük harfle başlıyorsa veya rakam/sembol içeriyorsa
        // özel isim / teknik tanımlayıcı kabul edilir.
        if (!visibleText.Any(char.IsWhiteSpace))
        {
            var cleanWord = visibleText.Trim('.', ',', ':', ';', '-', '–', '—', '(', ')', '[', ']', '"', '\'', '“', '”');
            if (!string.IsNullOrEmpty(cleanWord) && !TranslatableSingleWords.Contains(cleanWord))
            {
                bool hasDigitOrSymbol = cleanWord.Any(c => char.IsDigit(c) || c is '_' or '/' or '+' or '#');
                bool startsWithUpper = char.IsUpper(cleanWord[0]);
                if (hasDigitOrSymbol || startsWithUpper)
                {
                    return true;
                }
            }
        }

        return false;
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
            if (normalized.Contains(t.Token, StringComparison.Ordinal))
            {
                normalized = normalized.Replace(t.Token, t.OriginalMarkup, StringComparison.Ordinal);
            }
            else
            {
                // Parite Koruması: Model bu belirteci metin içinden düşürdüyse,
                // kod bloğu, bağlantı veya görselin sessizce yok olmasını önlemek için orijinal işaretlemeyi sonuna iliştir
                normalized += " " + t.OriginalMarkup;
            }
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

        // Dizin çapaları, dipnot referansları ve dış web bağlantıları (http/https)
        if (tag == "a" && (
            element.GetAttribute("href")?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ||
            element.GetAttribute("data-type") == "indexterm" ||
            element.GetAttribute("epub:type") == "indexterm" ||
            element.GetAttribute("epub:type") == "noteref" ||
            element.ClassList.Contains("indexterm") ||
            element.ClassList.Contains("customlink")))
        {
            return true;
        }

        // Akademik atıf sarmalayıcıları (<span class="customlink">)
        if (tag == "span" && element.ClassList.Contains("customlink"))
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
