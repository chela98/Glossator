namespace Glossator.Tests.CLI;

using Glossator.Cli.Infrastructure;
using Glossator.Core.Models.Epub;
using Xunit;

public class RecentBooksManagerTests : IDisposable
{
    private readonly string _tempDir;

    public RecentBooksManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"recent_books_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CountExistingTranslations_WithBothHtmlAndXhtml_CountsCorrectly()
    {
        // 1. Dosyaları oluştur
        File.WriteAllText(Path.Combine(_tempDir, "ch01_tr.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_tempDir, "ch02_tr.xhtml"), "<html></html>");
        File.WriteAllText(Path.Combine(_tempDir, "ch03.html"), "<html></html>"); // Orijinal, çeviri değil
        File.WriteAllText(Path.Combine(_tempDir, "ch04.xhtml"), "<html></html>"); // Orijinal, çeviri değil

        // 2. Alt klasörde de kontrol
        var subDir = Path.Combine(_tempDir, "text");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "appendix_tr.xhtml"), "<html></html>");

        var count = RecentBooksManager.CountExistingTranslations(_tempDir);
        Assert.Equal(3, count); // ch01_tr.html + ch02_tr.xhtml + appendix_tr.xhtml
    }

    [Fact]
    public void CountExistingTranslations_NonExistentDirectory_ReturnsZeroWithoutThrowing()
    {
        var nonExistent = Path.Combine(_tempDir, "does_not_exist");
        var count = RecentBooksManager.CountExistingTranslations(nonExistent);
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("Deep Learning with Python", "François Chollet", "Manning", "Teknik Yayıncı")]
    [InlineData("Building AI Agents with .NET", "Packt Publishing", "Packt", "Packt")]
    [InlineData("Mastering Algorithms", "Wiley", "Wiley", "Wiley")]
    public void DetectPublisher_MatchesKnownSignatures(string title, string author, string opfDir, string expected)
    {
        var package = new EpubPackage
        {
            Title = title,
            Author = author,
            OpfDirectory = opfDir
        };

        var publisher = RecentBooksManager.DetectPublisher(package);
        Assert.Equal(expected, publisher);
    }

    [Fact]
    public void DetectPublisher_WhenPathContainsCh_DoesNotFalsePositiveAsOReilly()
    {
        // Dosya yolu "Architecture" gibi içinde "ch" barındırsa bile, dosya adı "ch..." ile başlamadıkça O'Reilly sayılmamalı
        var bookDir = Path.Combine(_tempDir, "Architecture_Books");
        Directory.CreateDirectory(bookDir);
        File.WriteAllText(Path.Combine(bookDir, "intro.html"), "<html></html>");

        var package = new EpubPackage
        {
            Title = "Software Architecture",
            Author = "Martin Fowler",
            OpfDirectory = bookDir
        };

        var publisher = RecentBooksManager.DetectPublisher(package);
        Assert.Equal("Teknik Yayıncı", publisher);
    }

    [Fact]
    public async Task RemoveBookAsync_RemovesSpecifiedBookFromRecentList()
    {
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempDir);
        try
        {
            var initialList = new List<RecentBookEntry>
            {
                new() { Path = Path.Combine(_tempDir, "book1.epub"), Title = "Book 1", TotalChapters = 5, TranslatedChapters = 1 },
                new() { Path = Path.Combine(_tempDir, "book2.epub"), Title = "Book 2", TotalChapters = 8, TranslatedChapters = 2 },
            };
            await RecentBooksManager.SaveRecentBooksAsync(initialList);

            var removed = await RecentBooksManager.RemoveBookAsync(Path.Combine(_tempDir, "book1.epub"));
            Assert.True(removed);

            var remaining = await RecentBooksManager.LoadRecentBooksAsync();
            Assert.Single(remaining);
            Assert.Equal("Book 2", remaining[0].Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLOSSATOR_HOME", null);
        }
    }

    [Fact]
    public async Task CleanMissingBooksAsync_RemovesOnlyNonExistentPaths()
    {
        Environment.SetEnvironmentVariable("GLOSSATOR_HOME", _tempDir);
        try
        {
            var realBookFile = Path.Combine(_tempDir, "real_book.epub");
            File.WriteAllText(realBookFile, "content");

            var fakeBookFile = Path.Combine(_tempDir, "deleted_book.epub");

            var initialList = new List<RecentBookEntry>
            {
                new() { Path = realBookFile, Title = "Real Book", TotalChapters = 5, TranslatedChapters = 1 },
                new() { Path = fakeBookFile, Title = "Fake Book", TotalChapters = 8, TranslatedChapters = 0 },
            };
            await RecentBooksManager.SaveRecentBooksAsync(initialList);

            var cleanedCount = await RecentBooksManager.CleanMissingBooksAsync();
            Assert.Equal(1, cleanedCount);

            var remaining = await RecentBooksManager.LoadRecentBooksAsync();
            Assert.Single(remaining);
            Assert.Equal("Real Book", remaining[0].Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLOSSATOR_HOME", null);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
