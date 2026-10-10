namespace Glossator.Core.Serialization;

using Glossator.Core.Models.Glossary;
using YamlDotNet.Serialization;

public sealed class GlossaryItemModel
{
    [YamlMember(Alias = "english")]
    public required string English { get; set; }

    [YamlMember(Alias = "turkish")]
    public string? Turkish { get; set; }

    [YamlMember(Alias = "policy")]
    public GlossingPolicy Policy { get; set; } = GlossingPolicy.ProgressiveGloss;

    [YamlMember(Alias = "definition")]
    public string? Definition { get; set; }

    [YamlMember(Alias = "reviewed")]
    public bool Reviewed { get; set; } = true;

    [YamlMember(Alias = "source")]
    public string? Source { get; set; }
}

public sealed class GlossaryFileModel
{
    [YamlMember(Alias = "version")]
    public string Version { get; set; } = "1.0";

    [YamlMember(Alias = "last_updated")]
    public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");

    [YamlMember(Alias = "terms")]
    public List<GlossaryItemModel> Terms { get; set; } = [];
}
