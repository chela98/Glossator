using System.ClientModel;
using Glossator.AI.Configuration;
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
        services.AddKeyedSingleton<IChatClient>(agentRoleKey, (sp, key) =>
        {
            var options = sp.GetRequiredService<IOptions<GlossatorAiOptions>>().Value;

            if (!options.Agents.TryGetValue(agentRoleKey, out var agentConfig))
            {
                throw new InvalidOperationException($"'{agentRoleKey}' ajanı için yapılandırma bulunamadı. Lütfen appsettings.json dosyasını kontrol edin.");
            }

            if (!options.Providers.TryGetValue(agentConfig.Provider, out var providerConfig))
            {
                throw new InvalidOperationException($"'{agentConfig.Provider}' sağlayıcısı bulunamadı (Ajan: {agentRoleKey}). Lütfen Providers listesini kontrol edin.");
            }

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(providerConfig.Endpoint),
                NetworkTimeout = TimeSpan.FromSeconds(600),
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 1)
            };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(providerConfig.ApiKey),
                clientOptions);

            var innerClient = openAiClient.GetChatClient(agentConfig.ModelId).AsIChatClient();

            // .NET 10 Pipeline Builder
            var builder = innerClient.AsBuilder();

            // Eğer reasoning_effort tanımlanmışsa (örn: "none"), middleware ile ChatOptions içine enjekte et
            if (!string.IsNullOrWhiteSpace(agentConfig.ReasoningEffort))
            {
                builder.Use((messages, chatOptions, next, ct) =>
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    chatOptions.AdditionalProperties["reasoning_effort"] = agentConfig.ReasoningEffort;
                    return next(messages, chatOptions, ct);
                });
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
            reviewerPrompt);
    }
}