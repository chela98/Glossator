using Glossator.AI.Prompts;
using Xunit;

namespace Glossator.Tests.AI;

public class PromptLoaderTests
{
    [Fact]
    public async Task LoadPromptAsync_LoadsTranslatorPromptFromEmbeddedResource()
    {
        // Act
        var content = await PromptLoader.LoadPromptAsync("TranslatorPrompt.md");

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
        Assert.Contains("O'REILLY", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadPromptAsync_LoadsStyleAnalystPromptFromEmbeddedResource()
    {
        // Act
        var content = await PromptLoader.LoadPromptAsync("StyleAnalystPrompt.md");

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public async Task LoadPromptAsync_LoadsReviewerPromptFromEmbeddedResource()
    {
        // Act
        var content = await PromptLoader.LoadPromptAsync("ReviewerPrompt.md");

        // Assert
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }

    [Fact]
    public async Task LoadPromptAsync_WhenLocalDiskFileExists_PrefersDiskFileOverEmbedded()
    {
        // Arrange
        var tempDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
        Directory.CreateDirectory(tempDir);
        var customPromptFile = Path.Combine(tempDir, "CustomTestPrompt.md");
        const string customText = "# Özel Biyoloji ve Tıp Çevirmen İstemi\nBu metin diskten okunmuştur.";

        try
        {
            await File.WriteAllTextAsync(customPromptFile, customText);

            // Act
            var content = await PromptLoader.LoadPromptAsync("CustomTestPrompt.md");

            // Assert
            Assert.Equal(customText, content);
        }
        finally
        {
            if (File.Exists(customPromptFile))
            {
                File.Delete(customPromptFile);
            }
        }
    }

    [Fact]
    public async Task LoadPromptAsync_WhenTranslatorPromptOverriddenOnDisk_ReturnsDiskVersionWithoutRecompilation()
    {
        // Arrange: Gerçek TranslatorPrompt.md dosyasını EXE dizinindeki Prompts klasöründe taklit et
        var tempDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
        Directory.CreateDirectory(tempDir);
        var diskPromptFile = Path.Combine(tempDir, "TranslatorPrompt.md");
        const string customBiologyPrompt = "# Moleküler Biyoloji Özel Çeviri İstemi (Diskten Değiştirildi)";

        try
        {
            // Kullanıcı diskteki promptu not defteriyle değiştirdi:
            await File.WriteAllTextAsync(diskPromptFile, customBiologyPrompt);

            // Act: Motor istemi yüklediğinde gömülü olanı değil, diskteki yeni içeriği almalıdır
            var content = await PromptLoader.LoadPromptAsync("TranslatorPrompt.md");

            // Assert
            Assert.Equal(customBiologyPrompt, content);
        }
        finally
        {
            if (File.Exists(diskPromptFile))
            {
                File.Delete(diskPromptFile);
            }
        }
    }

    [Fact]
    public async Task LoadPromptAsync_WhenFileNotFound_ThrowsFileNotFoundException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PromptLoader.LoadPromptAsync("NonExistentPromptFile_12345.md"));
    }
}
