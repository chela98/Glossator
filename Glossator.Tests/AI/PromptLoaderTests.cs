using System;
using System.IO;
using System.Threading.Tasks;
using Glossator.AI.Prompts;
using Xunit;

namespace Glossator.Tests.AI;

public class PromptLoaderTests : IDisposable
{
    private readonly string _tempBaseDir;

    public PromptLoaderTests()
    {
        _tempBaseDir = Path.Combine(Path.GetTempPath(), $"glossator_prompts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempBaseDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempBaseDir))
                Directory.Delete(_tempBaseDir, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData("TranslatorPrompt.md")]
    [InlineData("StyleAnalystPrompt.md")]
    [InlineData("ReviewerPrompt.md")]
    public async Task LoadPromptAsync_WhenNoDiskOverrideInBaseDir_LoadsEmbeddedPromptResource(string fileName)
    {
        // Arrange: Boş izole baz dizin -> gömülü kaynak fallback'i devreye girmeli
        var emptyBaseDir = Path.Combine(_tempBaseDir, "empty");
        Directory.CreateDirectory(emptyBaseDir);

        // Act
        var content = await PromptLoader.LoadPromptAsync(fileName, emptyBaseDir);
        var activePath = PromptLoader.GetActiveDiskPath(fileName, emptyBaseDir);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Null(activePath);
    }

    [Fact]
    public async Task LoadPromptAsync_AppliesDiskPriorityOrder_PromptsSubfolderBeforeDirectBaseDirAndEmbedded()
    {
        // Arrange
        var directPath = Path.Combine(_tempBaseDir, "TranslatorPrompt.md");
        var subFolder = Path.Combine(_tempBaseDir, "Prompts");
        Directory.CreateDirectory(subFolder);
        var subFolderPath = Path.Combine(subFolder, "TranslatorPrompt.md");

        await File.WriteAllTextAsync(directPath, "DIRECT_OVERRIDE");
        await File.WriteAllTextAsync(subFolderPath, "PROMPTS_SUBFOLDER_OVERRIDE");

        // Act & Assert 1: Prompts/ alt klasörü en yüksek öncelikli olmalı
        var highest = await PromptLoader.LoadPromptAsync("TranslatorPrompt.md", _tempBaseDir);
        Assert.Equal("PROMPTS_SUBFOLDER_OVERRIDE", highest);
        Assert.Equal(subFolderPath, PromptLoader.GetActiveDiskPath("TranslatorPrompt.md", _tempBaseDir));

        // Act & Assert 2: Prompts/ altındaki silinirse doğrudan baseDirectory dosyasını almalı
        File.Delete(subFolderPath);
        var secondary = await PromptLoader.LoadPromptAsync("TranslatorPrompt.md", _tempBaseDir);
        Assert.Equal("DIRECT_OVERRIDE", secondary);
        Assert.Equal(directPath, PromptLoader.GetActiveDiskPath("TranslatorPrompt.md", _tempBaseDir));
    }

    [Fact]
    public async Task LoadPromptAsync_WhenFileNotFoundOnDiskOrEmbedded_ThrowsFileNotFoundException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PromptLoader.LoadPromptAsync("NonExistentPrompt_XYZ.md", _tempBaseDir));
        Assert.Contains("NonExistentPrompt_XYZ.md", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadPromptAsync_WhenFileNameNullOrWhitespace_ThrowsArgumentException(string invalidName)
    {
        // Act & Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            PromptLoader.LoadPromptAsync(invalidName, _tempBaseDir));
    }
}
