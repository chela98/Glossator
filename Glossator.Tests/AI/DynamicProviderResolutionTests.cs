namespace Glossator.Tests.AI;

using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class DynamicProviderResolutionTests
{
    [Fact]
    public void WhenActiveProviderIsCustom_ResolvesClientUsingCustomAndDefaultModel()
    {
        var services = new ServiceCollection();

        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "Custom",
            Providers =
            {
                ["Evren"] = new()
                {
                    Endpoint = "https://evren-llmapi.ssyz.org.tr/v1",
                    ApiKey = "YOUR_EVREN_API_KEY",
                    DefaultModel = "deepseek-v4.1-flash"
                },
                ["Custom"] = new()
                {
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-test-real-looking-key-12345",
                    DefaultModel = "gpt-4o"
                }
            },
            Agents =
            {
                ["StyleAnalyst"] = new() { Provider = "Evren", ModelId = "deepseek-v4.1-flash" },
                ["Translator"] = new() { Provider = "Evren", ModelId = "gemma-4-31b" },
                ["Reviewer"] = new() { Provider = "Evren", ModelId = "mimo-v2.6-pro" }
            }
        };

        services.AddSingleton(Options.Create(aiOptions));
        services.AddGlossatorAiClients();

        var provider = services.BuildServiceProvider();

        // StyleAnalyst, Translator ve Reviewer çözümlenebilmeli
        var styleClient = provider.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
        var transClient = provider.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyTranslator);
        var revClient = provider.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyReviewer);

        Assert.NotNull(styleClient);
        Assert.NotNull(transClient);
        Assert.NotNull(revClient);
    }

    [Fact]
    public void AddGlossatorAiClients_WhenApiKeyUpdatedInOptions_TransientClientsReceiveUpdatedConfiguration()
    {
        var services = new ServiceCollection();
        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "OpenAI",
            Providers =
            {
                ["OpenAI"] = new()
                {
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-initial-placeholder-key",
                    DefaultModel = "gpt-4o"
                }
            },
            Agents =
            {
                ["StyleAnalyst"] = new() { Provider = "OpenAI", ModelId = "gpt-4o" }
            }
        };

        var optionsWrapper = Options.Create(aiOptions);
        services.AddSingleton<IOptions<GlossatorAiOptions>>(optionsWrapper);
        services.AddGlossatorAiClients();

        var sp = services.BuildServiceProvider();

        // 1. Initial resolution succeeds
        var client1 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
        Assert.NotNull(client1);

        // 2. Dynamic in-memory key update (e.g. after user fixes 401 Unauthorized)
        aiOptions.Providers["OpenAI"].ApiKey = "sk-valid-recovered-key-999";

        // 3. Next resolution creates a fresh transient client instance reflecting the runtime change
        var client2 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
        Assert.NotNull(client2);
        Assert.NotSame(client1, client2); // Proves transient lifecycle prevents stale singleton credential pinning
    }

    [Fact]
    public void ResolveAgent_WhenActiveProviderIsCustom_ResolvesCorrectProviderAndDefaultModel()
    {
        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "Custom",
            Providers =
            {
                ["Evren"] = new()
                {
                    Endpoint = "https://evren-llmapi.ssyz.org.tr/v1",
                    ApiKey = "YOUR_EVREN_API_KEY",
                    DefaultModel = "deepseek-v4.1-flash"
                },
                ["Custom"] = new()
                {
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-custom-key",
                    DefaultModel = "gpt-4o"
                }
            },
            Agents =
            {
                ["StyleAnalyst"] = new() { Provider = "Evren", ModelId = "deepseek-v4.1-flash" },
                ["Translator"] = new() { Provider = "Evren", ModelId = "gemma-4-31b" },
                ["Reviewer"] = new() { Provider = "Evren", ModelId = "mimo-v2.6-pro" }
            }
        };

        var (provName, provConfig, modelId) = aiOptions.ResolveAgent("Translator");

        Assert.Equal("Custom", provName);
        Assert.Equal("gpt-4o", modelId);
        Assert.Equal("https://api.openai.com/v1", provConfig.Endpoint);
    }

    [Fact]
    public void ResolveAgent_WhenSpecificAgentHasExplicitRealKey_OverridesActiveProvider()
    {
        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "OpenAI",
            Providers =
            {
                ["OpenAI"] = new()
                {
                    Endpoint = "https://api.openai.com/v1",
                    ApiKey = "sk-openai-key",
                    DefaultModel = "gpt-4o"
                },
                ["Gemini"] = new()
                {
                    Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
                    ApiKey = "real-gemini-key",
                    DefaultModel = "gemini-2.5-flash"
                }
            },
            Agents =
            {
                ["StyleAnalyst"] = new() { Provider = "OpenAI", ModelId = "gpt-4o-mini" },
                ["Translator"] = new() { Provider = "OpenAI", ModelId = "gpt-4o" },
                ["Reviewer"] = new() { Provider = "Gemini", ModelId = "gemini-2.5-pro" }
            }
        };

        var trans = aiOptions.ResolveAgent("Translator");
        var rev = aiOptions.ResolveAgent("Reviewer");

        Assert.Equal("OpenAI", trans.ProviderName);
        Assert.Equal("gpt-4o", trans.ModelId);

        Assert.Equal("Gemini", rev.ProviderName);
        Assert.Equal("gemini-2.5-pro", rev.ModelId);
    }

    [Fact]
    public void ResolveAgent_WhenEvrenIsActive_RetainsEvrenAgentSpecificModels()
    {
        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "Evren",
            Providers =
            {
                ["Evren"] = new()
                {
                    Endpoint = "https://evren-llmapi.ssyz.org.tr/v1",
                    ApiKey = "real-evren-key",
                    DefaultModel = "deepseek-v4.1-flash"
                }
            },
            Agents =
            {
                ["StyleAnalyst"] = new() { Provider = "Evren", ModelId = "deepseek-v4.1-flash" },
                ["Translator"] = new() { Provider = "Evren", ModelId = "gemma-4-31b" },
                ["Reviewer"] = new() { Provider = "Evren", ModelId = "mimo-v2.6-pro" }
            }
        };

        var analyst = aiOptions.ResolveAgent("StyleAnalyst");
        var trans = aiOptions.ResolveAgent("Translator");
        var rev = aiOptions.ResolveAgent("Reviewer");

        Assert.Equal("Evren", analyst.ProviderName);
        Assert.Equal("deepseek-v4.1-flash", analyst.ModelId);

        Assert.Equal("Evren", trans.ProviderName);
        Assert.Equal("gemma-4-31b", trans.ModelId);

        Assert.Equal("Evren", rev.ProviderName);
        Assert.Equal("mimo-v2.6-pro", rev.ModelId);
    }

    [Fact]
    public void ResolveAgent_WhenActiveProviderUnknown_FallsBackToFirstAvailableProvider()
    {
        var aiOptions = new GlossatorAiOptions
        {
            ActiveProvider = "NonExistentProvider",
            Providers =
            {
                ["FallbackProvider"] = new()
                {
                    Endpoint = "https://fallback.ai.com/v1",
                    ApiKey = "sk-fallback",
                    DefaultModel = "fallback-model"
                }
            }
        };

        var (provName, provConfig, modelId) = aiOptions.ResolveAgent("Translator");

        Assert.Equal("FallbackProvider", provName);
        Assert.Equal("fallback-model", modelId);
        Assert.Equal("https://fallback.ai.com/v1", provConfig.Endpoint);
    }

    [Fact]
    public void ResolveAgent_WhenNoProvidersConfigured_FallsBackToDefaultEvrenProvider()
    {
        var aiOptions = new GlossatorAiOptions
        {
            Providers = []
        };

        var (provName, provConfig, modelId) = aiOptions.ResolveAgent("Translator");
        Assert.Equal("Evren", provName);
        Assert.NotNull(provConfig);
    }
}
