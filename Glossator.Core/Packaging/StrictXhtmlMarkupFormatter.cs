using System.Text;
using AngleSharp;
using AngleSharp.Dom;

namespace Glossator.Core.Packaging;

/// <summary>
/// W3C EPUB 3 / XHTML 1.0 Strict ve XML 1.0 kurallarına tam uyumlu AngleSharp biçimlendiricisi.
/// Tüm void elemanları katı ' />' ile kapatır, HTML5 adlandırılmış entity'lerini UTF-8 / XML numerik
/// biçimine (&amp;#160;) dönüştürür ve XML isim uzaylarını korur.
/// </summary>
public sealed class StrictXhtmlMarkupFormatter : IMarkupFormatter
{
    public static readonly StrictXhtmlMarkupFormatter Instance = new();

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "woff"
    };

    public string OpenTag(IElement element, bool selfClosing)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(element.NodeName.ToLowerInvariant());

        foreach (var attr in element.Attributes)
        {
            var attrVal = EscapeXmlAttribute(attr.Value);
            sb.Append(' ').Append(attr.Name).Append("=\"").Append(attrVal).Append('"');
        }

        if (IsVoidOrSelfClosing(element, selfClosing))
        {
            sb.Append(" />");
        }
        else
        {
            sb.Append('>');
        }

        return sb.ToString();
    }

    public string CloseTag(IElement element, bool selfClosing)
    {
        if (IsVoidOrSelfClosing(element, selfClosing))
        {
            return string.Empty;
        }
        return $"</{element.NodeName.ToLowerInvariant()}>";
    }

    public string Text(ICharacterData text) => EscapeXmlText(text.Data);

    public string LiteralText(ICharacterData text) => text.Data;

    public string Comment(IComment comment) => $"<!--{comment.Data}-->";
    public string Processing(IProcessingInstruction processing) => $"<?{processing.Target} {processing.Data}?>";
    public string Doctype(IDocumentType doctype) => "<!DOCTYPE html>";

    private static bool IsVoidOrSelfClosing(IElement element, bool selfClosing)
    {
        if (VoidElements.Contains(element.NodeName))
        {
            return true;
        }

        // SVG ve MathML altındaki boş elemanlar self-closing olmalıdır
        if (element.NamespaceUri is "http://www.w3.org/2000/svg" or "http://www.w3.org/1998/Math/MathML"
            || element.Owner?.ContentType is "image/svg+xml")
        {
            return !element.HasChildNodes;
        }

        return false;
    }

    private static string EscapeXmlText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\u00A0': sb.Append("&#160;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string EscapeXmlAttribute(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '\u00A0': sb.Append("&#160;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

public static class DocumentXhtmlExtensions
{
    /// <summary>
    /// Kök etiket dışındaki yapısal ve metinsel elemanlarda kalan çelişkili yabancı dil özniteliklerini
    /// (lang="en*", xml:lang="en*") temizler, böylece tüm belge kökteki lang="tr" etiketini miras alır.
    /// Kod blokları (code, pre, kbd, samp) bu temizlikten muaf tutulur.
    /// </summary>
    public static void CleanConflictingLanguageAttributes(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.DocumentElement;
        if (root == null) return;

        var allElements = document.QuerySelectorAll("*");
        foreach (var el in allElements)
        {
            if (ReferenceEquals(el, root)) continue;

            // Kod bloklarını ve teknik etiketleri atla
            var tagName = el.TagName.ToLowerInvariant();
            if (tagName is "code" or "pre" or "kbd" or "samp") continue;
            if (el.Closest("code, pre, kbd, samp") != null) continue;

            var lang = el.GetAttribute("lang");
            if (!string.IsNullOrEmpty(lang) && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                el.RemoveAttribute("lang");
            }

            var xmlLang = el.GetAttribute("xml:lang");
            if (!string.IsNullOrEmpty(xmlLang) && xmlLang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                el.RemoveAttribute("xml:lang");
            }
        }
    }

    /// <summary>
    /// IDocument belgesini katı W3C EPUB 3 / XHTML 1.0 standartlarında XML olarak serileştirir.
    /// Kök html etiketine lang="tr", xml:lang="tr" ve xmlns="http://www.w3.org/1999/xhtml" özniteliklerini
    /// enjekte eder; gövdede kalan çelişkili İngilizce dil etiketlerini temizler.
    /// </summary>
    public static string ToStrictXhtml(this IDocument document, string defaultLanguage = "tr")
    {
        ArgumentNullException.ThrowIfNull(document);

        // html kök etiketinde xmlns="http://www.w3.org/1999/xhtml", lang ve xml:lang garantile
        var htmlElement = document.DocumentElement;
        if (htmlElement != null)
        {
            if (string.IsNullOrEmpty(htmlElement.GetAttribute("xmlns")))
            {
                htmlElement.SetAttribute("xmlns", "http://www.w3.org/1999/xhtml");
            }

            if (!string.IsNullOrWhiteSpace(defaultLanguage))
            {
                htmlElement.SetAttribute("lang", defaultLanguage);
                htmlElement.SetAttribute("xml:lang", defaultLanguage);
                document.CleanConflictingLanguageAttributes();
            }
        }

        using var writer = new StringWriter();
        document.ToHtml(writer, StrictXhtmlMarkupFormatter.Instance);
        var xhtml = writer.ToString();

        if (!xhtml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            xhtml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xhtml;
        }
        return xhtml;
    }
}
