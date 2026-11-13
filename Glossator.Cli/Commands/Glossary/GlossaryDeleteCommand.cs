using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryDeleteSettings : CommandSettings
{
    [Description("Silinecek terimin ID'si veya İngilizce adı.")]
    [CommandArgument(0, "[TERM_OR_ID]")]
    public string? TermOrId { get; set; }

    [Description("Silinecek terimin ID'si.")]
    [CommandOption("-i|--id <ID>")]
    public int? Id { get; set; }

    [Description("Silinecek İngilizce terim.")]
    [CommandOption("-e|--en <TERM>")]
    public string? EnglishTerm { get; set; }
}

public sealed class GlossaryDeleteCommand(SqliteGlossaryRepository repository) : AsyncCommand<GlossaryDeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryDeleteSettings settings, CancellationToken cancellationToken)
    {
        GlossaryEntry? target = null;

        if (settings.Id.HasValue)
        {
            target = await repository.GetTermByIdAsync(settings.Id.Value, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(settings.EnglishTerm))
        {
            target = await repository.GetTermByEnglishAsync(settings.EnglishTerm.Trim(), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(settings.TermOrId))
        {
            var arg = settings.TermOrId.Trim();
            if (int.TryParse(arg, out var parsedId))
            {
                target = await repository.GetTermByIdAsync(parsedId, cancellationToken);
            }
            target ??= await repository.GetTermByEnglishAsync(arg, cancellationToken);
        }
        else if (!Console.IsInputRedirected)
        {
            var query = AnsiConsole.Ask<string>("[cyan]Silmek istediğiniz terimi arayın (İngilizce/Türkçe/ID):[/] ");
            if (int.TryParse(query.Trim(), out var qId))
            {
                target = await repository.GetTermByIdAsync(qId, cancellationToken);
            }

            if (target == null)
            {
                var matches = await repository.SearchTermsAsync(query, cancellationToken);
                if (matches.Count == 0)
                {
                    AnsiConsole.MarkupLine("[bold yellow]Eşleşen terim bulunamadı.[/]");
                    return 1;
                }

                target = AnsiConsole.Prompt(
                    new SelectionPrompt<GlossaryEntry>()
                        .Title("[red]Silmek istediğiniz terimi seçin:[/]")
                        .UseConverter(t => $"#{t.Id} {t.EnglishTerm} -> {t.TurkishTerm ?? "-"} ({t.Policy})")
                        .AddChoices(matches));
            }
        }

        if (target == null)
        {
            AnsiConsole.MarkupLine("[bold red]HATA:[/] Silinecek terim veritabanında bulunamadı.");
            return 1;
        }

        var deleted = await repository.DeleteTermAsync(target.Id, cancellationToken);
        if (!deleted)
        {
            AnsiConsole.MarkupLine("[bold red]HATA:[/] Terim silinemedi.");
            return 1;
        }

        var panel = new Panel(
            new Markup(
                $"[bold white]Silinen ID:[/] #{target.Id}\n" +
                $"[bold white]İngilizce:[/] [cyan]{Markup.Escape(target.EnglishTerm)}[/]\n" +
                $"[bold white]Türkçe:[/] [green]{Markup.Escape(target.TurkishTerm ?? "-")}[/]\n" +
                $"[bold white]Politika:[/] [yellow]{target.Policy}[/]"
            ))
            .Header("[bold green]OK: Terim SQLite Veritabanından Silindi[/]")
            .BorderColor(Color.Green);

        AnsiConsole.Write(panel);
        return 0;
    }
}
