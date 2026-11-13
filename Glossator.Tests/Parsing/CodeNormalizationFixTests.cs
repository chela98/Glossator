using AngleSharp.Dom;
using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class CodeNormalizationFixTests
{
    [Fact]
    public void SegmentHtml_WithSelfClosingCodeTags_DoesNotLeakCodeIntoParagraphs()
    {
        // Arrange: Pygments / XHTML syntax highlighters often emit self-closing <code class="w"/> for whitespace
        var html = @"<pre><code class=""c1"">// comment</code><code class=""w""/>
<code class=""k"">public</code> <code class=""kd"">class</code> <code class=""nc"">Foo</code><code class=""w""/>
</pre>
<p>First body paragraph outside pre</p>
<p>Second body paragraph outside pre</p>";

        // Act
        var (doc, segments) = HtmlSegmenter.SegmentHtml(html);

        // Assert: Paragraphs must never be nested inside code elements
        var pList = doc.QuerySelectorAll("p");
        Assert.Equal(2, pList.Length);
        foreach (var p in pList)
        {
            var parent = p.ParentElement;
            Assert.NotEqual("code", parent?.TagName?.ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData(@"<code class=""w""/>", @"<code class=""w""></code>")]
    [InlineData(@"<a href=""#test""/>", @"<a href=""#test""></a>")]
    [InlineData(@"<span class=""label""/>", @"<span class=""label""></span>")]
    [InlineData(@"<kbd class=""key""/>", @"<kbd class=""key""></kbd>")]
    public void NormalizeXhtmlContent_InlineSelfClosingTags_AreExpandedToFullPairs(string input, string expected)
    {
        var result = HtmlSegmenter.NormalizeXhtmlContent(input);
        Assert.Equal(expected, result);
    }
}
