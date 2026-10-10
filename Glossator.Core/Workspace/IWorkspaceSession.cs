namespace Glossator.Core.Workspace;

using Glossator.Core.Models.Epub;

public interface IWorkspaceSession : IAsyncDisposable, IDisposable
{
    Guid SessionId { get; }
    WorkspaceMode Mode { get; }
    string WorkingDirectory { get; }      // EPUB kök klasörü (unpacked veya kaynak klasör)
    string StagingDirectory { get; }      // Geçici çıktı/staging klasörü
    string ResolvedOutputPath { get; }    // Nihai .epub çıktısının kaydedileceği kesin yol
    EpubPackage Package { get; }

    Task<string> GetChapterWorkingDirectoryAsync(string relativeHref, CancellationToken cancellationToken = default);
    Task CommitOutputAsync(string stagingPackagePath, CancellationToken cancellationToken = default);
}
