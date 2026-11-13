using AngleSharp.Html.Parser;
using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class DomTreeWalkerMaskerTests
{
    private readonly HtmlParser _parser = new();

    [Fact]
    public void MaskElement_WithCodeAndIndexTerms_MasksWithFixedWidthTokens()
    {
        // Arrange
        var html = "<p>Run <code>dotnet build</code> and see <a data-type=\"indexterm\" data-primary=\"CLI\"></a> output.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert
        Assert.Equal(2, tokens.Count);
        Assert.Equal("⟦PH_000001⟧", tokens[0].Token);
        Assert.Equal("<code>dotnet build</code>", tokens[0].OriginalMarkup);
        Assert.Equal("code", tokens[0].TagName);

        Assert.Equal("⟦PH_000002⟧", tokens[1].Token);
        Assert.Contains("indexterm", tokens[1].OriginalMarkup);
        Assert.Equal("a", tokens[1].TagName);

        Assert.Equal("Run ⟦PH_000001⟧ and see ⟦PH_000002⟧ output.", maskedText);
    }

    [Theory]
    [InlineData("<p>Inline <code>var x = 1;</code> text.</p>", "code", "<code>var x = 1;</code>")]
    [InlineData("<p>Press <kbd>Ctrl+C</kbd> text.</p>", "kbd", "<kbd>Ctrl+C</kbd>")]
    [InlineData("<p>Output <samp>OK</samp> text.</p>", "samp", "<samp>OK</samp>")]
    [InlineData("<p>Variable <var>x</var> text.</p>", "var", "<var>x</var>")]
    [InlineData("<p>Formula <math><mi>x</mi></math> text.</p>", "math", "<math><mi>x</mi></math>")]
    [InlineData("<p>Icon <svg><circle cx=\"5\" cy=\"5\" r=\"5\"></circle></svg> text.</p>", "svg", "<svg><circle cx=\"5\" cy=\"5\" r=\"5\"></circle></svg>")]
    [InlineData("<p>Mono <tt>mono</tt> text.</p>", "tt", "<tt>mono</tt>")]
    [InlineData("<p>Footnote <sup class=\"footnote\">1</sup> text.</p>", "sup", "<sup class=\"footnote\">1</sup>")]
    [InlineData("<p>Note <sup id=\"fn1\">1</sup> text.</p>", "sup", "<sup id=\"fn1\">1</sup>")]
    public void MaskElement_WithProtectedTechnicalTags_MasksNodeAndPreservesOriginalMarkup(
        string html,
        string expectedTag,
        string expectedOriginalMarkup)
    {
        // Arrange
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert
        var token = Assert.Single(tokens);
        Assert.Equal("⟦PH_000001⟧", token.Token);
        Assert.Equal(expectedTag, token.TagName);
        Assert.Equal(expectedOriginalMarkup, token.OriginalMarkup);
        Assert.Contains("⟦PH_000001⟧", maskedText);
        Assert.DoesNotContain($"<{expectedTag}", maskedText);
    }

    [Fact]
    public void MaskElement_WithNestedCodeElements_MasksOnlyOuterElement()
    {
        // Arrange
        var html = "<p>Outer <code>before <code>nested</code> after</code> text.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert: Sadece en dıştaki code elemanı maskelenmeli, içteki dıştakinin içinde korunmalı
        Assert.Single(tokens);
        Assert.Equal("⟦PH_000001⟧", tokens[0].Token);
        Assert.Contains("<code>nested</code>", tokens[0].OriginalMarkup);
        Assert.Equal("Outer ⟦PH_000001⟧ text.", maskedText);
    }

    [Fact]
    public void MaskElement_WithPlainTextOnly_ReturnsInnerHtmlAndEmptyTokens()
    {
        // Arrange
        var html = "<p>Plain <em>emphasized</em> sentence without technical nodes.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert
        Assert.Empty(tokens);
        Assert.Equal("Plain <em>emphasized</em> sentence without technical nodes.", maskedText);
    }

    [Fact]
    public void UnmaskElement_WithValidTranslation_RestoresExactOriginalMarkup()
    {
        // Arrange
        var html = "<p>Run <code>dotnet build</code> to compile.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);
        var translated = "Derlemek için ⟦PH_000001⟧ komutunu çalıştırın.";

        // Act
        DomTreeWalkerMasker.Instance.UnmaskElement(p, translated, tokens);

        // Assert
        Assert.Equal("Derlemek için <code>dotnet build</code> komutunu çalıştırın.", p.InnerHtml);
    }

    [Theory]
    [InlineData("Şimdi [PH_1] çalıştırın.")]
    [InlineData("Şimdi [CODE_1] çalıştırın.")]
    [InlineData("Şimdi (PH_1) çalıştırın.")]
    [InlineData("Şimdi {PH_1} çalıştırın.")]
    [InlineData("Şimdi ⟦CODE_000001⟧ çalıştırın.")]
    [InlineData("Şimdi [PH-1] çalıştırın.")]
    public void UnmaskElement_WhenTokenHasVariedBracketsOrPrefixes_NormalizesAndUnmasks(string mutation)
    {
        // Arrange
        var html = "<p>Execute <code>dotnet test</code> now.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Act
        DomTreeWalkerMasker.Instance.UnmaskElement(p, mutation, tokens);

        // Assert
        Assert.Equal("Şimdi <code>dotnet test</code> çalıştırın.", p.InnerHtml);
    }

    [Fact]
    public void UnmaskElement_WhenTokenMissing_ThrowsInvalidOperationException()
    {
        // Arrange
        var html = "<p>Execute <code>dotnet run</code> now.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);
        var translationWithMissingToken = "Şimdi doğrudan çalıştırın.";

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DomTreeWalkerMasker.Instance.UnmaskElement(p, translationWithMissingToken, tokens));

        Assert.Contains("Kritik Token Kaybı", ex.Message);
        Assert.Contains("⟦PH_000001⟧", ex.Message);
    }

    [Fact]
    public void UnmaskElement_WhenTranslatedTextIsWhitespace_LeavesOriginalElementUntouched()
    {
        // Arrange
        var html = "<p>Original <code>dotnet run</code> content.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Act
        DomTreeWalkerMasker.Instance.UnmaskElement(p, "   ", tokens);

        // Assert
        Assert.Equal("Original <code>dotnet run</code> content.", p.InnerHtml);
    }

    [Fact]
    public void RestorePlaceholders_WhenLlmDropsToken_PreservesOriginalMarkupAtEnd()
    {
        // Arrange: Model bir belirteci (örneğin python kod bloğunu) metinden düşürmüş olsun
        var tokens = new List<MaskingToken>
        {
            new("⟦PH_000001⟧", 1, "<code class=\"python\">def hello(): pass</code>", "code")
        };
        var translationWithDroppedToken = "Bu fonksiyon selam verir.";

        // Act
        var restored = DomTreeWalkerMasker.RestorePlaceholders(translationWithDroppedToken, tokens);

        // Assert
        Assert.Equal("Bu fonksiyon selam verir. <code class=\"python\">def hello(): pass</code>", restored);
    }

    [Fact]
    public void MaskElement_WithCustomLinkCitation_MasksOuterSpanAndRestoresAccurately()
    {
        // Arrange: O'Reilly akademik atıf bağlantısı: <span class="customlink"><a href="...">Warfield, 2024</a></span>
        var html = "<p>EyeLevel.ai (<span class=\"customlink\"><a href=\"https://oreil.ly/V_roj\">Warfield, 2024</a></span>) tarafından yapılan araştırma.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert: Sadece dıştaki span.customlink tek bir token olarak maskelenmeli, içteki <a> ayrıca maskelenmemeli
        Assert.Single(tokens);
        Assert.Equal("⟦PH_000001⟧", tokens[0].Token);
        Assert.Contains("Warfield, 2024", tokens[0].OriginalMarkup);
        Assert.Contains("https://oreil.ly/V_roj", tokens[0].OriginalMarkup);
        Assert.Equal("EyeLevel.ai (⟦PH_000001⟧) tarafından yapılan araştırma.", maskedText);

        // Geri yükleme (Unmask) testi
        DomTreeWalkerMasker.Instance.UnmaskElement(p, maskedText, tokens);
        Assert.Contains("<span class=\"customlink\"><a href=\"https://oreil.ly/V_roj\">Warfield, 2024</a></span>", p.InnerHtml);
    }

    [Fact]
    public void MaskElement_WithExternalHyperlink_MasksDirectly()
    {
        // Arrange
        var html = "<p>Daha fazla bilgi için <a href=\"https://example.com/docs\">Dokümantasyon</a> sayfasını ziyaret edin.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert
        Assert.Single(tokens);
        Assert.Equal("⟦PH_000001⟧", tokens[0].Token);
        Assert.Contains("https://example.com/docs", tokens[0].OriginalMarkup);
        Assert.Equal("Daha fazla bilgi için ⟦PH_000001⟧ sayfasını ziyaret edin.", maskedText);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ⟦PH_000001⟧   ", true)]
    [InlineData("⟦PH_000001⟧⟦PH_000002⟧", true)]
    [InlineData("1.2", true)]
    [InlineData("⟦PH_000001⟧.", true)]
    [InlineData("<a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch04.html#ch04\">4</a>", true)]
    [InlineData("<a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch03.html#ch03\">3</a>, <a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch04.html#ch04\">4</a>", true)]
    [InlineData("Cognee", true)]
    [InlineData("Mem0", true)]
    [InlineData("Zep", true)]
    [InlineData("Neo4j", true)]
    [InlineData("⟦PH_000001⟧Cognee", true)]
    [InlineData("Hello world ⟦PH_000001⟧", false)]
    [InlineData("Eylem Körlüğü", false)]
    [InlineData("Performance", false)]
    [InlineData("Cost", false)]
    [InlineData("Modularity", false)]
    [InlineData("Tip", false)]
    [InlineData("Note", false)]
    [InlineData("Summary", false)]
    [InlineData("<a data-type=\"xref\" href=\"ch04.html#ch04\">Chapter&#160;4</a>", false)]
    [InlineData("<a class=\"email\" href=\"mailto:support@oreilly.com\"><em>support@oreilly.com</em></a>", true)]
    [InlineData("permissions@oreilly.com", true)]
    [InlineData("oreilly.com", true)]
    [InlineData("O’Reilly Media, Inc.", true)]
    [InlineData("Potomac Indexing, LLC", true)]
    [InlineData("141 Stony Circle, Suite 195", true)]
    [InlineData("Santa Rosa, CA 95401", true)]
    [InlineData("P.O. Box 1234", true)]
    [InlineData("1. Chapter Overview", false)]
    [InlineData("4 reasons why agents fail.", false)]
    public void IsPureTechnicalOrPlaceholder_AccuratelyDistinguishesXrefNumbersAndBrandNamesFromTranslatableText(string? input, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, DomTreeWalkerMasker.IsPureTechnicalOrPlaceholder(input));
    }

    [Theory]
    [InlineData("Agentic GraphRAG", "Agentic GraphRAG", "Agentic GraphRAG", "Anthony Alcaraz", true)]
    [InlineData("AI Engineering", "AI Engineering", "AI Engineering (for True Epub)", "Chip Huyen", true)]
    [InlineData("Sam Julien", "Sam Julien", "Agentic GraphRAG", "Anthony Alcaraz and Sam Julien", true)]
    [InlineData("O’Reilly Online Learning", "O’Reilly Online Learning", "Other Book", null, true)]
    [InlineData("<em>Agentic GraphRAG</em>", "<em>Agentic GraphRAG</em>", "Agentic GraphRAG", null, true)]
    [InlineData("Agentic GraphRAG", null, "Agentic GraphRAG", null, false)]
    [InlineData("Agentic GraphRAG", "   ", "Agentic GraphRAG", null, false)]
    [InlineData("Distributed systems coordinate state reliably over networks.", "Distributed systems coordinate state reliably over networks.", "Agentic GraphRAG", null, false)]
    [InlineData("Related Work", "Related Work", "Agentic GraphRAG", null, false)]
    public void HtmlSegment_IsTranslationComplete_AcceptsBookMetadataAndBrandsWhileRejectingUntranslatedProse(
        string maskedText,
        string? translatedText,
        string? bookTitle,
        string? bookAuthor,
        bool expected)
    {
        // Arrange
        var seg = new HtmlSegment
        {
            Index = 0,
            TagName = "p",
            MaskedText = maskedText,
            TranslatedText = translatedText
        };

        // Act & Assert
        Assert.Equal(expected, seg.IsTranslationComplete(bookTitle, bookAuthor));
    }
}
