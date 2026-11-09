using System.ClientModel;
using Glossator.AI.Configuration;
using Glossator.AI.RateLimiting;
using Glossator.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Glossator.AI.Extensions;

public static class AiServiceExtensions
{
    public const string KeyStyleAnalyst = "StyleAnalyst";
    public const string KeyTranslator = "Translator";
    public const string KeyReviewer = "Reviewer";

    public static IServiceCollection AddGlossatorAiClients(this IServiceCollection services)
    {
        // 0. Central TokenBucketRateLimiter Singleton (60 RPM / TPM)
        services.AddSingleton<System.Threading.RateLimiting.RateLimiter>(_ =>
            new System.Threading.RateLimiting.TokenBucketRateLimiter(new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 100,
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst
            }));

        // 1. StyleAnalyst Keyed Service
        RegisterAgentChatClient(services, KeyStyleAnalyst);

        // 2. Translator Keyed Service
        RegisterAgentChatClient(services, KeyTranslator);

        // 3. Reviewer Keyed Service
        RegisterAgentChatClient(services, KeyReviewer);

        return services;
    }

    private static void RegisterAgentChatClient(IServiceCollection services, string agentRoleKey)
    {
        services.AddKeyedTransient<IChatClient>(agentRoleKey, (sp, key) =>
        {
            var options = sp.GetRequiredService<IOptions<GlossatorAiOptions>>().Value;
            var (providerKey, providerConfig, modelId) = options.ResolveAgent(agentRoleKey);

            if (providerConfig == null || string.IsNullOrWhiteSpace(providerConfig.Endpoint))
            {
                throw new InvalidOperationException($"'{agentRoleKey}' ajanı için geçerli bir AI sağlayıcısı (Endpoint) bulunamadı. Lütfen appsettings.json dosyasını kontrol edin.");
            }

            options.Agents.TryGetValue(agentRoleKey, out var agentConfig);

            var apiKey = string.IsNullOrWhiteSpace(providerConfig.ApiKey) || providerConfig.ApiKey.Contains("YOUR_")
                ? (providerConfig.Endpoint.Contains("localhost") || providerConfig.Endpoint.Contains("127.0.0.1") ? "dummy-local-key" : providerConfig.ApiKey)
                : providerConfig.ApiKey;

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(providerConfig.Endpoint),
                NetworkTimeout = TimeSpan.FromSeconds(180),
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0)
            };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                clientOptions);

            var innerClient = openAiClient.GetChatClient(modelId).AsIChatClient();

            // .NET 10 Pipeline Builder
            var builder = innerClient.AsBuilder();

            // Eğer reasoning_effort tanımlanmışsa (örn: "none"), middleware ile ChatOptions içine enjekte et
            if (agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.ReasoningEffort))
            {
                var effort = agentConfig.ReasoningEffort;
                builder.Use((messages, chatOptions, next, ct) =>
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    chatOptions.AdditionalProperties["reasoning_effort"] = effort;
                    return next(messages, chatOptions, ct);
                });
            }

            // Transport Seviyesinde 429 / 503 Jittered Exponential Backoff
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Glossator.AI.Clients.ResilienceChatClient>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Glossator.AI.Clients.ResilienceChatClient>.Instance;

            builder.Use(c => new Glossator.AI.Clients.ResilienceChatClient(c, logger));

            // Merkezi RateLimiter ara yazılımı
            var rateLimiter = sp.GetService<System.Threading.RateLimiting.RateLimiter>();
            if (rateLimiter != null)
            {
                builder.UseRateLimiting(rateLimiter);
            }

            // Microsoft.Extensions.AI Resmi Loglama Ara Yazılımı (Aspire Dashboard Canlı İstek/Yanıt ve Token Akışı)
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            if (loggerFactory != null)
            {
                builder.UseLogging(loggerFactory);
            }

            return builder
                .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
                .Build(sp);
        });
    }

    public static async Task<BookLocalizationPipeline> CreateBookLocalizationPipelineAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var styleAnalyst = services.GetRequiredKeyedService<IChatClient>(KeyStyleAnalyst);
        var translator = services.GetRequiredKeyedService<IChatClient>(KeyTranslator);
        var reviewer = services.GetRequiredKeyedService<IChatClient>(KeyReviewer);
        var postProcessor = services.GetRequiredService<Glossator.Core.Rules.TermGlossingPostProcessor>();
        var logger = services.GetService<Microsoft.Extensions.Logging.ILogger<BookLocalizationPipeline>>();

        var stylePrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("StyleAnalystPrompt.md", cancellationToken);
        var translatorPrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("TranslatorPrompt.md", cancellationToken);
        var reviewerPrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("ReviewerPrompt.md", cancellationToken);

        return new BookLocalizationPipeline(
            styleAnalyst,
            translator,
            reviewer,
            postProcessor,
            stylePrompt,
            translatorPrompt,
            reviewerPrompt,
            logger);
    }
}
