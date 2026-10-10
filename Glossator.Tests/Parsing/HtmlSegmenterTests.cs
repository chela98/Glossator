using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class HtmlSegmenterTests
{
    [Fact]
    public void SegmentHtml_PreservesPreBlocksUntouched()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <h1>Title</h1>
                <p>A paragraph with <code>inline code</code>.</p>
                <pre><code>public void DoSomething() { Console.WriteLine("Hello"); }</code></pre>
            </body>
            </html>
            """;

        // Act
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("h1", segments[0].TagName);
        Assert.Equal("p", segments[1].TagName);

        // pre içindeki code asla segmente dahil edilmemeli
        Assert.DoesNotContain(segments, s => s.TagName == "pre");
        Assert.DoesNotContain(segments, s => s.OriginalInnerHtml.Contains("DoSomething"));
    }

    [Fact]
    public void ApplyTranslations_UpdatesDomElementCorrectly()
    {
        // Arrange
        var html = "<html><body><p>Welcome to <code>Glossator</code> tool.</p></body></html>";
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        Assert.Single(segments);
        var seg = segments[0];
        Assert.Contains("⟦PH_000001⟧", seg.MaskedText);

        seg.TranslatedText = "⟦PH_000001⟧ aracına hoş geldiniz.";

        // Act
        HtmlSegmenter.ApplyTranslations(segments);

        // Assert
        Assert.Equal("<code>Glossator</code> aracına hoş geldiniz.", seg.DomElement.InnerHtml);
        var xhtml = HtmlSegmenter.ToXhtml(document);
        Assert.Contains("<code>Glossator</code> aracına hoş geldiniz.", xhtml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xhtml);
    }

    [Fact]
    public void SegmentHtml_WithMultiplePlaceholders_DoesNotCollideOnPrefixes()
    {
        // Arrange: 12 adet kod bloğu içeren bir paragraf (PH_000001 ile PH_000010, PH_000011, PH_000012)
        var html = "<p>" + string.Join(" and ", Enumerable.Range(1, 12).Select(i => $"<code>code_{i}</code>")) + "</p>";
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        Assert.Single(segments);
        var seg = segments[0];
        Assert.Equal(12, seg.Tokens.Count);

        // Çeviri simülasyonu: token sırasını değiştirerek bir metin üret
        seg.TranslatedText = string.Join(" ve ", Enumerable.Range(1, 12).Select(i => $"⟦PH_{i:D6}⟧"));

        // Act
        var restored = seg.ReconstructHtml();

        // Assert: Bütün kod blokları hatasız geri takılmış olmalı
        for (int i = 1; i <= 12; i++)
        {
            Assert.Contains($"<code>code_{i}</code>", restored);
            Assert.DoesNotContain($"⟦PH_{i:D6}⟧", restored);
        }
    }
}
