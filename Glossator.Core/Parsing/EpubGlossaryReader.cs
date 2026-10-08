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
        var dtElements = document.QuerySelectorAll("dt").ToList();

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

            entries.Add(new GlossaryEntry
            {
                EnglishTerm = termText,
                EnglishDefinition = definition,
                Policy = GlossingPolicy.ProgressiveGloss
            });
        }

        return entries;
    }
}