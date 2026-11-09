using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossarySearchSettings : CommandSettings
{
    [Description("Aranacak metin (İngilizce, Türkçe veya tanım).")]
    [CommandArgument(0, "<QUERY>")]
    public string Query { get; set; } = string.Empty;
}

public sealed class GlossarySearchCommand(SqliteGlossaryRepository repository) : AsyncCommand<GlossarySearchSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossarySearchSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Query))
        {
            AnsiConsole.MarkupLine("[bold red]HATA:[/] Lütfen aranacak bir kelime girin.");
            return 1;
        }

        var results = await repository.SearchTermsAsync(settings.Query, cancellationToken);
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]'{Markup.Escape(settings.Query)}' ile eşleşen hiçbir terim bulunamadı.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.Title($"[bold cyan]Arama Sonuçları: '{Markup.Escape(settings.Query)}' ({results.Count} Eşleşme)[/]");
        table.AddColumn(new TableColumn("[bold dim]ID[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]İngilizce Terim[/]"));
        table.AddColumn(new TableColumn("[bold]Türkçe Karşılık[/]"));
        table.AddColumn(new TableColumn("[bold]Politika[/]"));
        table.AddColumn(new TableColumn("[bold]Onay[/]").Centered());
        table.AddColumn(new TableColumn("[bold dim]Kaynak[/]"));

        foreach (var term in results)
        {
            var policyColor = term.Policy switch
            {
                GlossingPolicy.AlwaysEnglish => "magenta",
                GlossingPolicy.AlwaysTurkish => "green",
                _ => "cyan"
            };

            var reviewedBadge = term.IsReviewed
                ? "[bold green]ONAYLI[/]"
                : "[bold yellow]TASLAK[/]";

            table.AddRow(
                $"[dim]{term.Id}[/]",
                $"[bold white]{Markup.Escape(term.EnglishTerm)}[/]",
                term.TurkishTerm != null ? $"[{policyColor}]{Markup.Escape(term.TurkishTerm)}[/]" : "[dim]-[/]",
                $"[{policyColor}]{term.Policy}[/]",
                reviewedBadge,
                $"[dim]{Markup.Escape(term.SourceBook ?? "Varsayılan")}[/]"
            );
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
