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
        // Act
        var result = PathSanitizer.SanitizeFileName(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("...   ")]
    [InlineData("<>|?*")]
    public void SanitizeFileName_WithEmptyNullOrOnlyInvalidChars_ReturnsDefaultName(string? input)
    {
        // Act
        var result = PathSanitizer.SanitizeFileName(input!);

        // Assert
        Assert.Equal("Glossator_Book", result);
    }

    [Fact]
    public void SanitizeFileName_WithInvalidCharsSpacesAndTrailingDots_CleansProperly()
    {
        // Arrange
        var input = "Agentic: Design Patterns? <Draft>|* ...   ";

        // Act
        var result = PathSanitizer.SanitizeFileName(input);

        // Assert
        Assert.Equal("Agentic_Design_Patterns_Draft", result);
        Assert.DoesNotContain(":", result);
        Assert.False(result.EndsWith('.'));
        Assert.False(result.EndsWith(' '));
    }

    [Fact]
    public void SanitizeFileName_WithNTFSAlternateDataStream_StripsStreamDelimiter()
    {
        // Arrange
        var maliciousInput = "safe_book.epub:evil_payload.exe";

        // Act
        var result = PathSanitizer.SanitizeFileName(maliciousInput);

        // Assert
        Assert.DoesNotContain(":", result);
        Assert.Equal("safe_book.epubevil_payload.exe", result);
    }

    [Fact]
    public void SanitizeFileName_WithExtremelyLongNameAndExtension_TruncatesBaseAndPreservesExtension()
    {
        // Arrange
        var longTitle = new string('A', 300) + ".epub";

        // Act
        var result = PathSanitizer.SanitizeFileName(longTitle, maxBaseLength: 50);

        // Assert
        Assert.Equal(new string('A', 50) + ".epub", result);
    }

    [Fact]
    public void ResolveSafeOutputPath_WithExplicitPath_UsesExplicitPath()
    {
        // Arrange
        var input = Path.Combine(Path.GetTempPath(), "Books", "Sample.epub");
        var explicitOutput = Path.Combine(Path.GetTempPath(), "Exports", "Final_Book.epub");

        // Act
        var result = PathSanitizer.ResolveSafeOutputPath(input, explicitOutput, "Sample Title");

        // Assert
        Assert.Equal(Path.GetFullPath(explicitOutput), result);
    }

    [Fact]
    public void ResolveSafeOutputPath_WithEpubInput_PlacesOutputInSameDirectory()
    {
        // Arrange
        var epubFile = Path.Combine(Path.GetTempPath(), $"PathSanitizer_{Guid.NewGuid():N}.epub");
        File.WriteAllText(epubFile, "dummy");

        try
        {
            // Act
            var result = PathSanitizer.ResolveSafeOutputPath(epubFile, null, "Building AI Agents");

            // Assert
            Assert.Equal(Path.GetDirectoryName(epubFile), Path.GetDirectoryName(result));
            Assert.Equal("Building_AI_Agents_TR.epub", Path.GetFileName(result));
        }
        finally
        {
            if (File.Exists(epubFile)) File.Delete(epubFile);
        }
    }

    [Fact]
    public void ResolveSafeOutputPath_WithDirectoryInput_PlacesOutputInParentDirectory()
    {
        // Arrange
        var parentDir = Path.Combine(Path.GetTempPath(), $"ParentBook_{Guid.NewGuid():N}");
        var bookDir = Path.Combine(parentDir, "ExtractedBook");
        Directory.CreateDirectory(bookDir);

        try
        {
            // Act
            var result = PathSanitizer.ResolveSafeOutputPath(bookDir, null, "Extracted Title");

            // Assert
            Assert.Equal(parentDir, Path.GetDirectoryName(result));
            Assert.Equal("Extracted_Title_TR.epub", Path.GetFileName(result));
        }
        finally
        {
            if (Directory.Exists(parentDir)) Directory.Delete(parentDir, true);
        }
    }

    [Fact]
    public void ResolveSafeOutputPath_WithDirectoryTraversalInTitle_DoesNotEscapeBaseDirectory()
    {
        // Arrange
        var baseDir = Path.Combine(Path.GetTempPath(), $"EpubBase_{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        var inputEpub = Path.Combine(baseDir, "original.epub");
        File.WriteAllText(inputEpub, "dummy");

        try
        {
            var maliciousTitle = "../../../../../Windows/System32/evil";

            // Act
            var resolved = PathSanitizer.ResolveSafeOutputPath(inputEpub, null, maliciousTitle);

            // Assert
            Assert.StartsWith(baseDir, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", resolved);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }

    [Theory]
    [InlineData("../outside.opf")]
    [InlineData(@"..\..\outside.opf")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\attacker\share\evil.xml")]
    [InlineData("../book-evil/content.opf")]
    [InlineData("sub/../../escape.xhtml")]
    public void ResolveWithinRoot_WithTraversalOrAbsolutePath_ThrowsSecurityException(string maliciousPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "book");
        Assert.Throws<System.Security.SecurityException>(() => PathSanitizer.ResolveWithinRoot(root, maliciousPath));
    }

    [Fact]
    public void ResolveWithinRoot_WithSiblingSubdirInsideAllowedRoot_ResolvesSuccessfullyAndBlocksParentEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), $"epub_root_{Guid.NewGuid():N}");
        var opfDir = Path.Combine(root, "OEBPS");
        var resolved = PathSanitizer.ResolveWithinRoot(root, opfDir, "../Images/cover.png");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "Images", "cover.png")), resolved);
        Assert.Throws<System.Security.SecurityException>(() => PathSanitizer.ResolveWithinRoot(root, opfDir, "../../outside.png"));
    }
}
