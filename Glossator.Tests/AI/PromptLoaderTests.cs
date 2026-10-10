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
    public async Task LoadPromptAsync_WhenFileNotFound_ThrowsFileNotFoundException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PromptLoader.LoadPromptAsync("NonExistentPromptFile_12345.md"));
    }
}
