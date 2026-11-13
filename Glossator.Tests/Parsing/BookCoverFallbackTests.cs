using Glossator.Core.Models.Epub;
using Xunit;

namespace Glossator.Tests.Parsing;

public class BookCoverFallbackTests
{
    [Fact]
    public void ForBook_MultiWordTitle_ExtractsFirstTwoInitials()
    {
        var fallback = BookCoverFallback.ForBook("Designing Data-Intensive Applications", "Martin Kleppmann");

        Assert.Equal("DD", fallback.Monogram);
        Assert.StartsWith("#", fallback.BackgroundHex);
        Assert.StartsWith("#", fallback.AccentHex);
        Assert.Equal(7, fallback.BackgroundHex.Length);
    }

    [Fact]
    public void ForBook_SingleWordTitle_ExtractsFirstTwoCharacters()
    {
        var fallback = BookCoverFallback.ForBook("Kubernetes");

        Assert.Equal("KU", fallback.Monogram);
    }

    [Fact]
    public void ForBook_EmptyTitleAndAuthor_FallsBackToEpMonogram()
    {
        var fallback = BookCoverFallback.ForBook("   ", null);

        Assert.Equal("EP", fallback.Monogram);
    }

    [Fact]
    public void ForBook_SameBookCalledTwice_ProducesDeterministicPalette()
    {
        var first = BookCoverFallback.ForBook("Clean Architecture", "Robert C. Martin");
        var second = BookCoverFallback.ForBook("Clean Architecture", "Robert C. Martin");

        Assert.Equal(first, second);
    }
}
