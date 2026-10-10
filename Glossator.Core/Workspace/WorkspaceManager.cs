namespace Glossator.Core.Workspace;

using Glossator.Core.Packaging;
using Glossator.Core.Parsing;
using Glossator.Core.Security;

public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly IPackageDeliveryService _deliveryService;
    private static readonly string GlobalTempRoot = Path.Combine(Path.GetTempPath(), "Glossator", "sessions");

    public WorkspaceManager(IPackageDeliveryService deliveryService)
    {
        _deliveryService = deliveryService;
    }

    public async Task<IWorkspaceSession> CreateSessionAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid();
        var sessionRoot = Path.Combine(GlobalTempRoot, sessionId.ToString("N"));
        var unpackedDir = Path.Combine(sessionRoot, "unpacked");
        var stagingDir = Path.Combine(sessionRoot, "staging");

        Directory.CreateDirectory(unpackedDir);
        Directory.CreateDirectory(stagingDir);

        WorkspaceMode mode;
        string workingDir;

        if (File.Exists(options.InputPath) && options.InputPath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            mode = WorkspaceMode.ArchiveExtraction;
            await Task.Run(() => SafeZipExtractor.ExtractSafely(options.InputPath, unpackedDir), cancellationToken);
            workingDir = unpackedDir;
        }
        else if (Directory.Exists(options.InputPath))
        {
            mode = WorkspaceMode.InPlaceDirectory;
            workingDir = Path.GetFullPath(options.InputPath);
        }
        else
        {
            throw new FileNotFoundException($"Girdi dosyası veya dizini bulunamadı: {options.InputPath}");
        }

        var package = EpubManifestReader.Read(workingDir);
        var resolvedOutputPath = PathSanitizer.ResolveSafeOutputPath(
            options.InputPath,
            options.ExplicitOutputPath,
            package.Title);

        return new WorkspaceSession(
            sessionId,
            mode,
            sessionRoot,
            workingDir,
            stagingDir,
            resolvedOutputPath,
            package,
            options.AutoCleanupOnDispose,
            _deliveryService);
    }

    public async Task CleanupOrphanedSessionsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(GlobalTempRoot))
        {
            return;
        }

        await Task.Run(() =>
        {
            var threshold = DateTime.UtcNow - olderThan;
            foreach (var dir in Directory.GetDirectories(GlobalTempRoot))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.CreationTimeUtc < threshold)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                    // Kilitli klasörler sessizce atlanır
                }
            }
        }, cancellationToken);
    }
}
