using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glossator.AI.Clients;

/// <summary>
/// LLM API çağrılarında 429 (Too Many Requests), 502 (Bad Gateway), 503 (Service Unavailable),
/// 504 (Gateway Timeout), 408 (Request Timeout), HTTP zaman aşımları ve geçici ağ kesintilerini
/// yakalayan; Retry-After başlıklarına saygı duyarak üst sınırı korumalı jitter'lı üstel geri çekilme
/// (exponential backoff) uygulayan kurumsal dayanıklılık istemcisi.
/// </summary>
public sealed class ResilienceChatClient(
    IChatClient innerClient,
    ILogger<ResilienceChatClient> logger,
    int maxRetries = 3,
    TimeSpan initialDelay = default) : DelegatingChatClient(innerClient)
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _initialDelay = initialDelay == default ? TimeSpan.FromSeconds(2) : initialDelay;
    private readonly int _maxAttempts = Math.Max(1, maxRetries);

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
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken) && attempt < _maxAttempts)
            {
                var waitDelay = ComputeRetryDelay(ex, delay, out delay);

                logger.LogWarning(ex,
                    "API Kota Sınırı (429) veya Geçici Hata yakalandı. {DelaySec:F2}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    waitDelay.TotalSeconds, attempt, _maxAttempts);

                await Task.Delay(waitDelay, cancellationToken).ConfigureAwait(false);
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
                streamStarted = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken) && attempt < _maxAttempts)
            {
                var waitDelay = ComputeRetryDelay(ex, delay, out delay);

                logger.LogWarning(ex,
                    "Streaming API Kota Sınırı (429) veya Geçici Hata yakalandı. {DelaySec:F2}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    waitDelay.TotalSeconds, attempt, _maxAttempts);

                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                await Task.Delay(waitDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                UsageContent? latestUsage = null;
                ChatResponseUpdate? lastUpdate = null;

                while (streamStarted)
                {
                    var current = enumerator.Current;

                    // KÜMÜLATİF TOKEN ŞİŞMESİ KORUMASI (.NET Aspire / OpenTelemetryChatClient için):
                    // Gemini gibi sağlayıcılar her SSE chunk'ında o ana kadarki kümülatif UsageContent'i gönderir.
                    // UseOpenTelemetry() ise akıştaki tüm UsageContent'leri üst üste toplar (10K -> 100K şişmesi).
                    // Ara chunk'lardaki UsageContent'leri sıyırıp sadece en son geçerli UsageContent'i akış sonunda iletiyoruz.
                    for (var i = current.Contents.Count - 1; i >= 0; i--)
                    {
                        if (current.Contents[i] is UsageContent uc)
                        {
                            latestUsage = NormalizeUsageContent(uc);
                            current.Contents.RemoveAt(i);
                        }
                    }

                    lastUpdate = current;
                    yield return current;
                    streamStarted = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }

                if (latestUsage != null)
                {
                    var usageUpdate = new ChatResponseUpdate
                    {
                        Role = lastUpdate?.Role ?? ChatRole.Assistant,
                        ModelId = lastUpdate?.ModelId,
                        ResponseId = lastUpdate?.ResponseId,
                        Contents = [latestUsage]
                    };
                    yield return usageUpdate;
                }

                yield break;
            }
            finally
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Gemini OpenAI uyumluluk katmanı düşünme (thinking/reasoning) tokenlarını completion_tokens'a (OutputTokenCount)
    /// dahil etmeyip doğrudan total_tokens (TotalTokenCount) içine yazar (total = input + output + thinking).
    /// Bu metot gizli thinking farkını yakalar, AdditionalCounts["ReasoningTokenCount"] içine kaydeder ve
    /// faturalandırılan OutputTokenCount değerini gerçek toplam çıktıya (metin + düşünme) eşitler.
    /// </summary>
    private static UsageContent NormalizeUsageContent(UsageContent uc)
    {
        var details = uc.Details;
        if (details == null)
        {
            return uc;
        }

        var input = details.InputTokenCount ?? 0;
        var output = details.OutputTokenCount ?? 0;
        var total = details.TotalTokenCount ?? 0;

        if (total > (input + output) && input > 0)
        {
            var implicitReasoning = total - input - output;
            details.AdditionalCounts ??= [];
            if (!details.AdditionalCounts.ContainsKey("OutputTokenDetails.ReasoningTokenCount"))
            {
                details.AdditionalCounts["OutputTokenDetails.ReasoningTokenCount"] = implicitReasoning;
            }
            details.OutputTokenCount = total - input;
        }

        return uc;
    }

    private static TimeSpan ComputeRetryDelay(Exception ex, TimeSpan currentBackoff, out TimeSpan nextBackoff)
    {
        var cappedCurrent = currentBackoff > MaxRetryDelay ? MaxRetryDelay : currentBackoff;
        var waitDelay = cappedCurrent;

        if (TryGetRetryAfterDelay(ex, out var retryAfter) && retryAfter > waitDelay)
        {
            waitDelay = retryAfter > MaxRetryDelay ? MaxRetryDelay : retryAfter;
        }

        // Jitter ile üstel geri çekilme (thundering herd koruması) ve üst sınır
        var nextMs = cappedCurrent.TotalMilliseconds * 2 + Random.Shared.Next(100, 500);
        nextBackoff = TimeSpan.FromMilliseconds(Math.Min(MaxRetryDelay.TotalMilliseconds, nextMs));

        return waitDelay;
    }

    private static bool TryGetRetryAfterDelay(Exception ex, out TimeSpan delay)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is ClientResultException cre)
            {
                var headers = cre.GetRawResponse()?.Headers;
                if (headers != null)
                {
                    if (headers.TryGetValue("retry-after-ms", out var msRaw)
                        && double.TryParse(msRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                        && ms > 0)
                    {
                        delay = TimeSpan.FromMilliseconds(Math.Min(MaxRetryDelay.TotalMilliseconds, ms));
                        return true;
                    }

                    if (headers.TryGetValue("Retry-After", out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
                    {
                        if (double.TryParse(rawValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                            && seconds > 0)
                        {
                            var secMs = Math.Min(MaxRetryDelay.TotalMilliseconds, seconds * 1000.0);
                            delay = TimeSpan.FromMilliseconds(secMs);
                            return true;
                        }

                        if (DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryAt))
                        {
                            var delta = retryAt - DateTimeOffset.UtcNow;
                            if (delta > TimeSpan.Zero)
                            {
                                delay = delta > MaxRetryDelay ? MaxRetryDelay : delta;
                                return true;
                            }
                        }
                    }
                }
            }
        }

        delay = TimeSpan.Zero;
        return false;
    }

    private static bool IsTransientException(Exception ex, CancellationToken cancellationToken)
    {
        // 1. Çağıranın iptal sinyali kesinlikle geçici hata sayılamaz (Ctrl+C anında durmalı)
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // Exception zincirini (HttpClient TaskCanceledException(TimeoutException), ClientResultException(SocketException) vb.) denetle
        if (HasCallerCancellation(ex, cancellationToken))
        {
            return false;
        }

        for (var current = ex; current != null; current = current.InnerException)
        {
            // 2. System.ClientModel (OpenAI SDK) HTTP Durum Kodları
            if (current is ClientResultException cre && cre.Status > 0)
            {
                return IsTransientStatusCode(cre.Status);
            }

            // 3. Standart .NET HttpClient İstisna ve Durum Kodları
            if (current is HttpRequestException hre)
            {
                if (hre.StatusCode.HasValue)
                {
                    return IsTransientStatusCode((int)hre.StatusCode.Value);
                }

                // Durum kodu yoksa soket veya DNS bağlantı kopmasıdır; tekrar dene
                return true;
            }

            // 4. Ağ zaman aşımları (HttpClient TimeoutException sarmalı dahil), soket veya ham I/O akış kopmaları
            if (current is TimeoutException or SocketException || current.GetType() == typeof(IOException))
            {
                return true;
            }
        }

        // Salt iptal (içinde TimeoutException yoksa) yeniden denenmez
        if (ex is OperationCanceledException)
        {
            return false;
        }

        // 5. İkincil Fallback: Durum kodu fırlatmayan özel LLM proxy sunucuları için metin araması
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("502", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("504", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientStatusCode(int statusCode) => statusCode is
        408 // RequestTimeout
        or 429 // TooManyRequests
        or 502 // BadGateway
        or 503 // ServiceUnavailable
        or 504; // GatewayTimeout

    private static bool HasCallerCancellation(Exception ex, CancellationToken callerToken)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException oce
                && oce.CancellationToken.CanBeCanceled
                && oce.CancellationToken == callerToken)
            {
                return true;
            }
        }

        return false;
    }
}
