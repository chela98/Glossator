namespace Glossator.Tests.AI;

using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Glossator.AI.Services;
using Glossator.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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

        var styleMeta = styleClient.GetService<ChatClientMetadata>();
        var transMeta = transClient.GetService<ChatClientMetadata>();
        var revMeta = revClient.GetService<ChatClientMetadata>();

        Assert.NotNull(styleMeta);
        Assert.NotNull(transMeta);
        Assert.NotNull(revMeta);
        // Evren anahtarı placeholder ("YOUR_EVREN_API_KEY") olduğu için tüm ajanlar Custom sağlayıcısına ve gpt-4o modeline çözümlenmeli
        Assert.Equal("gpt-4o", styleMeta.DefaultModelId);
        Assert.Equal("gpt-4o", transMeta.DefaultModelId);
        Assert.Equal("gpt-4o", revMeta.DefaultModelId);
        Assert.Equal(new Uri("https://api.openai.com/v1"), styleMeta.ProviderUri);
    }

    [Fact]
    public void AddGlossatorAiClients_WhenOptionsMutatedAtRuntime_TransientClientsReflectNewModelAndEndpoint()
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

        // 1. Initial resolution reflects initial configuration
        var client1 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
        var meta1 = client1.GetService<ChatClientMetadata>();
        Assert.Equal("gpt-4o", meta1?.DefaultModelId);
        Assert.Equal(new Uri("https://api.openai.com/v1"), meta1?.ProviderUri);

        // 2. Runtime update of API key, model and endpoint
        aiOptions.Providers["OpenAI"].ApiKey = "sk-valid-recovered-key-999";
        aiOptions.Providers["OpenAI"].Endpoint = "https://custom-proxy.example.com/v1";
        aiOptions.Agents["StyleAnalyst"].ModelId = "gpt-4o-mini";

        // 3. Next resolution creates a transient instance carrying the updated configuration
        var client2 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
        var meta2 = client2.GetService<ChatClientMetadata>();
        Assert.NotSame(client1, client2);
        Assert.Equal("gpt-4o-mini", meta2?.DefaultModelId);
        Assert.Equal(new Uri("https://custom-proxy.example.com/v1"), meta2?.ProviderUri);
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
        Assert.Equal("gpt-4o", modelId);
    }

    [Fact]
    public void ResolveAgent_WhenAgentAssignedToKeylessLocalProvider_ResolvesLocalProviderAndModel()
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
                ["Ollama"] = new()
                {
                    Endpoint = "http://LOCALHOST:11434/v1",
                    ApiKey = "",
                    DefaultModel = "llama3.2"
                }
            },
            Agents =
            {
                ["Translator"] = new() { Provider = "Ollama", ModelId = "deepseek-r1:14b" }
            }
        };

        var (provName, provConfig, modelId) = aiOptions.ResolveAgent("Translator");
        Assert.Equal("Ollama", provName);
        Assert.Equal("deepseek-r1:14b", modelId);
        Assert.Equal("http://LOCALHOST:11434/v1", provConfig.Endpoint);
    }

    [Theory]
    [InlineData("gemini-2.0-flash-lite", 0.075, 0.30)]
    [InlineData("gemini-2.5-flash-lite", 0.10, 0.40)]
    [InlineData("gemini-3.1-flash-lite", 0.25, 1.50)]
    [InlineData("gemini-2.5-pro", 1.25, 10.00)]
    [InlineData("gemini-3-pro-preview", 2.00, 12.00)]
    [InlineData("gemini-1.5-flash", 0.075, 0.30)]
    [InlineData("gemini-2.0-flash", 0.10, 0.40)]
    [InlineData("gemini-2.5-flash", 0.30, 2.50)]
    [InlineData("gemini-3-flash", 0.50, 3.00)]
    public void GetGeminiModelRatesPerMillionUsd_ReturnsExpectedRatesByFamilyAndGeneration(
        string modelId,
        double expectedInputUsd,
        double expectedOutputUsd)
    {
        // Act
        var (inputUsd, outputUsd) = GlossatorAiOptions.GetGeminiModelRatesPerMillionUsd(modelId);

        // Assert
        Assert.Equal(expectedInputUsd, inputUsd);
        Assert.Equal(expectedOutputUsd, outputUsd);
    }

    [Theory]
    [InlineData("StyleAnalyst", "Evren", "https://evren-llmapi.ssyz.org.tr/v1", "deepseek-v4.1-flash", 0.0, 0.0, true)]
    [InlineData("Reviewer", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-2.5-pro", 1.25, 10.00, false)]
    [InlineData("Translator", "CustomProxy", "https://proxy.example.com/v1", "gemini-2.5-flash", 0.30, 2.50, false)]
    public void ResolveStagePricing_DeterminesFreeVsGeminiPricingAccurately(
        string stageName,
        string providerName,
        string endpoint,
        string modelId,
        double expectedInputRate,
        double expectedOutputRate,
        bool expectedIsFree)
    {
        // Arrange
        var agentKey = stageName.StartsWith("Style") ? "StyleAnalyst" : stageName.StartsWith("Review") ? "Reviewer" : "Translator";
        var options = new GlossatorAiOptions
        {
            ActiveProvider = providerName,
            Providers =
            {
                [providerName] = new() { Endpoint = endpoint, ApiKey = "sk-test-real-key", DefaultModel = modelId }
            },
            Agents =
            {
                [agentKey] = new() { Provider = providerName, ModelId = modelId }
            }
        };

        // Act
        var pricing = options.ResolveStagePricing(stageName);

        // Assert
        Assert.Equal(providerName, pricing.ProviderName);
        Assert.Equal(modelId, pricing.ModelId);
        Assert.Equal(expectedInputRate, pricing.InputRatePerMillionUsd);
        Assert.Equal(expectedOutputRate, pricing.OutputRatePerMillionUsd);
        Assert.Equal(expectedIsFree, pricing.IsFree);
    }

    [Fact]
    public void DynamicConfigurationReload_UpdatesKeyedClientViaOptionsMonitor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"glossator-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "dynamic_settings.json");

        try
        {
            File.WriteAllText(configPath, """
            {
              "GlossatorAi": {
                "ActiveProvider": "OpenAI",
                "Providers": {
                  "OpenAI": {
                    "Endpoint": "https://api.openai.com/v1",
                    "ApiKey": "sk-initial-001",
                    "DefaultModel": "gpt-4o"
                  }
                },
                "Agents": {
                  "StyleAnalyst": { "Provider": "OpenAI", "ModelId": "gpt-4o" }
                }
              }
            }
            """);

            var config = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: false, reloadOnChange: false)
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.Configure<GlossatorAiOptions>(config.GetSection("GlossatorAi"));
            services.AddGlossatorAiClients();

            using var sp = services.BuildServiceProvider();
            var client1 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
            Assert.Equal("gpt-4o", client1.GetService<ChatClientMetadata>()?.DefaultModelId);

            File.WriteAllText(configPath, """
            {
              "GlossatorAi": {
                "ActiveProvider": "OpenAI",
                "Providers": {
                  "OpenAI": {
                    "Endpoint": "https://proxy.example.com/v1",
                    "ApiKey": "sk-reloaded-002",
                    "DefaultModel": "gpt-4.1"
                  }
                },
                "Agents": {
                  "StyleAnalyst": { "Provider": "OpenAI", "ModelId": "gpt-4.1" }
                }
              }
            }
            """);

            config.Reload();

            var client2 = sp.GetRequiredKeyedService<IChatClient>(AiServiceExtensions.KeyStyleAnalyst);
            var meta2 = client2.GetService<ChatClientMetadata>();
            Assert.Equal("gpt-4.1", meta2?.DefaultModelId);
            Assert.Equal(new Uri("https://proxy.example.com/v1"), meta2?.ProviderUri);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddGlossatorServices_RegistersHttpClientFactoryAndSingletonExchangeRateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new GlossatorAiOptions()));
        services.AddGlossatorAiClients();
        services.AddGlossatorExchangeRateService();

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetService<IHttpClientFactory>();
        var discovery = sp.GetService<IProviderDiscoveryService>();
        var rate1 = sp.GetService<IExchangeRateService>();
        var rate2 = sp.GetService<IExchangeRateService>();

        Assert.NotNull(factory);
        Assert.NotNull(discovery);
        Assert.NotNull(rate1);
        Assert.Same(rate1, rate2);
    }
}
