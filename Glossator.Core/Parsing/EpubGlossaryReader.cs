using AngleSharp.Html.Parser;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;

namespace Glossator.Core.Parsing;

public static class EpubGlossaryReader
{
    private static readonly HtmlParser Parser = new();

    public static List<GlossaryEntry> Read(EpubPackage package)
    {
        if (package.GlossaryItem == null) return [];

        var fullPath = Path.Combine(package.OpfDirectory, package.GlossaryItem.Href);
        if (!File.Exists(fullPath)) return [];

        var htmlContent = File.ReadAllText(fullPath);
        var document = Parser.ParseDocument(htmlContent);

        var entries = new List<GlossaryEntry>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dtElements = document.QuerySelectorAll("dt").ToList();

        if (dtElements.Count > 0)
        {
            foreach (var dt in dtElements)
            {
                var termText = dt.TextContent.Trim();
                if (string.IsNullOrEmpty(termText)) continue;

                var dd = dt.NextElementSibling;
                string? definition = null;

                if (dd != null && dd.TagName.Equals("DD", StringComparison.OrdinalIgnoreCase))
                {
                    definition = dd.TextContent.Trim();
                }

                if (seenKeys.Add(termText))
                {
                    entries.Add(new GlossaryEntry
                    {
                        EnglishTerm = termText,
                        EnglishDefinition = definition,
                        Policy = GlossingPolicy.ProgressiveGloss
                    });
                }
            }
        }
        else
        {
            // Packt / Wiley alternatifleri: div.glossary-entry, p.glossary-term, vb.
            var altEntries = document.QuerySelectorAll("div.glossary-entry, div.entry, p.glossary-item");
            foreach (var el in altEntries)
            {
                var termEl = el.QuerySelector("strong, b, h3, h4, span.term");
                if (termEl == null) continue;
                var termText = termEl.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(termText)) continue;

                var defEl = el.QuerySelector("p:not(:has(strong)), span.definition");
                var def = defEl?.TextContent.Trim();

                if (seenKeys.Add(termText))
                {
                    entries.Add(new GlossaryEntry
                    {
                        EnglishTerm = termText,
                        EnglishDefinition = def,
                        Policy = GlossingPolicy.ProgressiveGloss
                    });
                }
            }
        }

        return entries;
    }
}