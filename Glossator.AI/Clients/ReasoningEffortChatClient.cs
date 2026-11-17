using Microsoft.Extensions.AI;

namespace Glossator.AI.Clients;

/// <summary>
/// ChatOptions üzerine yapılandırılmış reasoning_effort değerini enjekte eden hafif DelegatingChatClient.
/// AnonymousDelegatingChatClient'ın ortak delege kanal adaptörünün akış hatasında (ör. HTTP 503) arka plan
/// görevini sahipsiz bırakarak TaskScheduler.UnobservedTaskException üretmesini önler.
/// </summary>
internal sealed class ReasoningEffortChatClient(
    IChatClient innerClient,
    string reasoningEffort) : DelegatingChatClient(innerClient)
{
    private readonly string _effort = reasoningEffort.Trim();

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, EnrichOptions(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, EnrichOptions(options), cancellationToken);

    private ChatOptions EnrichOptions(ChatOptions? options)
    {
        var enriched = options?.Clone() ?? new ChatOptions();
        enriched.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        enriched.AdditionalProperties["reasoning_effort"] = _effort;

        var prevFactory = enriched.RawRepresentationFactory;
        enriched.RawRepresentationFactory = client =>
        {
            var raw = prevFactory?.Invoke(client) as OpenAI.Chat.ChatCompletionOptions
                      ?? new OpenAI.Chat.ChatCompletionOptions();
#pragma warning disable OPENAI001
            raw.ReasoningEffortLevel = Extensions.AiServiceExtensions.MapReasoningEffortLevel(_effort);
#pragma warning restore OPENAI001
            return raw;
        };

        return enriched;
    }
}
