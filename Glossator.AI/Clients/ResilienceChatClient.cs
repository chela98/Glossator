using System.ClientModel;
using System.Globalization;
using Glossator.AI.Diagnostics;
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
    private readonly int _maxRetries = Math.Max(0, maxRetries);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var delay = _initialDelay;

        for (var attempt = 1; ; attempt++)
        {
            ResilienceAttemptContext.Current?.RecordAttemptStart(attempt);
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (AiExceptionClassifier.IsTransient(ex, cancellationToken) && attempt <= _maxRetries)
            {
                var waitDelay = ComputeRetryDelay(ex, delay, out delay);

                logger.LogWarning(ex,
                    "API {FailureReason} yakalandı. {DelaySec:F2}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    AiExceptionClassifier.DescribeFailure(ex), waitDelay.TotalSeconds, attempt, _maxRetries);

                ResilienceAttemptContext.Current?.RecordRetryWait(waitDelay);
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

            ResilienceAttemptContext.Current?.RecordAttemptStart(attempt);
            try
            {
                enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                streamStarted = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (AiExceptionClassifier.IsTransient(ex, cancellationToken) && attempt <= _maxRetries)
            {
                var waitDelay = ComputeRetryDelay(ex, delay, out delay);

                logger.LogWarning(ex,
                    "Streaming {FailureReason} yakalandı. {DelaySec:F2}sn sonra yeniden deneniyor... (Deneme {Attempt}/{MaxRetries})",
                    AiExceptionClassifier.DescribeFailure(ex), waitDelay.TotalSeconds, attempt, _maxRetries);

                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                ResilienceAttemptContext.Current?.RecordRetryWait(waitDelay);
                await Task.Delay(waitDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                throw;
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
#pragma warning disable MEAI001
            details.OutputTextTokenCount ??= output;
#pragma warning restore MEAI001
            details.ReasoningTokenCount ??= implicitReasoning;
            details.AdditionalCounts ??= [];
            if (!details.AdditionalCounts.ContainsKey("OutputTokenDetails.ReasoningTokenCount"))
            {
                details.AdditionalCounts["OutputTokenDetails.ReasoningTokenCount"] = implicitReasoning;
            }
            details.OutputTokenCount = total - input;
        }

        return uc;
    }

    internal static TimeSpan ComputeRetryDelay(Exception ex, TimeSpan currentBackoff, out TimeSpan nextBackoff)
    {
        var cappedCurrent = currentBackoff > MaxRetryDelay ? MaxRetryDelay : currentBackoff;
        var hasRetryAfter = TryGetRetryAfterDelay(ex, out var retryAfter);

        TimeSpan waitDelay;
        if (hasRetryAfter && retryAfter > cappedCurrent)
        {
            var jitterMs = retryAfter >= TimeSpan.FromSeconds(1)
                ? Random.Shared.Next(100, 500)
                : Random.Shared.Next(1, Math.Max(2, (int)(retryAfter.TotalMilliseconds * 0.1) + 1));
            waitDelay = TimeSpan.FromMilliseconds(Math.Min(MaxRetryDelay.TotalMilliseconds, retryAfter.TotalMilliseconds + jitterMs));
        }
        else
        {
            waitDelay = cappedCurrent;
        }

        // Sonraki denemenin tabanı uygulanan gerçek beklemeden 2x büyür;
        // sunucu her seferinde aynı Retry-After değerini dönse bile bekleme artarak tırmanır (örn. 15 -> ~30 -> 60sn).
        var effectiveBaseMs = Math.Max(
            cappedCurrent.TotalMilliseconds,
            hasRetryAfter ? retryAfter.TotalMilliseconds : waitDelay.TotalMilliseconds);
        var expJitterMs = effectiveBaseMs >= 1000
            ? Random.Shared.Next(100, 500)
            : Random.Shared.Next(1, Math.Max(2, (int)(effectiveBaseMs * 0.2) + 1));
        var nextMs = effectiveBaseMs * 2 + expJitterMs;
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
}
