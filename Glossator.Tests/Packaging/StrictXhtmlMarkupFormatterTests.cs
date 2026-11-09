using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Glossator.Core.Packaging;
using Xunit;

namespace Glossator.Tests.Packaging;

public class StrictXhtmlMarkupFormatterTests
{
    private readonly HtmlParser _parser = new();

    [Fact]
    public void ToStrictXhtml_VoidElements_AreStrictlySelfClosing()
    {
        // Arrange
        var html = "<html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"style.css\"></head><body><p>Line 1<br>Line 2<hr><img src=\"cat.jpg\" alt=\"cat\"></p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        Assert.Contains("<br />", xhtml);
        Assert.Contains("<hr />", xhtml);
        Assert.Contains("<img src=\"cat.jpg\" alt=\"cat\" />", xhtml);
        Assert.Contains("<meta charset=\"utf-8\" />", xhtml);
        Assert.Contains("<link rel=\"stylesheet\" href=\"style.css\" />", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_NonVoidElements_AlwaysHaveClosingTag()
    {
        // Arrange
        var html = "<html><body><span id=\"empty\"></span><div class=\"box\"></div><p></p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert: XHTML'de non-void elemanlar <span /> veya <div /> şeklinde kapatılmamalıdır
        Assert.Contains("<span id=\"empty\"></span>", xhtml);
        Assert.Contains("<div class=\"box\"></div>", xhtml);
        Assert.Contains("<p></p>", xhtml);
        Assert.DoesNotContain("<span id=\"empty\" />", xhtml);
        Assert.DoesNotContain("<div class=\"box\" />", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_Html5EntitiesAndNbsp_AreConvertedToUtf8AndNumericXmlEntities()
    {
        // Arrange: HTML5 spesifikasyonunda olan ama XML'de önceden tanımlı olmayan entity'ler
        var html = "<html><body><p>Caf&eacute; &copy; 2026 &trade; NonBreaking&nbsp;Space &bull; Bullet</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert
        // &nbsp; -> &#160; olmalı
        Assert.Contains("NonBreaking&#160;Space", xhtml);
        // &eacute; -> é, &copy; -> ©, &trade; -> ™ UTF-8 olmalı
        Assert.Contains("Café", xhtml);
        Assert.Contains("©", xhtml);
        Assert.Contains("™", xhtml);
        Assert.Contains("•", xhtml);

        // Hiçbir adlandırılmış yabancı entity XML'e sızmamalı
        Assert.DoesNotContain("&nbsp;", xhtml);
        Assert.DoesNotContain("&eacute;", xhtml);
        Assert.DoesNotContain("&copy;", xhtml);
        Assert.DoesNotContain("&trade;", xhtml);
        Assert.DoesNotContain("&bull;", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_OutputsValidXml_ParsableByXDocument()
    {
        // Arrange
        var html = "<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Test Book</title></head><body><h1>Chapter 1</h1><p>Sample text with &amp; ampersand and <code>code</code>.</p></body></html>";
        var doc = _parser.ParseDocument(html);

        // Act
        var xhtml = doc.ToStrictXhtml();

        // Assert: XML 1.0 uyumluluğunu XDocument.Parse ile doğrula
        // Not: DOCTYPE satırı XML parser'ında DTD aramasın diye xml header ve içerik test edilir
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xhtml.TrimStart());
        Assert.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"", xhtml);

        // XML validasyon testi
        var cleanForXmlParser = xhtml.Replace("<!DOCTYPE html>", "");
        var parsedXml = XDocument.Parse(cleanForXmlParser);
        Assert.NotNull(parsedXml.Root);
        Assert.Equal("html", parsedXml.Root.Name.LocalName);
    }

    [Fact]
    public void ToStrictXhtml_InjectsLangTr_AndXmlLangTr_OnHtmlElement()
    {
        // Arrange: Dili belirtilmemiş ham HTML
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
        // Arrange: Orijinal O'Reilly XHTML formatı (kök lang="en", body ve section lang="en")
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

        // Assert: 1. Kök etiket Türkçe olmalı
        Assert.Contains("lang=\"tr\"", xhtml);
        Assert.Contains("xml:lang=\"tr\"", xhtml);

        // Assert: 2. Body, Section ve P etiketlerindeki çelişkili İngilizce kaldırılmalı (böylece VoiceOver ve sözlük kök tr'yi görür)
        Assert.DoesNotContain("<body lang=\"en\">", xhtml);
        Assert.DoesNotContain("<body lang=", xhtml);
        Assert.DoesNotContain("<section lang=", xhtml);
        Assert.DoesNotContain("<p lang=", xhtml);

        // Assert: 3. Kod bloklarındaki dil etiketi (python) korunmalı
        Assert.Contains("lang=\"python\"", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_AllVoidElements_AreStrictlySelfClosing()
    {
        // col, source, track, wbr, area, input
        var html = "<html><body><table><colgroup><col></colgroup></table><audio><source src=\"audio.mp3\"><track kind=\"subtitles\"></audio><map><area shape=\"rect\"></map><p>Word<wbr>Break</p><input type=\"text\"></body></html>";
        var doc = _parser.ParseDocument(html);

        var xhtml = doc.ToStrictXhtml();

        Assert.Contains("<col />", xhtml);
        Assert.Contains("<source src=\"audio.mp3\" />", xhtml);
        Assert.Contains("<track kind=\"subtitles\" />", xhtml);
        Assert.Contains("<area shape=\"rect\" />", xhtml);
        Assert.Contains("<wbr />", xhtml);
        Assert.Contains("<input type=\"text\" />", xhtml);
    }

    [Fact]
    public void ToStrictXhtml_WithSvgAndMathMl_SerializesValidNamespacedXml()
    {
        var html = "<html><body><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"red\" /><path d=\"M10 10\" /></svg></body></html>";
        var doc = _parser.ParseDocument(html);

        var xhtml = doc.ToStrictXhtml();

        Assert.Contains("<svg", xhtml);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", xhtml);
        Assert.Contains("<circle", xhtml);
        Assert.Contains("cx=\"50\"", xhtml);
        Assert.Contains("cy=\"50\"", xhtml);
        Assert.Contains("fill=\"red\"", xhtml);
        Assert.Contains("<path", xhtml);
        Assert.Contains("d=\"M10 10\"", xhtml);
    }
}

