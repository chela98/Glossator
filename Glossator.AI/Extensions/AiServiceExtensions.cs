using System.ClientModel;
using Glossator.AI.Configuration;
using Glossator.AI.RateLimiting;
using Glossator.AI.Services;
using Glossator.AI.Workflows;
using Glossator.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

        // 4. Sağlayıcı Ping ve Model Keşif Servisi
        services.AddHttpClient<IProviderDiscoveryService, ProviderDiscoveryService>(client =>
        {
            client.Timeout = ProviderDiscoveryService.DefaultRequestTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });

        return services;
    }

    internal static string ResolveEffectiveApiKey(string? apiKey, string endpoint)
    {
        return string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
            ? (GlossatorAiOptions.IsLocalEndpoint(endpoint) ? "dummy-local-key" : apiKey ?? string.Empty)
            : apiKey;
    }

#pragma warning disable OPENAI001
    internal static OpenAI.Chat.ChatReasoningEffortLevel MapReasoningEffortLevel(string effort)
    {
        var normalized = effort.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "none" => OpenAI.Chat.ChatReasoningEffortLevel.None,
            "minimal" => OpenAI.Chat.ChatReasoningEffortLevel.Minimal,
            "low" => OpenAI.Chat.ChatReasoningEffortLevel.Low,
            "medium" => OpenAI.Chat.ChatReasoningEffortLevel.Medium,
            "high" => OpenAI.Chat.ChatReasoningEffortLevel.High,
            "xhigh" => new OpenAI.Chat.ChatReasoningEffortLevel("xhigh"),
            _ => new OpenAI.Chat.ChatReasoningEffortLevel(normalized)
        };
    }
#pragma warning restore OPENAI001

    private static void RegisterAgentChatClient(IServiceCollection services, string agentRoleKey)
    {
        services.AddKeyedTransient<IChatClient>(agentRoleKey, (sp, key) =>
        {
            var options = ResolveLiveOptions(sp);
            var (providerKey, providerConfig, modelId) = options.ResolveAgent(agentRoleKey);

            if (providerConfig == null || string.IsNullOrWhiteSpace(providerConfig.Endpoint))
            {
                throw new InvalidOperationException($"'{agentRoleKey}' ajanı için geçerli bir AI sağlayıcısı (Endpoint) bulunamadı. Lütfen appsettings.json dosyasını kontrol edin.");
            }

            options.Agents.TryGetValue(agentRoleKey, out var agentConfig);

            var apiKey = ResolveEffectiveApiKey(providerConfig.ApiKey, providerConfig.Endpoint);

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
            // ÖNEMLİ SIRALAMA KURALI (ChatClientBuilder Dıştan İçe Sarmalar):
            // 1. İlk eklenen middleware EN DIŞ katmandır (StreamingChatHelper'a en yakın).
            // 2. Son eklenen middleware EN İÇ katmandır (OpenAIChatClient'a en yakın).
            var config = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            var enableSensitiveData = ResolveSensitiveTelemetryEnabled(options, config);

            // OpenTelemetry en dışta olmalıdır ki ResilienceChatClient'ın tekilleştirip normalize ettiği
            // nihai UsageContent (input/output/reasoning token) paketini eksiksiz yakalayabilsin.
            // Ham prompt/metin içerikleri varsayılan olarak gizlidir (CaptureMessageContent/EnableSensitiveTelemetry ile açılabilir).
            var builder = innerClient
                .AsBuilder()
                .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = enableSensitiveData);

            // Microsoft.Extensions.AI Resmi Loglama Ara Yazılımı
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            if (loggerFactory != null)
            {
                builder.UseLogging(loggerFactory);
            }

            // Transport Seviyesinde 429 / 503 Jittered Exponential Backoff + Streaming Usage Tekilleştirme
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Glossator.AI.Clients.ResilienceChatClient>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Glossator.AI.Clients.ResilienceChatClient>.Instance;

            builder.Use(c => new Glossator.AI.Clients.ResilienceChatClient(c, logger));

            // Merkezi RateLimiter ara yazılımı (her gerçek deneme için jeton alır)
            var rateLimiter = sp.GetService<System.Threading.RateLimiting.RateLimiter>();
            if (rateLimiter != null)
            {
                builder.UseRateLimiting(rateLimiter);
            }

            // Eğer reasoning_effort tanımlanmışsa (örn: "none", "minimal", "low", "medium", "high", "xhigh"),
            // en iç katmanda doğrudan OpenAI SDK ChatCompletionOptions.ReasoningEffortLevel üzerinden enjekte et
            if (agentConfig != null && !string.IsNullOrWhiteSpace(agentConfig.ReasoningEffort))
            {
                var effort = agentConfig.ReasoningEffort.Trim();
                builder.Use((messages, chatOptions, next, ct) =>
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    chatOptions.AdditionalProperties["reasoning_effort"] = effort;

                    var prevFactory = chatOptions.RawRepresentationFactory;
                    chatOptions.RawRepresentationFactory = client =>
                    {
                        var raw = prevFactory?.Invoke(client) as OpenAI.Chat.ChatCompletionOptions
                                  ?? new OpenAI.Chat.ChatCompletionOptions();
#pragma warning disable OPENAI001
                        raw.ReasoningEffortLevel = MapReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
                        return raw;
                    };

                    return next(messages, chatOptions, ct);
                });
            }

            return builder.Build(sp);
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
        var aiOptions = services.GetService<IOptionsMonitor<GlossatorAiOptions>>()?.CurrentValue
            ?? services.GetService<IOptions<GlossatorAiOptions>>()?.Value;
        var config = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var enableSensitiveTelemetry = ResolveSensitiveTelemetryEnabled(aiOptions, config);

        var stylePrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("StyleAnalystPrompt.md", cancellationToken).ConfigureAwait(false);
        var translatorPrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("TranslatorPrompt.md", cancellationToken).ConfigureAwait(false);
        var reviewerPrompt = await Glossator.AI.Prompts.PromptLoader.LoadPromptAsync("ReviewerPrompt.md", cancellationToken).ConfigureAwait(false);

        return new BookLocalizationPipeline(
            styleAnalyst,
            translator,
            reviewer,
            postProcessor,
            stylePrompt,
            translatorPrompt,
            reviewerPrompt,
            logger,
            enableSensitiveTelemetry);
    }

    private static GlossatorAiOptions ResolveLiveOptions(IServiceProvider sp)
    {
        var monitor = sp.GetService<IOptionsMonitor<GlossatorAiOptions>>();
        if (monitor is not null)
        {
            var hasCustomOptionsMonitor = monitor.GetType().Assembly != typeof(IOptionsMonitor<>).Assembly;
            var hasOptionConfig = sp.GetService<IConfigureOptions<GlossatorAiOptions>>() is not null
                || sp.GetService<IPostConfigureOptions<GlossatorAiOptions>>() is not null;

            if (hasCustomOptionsMonitor || hasOptionConfig)
            {
                return monitor.CurrentValue;
            }
        }

        return sp.GetService<IOptions<GlossatorAiOptions>>()?.Value
            ?? monitor?.CurrentValue
            ?? new GlossatorAiOptions();
    }

    public static IServiceCollection AddGlossatorExchangeRateService(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(ExchangeRateService), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });

        services.TryAddSingleton<IExchangeRateService>(sp =>
            new ExchangeRateService(
                sp.GetService<ILogger<ExchangeRateService>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ExchangeRateService))));

        return services;
    }

    private static bool ResolveSensitiveTelemetryEnabled(
        GlossatorAiOptions? aiOptions,
        Microsoft.Extensions.Configuration.IConfiguration? config)
    {
        var captureFromOtel = bool.TryParse(config?["OpenTelemetry:CaptureMessageContent"], out var parsed) && parsed;
        return (aiOptions?.EnableSensitiveTelemetry ?? false) || captureFromOtel;
    }
}
