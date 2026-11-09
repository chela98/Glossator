using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryListSettings : CommandSettings
{
    [Description("Yalnızca henüz incelenmemiş/onaylanmamış terimleri listeler.")]
    [CommandOption("-u|--unreviewed")]
    public bool UnreviewedOnly { get; set; }

    [Description("Belirli bir politikaya göre filtreler (ProgressiveGloss, AlwaysEnglish, AlwaysTurkish).")]
    [CommandOption("-p|--policy <POLICY>")]
    public string? Policy { get; set; }

    [Description("Terimlerde arama yapar.")]
    [CommandOption("-s|--search <QUERY>")]
    public string? Search { get; set; }

    [Description("Sayfa başına gösterilecek terim sayısı (varsayılan: 50).")]
    [CommandOption("-l|--limit <COUNT>")]
    [DefaultValue(50)]
    public int Limit { get; set; } = 50;

    [Description("Sayfa numarası (1'den başlar).")]
    [CommandOption("--page <PAGE>")]
    [DefaultValue(1)]
    public int Page { get; set; } = 1;
}

public sealed class GlossaryListCommand(SqliteGlossaryRepository repository) : AsyncCommand<GlossaryListSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryListSettings settings, CancellationToken cancellationToken)
    {
        var allTerms = await repository.GetAllTermsAsync(cancellationToken);

        if (settings.UnreviewedOnly)
        {
            allTerms = allTerms.Where(t => !t.IsReviewed).ToList();
        }

        if (!string.IsNullOrWhiteSpace(settings.Policy))
        {
            if (Enum.TryParse<GlossingPolicy>(settings.Policy, ignoreCase: true, out var parsedPolicy))
            {
                allTerms = allTerms.Where(t => t.Policy == parsedPolicy).ToList();
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]Uyarı:[/] Geçersiz politika filtresi: '{settings.Policy}'. Geçerli değerler: ProgressiveGloss, AlwaysEnglish, AlwaysTurkish");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Search))
        {
            var q = settings.Search.Trim();
            allTerms = allTerms.Where(t =>
                t.EnglishTerm.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (t.TurkishTerm != null && t.TurkishTerm.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (t.EnglishDefinition != null && t.EnglishDefinition.Contains(q, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        var totalCount = allTerms.Count;
        if (totalCount == 0)
        {
            AnsiConsole.MarkupLine("[bold yellow]Kriterlere uyan hiçbir sözlük terimi bulunamadı.[/]");
            return 0;
        }

        var pageSize = Math.Max(1, settings.Limit);
        var pageNumber = Math.Max(1, settings.Page);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var pagedTerms = allTerms.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

        var table = new Table().Border(TableBorder.Rounded);
        table.Title($"[bold cyan]Glossator Sözlüğü (Toplam: {totalCount} terim | Sayfa: {pageNumber}/{totalPages})[/]");
        table.AddColumn(new TableColumn("[bold dim]ID[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]İngilizce Terim[/]"));
        table.AddColumn(new TableColumn("[bold]Türkçe Karşılık[/]"));
        table.AddColumn(new TableColumn("[bold]Politika[/]"));
        table.AddColumn(new TableColumn("[bold]Onay[/]").Centered());
        table.AddColumn(new TableColumn("[bold dim]Kaynak[/]"));

        foreach (var term in pagedTerms)
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

        var reviewedCount = allTerms.Count(t => t.IsReviewed);
        var unreviewedCount = allTerms.Count(t => !t.IsReviewed);
        AnsiConsole.MarkupLine($"[dim]İncelenmiş:[/] [green]{reviewedCount}[/] | [dim]Onay Bekleyen:[/] [yellow]{unreviewedCount}[/] | [dim]Filtre:[/] {(settings.UnreviewedOnly ? "[yellow]Yalnızca Onaysızlar[/]" : "Tümü")}");

        return 0;
    }
}
