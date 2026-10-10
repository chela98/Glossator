using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glossator.AI.Clients;

/// <summary>
/// LLM API çağrılarında 429 (Too Many Requests), 503 ve geçici kota aşımı durumlarını yakalayan,
/// jitter'lı üstel geri çekilme (exponential backoff) uygulayan kurumsal dayanıklılık istemcisi.
/// </summary>
public sealed class ResilienceChatClient(
    IChatClient innerClient,
    ILogger<ResilienceChatClient> logger,
    int maxRetries = 5,
    TimeSpan initialDelay = default) : DelegatingChatClient(innerClient)
{
    private readonly TimeSpan _initialDelay = initialDelay == default ? TimeSpan.FromSeconds(3) : initialDelay;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var delay = _initialDelay;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (IsTransientException(ex) && attempt < maxRetries)
            {
                logger.LogWarning(ex,
                    "API Kota Sınırı (429) veya Geçici Hata yakalandı. {DelaySec:F1}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    delay.TotalSeconds, attempt, maxRetries);

                await Task.Delay(delay, cancellationToken);
                // Jitter ile üstel geri çekilme (thundering herd koruması)
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2 + Random.Shared.Next(100, 500));
            }
        }

        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    private static bool IsTransientException(Exception ex)
    {
        if (ex is ClientResultException cre && cre.Status == (int)HttpStatusCode.TooManyRequests)
            return true;

        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase);
    }
}
