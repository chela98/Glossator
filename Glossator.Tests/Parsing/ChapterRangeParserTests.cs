using Glossator.Core.Parsing;
using Xunit;

namespace Glossator.Tests.Parsing;

public class ChapterRangeParserTests
{
    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("1-3", 0)]
    [InlineData("1-3", -5)]
    public void Parse_WithEmptyInputOrNonPositiveMax_ReturnsEmpty(string? input, int maxChapters)
    {
        var result = ChapterRangeParser.Parse(input, maxChapters);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_WithMixedRangesAndSingles_ParsesAllExpectedChapters()
    {
        var result = ChapterRangeParser.Parse("1-3, 5, 8-10", 12);

        Assert.Equal(new HashSet<int> { 1, 2, 3, 5, 8, 9, 10 }, result);
    }

    [Fact]
    public void Parse_WithReversedBoundsAndOutOfRangeValues_ClampsSafely()
    {
        var result = ChapterRangeParser.Parse("5-3, 0-2, 9-99, 25", 10);

        Assert.Equal(new HashSet<int> { 1, 2, 3, 4, 5, 9, 10 }, result);
    }

    [Fact]
    public void Parse_WithCompletelyOutOfRangeInterval_IgnoresToken()
    {
        var result = ChapterRangeParser.Parse("15-20, 0", 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_WithMalformedTokens_IgnoresInvalidAndKeepsValid()
    {
        var result = ChapterRangeParser.Parse("abc, 2-x, , 4", 8);

        Assert.Equal(new HashSet<int> { 4 }, result);
    }
}
