namespace Glossator.Core.Workspace;

using Glossator.Core.Models.Epub;
using Glossator.Core.Packaging;

public sealed class WorkspaceSession : IWorkspaceSession
{
    private readonly bool _autoCleanup;
    private readonly string _sessionRoot;
    private readonly IPackageDeliveryService _deliveryService;
    private int _disposed;

    public Guid SessionId { get; }
    public WorkspaceMode Mode { get; }
    public string WorkingDirectory { get; }
    public string StagingDirectory { get; }
    public string ResolvedOutputPath { get; }
    public EpubPackage Package { get; }

    internal WorkspaceSession(
        Guid sessionId,
        WorkspaceMode mode,
        string sessionRoot,
        string workingDir,
        string stagingDir,
        string resolvedOutputPath,
        EpubPackage package,
        bool autoCleanup,
        IPackageDeliveryService deliveryService)
    {
        SessionId = sessionId;
        Mode = mode;
        _sessionRoot = sessionRoot;
        WorkingDirectory = workingDir;
        StagingDirectory = stagingDir;
        ResolvedOutputPath = resolvedOutputPath;
        Package = package;
        _autoCleanup = autoCleanup;
        _deliveryService = deliveryService;
    }

    public Task<string> GetChapterWorkingDirectoryAsync(string relativeHref, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(Package.OpfDirectory, relativeHref);
        return Task.FromResult(fullPath);
    }

    public async Task CommitOutputAsync(string stagingPackagePath, CancellationToken cancellationToken = default)
    {
        await _deliveryService.DeliverAtomicallyAsync(stagingPackagePath, ResolvedOutputPath, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_autoCleanup && Directory.Exists(_sessionRoot))
        {
            await ResilientDeleteDirectoryAsync(_sessionRoot);
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_autoCleanup && Directory.Exists(_sessionRoot))
        {
            ResilientDeleteDirectorySync(_sessionRoot);
        }

        GC.SuppressFinalize(this);
    }

    private static async Task ResilientDeleteDirectoryAsync(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay((attempt + 1) * 200);
            }
            catch
            {
                return;
            }
        }
    }

    private static void ResilientDeleteDirectorySync(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep((attempt + 1) * 200);
            }
            catch
            {
                return;
            }
        }
    }
}
