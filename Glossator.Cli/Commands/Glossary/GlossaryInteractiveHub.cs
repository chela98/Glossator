using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Serialization;
using Spectre.Console;

namespace Glossator.Cli.Commands.Glossary;

/// <summary>
/// Harici bir SQLite Browser gereksinimini tamamen ortadan kaldıran,
/// interaktif terminal tabanlı sözlük yönetim merkezi.
/// </summary>
public static class GlossaryInteractiveHub
{
    public static async Task RunAsync(
        SqliteGlossaryRepository repository,
        IGlossarySerializationService serializationService,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]GLOSSATOR SÖZLÜK VE TERİM YÖNETİM MERKEZİ[/]")
                    .PageSize(10)
                    .AddChoices(
                        "1. Tüm Terimleri Listele (Sayfalı Tablo)",
                        "2. Terim Ara (İngilizce / Türkçe)",
                        "3. Yeni Terim Ekle",
                        "4. Terim Düzenle / Politikayı Değiştir",
                        "5. Sözlüğü YAML Dosyasına Aktar (glossary.yaml)",
                        "6. YAML Dosyasından Sözlüğe İçe Aktar",
                        "0. Geri Dön / Devam Et"));

            switch (choice[0])
            {
                case '1':
                    await ShowPagedTermsAsync(repository, cancellationToken);
                    break;
                case '2':
                    await SearchTermsAsync(repository, cancellationToken);
                    break;
                case '3':
                    await AddTermAsync(repository, cancellationToken);
                    break;
                case '4':
                    await EditTermAsync(repository, cancellationToken);
                    break;
                case '5':
                    await ExportYamlAsync(repository, serializationService, cancellationToken);
                    break;
                case '6':
                    await ImportYamlAsync(repository, serializationService, cancellationToken);
                    break;
                case '0':
                    return;
            }
        }
    }

    private static async Task ShowPagedTermsAsync(SqliteGlossaryRepository repo, CancellationToken ct)
    {
        var allTerms = await repo.GetAllTermsAsync(ct);
        if (allTerms.Count == 0)
        {
            AnsiConsole.MarkupLine("[bold yellow]Sözlükte henüz terim bulunmuyor.[/]");
            return;
        }

        const int pageSize = 15;
        var currentPage = 1;
        var totalPages = (int)Math.Ceiling(allTerms.Count / (double)pageSize);

        while (!ct.IsCancellationRequested)
        {
            var pagedTerms = allTerms.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();

            var table = new Table().Border(TableBorder.Rounded);
            table.Title($"[bold cyan]Terim Listesi (Toplam: {allTerms.Count} | Sayfa {currentPage}/{totalPages})[/]");
            table.AddColumn(new TableColumn("[bold dim]ID[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]İngilizce Terim[/]"));
            table.AddColumn(new TableColumn("[bold]Türkçe Karşılık[/]"));
            table.AddColumn(new TableColumn("[bold]Politika[/]"));
            table.AddColumn(new TableColumn("[bold]Onay[/]").Centered());

            foreach (var t in pagedTerms)
            {
                var policyColor = t.Policy switch
                {
                    GlossingPolicy.AlwaysEnglish => "magenta",
                    GlossingPolicy.AlwaysTurkish => "green",
                    _ => "cyan"
                };

                table.AddRow(
                    $"[dim]{t.Id}[/]",
                    $"[white]{Markup.Escape(t.EnglishTerm)}[/]",
                    t.TurkishTerm != null ? $"[{policyColor}]{Markup.Escape(t.TurkishTerm)}[/]" : "[dim]-[/]",
                    $"[{policyColor}]{t.Policy}[/]",
                    t.IsReviewed ? "[green]ONAYLI[/]" : "[yellow]TASLAK[/]"
                );
            }

            AnsiConsole.Write(table);

            var options = new List<string>();
            if (currentPage < totalPages) options.Add("Sonraki Sayfa >");
            if (currentPage > 1) options.Add("Önceki Sayfa <");
            options.Add("Sayfa Numarasına Git #");
            options.Add("Menüye Dön (Geri)");

            var navChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[dim]Gezinme Seçenekleri:[/]")
                    .AddChoices(options));

            if (navChoice.StartsWith("Sonraki"))
            {
                currentPage++;
            }
            else if (navChoice.StartsWith("Önceki"))
            {
                currentPage--;
            }
            else if (navChoice.StartsWith("Sayfa Numarasına"))
            {
                currentPage = AnsiConsole.Prompt(
                    new TextPrompt<int>($"Sayfa numarası (1-{totalPages}):")
                        .DefaultValue(currentPage)
                        .Validate(p => p >= 1 && p <= totalPages ? ValidationResult.Success() : ValidationResult.Error("Geçersiz sayfa")));
            }
            else
            {
                break;
            }
        }
    }

    private static async Task SearchTermsAsync(SqliteGlossaryRepository repo, CancellationToken ct)
    {
        var query = AnsiConsole.Ask<string>("[yellow]Aranacak kelimeyi girin (İngilizce/Türkçe):[/] ");
        var results = await repo.SearchTermsAsync(query, ct);

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]'{Markup.Escape(query)}' ile eşleşen hiçbir terim bulunamadı.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.Title($"[bold cyan]Arama Sonuçları: '{Markup.Escape(query)}' ({results.Count} eşleşme)[/]");
        table.AddColumn("ID");
        table.AddColumn("İngilizce Terim");
        table.AddColumn("Türkçe Karşılık");
        table.AddColumn("Politika");
        table.AddColumn("Onay");

        foreach (var t in results)
        {
            var policyColor = t.Policy switch
            {
                GlossingPolicy.AlwaysEnglish => "magenta",
                GlossingPolicy.AlwaysTurkish => "green",
                _ => "cyan"
            };

            table.AddRow(
                $"#{t.Id}",
                $"[bold white]{Markup.Escape(t.EnglishTerm)}[/]",
                t.TurkishTerm != null ? $"[{policyColor}]{Markup.Escape(t.TurkishTerm)}[/]" : "[dim]-[/]",
                $"[{policyColor}]{t.Policy}[/]",
                t.IsReviewed ? "[green]ONAYLI[/]" : "[yellow]TASLAK[/]"
            );
        }

        AnsiConsole.Write(table);
    }

    private static async Task AddTermAsync(SqliteGlossaryRepository repo, CancellationToken ct)
    {
        try
        {
            var en = AnsiConsole.Ask<string>("[green]İngilizce terim:[/] ").Trim();
            var tr = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Türkçe karşılık (İsteğe bağlı, boş bırakılabilir):[/] ")
                    .AllowEmpty()).Trim();

            var policy = AnsiConsole.Prompt(
                new SelectionPrompt<GlossingPolicy>()
                    .Title("[cyan]Terim politikasını seçin:[/]")
                    .AddChoices(
                        GlossingPolicy.ProgressiveGloss,
                        GlossingPolicy.AlwaysTurkish,
                        GlossingPolicy.AlwaysEnglish));

            var entry = new GlossaryEntry
            {
                EnglishTerm = en,
                TurkishTerm = string.IsNullOrWhiteSpace(tr) ? null : tr,
                Policy = policy,
                IsReviewed = true,
                SourceBook = "TUI Manuel"
            };

            await repo.AddOrUpdateTermAsync(entry, ct);
            AnsiConsole.MarkupLine($"[bold green]OK:[/] Terim başarıyla eklendi: [white]{Markup.Escape(en)}[/] -> [green]{Markup.Escape(tr)}[/] ([yellow]{policy}[/])");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Terim ekleme hatası: {Markup.Escape(ex.Message)}");
        }
    }

    private static async Task EditTermAsync(SqliteGlossaryRepository repo, CancellationToken ct)
    {
        try
        {
            var query = AnsiConsole.Ask<string>("[cyan]Düzenlemek istediğiniz terimi arayın:[/] ");
            var matches = await repo.SearchTermsAsync(query, ct);

            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Eşleşen terim bulunamadı.[/]");
                return;
            }

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<GlossaryEntry>()
                    .Title("[green]Düzenlenecek terimi seçin:[/]")
                    .UseConverter(t => $"#{t.Id} {Markup.Escape(t.EnglishTerm)} -> {Markup.Escape(t.TurkishTerm ?? "-")} ({t.Policy})")
                    .AddChoices(matches));

            var newTr = AnsiConsole.Prompt(
                new TextPrompt<string>($"[yellow]Türkçe karşılık (Mevcut: '{selected.TurkishTerm ?? "-"}'):[/] ")
                    .DefaultValue(selected.TurkishTerm ?? "")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(newTr))
            {
                selected.TurkishTerm = newTr.Trim();
            }

            var updatePolicy = AnsiConsole.Confirm($"Politikayı güncelle (Mevcut: {selected.Policy})?", defaultValue: false);
            if (updatePolicy)
            {
                selected.Policy = AnsiConsole.Prompt(
                    new SelectionPrompt<GlossingPolicy>()
                        .Title("[cyan]Yeni politikayı seçin:[/]")
                        .AddChoices(
                            GlossingPolicy.ProgressiveGloss,
                            GlossingPolicy.AlwaysTurkish,
                            GlossingPolicy.AlwaysEnglish));
            }

            selected.IsReviewed = true;
            await repo.AddOrUpdateTermAsync(selected, ct);

            AnsiConsole.MarkupLine($"[bold green]OK:[/] Terim güncellendi: #{selected.Id} [white]{Markup.Escape(selected.EnglishTerm)}[/] -> [green]{Markup.Escape(selected.TurkishTerm ?? "-")}[/] ([yellow]{selected.Policy}[/])");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Terim düzenleme hatası: {Markup.Escape(ex.Message)}");
        }
    }

    private static async Task ExportYamlAsync(SqliteGlossaryRepository repo, IGlossarySerializationService service, CancellationToken ct)
    {
        try
        {
            var rawPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Çıktı YAML dosya veya klasör yolu:[/] [dim](Örn: glossary.yaml veya C:\\Klasor)[/]")
                    .DefaultValue("glossary.yaml"));

            var path = rawPath.Trim('"', '\'');

            // Eğer girilen yol mevcut bir klasörse veya ayracı ile bitiyorsa, dosya adını otomatik tamamla
            if (Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine(path, "glossary.yaml");
            }
            else if (string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                path += ".yaml";
            }

            var terms = await repo.GetAllTermsAsync(ct);
            if (terms.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Sözlükte aktarılacak hiçbir terim bulunmuyor.[/]");
                return;
            }

            await service.ExportAsync(terms, path, SerializationFormat.Yaml, ct);

            var fileInfo = new FileInfo(path);
            var fullPath = Path.GetFullPath(path);
            AnsiConsole.MarkupLine($"\n[bold green]OK:[/] Başarılı: Toplam [cyan]{terms.Count}[/] terim [yellow]{Markup.Escape(fullPath)}[/] dosyasına aktarıldı ({fileInfo.Length / 1024.0:F1} KB).\n");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Dışa aktarma hatası: {Markup.Escape(ex.Message)}\n");
        }
    }

    private static async Task ImportYamlAsync(SqliteGlossaryRepository repo, IGlossarySerializationService service, CancellationToken ct)
    {
        try
        {
            var rawPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]İçe aktarılacak YAML dosya veya klasör yolu:[/] ")
                    .DefaultValue("glossary.yaml"));

            var path = rawPath.Trim('"', '\'');

            if (Directory.Exists(path))
            {
                var candidateYaml = Path.Combine(path, "glossary.yaml");
                var candidateJson = Path.Combine(path, "glossary.json");
                if (File.Exists(candidateYaml))
                {
                    path = candidateYaml;
                }
                else if (File.Exists(candidateJson))
                {
                    path = candidateJson;
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Belirtilen klasörde 'glossary.yaml' veya 'glossary.json' bulunamadı: [dim]{Markup.Escape(path)}[/]\n");
                    return;
                }
            }

            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] Dosya bulunamadı: [dim]{Markup.Escape(path)}[/]\n");
                return;
            }

            var terms = await service.ImportAsync(path, ct);
            if (terms.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Dosyada aktarılacak geçerli terim bulunamadı.[/]");
                return;
            }

            foreach (var t in terms)
            {
                t.IsReviewed = true;
                await repo.AddOrUpdateTermAsync(t, ct);
            }

            AnsiConsole.MarkupLine($"\n[bold green]OK:[/] Başarılı: Toplam [cyan]{terms.Count}[/] terim [yellow]{Markup.Escape(Path.GetFullPath(path))}[/] dosyasından SQLite veritabanına aktarıldı.\n");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]HATA:[/] İçe aktarma hatası: {Markup.Escape(ex.Message)}\n");
        }
    }
}
