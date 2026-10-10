namespace Glossator.Core.Workspace;

public enum WorkspaceMode
{
    ArchiveExtraction, // .epub açıldı, izole temp dizini içinde
    InPlaceDirectory,  // Var olan klasörde doğrudan çalışılıyor
    IsolatedCopy       // Var olan klasör geçici alana klonlandı
}
