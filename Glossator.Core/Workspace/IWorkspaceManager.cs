namespace Glossator.Core.Workspace;

public interface IWorkspaceManager
{
    Task<IWorkspaceSession> CreateSessionAsync(WorkspaceOptions options, CancellationToken cancellationToken = default);
    Task CleanupOrphanedSessionsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}
