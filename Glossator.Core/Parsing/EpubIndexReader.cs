using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Parsing;

public static class EpubIndexReader
{
    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> ExcludedGenericTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "dataset", "datasets", "model", "models", "cost", "costs", "training",
        "scope", "performance", "practitioner", "practitioners", "scientist", "scientists",
        "engineer", "engineers", "tool", "tools", "design", "architecture", "architectures",
        "challenges", "solutions", "trust", "validation", "evaluation", "development",
        "algorithms", "algorithm", "audits", "audit", "value", "updates",
        "learning", "interaction", "communication", "aggregation", "operations", "operation",
        "principles", "system", "systems", "types", "memory", "testing", "risks", "risk",
        "compliance", "consistency", "coherence", "coordination", "privacy", "security",
        "benefits", "example", "examples"
    };

    /// <summary>
    /// EPUB dizin (index) dosyasından en üst seviyedeki ana teknik kavramları çeker.
    /// O'Reilly, Packt ve Wiley yayıncılarının dizin şablonlarını evrensel olarak destekler.
    /// </summary>
    public static List<GlossaryEntry> Read(EpubPackage package)
    {
        if (package.IndexItem == null || string.IsNullOrWhiteSpace(package.OpfDirectory)) return [];
        var allowedRoot = string.IsNullOrWhiteSpace(package.RootDirectory) ? package.OpfDirectory : package.RootDirectory;
        if (!Glossator.Core.Security.PathSanitizer.TryResolveWithinRoot(allowedRoot, package.OpfDirectory, package.IndexItem.Href, out var fullPath))
            return [];
        if (!File.Exists(fullPath)) return [];
        var htmlContent = File.ReadAllText(fullPath);
        var document = Parser.ParseDocument(htmlContent);
        var entries = new List<GlossaryEntry>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Kök seviyedeki liste elemanları (O'Reilly, Packt, Wiley, EPUB3)
        var rootListItems = document.QuerySelectorAll("div[data-type='indexdiv'] > ul > li");
        if (rootListItems.Length == 0)
        {
            rootListItems = document.QuerySelectorAll("nav[epub\\:type='index'] li, div.index li, section.index li, ul.index-list li, div.index-group li");
        }

        foreach (var li in rootListItems)
        {
            string? rawTerm = null;
            var termSpan = li.Children.FirstOrDefault(c =>
                c.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase) &&
                c.GetAttribute("data-type") == "index-term");

            if (termSpan != null)
            {
                rawTerm = termSpan.TextContent.Trim();
            }
            else
            {
                var aTerm = li.QuerySelector("a.index-term, span.index-term");
                if (aTerm != null)
                {
                    rawTerm = aTerm.TextContent.Trim();
                }
                else
                {
                    var firstA = li.QuerySelector("a");
                    if (firstA != null && li.TextContent.Contains(firstA.TextContent))
                    {
                        var idx = li.TextContent.IndexOf(firstA.TextContent, StringComparison.Ordinal);
                        rawTerm = idx > 0 ? li.TextContent[..idx].Trim() : firstA.TextContent.Trim();
                    }
                    else
                    {
                        rawTerm = li.TextContent.Trim();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(rawTerm)) continue;

            // Virgül kuralı: Virgülün solundaki gerçek teknik kavramı al, ardından uzunluğu kontrol et
            if (rawTerm.Contains(','))
            {
                rawTerm = rawTerm.Split(',')[0].Trim();
            }

            if (string.IsNullOrWhiteSpace(rawTerm) || rawTerm.Length < 2) continue;

            var normalized = rawTerm.ToLowerInvariant();
            if (ExcludedGenericTerms.Contains(normalized)) continue;

            if (seenKeys.Add(normalized))
            {
                entries.Add(new GlossaryEntry
                {
                    EnglishTerm = rawTerm,
                    Policy = GlossingPolicy.AlwaysTurkish
                });
            }
        }
        return entries;
    }
}