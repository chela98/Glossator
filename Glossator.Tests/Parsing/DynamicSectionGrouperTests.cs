using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class DynamicSectionGrouperTests
{
    [Fact]
    public void Group_WithNullOrEmptySegments_ReturnsEmptyList()
    {
        // Act & Assert
        Assert.Empty(HtmlSectionGrouper.Group(null!));
        Assert.Empty(HtmlSectionGrouper.Group([]));
    }

    [Fact]
    public void Group_WithH1_SplitsImmediately_EvenWithFewTokens()
    {
        // Arrange: h1 mutlak duvar kuralı; token sayısına bakılmaksızın derhal yeni bölüme geçer
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "h1", MaskedText = "Chapter 1: Foundations" },
            new() { Index = 1, TagName = "p", MaskedText = "This is paragraph 1." },
            new() { Index = 2, TagName = "h1", MaskedText = "Chapter 2: Advanced Topics" },
            new() { Index = 3, TagName = "p", MaskedText = "This is paragraph 2." }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "book");

        // Assert
        Assert.Equal(2, sections.Count);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal("book_sec00", sections[0].SectionKey);
        Assert.Equal(2, sections[0].Segments.Count);

        Assert.Equal("Chapter 2: Advanced Topics", sections[1].Title);
        Assert.Equal("book_sec01", sections[1].SectionKey);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_WithSubHeading_BelowMinTokenLimit_DoesNotSplit()
    {
        // Arrange: h2 alt başlığı geldiğinde sepet MinTokenLimit (1200) altındaysa bölünme OLMAMALIDIR
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "h1", MaskedText = "Chapter 1: Foundations" },
            new() { Index = 1, TagName = "p", MaskedText = "This is paragraph 1." },
            new() { Index = 2, TagName = "h2", MaskedText = "Section 1.1: Architecture" },
            new() { Index = 3, TagName = "p", MaskedText = "This is paragraph 2." }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch01");

        // Assert
        Assert.Single(sections);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(4, sections[0].Segments.Count);
    }

    [Fact]
    public void Group_WithSubHeading_AboveMinTokenLimit_SplitsCleanly()
    {
        // Arrange: Sepette MinTokenLimit (1200) üzerinde içerik varken h2 geldiğinde bölünmelidir
        var words = string.Join(" ", Enumerable.Repeat("distributed vector embeddings semantic knowledge graph", 35)); // 210 kelime -> ~284 token
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "h1", MaskedText = "Chapter 1: Foundations" }
        };

        for (int i = 1; i <= 5; i++)
        {
            segments.Add(new HtmlSegment
            {
                Index = i,
                TagName = "p",
                MaskedText = $"Content block {i}: {words}"
            });
        }

        segments.Add(new HtmlSegment { Index = 6, TagName = "h2", MaskedText = "Section 1.1: Architecture" });
        segments.Add(new HtmlSegment { Index = 7, TagName = "p", MaskedText = "Detail under new architecture." });

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch01");

        // Assert
        Assert.Equal(2, sections.Count);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(6, sections[0].Segments.Count);

        Assert.Equal("Section 1.1: Architecture", sections[1].Title);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_LongTextWithoutHeadings_EnforcesHardTokenLimit()
    {
        // Arrange: 30 adet uzun paragraf (~200 token her biri -> ~6.000 token)
        var longParagraphText = string.Join(" ", Enumerable.Repeat("enterprise architecture microservices distributed systems scalability resiliency", 20));
        var segments = new List<HtmlSegment>();

        for (int i = 0; i < 30; i++)
        {
            segments.Add(new HtmlSegment
            {
                Index = i,
                TagName = "p",
                MaskedText = $"Paragraph {i + 1}: {longParagraphText}"
            });
        }

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch02");

        // Assert: Her bölüm kesinlikle HardTokenLimit (2800) içinde kalmalıdır
        Assert.True(sections.Count > 1, "Uzun metin birden fazla bölüme ayrılmalıydı.");
        foreach (var sec in sections)
        {
            var totalTokens = sec.Segments.Sum(HtmlSectionGrouper.EstimateTokens);
            Assert.True(totalTokens <= HtmlSectionGrouper.HardTokenLimit,
                $"Bölüm token sayısı ({totalTokens}) hard limit sınırını ({HtmlSectionGrouper.HardTokenLimit}) aştı.");
        }
    }

    [Fact]
    public void Group_WithSingleGiantSegmentExceedingHardLimit_IsolatesSegmentWithoutDropping()
    {
        // Arrange: İlk segment normal, ikinci segment tek başına HardTokenLimit'i aşıyor (> 2800 token)
        var giantText = string.Join(" ", Enumerable.Repeat("word", 2300)); // ~3105 token
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "p", MaskedText = "Short intro paragraph." },
            new() { Index = 1, TagName = "p", MaskedText = giantText },
            new() { Index = 2, TagName = "p", MaskedText = "Closing paragraph." }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch05");

        // Assert: Dev segment kaybolmadan izole tek segmentlik bölüm olarak gruplanmalı
        Assert.Equal(3, sections.Count);
        Assert.Single(sections[0].Segments);
        Assert.Single(sections[1].Segments);
        Assert.Equal(1, sections[1].Segments[0].Index);
        Assert.Single(sections[2].Segments);
        Assert.Equal(2, sections[2].Segments[0].Index);
    }

    [Fact]
    public void Group_WithSoftLimitAndSemanticTag_SplitsAtSemanticBoundary()
    {
        // Arrange: SoftTokenLimit (2000) üstü, HardTokenLimit (2800) altı (5 x ~430 = ~2150 token)
        var paragraphWords = string.Join(" ", Enumerable.Repeat("software architecture patterns", 105));
        var segments = new List<HtmlSegment>();

        for (int i = 0; i < 5; i++)
        {
            segments.Add(new HtmlSegment
            {
                Index = i,
                TagName = "p",
                MaskedText = $"Content {i}: {paragraphWords}"
            });
        }

        segments.Add(new HtmlSegment { Index = 5, TagName = "h4", MaskedText = "Sub-Concept 1" });
        segments.Add(new HtmlSegment { Index = 6, TagName = "p", MaskedText = "Detail under sub-concept." });

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch03");

        // Assert
        Assert.Equal(2, sections.Count);
        Assert.Equal(5, sections[0].Segments.Count);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_WhenExceedingMaxSegmentsPerSection_SplitsAtMaxSegments()
    {
        // Arrange
        var segments = Enumerable.Range(0, 30)
            .Select(i => new HtmlSegment { Index = i, TagName = "p", MaskedText = $"Short paragraph {i}" })
            .ToList();

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch04");

        // Assert
        Assert.Equal(2, sections.Count);
        Assert.Equal(HtmlSectionGrouper.MaxSegmentsPerSection, sections[0].Segments.Count);
        Assert.Equal(30 - HtmlSectionGrouper.MaxSegmentsPerSection, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_WithLongHeadingContainingPlaceholders_StripsPlaceholdersAndTruncatesTitle()
    {
        // Arrange
        var longHeading = "⟦PH_000001⟧ Very Long Chapter Title About Autonomous Multi-Agent Distributed Architectures and Enterprise Design";
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "h1", MaskedText = longHeading }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch06");

        // Assert
        var sec = Assert.Single(sections);
        Assert.DoesNotContain("⟦PH_000001⟧", sec.Title);
        Assert.Equal(60, sec.Title.Length);
        Assert.EndsWith("...", sec.Title);
    }

    [Fact]
    public void Group_WithInlineHtmlAnchorsAndEntitiesInHeading_StripsTagsBeforeTruncating()
    {
        // Arrange
        var segments = new List<HtmlSegment>
        {
            new()
            {
                Index = 0,
                TagName = "h2",
                MaskedText = "<a class=\"h_ident\" id=\"h-ygn12/ieo+\" href=\"#h-ygn12/ieo+\"></a>Expressions &amp; Operators"
            },
            new()
            {
                Index = 1,
                TagName = "p",
                MaskedText = "Sample paragraph."
            }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch07");

        // Assert
        var sec = Assert.Single(sections);
        Assert.Equal("Expressions & Operators", sec.Title);
    }
}
