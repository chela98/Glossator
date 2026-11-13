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
    [InlineData("COM¹", "_COM¹")]
    [InlineData("COM².epub", "_COM².epub")]
    [InlineData("LPT¹", "_LPT¹")]
    [InlineData("CLOCK$", "_CLOCK$")]
    [InlineData("CON ", "_CON")]
    [InlineData("NUL.", "_NUL")]
    [InlineData("AUX .txt", "_AUX.txt")]
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
    public void SanitizeFileName_WithNTFSAlternateDataStream_StripsStreamDelimiter()
    {
        var maliciousInput = "safe_book.epub:evil_payload.exe";
        var result = PathSanitizer.SanitizeFileName(maliciousInput);
        Assert.DoesNotContain(":", result);
        Assert.Equal("safe_book.epubevil_payload.exe", result);
    }

    [Fact]
    public void SanitizeFileName_WithExtremelyLongName_TruncatesSafelyWithoutInvalidTrailingChars()
    {
        var longTitle = new string('A', 300) + "...   ";
        var result = PathSanitizer.SanitizeFileName(longTitle, maxBaseLength: 100);

        Assert.Equal(100, result.Length);
        Assert.False(result.EndsWith('.'));
        Assert.False(result.EndsWith(' '));
    }

    [Fact]
    public void ResolveSafeOutputPath_WithExplicitPath_UsesExplicitPath()
    {
        var input = Path.Combine(Path.GetTempPath(), "Books", "Sample.epub");
        var explicitOutput = Path.Combine(Path.GetTempPath(), "Exports", "Final_Book.epub");

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

    [Fact]
    public void ResolveSafeOutputPath_WithDirectoryTraversalInTitle_DoesNotEscapeBaseDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "EpubBase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var inputEpub = Path.Combine(baseDir, "original.epub");
        File.WriteAllText(inputEpub, "dummy");

        try
        {
            var maliciousTitle = "../../../../../Windows/System32/evil";
            var resolved = PathSanitizer.ResolveSafeOutputPath(inputEpub, null, maliciousTitle);

            // Çıktı kesinlikle baseDir klasörünün içinde kalmalı, asla üst dizinlere kaçmamalıdır
            Assert.StartsWith(baseDir, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", resolved);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }
}
