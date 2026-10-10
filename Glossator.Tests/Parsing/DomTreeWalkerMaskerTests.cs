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
}
