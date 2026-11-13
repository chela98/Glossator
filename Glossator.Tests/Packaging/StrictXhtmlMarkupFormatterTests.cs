using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Glossator.Core.Packaging;
using Xunit;

namespace Glossator.Tests.Packaging;

public class StrictXhtmlMarkupFormatterTests
{
    private readonly HtmlParser _parser = new();

    [Theory]
    [InlineData("<p>Line 1<br>Line 2</p>", "<br />")]
    [InlineData("<p>Section</p><hr><p>Next</p>", "<hr />")]
    [InlineData("<p><img src=\"cat.jpg\" alt=\"cat\"></p>", "<img src=\"cat.jpg\" alt=\"cat\" />")]
    [InlineData("<head><meta charset=\"utf-8\"></head>", "<meta charset=\"utf-8\" />")]
    [InlineData("<head><link rel=\"stylesheet\" href=\"style.css\"></head>", "<link rel=\"stylesheet\" href=\"style.css\" />")]
    [InlineData("<table><colgroup><col></colgroup></table>", "<col />")]
    [InlineData("<audio><source src=\"audio.mp3\"></audio>", "<source src=\"audio.mp3\" />")]
    [InlineData("<video><track kind=\"subtitles\"></video>", "<track kind=\"subtitles\" />")]
    [InlineData("<map><area shape=\"rect\"></map>", "<area shape=\"rect\" />")]
    [InlineData("<p>Word<wbr>Break</p>", "<wbr />")]
    [InlineData("<form><input type=\"text\"></form>", "<input type=\"text\" />")]
    public void ToStrictXhtml_VoidElements_AreStrictlySelfClosing(string bodyOrHeadSnippet, string expectedSelfClosingTag)
    {
        // Arrange
        var html = bodyOrHeadSnippet.StartsWith("<head>", System.StringComparison.Ordinal)
            ? $"<html>{bodyOrHeadSnippet}<body></body></html>"
            : $"<html><body>{bodyOrHeadSnippet}</body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains(expectedSelfClosingTag, xhtml);
    }

    [Fact]
    public void ToStrictXhtml_NonVoidElements_AlwaysHaveClosingTag()
    {
        // Arrange
        var html = "<html><body><span id=\"empty\"></span><div class=\"box\"></div><p></p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains("<span id=\"empty\"></span>", xhtml);
        Assert.Contains("<div class=\"box\"></div>", xhtml);
        Assert.Contains("<p></p>", xhtml);
        Assert.DoesNotContain("<span id=\"empty\" />", xhtml);
        Assert.DoesNotContain("<div class=\"box\" />", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_Html5EntitiesAndNbsp_AreConvertedToUtf8AndNumericXmlEntities()
    {
        // Arrange
        var html = "<html><body><p>Caf&eacute; &copy; 2026 &trade; NonBreaking&nbsp;Space &bull; Bullet</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains("NonBreaking&#160;Space", xhtml);
        Assert.Contains("Café", xhtml);
        Assert.Contains("©", xhtml);
        Assert.Contains("™", xhtml);
        Assert.Contains("•", xhtml);
        Assert.DoesNotContain("&nbsp;", xhtml);
        Assert.DoesNotContain("&eacute;", xhtml);
        Assert.DoesNotContain("&copy;", xhtml);
        Assert.DoesNotContain("&trade;", xhtml);
        Assert.DoesNotContain("&bull;", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_SpecialXmlCharactersInTextAndAttributes_AreProperlyEscaped()
    {
        // Arrange
        var html = "<html><body><p title=\"a &amp; b &quot;c&quot; 'd' &lt;e&gt;&nbsp;\">Text &amp; &lt;tag&gt; and\u00A0nbsp</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();
        var cleanXml = xhtml.Replace("<!DOCTYPE html>", "");
        var parsed = XDocument.Parse(cleanXml);

        // Assert
        Assert.Contains("title=\"a &amp; b &quot;c&quot; &apos;d&apos; &lt;e&gt;&#160;\"", xhtml);
        Assert.Contains("Text &amp; &lt;tag&gt; and&#160;nbsp", xhtml);
        Assert.NotNull(parsed.Root);
    }

    [Fact]
    public void ToStrictXhtml_OutputsValidXml_ParsableByXDocument()
    {
        // Arrange
        var html = "<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Test Book</title></head><body><h1>Chapter 1</h1><p>Sample text with &amp; ampersand and <code>code</code>.</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();
        var cleanForXmlParser = xhtml.Replace("<!DOCTYPE html>", "");
        var parsedXml = XDocument.Parse(cleanForXmlParser);

        // Assert
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xhtml.TrimStart());
        Assert.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"", xhtml);
        Assert.NotNull(parsedXml.Root);
        Assert.Equal("html", parsedXml.Root.Name.LocalName);
    }

    [Fact]
    public void ToStrictXhtml_InjectsLangTr_AndXmlLangTr_OnHtmlElement()
    {
        // Arrange
        var html = "<html><head><title>Test</title></head><body><p>Merhaba Dünya</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains("lang=\"tr\"", xhtml);
        Assert.Contains("xml:lang=\"tr\"", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_OverridesEnglishLang_AndCleansConflictingChildTags()
    {
        // Arrange
        var html = """
            <!DOCTYPE html>
            <html xml:lang="en" lang="en" xmlns="http://www.w3.org/1999/xhtml">
            <head><title>Chapter</title></head>
            <body lang="en">
                <section lang="en-US">
                    <p lang="en">Türkçe paragraf metni</p>
                    <pre><code lang="python">print('hello')</code></pre>
                </section>
            </body>
            </html>
            """;
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains("lang=\"tr\"", xhtml);
        Assert.Contains("xml:lang=\"tr\"", xhtml);
        Assert.DoesNotContain("<body lang=", xhtml);
        Assert.DoesNotContain("<section lang=", xhtml);
        Assert.DoesNotContain("<p lang=", xhtml);
        Assert.Contains("lang=\"python\"", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_WithSvgNamespace_SerializesValidSelfClosingElements()
    {
        // Arrange
        var html = "<html><body><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"red\" /><path d=\"M10 10\" /></svg></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();
        var parsedXml = XDocument.Parse(xhtml.Replace("<!DOCTYPE html>", ""));

        // Assert
        Assert.NotNull(parsedXml.Root);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", xhtml);
        Assert.Contains("<circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"red\" />", xhtml);
        Assert.Contains("<path d=\"M10 10\" />", xhtml);
    }
}
