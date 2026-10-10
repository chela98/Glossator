namespace Glossator.Tests.Security;

using Glossator.Core.Security;
using Xunit;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.epub", "_con.epub")]
    [InlineData("AUX.txt", "_AUX.txt")]
    [InlineData("PRN", "_PRN")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1.epub", "_COM1.epub")]
    public void SanitizeFileName_WithReservedDeviceNames_PrefixesUnderscore(string input, string expected)
    {
        var result = PathSanitizer.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileName_WithInvalidCharsAndSpaces_CleansProperly()
    {
        var input = "Agentic: Design Patterns? <Draft>|* .epub";
        var result = PathSanitizer.SanitizeFileName(input);
        Assert.Equal("Agentic_Design_Patterns_Draft.epub", result);
    }

    [Fact]
    public void SanitizeFileName_WithTrailingDotsAndSpaces_TrimsProperly()
    {
        var input = "My Book Title...   ";
        var result = PathSanitizer.SanitizeFileName(input);
        Assert.Equal("My_Book_Title", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SanitizeFileName_WithEmptyOrNull_ReturnsDefaultName(string? input)
    {
        var result = PathSanitizer.SanitizeFileName(input!);
        Assert.Equal("Glossator_Book", result);
    }

    [Fact]
    public void ResolveSafeOutputPath_WithExplicitPath_UsesExplicitPath()
    {
        var input = @"C:\Books\Sample.epub";
        var explicitOutput = @"D:\Exports\Final_Book.epub";

        var result = PathSanitizer.ResolveSafeOutputPath(input, explicitOutput, "Sample Title");

        Assert.Equal(Path.GetFullPath(explicitOutput), result);
    }

    [Fact]
    public void ResolveSafeOutputPath_WithEpubInput_PlacesOutputInSameDirectory()
    {
        var tempFile = Path.GetTempFileName();
        var epubFile = Path.ChangeExtension(tempFile, ".epub");
        File.Move(tempFile, epubFile);

        try
        {
            var result = PathSanitizer.ResolveSafeOutputPath(epubFile, null, "Building AI Agents");
            var expectedDir = Path.GetDirectoryName(epubFile);

            Assert.Equal(expectedDir, Path.GetDirectoryName(result));
            Assert.Equal("Building_AI_Agents_TR.epub", Path.GetFileName(result));
        }
        finally
        {
            if (File.Exists(epubFile)) File.Delete(epubFile);
        }
    }
}
