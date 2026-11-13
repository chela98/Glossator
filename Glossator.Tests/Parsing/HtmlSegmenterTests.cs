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
                <pre><p>Paragraph inside pre must be ignored</p><code>public void DoSomething() { Console.WriteLine("Hello"); }</code></pre>
            </body>
            </html>
            """;

        // Act
        var (_, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("h1", segments[0].TagName);
        Assert.Equal("p", segments[1].TagName);

        // pre içindeki hiçbir eleman segmente dahil edilmemeli
        Assert.DoesNotContain(segments, s => s.TagName == "pre");
        Assert.DoesNotContain(segments, s => s.OriginalInnerHtml.Contains("DoSomething"));
        Assert.DoesNotContain(segments, s => s.OriginalInnerHtml.Contains("ignored"));
    }

    [Fact]
    public void ApplyTranslations_UpdatesDomElementCorrectly()
    {
        // Arrange
        var html = "<html><body><p>Welcome to <code>Glossator</code> tool.</p></body></html>";
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        var seg = Assert.Single(segments);
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
        // Arrange: 12 adet kod bloğu içeren bir paragraf (PH_000001..PH_000012)
        var html = "<p>" + string.Join(" and ", Enumerable.Range(1, 12).Select(i => $"<code>code_{i}</code>")) + "</p>";
        var (_, segments) = HtmlSegmenter.SegmentHtml(html);

        var seg = Assert.Single(segments);
        Assert.Equal(12, seg.Tokens.Count);
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

    [Fact]
    public void SegmentHtml_WithListsTablesAndDefinitionLists_SegmentsAllTextualNodes()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <ul><li>List item 1</li><li>List item 2</li></ul>
                <table>
                    <thead><tr><th>Header 1</th></tr></thead>
                    <tbody><tr><td>Cell 1</td></tr></tbody>
                </table>
                <dl><dt>Term</dt><dd>Definition</dd></dl>
            </body>
            </html>
            """;

        // Act
        var (_, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert
        Assert.Equal(6, segments.Count);
        Assert.Contains(segments, s => s.TagName == "li" && s.MaskedText.Contains("List item 1"));
        Assert.Contains(segments, s => s.TagName == "li" && s.MaskedText.Contains("List item 2"));
        Assert.Contains(segments, s => s.TagName == "th" && s.MaskedText.Contains("Header 1"));
        Assert.Contains(segments, s => s.TagName == "td" && s.MaskedText.Contains("Cell 1"));
        Assert.Contains(segments, s => s.TagName == "dt" && s.MaskedText.Contains("Term"));
        Assert.Contains(segments, s => s.TagName == "dd" && s.MaskedText.Contains("Definition"));
    }

    [Fact]
    public void SegmentHtml_WithNestedLists_SegmentsBothParentAndChildTextAndAppliesTranslations()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <ul>
                    <li>Parent list item:
                        <ul>
                            <li>Child list item 1</li>
                            <li>Child list item 2</li>
                        </ul>
                    </li>
                </ul>
            </body>
            </html>
            """;

        // Act
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert & Translate
        Assert.Equal(3, segments.Count);
        var parentSeg = segments.First(s => s.MaskedText.Contains("Parent list item:"));
        var child1Seg = segments.First(s => s.MaskedText.Contains("Child list item 1"));
        var child2Seg = segments.First(s => s.MaskedText.Contains("Child list item 2"));

        parentSeg.TranslatedText = "Üst liste ögesi:";
        child1Seg.TranslatedText = "Alt liste ögesi 1";
        child2Seg.TranslatedText = "Alt liste ögesi 2";

        HtmlSegmenter.ApplyTranslations(segments);

        var xhtml = HtmlSegmenter.ToXhtml(document);
        Assert.Contains("Üst liste ögesi:", xhtml);
        Assert.Contains("Alt liste ögesi 1", xhtml);
        Assert.Contains("Alt liste ögesi 2", xhtml);
        Assert.DoesNotContain("Parent list item:", xhtml);
    }

    [Fact]
    public void SegmentHtml_WithBlockquoteContainingParagraphs_SegmentsChildParagraphsWithoutDuplication()
    {
        // Arrange
        var html = "<blockquote><p>Quoted first sentence.</p><p>Quoted second sentence.</p></blockquote>";

        // Act
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert: blockquote içinde <p> olduğu için yalnızca iki <p> segmente edilmeli
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal("p", s.TagName));
        segments[0].TranslatedText = "Alıntılanan birinci cümle.";
        segments[1].TranslatedText = "Alıntılanan ikinci cümle.";

        HtmlSegmenter.ApplyTranslations(segments);
        var xhtml = HtmlSegmenter.ToXhtml(document);
        Assert.Contains("<blockquote><p>Alıntılanan birinci cümle.</p><p>Alıntılanan ikinci cümle.</p></blockquote>", xhtml);
    }

    [Fact]
    public void SegmentHtml_WithSelfClosingIndextermAnchors_DoesNotSwallowText()
    {
        // Arrange
        var html = "<p>Imagine <a contenteditable=\"false\" data-primary=\"enterprise agentic AI\" data-type=\"indexterm\" id=\"eaai_ch\"/>a world where AI doesn’t just wait for your questions but pursues your goals.</p>";

        // Act
        var (document, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert
        var seg = Assert.Single(segments);
        Assert.Equal("Imagine ⟦PH_000001⟧a world where AI doesn’t just wait for your questions but pursues your goals.", seg.MaskedText);
        Assert.Single(seg.Tokens);
        Assert.Equal("a", seg.Tokens[0].TagName);
        Assert.Contains("indexterm", seg.Tokens[0].OriginalMarkup);

        seg.TranslatedText = "Şunu hayal et: ⟦PH_000001⟧yapay zekânın hedeflerinizin peşinden koştuğu bir dünya.";
        HtmlSegmenter.ApplyTranslations(segments);

        var xhtml = HtmlSegmenter.ToXhtml(document);
        Assert.Contains("Şunu hayal et:", xhtml);
        Assert.Contains("yapay zekânın hedeflerinizin peşinden koştuğu bir dünya.", xhtml);
        Assert.Contains("<a contenteditable=\"false\" data-primary=\"enterprise agentic AI\" data-type=\"indexterm\" id=\"eaai_ch\"></a>", xhtml);
    }

    [Fact]
    public void SegmentHtml_WithSelfClosingAnchorTag_PreservesDevOpsAndActionBlindnessText()
    {
        // Arrange
        var html = @"<div>
<p>The <a contenteditable=""false"" data-primary=""action blindness"" data-type=""indexterm"" id=""ab""/>shift from simple Q&amp;A to autonomous action exposes this vulnerability clearly. When your DevOps agent needs to diagnose a production issue</p>
<p>Without <a contenteditable=""false"" data-primary=""action blindness"" data-type=""indexterm"" id=""ab2""/>understanding relationships between entities, cascade failures through dependent systems</p>
</div>";

        // Act
        var (_, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert
        var devOpsSeg = segments.FirstOrDefault(s => s.MaskedText.Contains("DevOps agent"));
        Assert.NotNull(devOpsSeg);
        Assert.Contains("The ⟦PH_000001⟧shift from simple Q&amp;A", devOpsSeg.MaskedText);
        Assert.Contains("When your DevOps agent needs to diagnose a production issue", devOpsSeg.MaskedText);

        var actionBlindnessDesc = segments.FirstOrDefault(s => s.MaskedText.Contains("cascade failures through dependent systems"));
        Assert.NotNull(actionBlindnessDesc);
        Assert.Contains("Without ⟦PH_000001⟧understanding relationships between entities", actionBlindnessDesc.MaskedText);
    }

    [Fact]
    public void SegmentFile_ReadsHtmlFromDiskAndSegmentsCorrectly()
    {
        // Arrange
        var tempFile = Path.Combine(Path.GetTempPath(), $"segment_file_{Guid.NewGuid():N}.xhtml");
        File.WriteAllText(tempFile, "<html><body><h1>Chapter 1</h1><p>Body with <code>var x = 1;</code>.</p></body></html>");

        try
        {
            // Act
            var (_, segments) = HtmlSegmenter.SegmentFile(tempFile);

            // Assert
            Assert.Equal(2, segments.Count);
            Assert.Equal("Chapter 1", segments[0].MaskedText);
            Assert.Equal("Body with ⟦PH_000001⟧.", segments[1].MaskedText);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
