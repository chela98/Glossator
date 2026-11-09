using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryAddSettings : CommandSettings
{
    [Description("İngilizce terim (Örn: 'prompt engineering').")]
    [CommandOption("-e|--en <TERM>")]
    public string? EnglishTerm { get; set; }

    [Description("Türkçe çeviri karşılığı (Örn: 'istek mühendisliği').")]
    [CommandOption("-t|--tr <TERM>")]
    public string? TurkishTerm { get; set; }

    [Description("Terim politikası: ProgressiveGloss, AlwaysEnglish, AlwaysTurkish (Varsayılan: ProgressiveGloss).")]
    [CommandOption("-p|--policy <POLICY>")]
    public string? Policy { get; set; }

    [Description("Terim açıklaması / tanımı.")]
    [CommandOption("-d|--def <DEFINITION>")]
    public string? Definition { get; set; }

    [Description("Terimi doğrudan onaylı olarak kaydeder (Varsayılan: true).")]
    [CommandOption("-r|--reviewed")]
    public bool? Reviewed { get; set; }

    [Description("Terimin kaynak kitabı veya kökeni.")]
    [CommandOption("-s|--source <SOURCE>")]
    public string? Source { get; set; }
}

public sealed class GlossaryAddCommand(SqliteGlossaryRepository repository) : AsyncCommand<GlossaryAddSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryAddSettings settings, CancellationToken cancellationToken)
    {
        var englishTerm = settings.EnglishTerm;
        if (string.IsNullOrWhiteSpace(englishTerm))
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[bold red]HATA:[/] '--en' argümanı zorunludur.");
                return 1;
            }

            englishTerm = AnsiConsole.Ask<string>("[green]İngilizce terimi girin:[/] ");
        }

        var turkishTerm = settings.TurkishTerm;
        if (string.IsNullOrWhiteSpace(turkishTerm) && !Console.IsInputRedirected && settings.Policy != nameof(GlossingPolicy.AlwaysEnglish))
        {
            turkishTerm = AnsiConsole.Prompt(
                new TextPrompt<string>("[yellow]Türkçe karşılığı girin (İsteğe bağlı, boş bırakabilirsiniz):[/] ")
                    .AllowEmpty());
            if (string.IsNullOrWhiteSpace(turkishTerm)) turkishTerm = null;
        }

        var policy = GlossingPolicy.ProgressiveGloss;
        if (!string.IsNullOrWhiteSpace(settings.Policy))
        {
            if (!Enum.TryParse(settings.Policy, ignoreCase: true, out policy))
            {
                AnsiConsole.MarkupLine($"[bold yellow]Uyarı:[/] '{settings.Policy}' geçersiz politika, varsayılan 'ProgressiveGloss' kullanılacak.");
                policy = GlossingPolicy.ProgressiveGloss;
            }
        }
        else if (!Console.IsInputRedirected && string.IsNullOrWhiteSpace(settings.Policy))
        {
            policy = AnsiConsole.Prompt(
                new SelectionPrompt<GlossingPolicy>()
                    .Title("[cyan]Terim politikasını seçin:[/]")
                    .AddChoices(
                        GlossingPolicy.ProgressiveGloss,
                        GlossingPolicy.AlwaysTurkish,
                        GlossingPolicy.AlwaysEnglish));
        }

        var entry = new GlossaryEntry
        {
            EnglishTerm = englishTerm.Trim(),
            TurkishTerm = string.IsNullOrWhiteSpace(turkishTerm) ? null : turkishTerm.Trim(),
            Policy = policy,
            EnglishDefinition = string.IsNullOrWhiteSpace(settings.Definition) ? null : settings.Definition.Trim(),
            IsReviewed = settings.Reviewed ?? true,
            SourceBook = string.IsNullOrWhiteSpace(settings.Source) ? "CLI Manuel" : settings.Source.Trim()
        };

        await repository.AddOrUpdateTermAsync(entry, cancellationToken);

        var panel = new Panel(
            new Markup(
                $"[bold white]İngilizce:[/] [cyan]{Markup.Escape(entry.EnglishTerm)}[/]\n" +
                $"[bold white]Türkçe:[/] [green]{Markup.Escape(entry.TurkishTerm ?? "-")}[/]\n" +
                $"[bold white]Politika:[/] [yellow]{entry.Policy}[/]\n" +
                $"[bold white]Onaylı:[/] {(entry.IsReviewed ? "[green]Evet[/]" : "[yellow]Hayır[/]")}\n" +
                $"[bold white]Kaynak:[/] [dim]{Markup.Escape(entry.SourceBook)}[/]"
            ))
            .Header("[bold green]OK: Terim Sözlüğe Başarıyla Eklendi / Güncellendi[/]")
            .BorderColor(Color.Green);

        AnsiConsole.Write(panel);
        return 0;
    }
}
