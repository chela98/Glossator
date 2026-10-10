using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glossator.Core.Packaging;

namespace Glossator.Core.Parsing;

public static class HtmlSegmenter
{
    private static readonly HtmlParser Parser = new();

    // Çevrilecek metin içeren blok elementler
    private static readonly HashSet<string> TranslatableTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "figcaption", "caption", "th", "td", "blockquote", "dt", "dd", "li"
    };

    /// <summary>
    /// HTML dosyasını açar ve çevrilecek güvenli parçalara (segment) ayırır.
    /// <pre> blokları ve kodlar asla bozulmaz.
    /// </summary>
    public static (IDocument Document, List<HtmlSegment> Segments) SegmentFile(string htmlFilePath)
    {
        var htmlContent = File.ReadAllText(htmlFilePath);
        return SegmentHtml(htmlContent);
    }

    public static (IDocument Document, List<HtmlSegment> Segments) SegmentHtml(string htmlContent)
    {
        var document = Parser.ParseDocument(htmlContent);
        var segments = new List<HtmlSegment>();
        var segmentIndex = 0;

        var allElements = document.QuerySelectorAll("*");

        foreach (var element in allElements)
        {
            var tagName = element.TagName.ToLowerInvariant();

            // <pre> bloklarının içindeki hiçbir şeye dokunma!
            if (IsInsidePre(element))
            {
                continue;
            }

            // Metin bloğu ise (<p>, <h1>, <th> vb.) ve içinde başka kapsayıcı blok (<p>, <div>, <pre>, <table> vb.) barındırmıyorsa
            if (TranslatableTags.Contains(tagName) && !element.QuerySelectorAll("p, div, pre, table, ul, ol").Any())
            {
                var innerHtml = element.InnerHtml.Trim();
                if (string.IsNullOrWhiteSpace(innerHtml)) continue;

                // AST Maskeleme işlemi: <code>, <kbd>, <math>, <svg> ve index etiketlerini TreeWalker ile güvene al
                var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(element);

                if (!string.IsNullOrWhiteSpace(maskedText))
                {
                    var placeholders = tokens.ToDictionary(t => t.Token, t => t.OriginalMarkup);

                    segments.Add(new HtmlSegment
                    {
                        Index = segmentIndex++,
                        TagName = tagName,
                        OriginalInnerHtml = innerHtml,
                        MaskedText = maskedText,
                        Tokens = tokens,
                        Placeholders = placeholders,
                        DomElement = element
                    });
                }
            }
        }

        return (document, segments);
    }

    /// <summary>
    /// Çevirisi biten segment'leri DOM içine geri yazar.
    /// </summary>
    public static void ApplyTranslations(List<HtmlSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment.TranslatedText))
            {
                var restoredHtml = segment.ReconstructHtml();
                segment.DomElement.InnerHtml = restoredHtml;
            }
        }
    }

    /// <summary>
    /// AngleSharp DOM belgesini Google Play Books, Apple Books ve W3C EPUB standartlarına uygun
    /// katı XML/XHTML olarak serialize eder.
    /// </summary>
    public static string ToXhtml(this IDocument document)
    {
        return document.ToStrictXhtml();
    }

    private static bool IsInsidePre(IElement element)
    {
        var parent = element.ParentElement;
        while (parent != null)
        {
            if (parent.TagName.Equals("PRE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            parent = parent.ParentElement;
        }
        return false;
    }
}