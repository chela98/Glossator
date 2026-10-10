using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryImportSettings : CommandSettings
{
    [Description("İçe aktarılacak YAML veya JSON dosya yolu.")]
    [CommandArgument(0, "<FILEPATH>")]
    public string FilePath { get; set; } = string.Empty;

    [Description("Kaynak kitap veya kaynak adı (Varsayılan: dosya adı).")]
    [CommandOption("-s|--source <SOURCE>")]
    public string? Source { get; set; }

    [Description("İçe aktarılan terimleri onaylanmış olarak işaretler (Varsayılan: true).")]
    [CommandOption("-r|--reviewed")]
    public bool? MarkAsReviewed { get; set; }
}

public sealed class GlossaryImportCommand(SqliteGlossaryRepository repository, IGlossarySerializationService serializationService)
    : AsyncCommand<GlossaryImportSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryImportSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var filePath = (settings.FilePath ?? string.Empty).Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(filePath))
            {
                AnsiConsole.MarkupLine("[bold red]HATA:[/] İçe aktarılacak dosya yolu belirtilmedi.");
                return 1;
            }

            if (Directory.Exists(filePath))
            {
                var yamlCandidate = Path.Combine(filePath, "glossary.yaml");
                var jsonCandidate = Path.Combine(filePath, "glossary.json");
                if (File.Exists(yamlCandidate))
                {
                    filePath = yamlCandidate;
                }
                else if (File.Exists(jsonCandidate))
                {
                    filePath = jsonCandidate;
                }
            }

            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[bold red]HATA:[/] Belirtilen dosya bulunamadı: [dim]{Markup.Escape(filePath)}[/]");
                return 1;
            }

            var source = string.IsNullOrWhiteSpace(settings.Source)
                ? Path.GetFileNameWithoutExtension(filePath)
                : settings.Source.Trim();

            var terms = await serializationService.ImportAsync(filePath, cancellationToken);
            if (terms.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Dosyada içe aktarılacak terim bulunamadı.[/]");
                return 0;
            }

            var markReviewed = settings.MarkAsReviewed ?? true;
            var importedCount = 0;

            foreach (var term in terms)
            {
                term.SourceBook = source;
                if (markReviewed)
                {
                    term.IsReviewed = true;
                }
                await repository.AddOrUpdateTermAsync(term, cancellationToken);
                importedCount++;
            }

            var panel = new Panel(
                new Markup(
                    $"[bold white]Kaynak Dosya:[/] [cyan]{Markup.Escape(Path.GetFullPath(filePath))}[/]\n" +
                    $"[bold white]İçe Aktarılan Terim Sayısı:[/] [green]{importedCount}[/]\n" +
                    $"[bold white]Onay Durumu:[/] {(markReviewed ? "[green]✔ Onaylı (Reviewed)[/]" : "[yellow]⚠ Onaysız (Unreviewed)[/]")}\n" +
                    $"[bold white]Atanan Kaynak:[/] [yellow]{Markup.Escape(source)}[/]"
                ))
                .Header("[bold green]✔ Terimler Başarıyla SQLite Veritabanına İçe Aktarıldı[/]")
                .BorderColor(Color.Green);

            AnsiConsole.Write(panel);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]HATA:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
