using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glossator.Core.Packaging;

namespace Glossator.Core.Parsing;

public static partial class HtmlSegmenter
{
    private static readonly HtmlParser Parser = new();

    // Çevrilecek metin içeren blok elementler
    private static readonly HashSet<string> TranslatableTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "figcaption", "caption", "th", "td", "blockquote", "dt", "dd", "li"
    };

    // Alt blok kabul edilen elementler
    private static readonly HashSet<string> ChildBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "pre", "table", "ul", "ol", "blockquote", "figure", "section", "article", "header", "footer"
    };

    [GeneratedRegex(@"<(?<tag>a|span|abbr|sup|sub|em|strong|b|i)(?<attrs>(?:\s+[^>]*?)?)\s*/>", RegexOptions.IgnoreCase)]
    private static partial Regex SelfClosingInlineTagRegex();

    /// <summary>
    /// EPUB/XHTML dosyalarındaki self-closing satır içi etiketleri (&lt;a .../&gt;, &lt;span .../&gt; vb.)
    /// AngleSharp HTML5 ayrıştırıcısının etiketleri açık bırakıp devamındaki edebi metni yutmasını önlemek için
    /// &lt;a ...&gt;&lt;/a&gt; şeklinde tam kapatılmış etiketlere dönüştürür.
    /// </summary>
    public static string NormalizeXhtmlContent(string htmlContent)
    {
        if (string.IsNullOrEmpty(htmlContent)) return htmlContent;
        return SelfClosingInlineTagRegex().Replace(htmlContent, "<${tag}${attrs}></${tag}>");
    }

    /// <summary>
    /// HTML dosyasını açar ve çevrilecek güvenli parçalara (segment) ayırır.
    /// &lt;pre&gt; blokları ve kodlar asla bozulmaz.
    /// </summary>
    public static (IDocument Document, List<HtmlSegment> Segments) SegmentFile(string htmlFilePath)
    {
        var htmlContent = File.ReadAllText(htmlFilePath);
        return SegmentHtml(htmlContent);
    }

    public static (IDocument Document, List<HtmlSegment> Segments) SegmentHtml(string htmlContent)
    {
        var normalizedContent = NormalizeXhtmlContent(htmlContent);
        var document = Parser.ParseDocument(normalizedContent);
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
                    segments.Add(new HtmlSegment
                    {
                        Index = segmentIndex++,
                        TagName = tagName,
                        OriginalInnerHtml = innerHtml,
                        MaskedText = maskedText,
                        Tokens = tokens,
                        DomElement = element
                    });
                }
            }
            else if (TranslatableTags.Contains(tagName))
            {
                // Eleman içinde alt bloklar (örn. <li> içinde alt <ul> veya alt <ol>) var.
                // Bu durumda doğrudan çocuk olan satır içi düğümleri (text nodes, <span>, <code>, <strong> vb.)
                // blok düğümleri arasında gruplayarak ayrı segmentler olarak yakalayalım.
                var childNodes = element.ChildNodes.ToList();
                var currentRun = new List<INode>();

                void FlushCurrentRun()
                {
                    if (currentRun.Count == 0) return;

                    var combinedText = string.Concat(currentRun.Select(n => n.TextContent)).Trim();
                    if (!string.IsNullOrWhiteSpace(combinedText))
                    {
                        var wrapper = document.CreateElement("div");
                        foreach (var node in currentRun)
                        {
                            wrapper.AppendChild(node.Clone(deep: true));
                        }

                        var innerHtml = wrapper.InnerHtml.Trim();
                        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(wrapper);

                        if (!string.IsNullOrWhiteSpace(maskedText))
                        {
                            var nodesToReplace = currentRun.ToList();
                            var parent = element;

                            segments.Add(new HtmlSegment
                            {
                                Index = segmentIndex++,
                                TagName = tagName,
                                OriginalInnerHtml = innerHtml,
                                MaskedText = maskedText,
                                Tokens = tokens,
                                DomElement = element,
                                ReplacementAction = (restoredHtml) =>
                                {
                                    var parsedFragment = Parser.ParseFragment(restoredHtml, parent);
                                    var newNodes = parsedFragment.ToList();
                                    var firstNode = nodesToReplace.FirstOrDefault(n => n.Parent == parent);

                                    if (firstNode != null)
                                    {
                                        foreach (var newNode in newNodes)
                                        {
                                            parent.InsertBefore(newNode, firstNode);
                                        }
                                    }
                                    else
                                    {
                                        foreach (var newNode in newNodes)
                                        {
                                            parent.AppendChild(newNode);
                                        }
                                    }

                                    foreach (var oldNode in nodesToReplace)
                                    {
                                        if (oldNode.Parent == parent)
                                        {
                                            parent.RemoveChild(oldNode);
                                        }
                                    }
                                }
                            });
                        }
                    }

                    currentRun.Clear();
                }

                foreach (var childNode in childNodes)
                {
                    if (childNode is IElement childEl && ChildBlockTags.Contains(childEl.TagName))
                    {
                        FlushCurrentRun();
                    }
                    else
                    {
                        currentRun.Add(childNode);
                    }
                }

                FlushCurrentRun();
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
                if (segment.ReplacementAction != null)
                {
                    segment.ReplacementAction(restoredHtml);
                }
                else
                {
                    segment.DomElement.InnerHtml = restoredHtml;
                }
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
