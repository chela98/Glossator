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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceCollapseRegex();

    [GeneratedRegex(@"^(?:P\.?\s*O\.?\s*Box|Post\s+Office\s+Box)\s+\d[\w\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex PoBoxAddressRegex();

    [GeneratedRegex(@"^(?:\p{Lu}[\p{L}'’.\-]+(?:\s+\p{Lu}[\p{L}'’.\-]+){0,3},\s*\p{Lu}{2,3}\s+\d{4,6}(?:-\d{4})?|\p{Lu}[\p{L}'’.\-]+(?:\s+\p{Lu}[\p{L}'’.\-]+){0,2},\s*(?:\p{Lu}{2}\s+)?[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})$")]
    private static partial Regex CityPostalAddressRegex();

    [GeneratedRegex(@"\s*(?:,|;|&|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeparatorRegex();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingParentheticalRegex();

    private static readonly HashSet<string> LegalEntitySuffixes = new(StringComparer.Ordinal)
    {
        "Inc", "INC", "LLC", "Ltd", "LTD", "Corp", "CORP", "Co", "GmbH", "LLP", "PLLC", "AG", "LP", "Pty", "SpA", "SRL", "A.Ş"
    };

    private static readonly HashSet<string> EntityConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "&", "and", "of", "the", "for", "de", "la", "van", "von", "et", "ve"
    };

    private static readonly HashSet<string> AddressDesignators = new(StringComparer.OrdinalIgnoreCase)
    {
        "Street", "St", "Avenue", "Ave", "Boulevard", "Blvd", "Road", "Rd",
        "Circle", "Cir", "Drive", "Dr", "Lane", "Ln", "Court", "Ct",
        "Way", "Place", "Pl", "Plaza", "Square", "Sq", "Highway", "Hwy",
        "Parkway", "Pkwy", "Terrace", "Ter", "Suite", "Ste", "Apt", "Unit",
        "Floor", "Fl", "Room", "Rm"
    };

    private static readonly HashSet<string> PersonNameSuffixes = new(StringComparer.Ordinal)
    {
        "Jr", "Sr", "II", "III", "IV", "Ph.D", "PhD", "M.D", "MD"
    };

    private static readonly HashSet<string> AbbreviationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inc.", "Ltd.", "Co.", "Corp.", "Jr.", "Sr.", "St.", "Ave.", "Rd.", "Blvd.", "Dr.", "Ste.", "Ph.D.", "M.D.", "P.O."
    };

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

    internal static string ExtractVisibleText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var withoutPlaceholders = FuzzyPlaceholderRegex().Replace(text, " ");
        var stripped = HtmlTagOrEntityRegex().Replace(withoutPlaceholders, " ");
        return WhitespaceCollapseRegex().Replace(stripped, " ").Trim();
    }

    internal static string NormalizeComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        return WhitespaceCollapseRegex().Replace(value.Trim(), " ")
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('–', '-')
            .Replace('—', '-')
            .Trim();
    }

    /// <summary>
    /// Verilen metnin yalnızca yer tutuculardan (⟦PH_xxxxxx⟧), HTML etiketlerinden, sayılardan, boşluklardan,
    /// noktalama işaretlerinden, e-posta/URL/alan adlarından, yasal kuruluş veya açık adres satırlarından
    /// ya da çevrilmemesi doğal olan tek kelimelik özel ürün/teknoloji adlarından (örn: Cognee, Mem0, Zep, Neo4j)
    /// oluşup oluşmadığını kontrol eder.
    /// </summary>
    public static bool IsPureTechnicalOrPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var visibleText = ExtractVisibleText(text);
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

        // Eğer görünür metin boşluk içermeyen tek bir kelimeyse (örn: "Cognee", "Mem0", "Zep", "support@oreilly.com"):
        // Genel İngilizce başlık kelimelerinden biri değilse ve büyük harf, rakam/sembol veya iç nokta içeriyorsa
        // özel isim / e-posta / alan adı / teknik tanımlayıcı kabul edilir.
        if (!visibleText.Any(char.IsWhiteSpace))
        {
            var cleanWord = visibleText.Trim('.', ',', ':', ';', '-', '–', '—', '(', ')', '[', ']', '"', '\'', '“', '”');
            if (!string.IsNullOrEmpty(cleanWord) && !TranslatableSingleWords.Contains(cleanWord))
            {
                bool hasDigitOrSymbol = cleanWord.Any(c => char.IsDigit(c) || c is '_' or '/' or '\\' or '+' or '#' or '@' or ':');
                bool hasInternalDot = cleanWord.Contains('.');
                bool startsWithUpper = char.IsUpper(cleanWord[0]);
                if (hasDigitOrSymbol || hasInternalDot || startsWithUpper)
                {
                    return true;
                }
            }
        }

        if (IsContactAddressOrLegalEntity(visibleText))
        {
            return true;
        }

        return false;
    }

    internal static bool MatchesBookMetadata(string visibleText, string? bookTitle, string? bookAuthor)
    {
        var candidate = NormalizeComparableText(visibleText).Trim('.', ':', ' ');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(bookTitle))
        {
            var normalizedTitle = NormalizeComparableText(bookTitle).Trim('.', ':', ' ');
            if (MatchesTitleVariant(candidate, normalizedTitle))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(bookAuthor))
        {
            var normalizedAuthor = NormalizeComparableText(bookAuthor).Trim('.', ':', ' ');
            if (string.Equals(candidate, normalizedAuthor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var part in AuthorSeparatorRegex().Split(normalizedAuthor))
            {
                var trimmed = part.Trim();
                if (trimmed.Length >= 3 && string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool IsBrandOrProperEntityPhrase(string visibleText)
    {
        if (string.IsNullOrWhiteSpace(visibleText) || visibleText.EndsWith('?') || visibleText.EndsWith('!'))
        {
            return false;
        }

        var tokens = visibleText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 6)
        {
            return false;
        }

        bool hasBrandOrNameSignature = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            var raw = tokens[i];
            if (HasMidSentencePeriod(raw, isLastToken: i == tokens.Length - 1))
            {
                return false;
            }

            var clean = raw.Trim('.', ',', ':', ';', '(', ')', '[', ']', '"', '\'', '“', '”');
            if (string.IsNullOrEmpty(clean))
            {
                return false;
            }

            if (TranslatableSingleWords.Contains(clean))
            {
                return false;
            }

            if (i > 0 && EntityConnectors.Contains(clean))
            {
                continue;
            }

            if (!char.IsUpper(clean[0]) && !char.IsDigit(clean[0]))
            {
                return false;
            }

            if (HasBrandTokenSignature(clean) || (i == tokens.Length - 1 && PersonNameSuffixes.Contains(clean)))
            {
                hasBrandOrNameSignature = true;
            }
        }

        return hasBrandOrNameSignature;
    }

    private static bool MatchesTitleVariant(string candidate, string normalizedTitle)
    {
        if (string.Equals(candidate, normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var withoutSuffix = TrailingParentheticalRegex().Replace(normalizedTitle, string.Empty).Trim();
        if (withoutSuffix.Length >= 2 && string.Equals(candidate, withoutSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colonIdx = withoutSuffix.IndexOf(':');
        if (colonIdx > 1)
        {
            var mainTitle = withoutSuffix[..colonIdx].Trim();
            if (mainTitle.Length >= 2 && string.Equals(candidate, mainTitle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContactAddressOrLegalEntity(string visibleText)
    {
        if (visibleText.EndsWith('?') || visibleText.EndsWith('!'))
        {
            return false;
        }

        if (PoBoxAddressRegex().IsMatch(visibleText) || CityPostalAddressRegex().IsMatch(visibleText))
        {
            return true;
        }

        var tokens = visibleText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 10)
        {
            return false;
        }

        return IsLegalEntityName(tokens) || IsStreetAddress(tokens);
    }

    private static bool IsLegalEntityName(string[] tokens)
    {
        if (tokens.Length > 8)
        {
            return false;
        }

        var lastClean = tokens[^1].Trim('.', ',', ';', ')', ']');
        if (!LegalEntitySuffixes.Contains(lastClean))
        {
            return false;
        }

        bool hasAlphabeticWord = false;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            var raw = tokens[i];
            if (HasMidSentencePeriod(raw, isLastToken: false))
            {
                return false;
            }

            var clean = raw.Trim('.', ',', ';', '(', ')', '"', '\'', '“', '”');
            if (string.IsNullOrEmpty(clean))
            {
                return false;
            }

            if (EntityConnectors.Contains(clean))
            {
                continue;
            }

            if (!char.IsUpper(clean[0]) && !char.IsDigit(clean[0]))
            {
                return false;
            }

            if (clean.Any(char.IsLetter))
            {
                hasAlphabeticWord = true;
            }
        }

        return hasAlphabeticWord;
    }

    private static bool IsStreetAddress(string[] tokens)
    {
        var first = tokens[0];
        // "1. Chapter Overview" gibi numaralı başlık/liste öğelerini ele
        if (first.EndsWith('.') || !char.IsDigit(first[0]))
        {
            return false;
        }

        bool hasAddressDesignator = false;
        int numericTokenCount = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            var raw = tokens[i];
            if (HasMidSentencePeriod(raw, isLastToken: i == tokens.Length - 1))
            {
                return false;
            }

            var clean = raw.Trim('.', ',', ';', '#', '(', ')');
            if (string.IsNullOrEmpty(clean))
            {
                return false;
            }

            if (clean.Any(char.IsDigit))
            {
                numericTokenCount++;
            }

            if (AddressDesignators.Contains(clean))
            {
                hasAddressDesignator = true;
                continue;
            }

            if (i > 0 && EntityConnectors.Contains(clean))
            {
                continue;
            }

            if (!char.IsUpper(clean[0]) && !char.IsDigit(clean[0]))
            {
                return false;
            }
        }

        return hasAddressDesignator || numericTokenCount >= 2;
    }

    private static bool HasMidSentencePeriod(string rawToken, bool isLastToken)
    {
        if (rawToken.EndsWith('?') || rawToken.EndsWith('!'))
        {
            return true;
        }

        if (isLastToken || !rawToken.EndsWith('.'))
        {
            return false;
        }

        if (AbbreviationTokens.Contains(rawToken))
        {
            return false;
        }

        // Tek harfli orta ad kısaltmalarına izin ver (örn. "Paula L. Fleming")
        var stem = rawToken.TrimEnd('.');
        return stem.Length > 1;
    }

    private static bool HasBrandTokenSignature(string cleanToken)
    {
        for (int i = 1; i < cleanToken.Length; i++)
        {
            var prev = cleanToken[i - 1];
            var curr = cleanToken[i];
            if ((char.IsLower(prev) && char.IsUpper(curr)) ||
                ((prev is '\'' or '’') && char.IsUpper(curr)))
            {
                return true;
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
