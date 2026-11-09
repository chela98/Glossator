namespace Glossator.Cli.Infrastructure;

public static class DocumentTranslationStatus
{
    /// <summary>
    /// Bir belgenin eksiksiz olarak Türkçeye çevrilip çevrilmediğini belirler.
    /// Yarıda kalmış (.part) dosyaları tamamlandı saymaz; .done onayını veya nihai dosyayı doğrular.
    /// </summary>
    public static bool IsDocumentTranslated(string opfDirectory, string docHref)
    {
        if (string.IsNullOrWhiteSpace(opfDirectory) || string.IsNullOrWhiteSpace(docHref))
        {
            return false;
        }

        var docFullPath = Path.Combine(opfDirectory, docHref);
        var docDir = Path.GetDirectoryName(docFullPath) ?? opfDirectory;
        var baseName = Path.GetFileNameWithoutExtension(docHref);
        var ext = Path.GetExtension(docHref);

        // 1. .done onay dosyası varsa bölüm eksiksiz olarak tamamlanmıştır (.part kalmış olsa bile öncelik .done'dadır)
        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}.done")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml.done")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.html.done")))
        {
            return true;
        }

        // 2. Eğer .part ara çalışma dosyası mevcutsa (ve .done onaylanmamışsa), bölüm yarıda kalmıştır
        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}.part")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml.part")) ||
            File.Exists(Path.Combine(docDir, $"{baseName}_tr.html.part")))
        {
            return false;
        }

        // 2. Geriye dönük uyumluluk: .part dosyası yoksa ve dosya mevcutsa tamamlanmış kabul et
        if (!string.IsNullOrEmpty(ext) && File.Exists(Path.Combine(docDir, $"{baseName}_tr{ext}")))
        {
            return true;
        }

        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr.xhtml"))) return true;
        if (File.Exists(Path.Combine(docDir, $"{baseName}_tr.html"))) return true;

        return false;
    }
}
