using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Parsing;

public static class EpubIndexReader
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// EPUB dizin (index) dosyasından YALNIZCA en üst seviyedeki (kök) ana teknik kavramları çeker.
    /// Alt dallardaki arama notlarını (örn: - repairing, - building in agentic systems) eler.
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
            // Örn: "generative UIs, graphical agent interfaces" -> "generative UIs"
            // Örn: "entity recognition and extraction, building..." -> "entity recognition and extraction"
            if (rawTerm.Contains(','))
            {
                rawTerm = rawTerm.Split(',')[0].Trim();
            }
            var normalized = rawTerm.ToLowerInvariant();
            if (seenKeys.Add(normalized))
            {
                entries.Add(new GlossaryEntry
                {
                    EnglishTerm = rawTerm,
                    Policy = GlossingPolicy.ProgressiveGloss
                });
            }
        }
        return entries;
    }
}