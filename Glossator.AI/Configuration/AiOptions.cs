namespace Glossator.AI.Configuration;

public class AiProviderOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
}

public class AgentRoleOptions
{
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? ReasoningEffort { get; set; }
}

public class GlossatorAiOptions
{
    public string ActiveProvider { get; set; } = "Evren";
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AgentRoleOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public (string ProviderName, AiProviderOptions Provider, string ModelId) ResolveAgent(string agentRoleKey)
    {
        Agents.TryGetValue(agentRoleKey, out var agentConfig);

        // 1. Sağlayıcı (Provider) Belirleme:
        string selectedProviderName = string.Empty;
        AiProviderOptions? selectedProvider = null;

        // Ajanın kendi Provider'ı varsa ve ActiveProvider'dan farklıysa VE geçerli bir anahtara sahipse:
        if (agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.Provider)
            && !agentConfig.Provider.Equals(ActiveProvider, StringComparison.OrdinalIgnoreCase)
            && Providers.TryGetValue(agentConfig.Provider, out var specificProv)
            && !string.IsNullOrWhiteSpace(specificProv.ApiKey) && !specificProv.ApiKey.Contains("YOUR_"))
        {
            selectedProviderName = agentConfig.Provider;
            selectedProvider = specificProv;
        }
        // ActiveProvider tanımlıysa ve Providers içinde varsa:
        else if (!string.IsNullOrWhiteSpace(ActiveProvider) && Providers.TryGetValue(ActiveProvider, out var activeProv))
        {
            selectedProviderName = ActiveProvider;
            selectedProvider = activeProv;
        }
        // Ajanın Provider'ı tanımlıysa:
        else if (agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.Provider) && Providers.TryGetValue(agentConfig.Provider, out var fallbackProv))
        {
            selectedProviderName = agentConfig.Provider;
            selectedProvider = fallbackProv;
        }
        // Providers listesindeki ilk sağlayıcı:
        else if (Providers.Count > 0)
        {
            var first = Providers.First();
            selectedProviderName = first.Key;
            selectedProvider = first.Value;
        }
        else
        {
            selectedProviderName = "Evren";
            selectedProvider = new AiProviderOptions();
        }

        // 2. ModelId Belirleme:
        string resolvedModel;
        bool sameProvider = agentConfig != null && (
            string.IsNullOrWhiteSpace(agentConfig.Provider) 
            || agentConfig.Provider.Equals(selectedProviderName, StringComparison.OrdinalIgnoreCase));

        // Eğer aynı sağlayıcıysa ve agent'ın ModelId'si varsa:
        if (sameProvider && agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.ModelId)
            && !(selectedProviderName != "Evren" && (agentConfig.ModelId.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) || agentConfig.ModelId.StartsWith("gemma", StringComparison.OrdinalIgnoreCase) || agentConfig.ModelId.StartsWith("mimo", StringComparison.OrdinalIgnoreCase))))
        {
            resolvedModel = agentConfig.ModelId;
        }
        else if (!string.IsNullOrWhiteSpace(selectedProvider.DefaultModel))
        {
            resolvedModel = selectedProvider.DefaultModel;
        }
        else
        {
            resolvedModel = agentConfig?.ModelId ?? "gpt-4o";
        }

        return (selectedProviderName, selectedProvider, resolvedModel);
    }
}
