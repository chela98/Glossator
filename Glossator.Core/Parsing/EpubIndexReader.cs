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
        "benefits", "example", "examples", "swarms", "swarm", "video", "voice", "speech",
        "text", "graphs", "chains", "interfaces", "governance", "adoption", "alignment"
    };

    /// <summary>
    /// EPUB dizin (index) dosyasından YALNIZCA en üst seviyedeki (kök) ana teknik kavramları çeker.
    /// Genel sözlük kelimelerini eler, yalnızca gerçek teknik kavramları alır.
    /// </summary>
    public static List<GlossaryEntry> Read(EpubPackage package)
    {
        if (package.IndexItem == null) return [];
        var fullPath = Path.Combine(package.OpfDirectory, package.IndexItem.Href);
        if (!File.Exists(fullPath)) return [];
        var htmlContent = File.ReadAllText(fullPath);
        var document = Parser.ParseDocument(htmlContent);
        var entries = new List<GlossaryEntry>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Kök seviyedeki liste elemanları
        var rootListItems = document.QuerySelectorAll("div[data-type='indexdiv'] > ul > li");
        foreach (var li in rootListItems)
        {
            var termSpan = li.Children.FirstOrDefault(c =>
                c.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase) &&
                c.GetAttribute("data-type") == "index-term");
            if (termSpan == null) continue;
            var rawTerm = termSpan.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(rawTerm) || rawTerm.Length < 2) continue;
            // 1. VİRGÜL KURALI: Virgülün solundaki gerçek teknik kavramı al!
            if (rawTerm.Contains(','))
            {
                rawTerm = rawTerm.Split(',')[0].Trim();
            }
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