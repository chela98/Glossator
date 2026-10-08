using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Glossator.Core.Parsing;

public static class HtmlSegmenter
{
    private static readonly HtmlParser Parser = new();

    // Çevrilecek metin içeren blok elementler
    private static readonly HashSet<string> TranslatableTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "figcaption", "blockquote", "dt", "dd"
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

            // Metin bloğu ise (<p>, <h1> vb.) ve içinde başka alt blok (<p>, <div>) barındırmıyorsa
            if (TranslatableTags.Contains(tagName) && !element.QuerySelectorAll("p, div").Any())
            {
                var innerHtml = element.InnerHtml.Trim();
                if (string.IsNullOrWhiteSpace(innerHtml)) continue;

                // Maskeleme işlemi: <code> ve index etiketlerini güvene al
                var (maskedText, placeholders) = MaskInlineTags(innerHtml);

                if (!string.IsNullOrWhiteSpace(maskedText))
                {
                    segments.Add(new HtmlSegment
                    {
                        Index = segmentIndex++,
                        TagName = tagName,
                        OriginalInnerHtml = innerHtml,
                        MaskedText = maskedText,
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
    /// Paragraf içindeki <code>, <kbd>, ve O'Reilly dizin çapalarını ([[CODE_0]], [[INDEX_0]]) maskeler.
    /// </summary>
    private static (string MaskedText, Dictionary<string, string> Placeholders) MaskInlineTags(string innerHtml)
    {
        var placeholders = new Dictionary<string, string>();
        var tokenCounter = 0;
        var html = innerHtml;

        // 1. <a data-type="indexterm"...> etiketlerini maskele (O'Reilly dizin çapaları bozulmasın)
        html = Regex.Replace(html, @"<a[^>]+data-type=""indexterm""[^>]*>.*?</a>|<a[^>]+data-type=""indexterm""[^>]*/>|<a[^>]+data-type=""indexterm""[^>]*>", m =>
        {
            var token = $"[[INDEX_{tokenCounter++}]]";
            placeholders[token] = m.Value;
            return token;
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // 2. <code>...</code> ve <kbd>...</kbd> etiketlerini maskele (Kodlar çevrilmesin)
        html = Regex.Replace(html, @"<(code|kbd)[^>]*>.*?</\1>", m =>
        {
            var token = $"[[CODE_{tokenCounter++}]]";
            placeholders[token] = m.Value;
            return token;
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return (html, placeholders);
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