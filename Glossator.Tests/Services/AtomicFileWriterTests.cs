using Glossator.Core.Services;

namespace Glossator.Tests.Services;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"glossator-atomic-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteAllTextAsync_CreatesAndOverwritesWithoutLeavingTempFiles()
    {
        var targetPath = Path.Combine(_tempDir, "nested", "user_settings.json");

        await AtomicFileWriter.WriteAllTextAsync(targetPath, "{\"v\":1}");
        Assert.Equal("{\"v\":1}", await File.ReadAllTextAsync(targetPath));

        await AtomicFileWriter.WriteAllTextAsync(targetPath, "{\"v\":2}");
        Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(targetPath));

        var leftoverFiles = Directory.GetFiles(Path.GetDirectoryName(targetPath)!);
        Assert.Single(leftoverFiles);
        Assert.Equal(targetPath, leftoverFiles[0]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
