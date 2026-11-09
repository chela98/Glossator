namespace Glossator.Tests.CLI;

using Glossator.Cli.Infrastructure;
using Xunit;

public class DocumentTranslationStatusTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentTranslationStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"doc_status_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void IsDocumentTranslated_WhenNoFilesExist_ReturnsFalse()
    {
        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "chapter01.xhtml");
        Assert.False(result);
    }

    [Theory]
    [InlineData(null, "chapter01.xhtml")]
    [InlineData("", "chapter01.xhtml")]
    [InlineData("   ", "chapter01.xhtml")]
    [InlineData("dir", null)]
    [InlineData("dir", "")]
    public void IsDocumentTranslated_WithNullOrWhitespace_ReturnsFalseWithoutThrowing(string? dir, string? href)
    {
        var result = DocumentTranslationStatus.IsDocumentTranslated(dir!, href!);
        Assert.False(result);
    }

    [Fact]
    public void IsDocumentTranslated_WhenPartFileExists_ReturnsFalseEvenIfOutputExists()
    {
        // Yarım kalmış çeviri senaryosu: Dosya diskte var ama .part dosyası da var
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml"), "<html><body>Partial</body></html>");
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml.part"), "<html><body>Partial</body></html>");

        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "chapter01.xhtml");
        Assert.False(result, "Bölüm .part dosyası içeriyorsa asla tamamlandı sayılmamalıdır.");
    }

    [Fact]
    public void IsDocumentTranslated_WhenDoneFileExists_ReturnsTrue()
    {
        // Tamamlanmış bölüm: .done dosyası mevcut
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml"), "<html><body>Completed</body></html>");
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml.done"), DateTime.UtcNow.ToString("o"));

        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "chapter01.xhtml");
        Assert.True(result);
    }

    [Fact]
    public void IsDocumentTranslated_LegacyFallback_WhenOnlyFinalFileExistsWithoutPart_ReturnsTrue()
    {
        // Eski sürüm uyumluluğu: Sadece _tr.xhtml var, .part yok
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml"), "<html><body>Old Version</body></html>");

        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "chapter01.xhtml");
        Assert.True(result);
    }

    [Fact]
    public void IsDocumentTranslated_HtmlFallback_WorksForBothHtmlAndXhtml()
    {
        // Orijinal doc 'ch02.html' iken çıktı 'ch02_tr.html'
        File.WriteAllText(Path.Combine(_tempDir, "ch02_tr.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_tempDir, "ch02_tr.html.done"), "OK");

        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "ch02.html");
        Assert.True(result);
    }

    [Fact]
    public void IsDocumentTranslated_WhenBothDoneAndPartFileExist_ReturnsTrueBecauseDoneHasPrecedence()
    {
        // Bölüm tamamlanmış (.done var) fakat .part dosyası henüz silinememişse öncelik .done'dadır
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml"), "<html><body>Completed</body></html>");
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml.done"), DateTime.UtcNow.ToString("o"));
        File.WriteAllText(Path.Combine(_tempDir, "chapter01_tr.xhtml.part"), "<html><body>Residual Part</body></html>");

        var result = DocumentTranslationStatus.IsDocumentTranslated(_tempDir, "chapter01.xhtml");
        Assert.True(result, ".done mevcut olduğunda silinememiş .part dosyası tamamlanma durumunu bozmamalıdır.");
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
