using System.ClientModel;
using Glossator.AI.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat; // <-- ChatClient ve AsIChatClient için gerekli

namespace Glossator.AI.Extensions;

public static class AiServiceExtensions
{
    public const string KeyGemini = "gemini";
    public const string KeyEvrenSyzz = "evren-syzz";

    public static IServiceCollection AddGlossatorAiClients(
        this IServiceCollection services,
        GlossatorAiOptions options)
    {
        // 1. Gemini Keyed Service (Google OpenAI-compatible endpoint)
        services.AddKeyedSingleton<IChatClient>(KeyGemini, (sp, key) =>
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = string.IsNullOrWhiteSpace(options.Gemini.Endpoint)
                    ? new Uri("https://generativelanguage.googleapis.com/v1beta/openai/")
                    : new Uri(options.Gemini.Endpoint)
            };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(options.Gemini.ApiKey),
                clientOptions);

            // OpenAIClient -> GetChatClient -> AsIChatClient
            return openAiClient.GetChatClient(options.Gemini.ModelId).AsIChatClient();
        });

        // 2. evren.syzz Keyed Service (OpenAI Standard / Custom Endpoint)
        services.AddKeyedSingleton<IChatClient>(KeyEvrenSyzz, (sp, key) =>
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(options.EvrenSyzz.Endpoint)
            };

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(options.EvrenSyzz.ApiKey),
                clientOptions);

            // OpenAIClient -> GetChatClient -> AsIChatClient
            return openAiClient.GetChatClient(options.EvrenSyzz.ModelId).AsIChatClient();
        });

        return services;
    }
}