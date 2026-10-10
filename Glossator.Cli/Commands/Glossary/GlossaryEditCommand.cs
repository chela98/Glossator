using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryEditSettings : CommandSettings
{
    [Description("Düzenlenecek terimin ID'si.")]
    [CommandOption("-i|--id <ID>")]
    public int? Id { get; set; }

    [Description("Düzenlenecek İngilizce terim.")]
    [CommandOption("-e|--en <TERM>")]
    public string? EnglishTerm { get; set; }

    [Description("Yeni Türkçe çeviri karşılığı.")]
    [CommandOption("-t|--tr <TERM>")]
    public string? TurkishTerm { get; set; }

    [Description("Yeni politika: ProgressiveGloss, AlwaysEnglish, AlwaysTurkish.")]
    [CommandOption("-p|--policy <POLICY>")]
    public string? Policy { get; set; }

    [Description("Onay durumunu ayarlar.")]
    [CommandOption("-r|--reviewed <BOOL>")]
    public bool? Reviewed { get; set; }
}

public sealed class GlossaryEditCommand(SqliteGlossaryRepository repository) : AsyncCommand<GlossaryEditSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryEditSettings settings, CancellationToken cancellationToken)
    {
        GlossaryEntry? existing = null;

        if (settings.Id.HasValue)
        {
            existing = await repository.GetTermByIdAsync(settings.Id.Value, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(settings.EnglishTerm))
        {
            existing = await repository.GetTermByEnglishAsync(settings.EnglishTerm, cancellationToken);
        }
        else if (!Console.IsInputRedirected)
        {
            var search = AnsiConsole.Ask<string>("[cyan]Düzenlemek istediğiniz terimi arayın (İngilizce/Türkçe):[/] ");
            var matches = await repository.SearchTermsAsync(search, cancellationToken);
            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Eşleşen terim bulunamadı.[/]");
                return 1;
            }

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<GlossaryEntry>()
                    .Title("[green]Düzenlemek istediğiniz terimi seçin:[/]")
                    .UseConverter(t => $"#{t.Id} {t.EnglishTerm} -> {t.TurkishTerm ?? "-"} ({t.Policy})")
                    .AddChoices(matches));

            existing = selection;
        }

        if (existing == null)
        {
            AnsiConsole.MarkupLine("[bold red]HATA:[/] Belirtilen terim veritabanında bulunamadı.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]Mevcut Terim:[/] #{existing.Id} [bold white]{Markup.Escape(existing.EnglishTerm)}[/] -> [green]{Markup.Escape(existing.TurkishTerm ?? "-")}[/] ([yellow]{existing.Policy}[/])");

        if (settings.TurkishTerm != null)
        {
            existing.TurkishTerm = settings.TurkishTerm.Trim();
        }
        else if (!Console.IsInputRedirected)
        {
            var newTr = AnsiConsole.Prompt(
                new TextPrompt<string>($"[yellow]Yeni Türkçe karşılık (Değiştirmemek için Enter):[/] ")
                    .DefaultValue(existing.TurkishTerm ?? "")
                    .AllowEmpty());
            if (!string.IsNullOrWhiteSpace(newTr))
            {
                existing.TurkishTerm = newTr.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Policy))
        {
            if (Enum.TryParse<GlossingPolicy>(settings.Policy, ignoreCase: true, out var p))
            {
                existing.Policy = p;
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]⚠ '{settings.Policy}' geçersiz politika; politika değiştirilmedi.[/]");
            }
        }
        else if (!Console.IsInputRedirected)
        {
            var updatePolicy = AnsiConsole.Confirm("[cyan]Politikayı güncellemek istiyor musunuz?[/]", defaultValue: false);
            if (updatePolicy)
            {
                existing.Policy = AnsiConsole.Prompt(
                    new SelectionPrompt<GlossingPolicy>()
                        .Title("[cyan]Yeni politikayı seçin:[/]")
                        .AddChoices(
                            GlossingPolicy.ProgressiveGloss,
                            GlossingPolicy.AlwaysTurkish,
                            GlossingPolicy.AlwaysEnglish));
            }
        }

        if (settings.Reviewed.HasValue)
        {
            existing.IsReviewed = settings.Reviewed.Value;
        }
        else
        {
            existing.IsReviewed = true;
        }

        await repository.AddOrUpdateTermAsync(existing, cancellationToken);

        var panel = new Panel(
            new Markup(
                $"[bold white]ID:[/] #{existing.Id}\n" +
                $"[bold white]İngilizce:[/] [cyan]{Markup.Escape(existing.EnglishTerm)}[/]\n" +
                $"[bold white]Türkçe:[/] [green]{Markup.Escape(existing.TurkishTerm ?? "-")}[/]\n" +
                $"[bold white]Politika:[/] [yellow]{existing.Policy}[/]\n" +
                $"[bold white]Onaylı:[/] {(existing.IsReviewed ? "[green]✔ Evet[/]" : "[yellow]⚠ Hayır[/]")}"
            ))
            .Header("[bold green]✔ Terim Başarıyla Güncellendi[/]")
            .BorderColor(Color.Green);

        AnsiConsole.Write(panel);
        return 0;
    }
}
