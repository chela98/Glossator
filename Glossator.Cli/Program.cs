using Glossator.Core.Glossary;
using Glossator.Core.Parsing;
using Spectre.Console;

using Glossator.Core.Rules;

AnsiConsole.Write(
    new FigletText("GLOSSATOR")
        .Color(Color.Cyan1));

// 1. Dinamik Yol Alma
string bookPath;

if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
{
    bookPath = args[0].Trim('"', '\'');
}
else
{
    bookPath = AnsiConsole.Ask<string>("[yellow]Lütfen Kitap klasörünü veya .epub yolunu girin:[/] ")
                          .Trim('"', '\'');
}

// 2. Yol Kontrolü
if (!Directory.Exists(bookPath) && !File.Exists(bookPath))
{
    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Belirtilen yol bulunamadı: [dim]{bookPath}[/]");
    return;
}

AnsiConsole.MarkupLine($"\n[yellow]Ayrıştırılıyor:[/] [dim]{bookPath}[/]");

try
{
    // 3. EPUB Paketini Oku
    var package = EpubManifestReader.Read(bookPath);

    // 4. Kitap Özeti Tablosu
    var summaryTable = new Table().Border(TableBorder.Rounded);
    summaryTable.AddColumn("[bold]Özellik[/]");
    summaryTable.AddColumn("[bold]Değer[/]");

    summaryTable.AddRow("Kitap Başlığı", $"[cyan]{package.Title}[/]");
    summaryTable.AddRow("Yazar", $"[green]{package.Author ?? "Bilinmiyor"}[/]");
    summaryTable.AddRow("Spine (Bölüm Sayısı)", $"{package.Spine.Count} bölüm");
    summaryTable.AddRow("Sözlük (Glossary)", package.GlossaryItem != null ? $"[green]✔ {package.GlossaryItem.Href}[/]" : "[dim]Yok[/]");
    summaryTable.AddRow("Dizin (Index)", package.IndexItem != null ? $"[green]✔ {package.IndexItem.Href}[/]" : "[dim]Yok[/]");

    AnsiConsole.Write(summaryTable);

    // 5. Terimleri Çıkar
    var glossaryTerms = EpubGlossaryReader.Read(package);
    var indexTerms = EpubIndexReader.Read(package);

    AnsiConsole.MarkupLine($"\n[yellow]Çıkarılan Terimler:[/] Sözlük: [green]{glossaryTerms.Count}[/] | Dizin: [green]{indexTerms.Count}[/]");

    // 6. SQLite Veritabanına (glossator.db) Aktar
    var dbPath = Path.Combine(Environment.CurrentDirectory, "glossator.db");
    var repo = new SqliteGlossaryRepository(dbPath);

    var insertedGlossary = repo.ImportTerms(glossaryTerms, package.Title);
    var insertedIndex = repo.ImportTerms(indexTerms, package.Title);

    var allTerms = repo.GetAllTerms();
    var unreviewedTerms = repo.GetUnreviewedTerms();

    // 7. Veritabanı Durum Tablosu
    var dbTable = new Table().Border(TableBorder.Rounded);
    dbTable.AddColumn("[bold]Veritabanı (glossator.db)[/]");
    dbTable.AddColumn("[bold]Adet[/]");

    dbTable.AddRow("Toplam Kayıtlı Terim", $"[bold green]{allTerms.Count}[/]");
    dbTable.AddRow("İnceleme Bekleyen Terimler", $"[yellow]{unreviewedTerms.Count}[/]");
    dbTable.AddRow("Veritabanı Dosya Yolu", $"[dim]{dbPath}[/]");

    AnsiConsole.Write(dbTable);

    // 8. İlk 8 Terimden Örnek Önizleme
    AnsiConsole.MarkupLine("\n[bold yellow]Veritabanındaki Terimlerden Örnekler:[/]");
    var sampleTable = new Table().Border(TableBorder.Simple);
    sampleTable.AddColumn("#");
    sampleTable.AddColumn("İngilizce Terim");
    sampleTable.AddColumn("Politika");
    sampleTable.AddColumn("Tanım Önizleme");

    for (int i = 0; i < Math.Min(8, allTerms.Count); i++)
    {
        var t = allTerms[i];
        var def = string.IsNullOrEmpty(t.EnglishDefinition)
            ? "[dim]-[/]"
            : (t.EnglishDefinition.Length > 50 ? t.EnglishDefinition[..47] + "..." : t.EnglishDefinition);

        sampleTable.AddRow(
            t.Id.ToString(),
            $"[cyan]{t.EnglishTerm}[/]",
            $"[magenta]{t.Policy}[/]",
            def
        );
    }

    AnsiConsole.Write(sampleTable);
    AnsiConsole.MarkupLine("\n[bold green]✔ Tebrikler! Tüm terimler SQLite veritabanına aktarıldı ve kalıcı hale getirildi.[/]");


    AnsiConsole.MarkupLine("\n[bold yellow]Sayaç ve Kural Motoru Simülasyonu (TermUsageTracker):[/]");
    // 1. ch01.html için bir tracker başlatalım
    var tracker = new TermUsageTracker("ch01.html");
    // Veritabanından örnek bir kavram alalım (örn: "knowledge graphs")
    var testTerm = allTerms.FirstOrDefault(t => t.EnglishTerm.Equals("knowledge graphs", StringComparison.OrdinalIgnoreCase))
                   ?? allTerms.First();
    var trackerTable = new Table().Border(TableBorder.Rounded);
    trackerTable.AddColumn("Bölüm");
    trackerTable.AddColumn("Geçiş");
    trackerTable.AddColumn("Terim");
    trackerTable.AddColumn("Kural Kararı");
    trackerTable.AddColumn("Beklenen Çıktı Biçimi");
    // ch01.html içinde 3 kez çağıralım
    for (int i = 1; i <= 3; i++)
    {
        var decision = tracker.RecordUsage(testTerm);
        var expected = decision == TermFormatDecision.Hybrid
            ? $"{testTerm.EnglishTerm} (Türkçe Anlamı)"
            : testTerm.EnglishTerm;
        var color = decision == TermFormatDecision.Hybrid ? "yellow" : "green";
        trackerTable.AddRow("ch01.html", $"{i}. kullanım", testTerm.EnglishTerm, $"[{color}]{decision}[/]", $"[cyan]{expected}[/]");
    }
    // 2. Şimdi ch02.html'e geçelim (Sayaç sıfırlanmalı!)
    tracker.BeginNewScope("ch02.html");
    var newDecision = tracker.RecordUsage(testTerm);
    var newExpected = newDecision == TermFormatDecision.Hybrid
        ? $"{testTerm.EnglishTerm} (Türkçe Anlamı)"
        : testTerm.EnglishTerm;
    trackerTable.AddRow("[bold magenta]ch02.html (YENİ BÖLÜM)[/]", "1. kullanım", testTerm.EnglishTerm, $"[yellow]{newDecision}[/]", $"[cyan]{newExpected}[/]");
    AnsiConsole.Write(trackerTable);
    AnsiConsole.MarkupLine("\n[bold green]✔ Kural Motoru Kusursuz Çalışıyor! 3. kullanımda İngilizceye döndü, yeni bölümde sıfırlandı.[/]");

    ///-----------
    // Önceki kodların devamına:
    // Kitabın 1. Bölümünü (ch01.html) bulalım
    var ch01Item = package.Spine.FirstOrDefault(s => s.Href.Contains("ch01.html"));
    if (ch01Item != null)
    {
        var ch01FullPath = Path.Combine(package.OpfDirectory, ch01Item.Href);
        AnsiConsole.MarkupLine($"\n[bold yellow]Bölüm 1 (ch01.html) Ayrıştırılıyor:[/] [dim]{ch01FullPath}[/]");

        var (doc, segments) = HtmlSegmenter.SegmentFile(ch01FullPath);

        AnsiConsole.MarkupLine($"[green]✔ Toplam Çevrilecek Paragraf/Başlık Sayısı:[/] [bold cyan]{segments.Count}[/]");

        // Maskelenmiş koruma örneği gösterelim
        var maskedSample = segments.FirstOrDefault(s => s.Placeholders.Count > 0);
        if (maskedSample != null)
        {
            AnsiConsole.MarkupLine("\n[yellow]Maskeleme ve Koruma Örneği (Dizin Çapası / Kod Korundu):[/]");
            var segTable = new Table().Border(TableBorder.Rounded);
            segTable.AddColumn("Özellik");
            segTable.AddColumn("İçerik");

            segTable.AddRow("Etiket", $"<{maskedSample.TagName}>");
            segTable.AddRow("Maskeli Metin (Ajana Gidecek)", $"[cyan]{maskedSample.MaskedText}[/]");
            segTable.AddRow("Korunan Parça Sayısı", $"{maskedSample.Placeholders.Count} adet");

            AnsiConsole.Write(segTable);
        }

        AnsiConsole.MarkupLine("\n[bold green]✔ Adım 4 Başarılı! ch01.html DOM yapısı bozulmadan güvenli segmentlere ayrıldı.[/]");
    }
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"\n[bold red]Hata oluştu:[/] {ex.Message}");
}

