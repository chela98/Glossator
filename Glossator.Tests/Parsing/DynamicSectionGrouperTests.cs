using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class DynamicSectionGrouperTests
{
    [Fact]
    public void Group_WithMajorHeadings_SplitsAtEveryMajorHeading()
    {
        // Arrange
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
        Assert.Equal(2, sections.Count);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(2, sections[0].Segments.Count);

        Assert.Equal("Section 1.1: Architecture", sections[1].Title);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_LongTextWithoutHeadings_EnforcesHardTokenLimit()
    {
        // Arrange: 30 adet uzun paragraf (~150 kelime her biri -> ~200 token x 30 = ~6.000 token)
        // Hiçbir başlık yok, hepsi <p>
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

        // Assert: 25.000 token patlaması olmamalı, birden fazla güvenli bölüme ayrılmalı
        Assert.True(sections.Count > 1, "Uzun metin birden fazla bölüme ayrılmalıydı.");

        // Hiçbir bölümün toplam token sayısı hard limit olan 2.500'ü aşmamalı
        foreach (var sec in sections)
        {
            var totalTokens = sec.Segments.Sum(s => HtmlSectionGrouper.EstimateTokens(s));
            // Bir bölüm tek başına dev bir paragraf içermiyorsa 2.500 sınırında bölünmelidir
            Assert.True(totalTokens <= HtmlSectionGrouper.HardTokenLimit + 250, 
                $"Bölüm token sayısı ({totalTokens}) hard limit sınırını aştı.");
        }
    }

    [Fact]
    public void Group_WithSoftLimitAndSemanticTag_SplitsAtSemanticBoundary()
    {
        // Arrange: 1.800 token'ı aşacak ama 2.500 hard limit'i aşmayacak kadar paragraf üret (5 x ~380 token = ~1.900 token)
        var paragraphWords = string.Join(" ", Enumerable.Repeat("software architecture patterns", 95)); // 285 kelime -> ~385 token
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

        // Semantic sınır: <h4>
        segments.Add(new HtmlSegment
        {
            Index = 5,
            TagName = "h4",
            MaskedText = "Sub-Concept 1"
        });
        segments.Add(new HtmlSegment
        {
            Index = 6,
            TagName = "p",
            MaskedText = "Detail under sub-concept."
        });

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch03");

        // Assert: 1.800 soft limit aşılıp <h4> geldiğinde bölünmeli
        Assert.Equal(2, sections.Count);
        Assert.Equal(5, sections[0].Segments.Count);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_WhenExceedingMaxSegmentsPerSection_SplitsAt20Segments()
    {
        // Arrange: 25 adet kısa paragraf
        var segments = new List<HtmlSegment>();
        for (int i = 0; i < 25; i++)
        {
            segments.Add(new HtmlSegment
            {
                Index = i,
                TagName = "p",
                MaskedText = $"Short paragraph {i}"
            });
        }

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch04");

        // Assert: Maksimum 20 segment sınırında bölünmeli
        Assert.Equal(2, sections.Count);
        Assert.Equal(20, sections[0].Segments.Count);
        Assert.Equal(5, sections[1].Segments.Count);
    }
}
