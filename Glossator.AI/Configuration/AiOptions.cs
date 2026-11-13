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

    public static bool IsLocalEndpoint(string? endpoint)
        => !string.IsNullOrWhiteSpace(endpoint)
           && (endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
               || endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase));

    public (string ProviderName, AiProviderOptions Provider, string ModelId) ResolveAgent(string agentRoleKey)
    {
        Agents.TryGetValue(agentRoleKey, out var agentConfig);

        // 1. Sağlayıcı (Provider) Belirleme:
        string selectedProviderName = string.Empty;
        AiProviderOptions? selectedProvider = null;

        // Ajanın kendi Provider'ı varsa ve ActiveProvider'dan farklıysa VE kullanılabilir durumdaysa (gerçek key veya localhost):
        if (agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.Provider)
            && !agentConfig.Provider.Equals(ActiveProvider, StringComparison.OrdinalIgnoreCase)
            && Providers.TryGetValue(agentConfig.Provider, out var specificProv)
            && (IsLocalEndpoint(specificProv.Endpoint) || (!string.IsNullOrWhiteSpace(specificProv.ApiKey) && !specificProv.ApiKey.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))))
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
        // (Yalnızca OpenAI/Gemini resmi sağlayıcılarında varsayılan Evren model sızıntılarını engelle; yerel/özel sağlayıcılarda deepseek/gemma serbesttir)
        bool isStrictCloudProvider = selectedProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                                     || selectedProviderName.Equals("Gemini", StringComparison.OrdinalIgnoreCase);
        bool looksLikeEvrenSpecificModel = agentConfig != null &&
            (agentConfig.ModelId.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)
             || agentConfig.ModelId.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
             || agentConfig.ModelId.StartsWith("mimo", StringComparison.OrdinalIgnoreCase));

        if (sameProvider && agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.ModelId)
            && !(isStrictCloudProvider && looksLikeEvrenSpecificModel))
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

    /// <summary>
    /// Aşama (stage) veya ajan adına göre sağlayıcıyı, modeli ve 1M token başına USD maliyetini çözümler.
    /// Yalnızca Gemini modelleri ücretlendirilir; Evren API ve diğer sağlayıcılar ücretsiz ($0.00) kabul edilir.
    /// </summary>
    public (string ProviderName, string ModelId, double InputRatePerMillionUsd, double OutputRatePerMillionUsd, bool IsFree) ResolveStagePricing(string stageName)
    {
        var agentKey = stageName switch
        {
            var s when s.StartsWith("Style", StringComparison.OrdinalIgnoreCase) => "StyleAnalyst",
            var s when s.StartsWith("Review", StringComparison.OrdinalIgnoreCase) => "Reviewer",
            _ => "Translator"
        };

        var (providerName, provider, modelId) = ResolveAgent(agentKey);

        var isGemini = providerName.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
                       || (provider?.Endpoint?.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase) ?? false)
                       || modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

        if (!isGemini)
        {
            return (providerName, modelId, 0.0, 0.0, true);
        }

        var (inRate, outRate) = GetGeminiModelRatesPerMillionUsd(modelId);
        return (providerName, modelId, inRate, outRate, false);
    }

    /// <summary>
    /// Google Gemini Developer API resmi model tarifesi ($ / 1M Token - Girdi ve Çıktı/Reasoning dahil).
    /// </summary>
    public static (double InputUsd, double OutputUsd) GetGeminiModelRatesPerMillionUsd(string modelId)
    {
        var m = (modelId ?? string.Empty).Trim().ToLowerInvariant();

        // 1. Flash-Lite Ailesi (En ekonomik katman)
        if (m.Contains("flash-lite"))
        {
            if (m.Contains("2.0") || m.Contains("1.5"))
                return (0.075, 0.30);
            if (m.Contains("2.5"))
                return (0.10, 0.40);
            // Gemini 3.x Flash-Lite (örn: gemini-3.1-flash-lite, gemini-3.5-flash-lite)
            return (0.25, 1.50);
        }

        // 2. Pro Ailesi (Derin muhakeme katmanı)
        if (m.Contains("pro"))
        {
            if (m.Contains("3.") || m.Contains("3-"))
                return (2.00, 12.00);
            // Gemini 1.5 Pro / 2.5 Pro
            return (1.25, 10.00);
        }

        // 3. Standart Flash Ailesi (Hız/Kalite dengesi)
        if (m.Contains("1.5"))
            return (0.075, 0.30);
        if (m.Contains("2.0"))
            return (0.10, 0.40);
        if (m.Contains("2.5"))
            return (0.30, 2.50);

        // Gemini 3.x Flash (örn: gemini-3-flash, gemini-3.5-flash, gemini-3.8-flash)
        return (0.50, 3.00);
    }
}
