namespace Glossator.AI.Configuration;

public class AiProviderOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public class AgentRoleOptions
{
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? ReasoningEffort { get; set; }
}

public class GlossatorAiOptions
{
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AgentRoleOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

