using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class DynamicSectionGrouperTests
{
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

        // Assert: h1 mutlak sınır olduğu için 2 bölüme ayrılmalıdır
        Assert.Equal(2, sections.Count);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(2, sections[0].Segments.Count);

        Assert.Equal("Chapter 2: Advanced Topics", sections[1].Title);
        Assert.Equal(2, sections[1].Segments.Count);
    }

    [Fact]
    public void Group_WithSubHeading_BelowMinTokenLimit_DoesNotSplit()
    {
        // Arrange: h2 alt başlığı geldiğinde sepet 800 token'ın altındaysa bölünme OLMAMALIDIR
        var segments = new List<HtmlSegment>
        {
            new() { Index = 0, TagName = "h1", MaskedText = "Chapter 1: Foundations" },
            new() { Index = 1, TagName = "p", MaskedText = "This is paragraph 1." }, // ~5 token
            new() { Index = 2, TagName = "h2", MaskedText = "Section 1.1: Architecture" },
            new() { Index = 3, TagName = "p", MaskedText = "This is paragraph 2." }
        };

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch01");

        // Assert: 800 token dolmadığı için tek bir section içinde birleşmelidir
        Assert.Single(sections);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(4, sections[0].Segments.Count);
    }

    [Fact]
    public void Group_WithSubHeading_AboveMinTokenLimit_SplitsCleanly()
    {
        // Arrange: Sepette 800 token'dan fazla içerik varken h2 geldiğinde bölünmelidir
        // 5 adet ~200 kelimelik paragraf = ~1000 token (> 800)
        var words = string.Join(" ", Enumerable.Repeat("distributed vector embeddings semantic knowledge graph", 35));
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

        // 800 token'dan sonra gelen h2 alt başlığı
        segments.Add(new HtmlSegment
        {
            Index = 6,
            TagName = "h2",
            MaskedText = "Section 1.1: Architecture"
        });
        segments.Add(new HtmlSegment
        {
            Index = 7,
            TagName = "p",
            MaskedText = "Detail under new architecture."
        });

        // Act
        var sections = HtmlSectionGrouper.Group(segments, "ch01");

        // Assert: 800 token aşıldığı için h2'de temizce 2 bölüme ayrılmalıdır
        Assert.Equal(2, sections.Count);
        Assert.Equal("Chapter 1: Foundations", sections[0].Title);
        Assert.Equal(6, sections[0].Segments.Count); // h1 + 5 paragraf

        Assert.Equal("Section 1.1: Architecture", sections[1].Title);
        Assert.Equal(2, sections[1].Segments.Count); // h2 + 1 paragraf
    }

    [Fact]
    public void Group_LongTextWithoutHeadings_EnforcesHardTokenLimit()
    {
        // Arrange: 30 adet uzun paragraf (~150 kelime her biri -> ~200 token x 30 = ~6.000 token)
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

        // Assert: 2.500 token üst sınırında güvenli alt bölümlere ayrılmalı
        Assert.True(sections.Count > 1, "Uzun metin birden fazla bölüme ayrılmalıydı.");

        foreach (var sec in sections)
        {
            var totalTokens = sec.Segments.Sum(s => HtmlSectionGrouper.EstimateTokens(s));
            Assert.True(totalTokens <= HtmlSectionGrouper.HardTokenLimit + 250, 
                $"Bölüm token sayısı ({totalTokens}) hard limit sınırını aştı.");
        }
    }

    [Fact]
    public void Group_WithSoftLimitAndSemanticTag_SplitsAtSemanticBoundary()
    {
        // Arrange: 1.800 token'ı aşacak ama 2.500 hard limit'i aşmayacak kadar paragraf üret (5 x ~380 token = ~1.900 token)
        var paragraphWords = string.Join(" ", Enumerable.Repeat("software architecture patterns", 95));
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
    public void Group_WhenExceedingMaxSegmentsPerSection_SplitsAt25Segments()
    {
        // Arrange: 30 adet kısa paragraf
        var segments = new List<HtmlSegment>();
        for (int i = 0; i < 30; i++)
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

        // Assert: Maksimum 25 segment sınırında bölünmeli (İlk bölümde 25, ikinci bölümde 5)
        Assert.Equal(2, sections.Count);
        Assert.Equal(25, sections[0].Segments.Count);
        Assert.Equal(5, sections[1].Segments.Count);
    }
}
