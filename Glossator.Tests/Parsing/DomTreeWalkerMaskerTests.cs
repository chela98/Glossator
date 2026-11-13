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

        Assert.Contains("Run ⟦PH_000001⟧ and see ⟦PH_000002⟧ output.", maskedText);
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
    public void MaskElement_WithSvgAndMathAndKbd_ProtectsAllTechnicalNodes()
    {
        // Arrange
        var html = "<p>Press <kbd>Ctrl+C</kbd> or evaluate <math><mi>x</mi></math> and view <svg><circle cx=\"5\" cy=\"5\" r=\"5\"/></svg>.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        // Act
        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Assert
        Assert.Equal(3, tokens.Count);
        Assert.Equal("kbd", tokens[0].TagName);
        Assert.Equal("math", tokens[1].TagName);
        Assert.Equal("svg", tokens[2].TagName);
        Assert.Contains("⟦PH_000001⟧", maskedText);
        Assert.Contains("⟦PH_000002⟧", maskedText);
        Assert.Contains("⟦PH_000003⟧", maskedText);
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

    [Fact]
    public void UnmaskElement_WhenTokenMutatedByModel_NormalizesFuzzyTokens()
    {
        // Arrange
        var html = "<p>Execute <code>dotnet test</code> now.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Model [PH_1] veya [CODE_1] şeklinde deforme etmiş olsun
        var translatedWithMutation = "Şimdi [PH_1] çalıştırın.";

        // Act
        DomTreeWalkerMasker.Instance.UnmaskElement(p, translatedWithMutation, tokens);

        // Assert
        Assert.Equal("Şimdi <code>dotnet test</code> çalıştırın.", p.InnerHtml);
    }

    [Theory]
    [InlineData("Şimdi (PH_1) çalıştırın.")]
    [InlineData("Şimdi {PH_1} çalıştırın.")]
    [InlineData("Şimdi ⟦CODE_000001⟧ çalıştırın.")]
    [InlineData("Şimdi [PH-1] çalıştırın.")]
    public void UnmaskElement_WhenTokenHasVariedBracketsOrPrefixes_NormalizesAndUnmasks(string mutation)
    {
        var html = "<p>Execute <code>dotnet test</code> now.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        DomTreeWalkerMasker.Instance.UnmaskElement(p, mutation, tokens);

        Assert.Equal("Şimdi <code>dotnet test</code> çalıştırın.", p.InnerHtml);
    }

    [Fact]
    public void MaskElement_WithSampVarAndTt_ProtectsAllTechnicalNodes()
    {
        var html = "<p>Variable <var>x</var> produces output <samp>success</samp> in <tt>monospace</tt>.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;

        var (maskedText, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        Assert.Equal(3, tokens.Count);
        Assert.Equal("var", tokens[0].TagName);
        Assert.Equal("samp", tokens[1].TagName);
        Assert.Equal("tt", tokens[2].TagName);
        Assert.Contains("Variable ⟦PH_000001⟧ produces output ⟦PH_000002⟧ in ⟦PH_000003⟧.", maskedText);
    }

    [Fact]
    public void UnmaskElement_WhenTokenMissing_ThrowsInvalidOperationException()
    {
        // Arrange
        var html = "<p>Execute <code>dotnet run</code> now.</p>";
        var doc = _parser.ParseDocument(html);
        var p = doc.QuerySelector("p")!;
        var (_, tokens) = DomTreeWalkerMasker.Instance.MaskElement(p);

        // Model tokeni tamamen yutmuş/silmiş olsun
        var translationWithMissingToken = "Şimdi doğrudan çalıştırın.";

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DomTreeWalkerMasker.Instance.UnmaskElement(p, translationWithMissingToken, tokens));

        Assert.Contains("Kritik Token Kaybı", ex.Message);
        Assert.Contains("⟦PH_000001⟧", ex.Message);
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

        // Act: RestorePlaceholders çağrıldığında kod bloğu sessizce kaybolmamalı, korunmalıdır
        var restored = DomTreeWalkerMasker.RestorePlaceholders(translationWithDroppedToken, tokens);

        // Assert
        Assert.NotNull(restored);
        Assert.Contains("<code class=\"python\">def hello(): pass</code>", restored);
        Assert.StartsWith("Bu fonksiyon selam verir.", restored);
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
        // Arrange: Harici doğrudan web linki
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
    [InlineData("<a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch04.html#ch04\">4</a>", true)]
    [InlineData("<a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch03.html#ch03\">3</a>, <a data-type=\"xref\" data-xrefstyle=\"select:labelnumber\" href=\"ch04.html#ch04\">4</a>", true)]
    [InlineData("Cognee", true)]
    [InlineData("Mem0", true)]
    [InlineData("Zep", true)]
    [InlineData("Neo4j", true)]
    [InlineData("⟦PH_000001⟧Cognee", true)]
    [InlineData("Performance", false)]
    [InlineData("Cost", false)]
    [InlineData("Modularity", false)]
    [InlineData("Tip", false)]
    [InlineData("Note", false)]
    [InlineData("Summary", false)]
    [InlineData("<a data-type=\"xref\" href=\"ch04.html#ch04\">Chapter&#160;4</a>", false)]
    public void IsPureTechnicalOrPlaceholder_AccuratelyDistinguishesXrefNumbersAndBrandNamesFromTranslatableText(string input, bool expected)
    {
        Assert.Equal(expected, DomTreeWalkerMasker.IsPureTechnicalOrPlaceholder(input));
    }
}
