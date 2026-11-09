using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class HtmlSectionUnpackerTests
{
    [Fact]
    public void TryUnpackTranslations_Stage1Canonical_UnpacksAllBlocks()
    {
        // Arrange
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" },
                new HtmlSegment { Index = 2, TagName = "p", MaskedText = "Original 2" }
            ]
        };

        var response = """
            [BLOK_001 | TAG: p]
            Birinci paragraf çevirisi.
            [/BLOK_001]

            [BLOK_002 | TAG: p]
            İkinci paragraf çevirisi.
            [/BLOK_002]
            """;

        // Act
        var success = section.TryUnpackTranslations(response, out var missing);

        // Assert
        Assert.True(success);
        Assert.Empty(missing);
        Assert.Equal("Birinci paragraf çevirisi.", section.Segments[0].TranslatedText);
        Assert.Equal("İkinci paragraf çevirisi.", section.Segments[1].TranslatedText);
    }

    [Fact]
    public void TryUnpackTranslations_Stage2Lookahead_UnpacksWhenClosingTagsOmitted()
    {
        // Arrange: LLM kapanış [/BLOK_xxx] etiketini unuttu
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" },
                new HtmlSegment { Index = 2, TagName = "p", MaskedText = "Original 2" }
            ]
        };

        var responseWithoutClosingTags = """
            [BLOK_001]
            Birinci paragraf kapanışsız çeviri.
            [BLOK_002]
            İkinci paragraf kapanışsız çeviri.
            """;

        // Act
        var success = section.TryUnpackTranslations(responseWithoutClosingTags, out var missing);

        // Assert
        Assert.True(success);
        Assert.Empty(missing);
        Assert.Equal("Birinci paragraf kapanışsız çeviri.", section.Segments[0].TranslatedText);
        Assert.Equal("İkinci paragraf kapanışsız çeviri.", section.Segments[1].TranslatedText);
    }

    [Fact]
    public void TryUnpackTranslations_Stage3MarkdownHeaders_UnpacksFallbackFormat()
    {
        // Arrange: LLM Markdown formatında yanıt verdi
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" },
                new HtmlSegment { Index = 2, TagName = "p", MaskedText = "Original 2" }
            ]
        };

        var markdownResponse = """
            ### BLOK 001
            Markdown başlığıyla gelen birinci metin.

            ### BLOK 002
            Markdown başlığıyla gelen ikinci metin.
            """;

        // Act
        var success = section.TryUnpackTranslations(markdownResponse, out var missing);

        // Assert
        Assert.True(success);
        Assert.Empty(missing);
        Assert.Equal("Markdown başlığıyla gelen birinci metin.", section.Segments[0].TranslatedText);
        Assert.Equal("Markdown başlığıyla gelen ikinci metin.", section.Segments[1].TranslatedText);
    }

    [Fact]
    public void TryUnpackTranslations_WhenBlockMissing_ReturnsFalseAndReportsMissingIndices()
    {
        // Arrange
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" },
                new HtmlSegment { Index = 2, TagName = "p", MaskedText = "Original 2" },
                new HtmlSegment { Index = 3, TagName = "p", MaskedText = "Original 3" }
            ]
        };

        var partialResponse = """
            [BLOK_001]
            Yalnızca birinci blok çevrildi.
            [/BLOK_001]
            [BLOK_003]
            Üçüncü blok çevrildi.
            [/BLOK_003]
            """;

        // Act
        var success = section.TryUnpackTranslations(partialResponse, out var missing);

        // Assert
        Assert.False(success);
        Assert.Single(missing);
        Assert.Equal(2, missing[0]);
        Assert.Equal("Yalnızca birinci blok çevrildi.", section.Segments[0].TranslatedText);
        Assert.Null(section.Segments[1].TranslatedText);
        Assert.Equal("Üçüncü blok çevrildi.", section.Segments[2].TranslatedText);
    }

    [Fact]
    public void CreateDeltaSection_CreatesSubSectionWithOnlyMissingBlocks()
    {
        // Arrange
        var seg1 = new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" };
        var seg2 = new HtmlSegment { Index = 2, TagName = "p", MaskedText = "Original 2" };
        var seg3 = new HtmlSegment { Index = 3, TagName = "p", MaskedText = "Original 3" };

        var section = new HtmlSection
        {
            SectionIndex = 1,
            SectionKey = "ch01_sec01",
            Title = "Mimari Temeller",
            Segments = [seg1, seg2, seg3]
        };

        // Act
        var delta = section.CreateDeltaSection([2]);

        // Assert
        Assert.Single(delta.Segments);
        Assert.Equal(2, delta.Segments[0].Index);
        Assert.Equal("ch01_sec01_delta", delta.SectionKey);
        Assert.Contains("Kurtarma", delta.Title);
    }

    [Fact]
    public void TryUnpackTranslations_WithVariedZeroPadding_UnpacksAccurately()
    {
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original 1" },
                new HtmlSegment { Index = 42, TagName = "p", MaskedText = "Original 42" }
            ]
        };

        var response = """
            [BLOK_1]
            Tek basamaklı indeks çevirisi.
            [/BLOK_1]

            [BLOK_42]
            İki basamaklı indeks çevirisi.
            [/BLOK_42]
            """;

        var success = section.TryUnpackTranslations(response, out var missing);

        Assert.True(success);
        Assert.Empty(missing);
        Assert.Equal("Tek basamaklı indeks çevirisi.", section.Segments[0].TranslatedText);
        Assert.Equal("İki basamaklı indeks çevirisi.", section.Segments[1].TranslatedText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryUnpackTranslations_WithEmptyOrNullResponse_ReturnsFalse(string? emptyResponse)
    {
        var section = new HtmlSection
        {
            SectionIndex = 0,
            Segments = [new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Original" }]
        };

        var success = section.TryUnpackTranslations(emptyResponse!, out var missing);

        Assert.False(success);
        Assert.Single(missing);
    }
}
