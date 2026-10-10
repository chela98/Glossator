using System.ComponentModel;
using Glossator.Core.Glossary;
using Glossator.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Glossator.Cli.Commands.Glossary;

public sealed class GlossaryExportSettings : CommandSettings
{
    [Description("Dışa aktarılacak dosya yolu (Varsayılan: 'glossary.yaml').")]
    [CommandArgument(0, "[FILEPATH]")]
    public string? FilePath { get; set; }

    [Description("Format: yaml veya json (Belirtilmezse dosya uzantısından çıkarılır).")]
    [CommandOption("-f|--format <FORMAT>")]
    public string? Format { get; set; }
}

public sealed class GlossaryExportCommand(SqliteGlossaryRepository repository, IGlossarySerializationService serializationService)
    : AsyncCommand<GlossaryExportSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlossaryExportSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var targetPath = (settings.FilePath ?? "glossary.yaml").Trim('"', '\'');
            var ext = Path.GetExtension(targetPath).ToLowerInvariant();

            var format = SerializationFormat.Yaml;
            if (!string.IsNullOrWhiteSpace(settings.Format))
            {
                format = settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase)
                    ? SerializationFormat.Json
                    : SerializationFormat.Yaml;
            }
            else if (ext == ".json")
            {
                format = SerializationFormat.Json;
            }

            if (Directory.Exists(targetPath) || targetPath.EndsWith(Path.DirectorySeparatorChar) || targetPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                targetPath = Path.Combine(targetPath, format == SerializationFormat.Json ? "glossary.json" : "glossary.yaml");
            }
            else if (string.IsNullOrEmpty(Path.GetExtension(targetPath)))
            {
                targetPath += format == SerializationFormat.Json ? ".json" : ".yaml";
            }

            var terms = await repository.GetAllTermsAsync(cancellationToken);
            if (terms.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold yellow]Sözlükte dışa aktarılacak hiçbir terim bulunamadı.[/]");
                return 0;
            }

            await serializationService.ExportAsync(terms, targetPath, format, cancellationToken);

            var fileInfo = new FileInfo(targetPath);
            var panel = new Panel(
                new Markup(
                    $"[bold white]Dosya:[/] [cyan]{Markup.Escape(Path.GetFullPath(targetPath))}[/]\n" +
                    $"[bold white]Format:[/] [yellow]{format}[/]\n" +
                    $"[bold white]Aktarılan Terim Sayısı:[/] [green]{terms.Count}[/]\n" +
                    $"[bold white]Dosya Boyutu:[/] [green]{fileInfo.Length / 1024.0:F1} KB[/]"
                ))
                .Header("[bold green]✔ Git-Dostu Sözlük Dosyası Başarıyla Dışa Aktarıldı[/]")
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
