namespace Glossator.Core.Workspace;

public sealed record WorkspaceOptions
{
    public required string InputPath { get; init; }
    public string? ExplicitOutputPath { get; init; }
    public WorkspaceMode PreferredMode { get; init; } = WorkspaceMode.ArchiveExtraction;
    public bool AutoCleanupOnDispose { get; init; } = true;
}
