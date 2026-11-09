using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glossator.AI.Clients;

/// <summary>
/// LLM API çağrılarında 429 (Too Many Requests), 503 (Service Unavailable), 504 (Gateway Timeout),
/// 408 (Request Timeout) ve geçici ağ kesintilerini yakalayan, jitter'lı üstel geri çekilme
/// (exponential backoff) uygulayan kurumsal dayanıklılık istemcisi.
/// </summary>
public sealed class ResilienceChatClient(
    IChatClient innerClient,
    ILogger<ResilienceChatClient> logger,
    int maxRetries = 3,
    TimeSpan initialDelay = default) : DelegatingChatClient(innerClient)
{
    private readonly TimeSpan _initialDelay = initialDelay == default ? TimeSpan.FromSeconds(2) : initialDelay;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var delay = _initialDelay;

        for (var attempt = 1; ; attempt++)
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
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var delay = _initialDelay;

        for (var attempt = 1; ; attempt++)
        {
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            var streamStarted = false;

            try
            {
                enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                streamStarted = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (IsTransientException(ex) && attempt < maxRetries)
            {
                logger.LogWarning(ex,
                    "Streaming API Kota Sınırı (429) veya Geçici Hata yakalandı. {DelaySec:F1}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    delay.TotalSeconds, attempt, maxRetries);

                if (enumerator != null)
                {
                    await enumerator.DisposeAsync();
                }

                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2 + Random.Shared.Next(100, 500));
                continue;
            }

            try
            {
                while (streamStarted)
                {
                    yield return enumerator.Current;
                    streamStarted = await enumerator.MoveNextAsync();
                }
                yield break;
            }
            finally
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync();
                }
            }
        }
    }

    private static bool IsTransientException(Exception ex)
    {
        // 1. İptal sinyali kesinlikle geçici hata sayılamaz (Ctrl+C anında durmalı)
        if (ex is OperationCanceledException)
        {
            return false;
        }

        // 2. System.ClientModel (OpenAI SDK) HTTP Durum Kodları
        if (ex is ClientResultException cre)
        {
            var status = cre.Status;
            if (status is 429 // TooManyRequests
                or 503       // ServiceUnavailable
                or 504       // GatewayTimeout
                or 408)      // RequestTimeout
            {
                return true;
            }
        }

        // 3. Standart .NET HttpClient İstisna ve Durum Kodları
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode.HasValue)
            {
                var code = (int)hre.StatusCode.Value;
                if (code is 429 or 503 or 504 or 408)
                {
                    return true;
                }
            }

            // Soket veya DNS bağlantı kopmaları
            return true;
        }

        // 4. Zaman aşımı ve soket istisnaları
        if (ex is TimeoutException or SocketException)
        {
            return true;
        }

        // 5. İkincil Fallback: Durum kodu fırlatmayan özel LLM proxy sunucuları için metin araması
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase);
    }
}
