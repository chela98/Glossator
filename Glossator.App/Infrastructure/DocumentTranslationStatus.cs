using System.IO;

namespace Glossator.App.Infrastructure;

public enum ChapterTranslationState
{
    Pending,
    Partial,
    Completed
}

public static class DocumentTranslationStatus
{
    public static ChapterTranslationState GetState(string opfDirectory, string docHref)
    {
        if (string.IsNullOrWhiteSpace(opfDirectory) || string.IsNullOrWhiteSpace(docHref))
        {
            return ChapterTranslationState.Pending;
        }

        var docFullPath = Path.Combine(opfDirectory, docHref);
        var docDir = Path.GetDirectoryName(docFullPath) ?? opfDirectory;
        var baseName = Path.GetFileNameWithoutExtension(docHref);
        var ext = Path.GetExtension(docHref);

        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}.done")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml.done")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.html.done")))
        {
            return ChapterTranslationState.Completed;
        }

        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}.part")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml.part")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.html.part")))
        {
            return ChapterTranslationState.Partial;
        }

        if ((!string.IsNullOrEmpty(ext) && File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}"))) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.html")))
        {
            return ChapterTranslationState.Completed;
        }

        return ChapterTranslationState.Pending;
    }

    public static bool IsDocumentTranslated(string opfDirectory, string docHref)
        => GetState(opfDirectory, docHref) == ChapterTranslationState.Completed;
}
