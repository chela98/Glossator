using System.Diagnostics;
using System.Text;
using Glossator.AI.Diagnostics;
using Glossator.AI.Models;
using Microsoft.Extensions.AI;

namespace Glossator.AI.Streaming;

public record StreamingChatResult(
    ChatResponse? Response,
    string Text,
    long TimeToFirstTokenMs,
    double TokensPerSecond,
    long OutputTokens,
    long BilledOutputTokens = 0,
    double OverallThroughput = 0,
    long InputTokens = 0,
    long CachedInputTokens = 0,
    long ReasoningTokens = 0,
    long TimeToFirstChunkMs = -1,
    long EndToEndDurationMs = 0)
{
    /// <summary>
    /// Kullanıcı dostu, kafa karışıklığını önleyen zengin özet dizesi döner.
    /// Örn: "44,3 tok/s | Çıktı: 249 tok | Fatura: 553 tok | Cache: 3.072 (%29,9) | TTFT: 19660ms"
    /// </summary>
    public string FormatSummary(int attempt = 1)
    {
        var billed = BilledOutputTokens > 0 ? BilledOutputTokens : OutputTokens;
        var ttftStr = TimeToFirstTokenMs >= 0 ? $" | TTFT: {TimeToFirstTokenMs}ms" : "";
        var cacheStr = CachedInputTokens > 0
            ? (InputTokens > 0
                ? $" | Cache: {CachedInputTokens:N0} (%{(double)CachedInputTokens * 100.0 / InputTokens:F1})"
                : $" | Cache: {CachedInputTokens:N0}")
            : "";
        var attemptStr = attempt > 1 ? $" | Deneme: {attempt}" : "";

        if (billed > OutputTokens && (ReasoningTokens > 0 || (billed - OutputTokens) > 100))
        {
            return $"{TokensPerSecond:F1} tok/s | Çıktı: {OutputTokens:N0} tok | Fatura: {billed:N0} tok{cacheStr}{ttftStr}{attemptStr}";
        }

        return $"{TokensPerSecond:F1} tok/s | {OutputTokens:N0} tok{cacheStr}{ttftStr}{attemptStr}";
    }
}

/// <summary>
/// Server-Sent Events (SSE) tabanlı canlı token akışını (Streaming) yöneten,
/// ilk parça/token varış süresini (TTFC/TTFT), cache hit'i, reasoning muhasebesini
/// ve saniye başına token hızını (TPS) hesaplayan telemetri yardımcısı.
/// </summary>
public static class StreamingChatHelper
{
    private readonly record struct ResolvedUsageMetrics(
        long InputTokens,
        long CachedInputTokens,
        long BilledOutputTokens,
        long VisibleTextTokens,
        long ReasoningTokens,
        long TotalTokens);

    public static async Task<StreamingChatResult> StreamResponseAsync(
        IChatClient chatClient,
        IEnumerable<ChatMessage> messages,
        string stageName,
        int attempt = 1,
        Activity? activity = null,
        Action<PipelineStepLog>? stepLogger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);

        var stopwatch = Stopwatch.StartNew();
        long firstChunkE2eMs = -1;
        long firstChunkActiveMs = -1;
        long ttftE2eMs = -1;
        long ttftActiveMs = -1;
        var sb = new StringBuilder();
        var updates = new List<ChatResponseUpdate>();
        int estimatedTokenCount = 0;
        long lastReportMs = 0;

        using var attemptScope = ResilienceAttemptContext.BeginScope();
        var tracker = attemptScope.Tracker;
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        stepLogger?.Invoke(new PipelineStepLog(
            Stage: stageName,
            Summary: "Bağlantı kuruluyor / Yanıt bekleniyor...",
            Attempt: attempt,
            IsTransient: true));

        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(heartbeatCts.Token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref ttftActiveMs) >= 0)
                    {
                        break;
                    }

                    var elapsedSec = (int)(stopwatch.ElapsedMilliseconds / 1000);
                    stepLogger?.Invoke(new PipelineStepLog(
                        Stage: stageName,
                        Summary: $"Model Düşünüyor / Yanıt Bekleniyor... ({elapsedSec}s)",
                        Attempt: attempt,
                        IsTransient: true));
                }
            }
            catch (OperationCanceledException)
            {
                // Normal durdurma
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                var e2eNowMs = stopwatch.ElapsedMilliseconds;
                var activeNowMs = Math.Max(0L, (long)Stopwatch.GetElapsedTime(tracker.ActiveAttemptStartTimestamp).TotalMilliseconds);

                if (firstChunkE2eMs < 0)
                {
                    firstChunkE2eMs = e2eNowMs;
                    firstChunkActiveMs = activeNowMs;
                }

                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    if (ttftE2eMs < 0)
                    {
                        ttftE2eMs = e2eNowMs;
                        ttftActiveMs = activeNowMs;
                        heartbeatCts.Cancel();
                        GlossatorDiagnostics.TimeToFirstTokenHistogram.Record(ttftActiveMs);
                        activity?.SetTag("gen_ai.response.ttft_ms", ttftActiveMs);
                    }

                    sb.Append(text);
                    estimatedTokenCount += Math.Max(1, (text.Length + 2) / 4);

                    var streamingElapsedMs = activeNowMs - ttftActiveMs;
                    if (e2eNowMs - lastReportMs >= 300)
                    {
                        string tpsDisplay;
                        if (streamingElapsedMs >= 200)
                        {
                            var currentTps = estimatedTokenCount / (streamingElapsedMs / 1000.0);
                            tpsDisplay = $"{currentTps:F1} tok/s";
                        }
                        else
                        {
                            tpsDisplay = "başlatılıyor...";
                        }

                        stepLogger?.Invoke(new PipelineStepLog(
                            Stage: stageName,
                            Summary: $"Canlı Token Akışı: ~{estimatedTokenCount} token ({tpsDisplay} | TTFT: {ttftActiveMs}ms)",
                            Attempt: attempt,
                            IsTransient: true));

                        lastReportMs = e2eNowMs;
                    }
                }
            }
        }
        catch (NotImplementedException)
        {
            heartbeatCts.Cancel();
            var nonStreamingResp = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var nonStreamText = nonStreamingResp.Text?.Trim() ?? string.Empty;
            var e2eTotalMs = Math.Max(1.0, stopwatch.Elapsed.TotalMilliseconds);
            var activeTotalMs = Math.Max(1.0, Stopwatch.GetElapsedTime(tracker.ActiveAttemptStartTimestamp).TotalMilliseconds);
            var metrics = ComputeUsageMetrics(nonStreamingResp.Usage, messages, nonStreamText, fallbackChunkTokens: 0);
            var effectiveTtft = (long)Math.Round(activeTotalMs);

            return RecordTelemetryAndBuildResult(
                stageName: stageName,
                response: nonStreamingResp,
                fullText: nonStreamText,
                metrics: metrics,
                isStreaming: false,
                activeTtfcMs: effectiveTtft,
                activeTtftMs: effectiveTtft,
                e2eTtftMs: (long)Math.Round(e2eTotalMs),
                activeTotalMs: activeTotalMs,
                e2eTotalMs: e2eTotalMs,
                tracker: tracker,
                activity: activity);
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal heartbeat durdurma
            }
        }

        stopwatch.Stop();
        var streamE2eTotalMs = Math.Max(1.0, stopwatch.Elapsed.TotalMilliseconds);
        var streamActiveTotalMs = Math.Max(1.0, Stopwatch.GetElapsedTime(tracker.ActiveAttemptStartTimestamp).TotalMilliseconds);

        ChatResponse? composedResponse = null;
        try
        {
            composedResponse = updates.ToChatResponse();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Birleştirme hatası durumunda boş bırakılır
        }

        // KÜMÜLATİF TOKEN KATLANMA DÜZELTMESİ:
        // Akış boyunca her chunk kümülatif UsageContent taşıyabileceği için son geçerli UsageContent esas alınır.
        var lastUsage = updates
            .SelectMany(u => u.Contents.OfType<UsageContent>())
            .LastOrDefault()?.Details
            ?? composedResponse?.Usage;

        if (composedResponse != null && lastUsage != null)
        {
            composedResponse.Usage = lastUsage;
        }

        var fullText = sb.ToString();
        var resolvedMetrics = ComputeUsageMetrics(lastUsage, messages, fullText, estimatedTokenCount);
        var finalActiveTtft = ttftActiveMs >= 0 ? ttftActiveMs : (long)Math.Round(streamActiveTotalMs);
        var finalActiveTtfc = firstChunkActiveMs >= 0 ? firstChunkActiveMs : finalActiveTtft;
        var finalE2eTtft = ttftE2eMs >= 0 ? ttftE2eMs : (long)Math.Round(streamE2eTotalMs);

        return RecordTelemetryAndBuildResult(
            stageName: stageName,
            response: composedResponse,
            fullText: fullText,
            metrics: resolvedMetrics,
            isStreaming: true,
            activeTtfcMs: finalActiveTtfc,
            activeTtftMs: finalActiveTtft,
            e2eTtftMs: finalE2eTtft,
            activeTotalMs: streamActiveTotalMs,
            e2eTotalMs: streamE2eTotalMs,
            tracker: tracker,
            activity: activity);
    }

    private static ResolvedUsageMetrics ComputeUsageMetrics(
        UsageDetails? usage,
        IEnumerable<ChatMessage> messages,
        string fullText,
        int fallbackChunkTokens)
    {
        var estimatedInputTokens = messages.Sum(m => Math.Max(1L, ((m.Text?.Length ?? 0) + 2) / 4));
        var estimatedTextTokens = fullText.Length > 0
            ? Math.Max(1L, (fullText.Length + 2) / 4)
            : Math.Max(0L, fallbackChunkTokens);

        var inputTokens = Math.Max(0L, usage?.InputTokenCount ?? estimatedInputTokens);

        long? explicitCached = usage?.CachedInputTokenCount;
        if (!explicitCached.HasValue && usage?.AdditionalCounts != null
            && usage.AdditionalCounts.TryGetValue("InputTokenDetails.CachedTokenCount", out var addCached))
        {
            explicitCached = addCached;
        }
        var cachedInputTokens = Math.Clamp(explicitCached ?? 0L, 0L, inputTokens);

        // MEAI 10.10.0 birinci sınıf ReasoningTokenCount özelliğini önceliklendir;
        // yoksa eski AdditionalCounts["OutputTokenDetails.ReasoningTokenCount"] anahtarını oku.
        long? explicitReasoning = usage?.ReasoningTokenCount;
        if (!explicitReasoning.HasValue && usage?.AdditionalCounts != null
            && usage.AdditionalCounts.TryGetValue("OutputTokenDetails.ReasoningTokenCount", out var addReasoning))
        {
            explicitReasoning = addReasoning;
        }

        long? rawOutput = usage?.OutputTokenCount;
#pragma warning disable MEAI001
        long? explicitVisibleText = usage?.OutputTextTokenCount;
#pragma warning restore MEAI001
        long? rawTotal = usage?.TotalTokenCount;

        // Gemini gibi TotalTokenCount = Input + VisibleOutput + Thinking dönen ancak ReasoningTokenCount set etmeyen kaynaklar:
        if (rawTotal.HasValue && usage?.InputTokenCount.HasValue == true && rawOutput.HasValue
            && rawTotal.Value > usage.InputTokenCount.Value + rawOutput.Value)
        {
            var implicitReasoning = rawTotal.Value - usage.InputTokenCount.Value - rawOutput.Value;
            explicitReasoning ??= implicitReasoning;
            explicitVisibleText ??= rawOutput.Value;
            rawOutput = rawTotal.Value - usage.InputTokenCount.Value;
        }

        long billedOutputTokens;
        long reasoningTokens;
        long visibleTextTokens;

        if (rawOutput.HasValue)
        {
            var serverOutput = Math.Max(0L, rawOutput.Value);
            reasoningTokens = Math.Max(0L, explicitReasoning ?? 0L);

            if (explicitVisibleText.HasValue)
            {
                visibleTextTokens = Math.Max(0L, explicitVisibleText.Value);
                billedOutputTokens = Math.Max(serverOutput, visibleTextTokens + reasoningTokens);
            }
            else if (serverOutput >= reasoningTokens)
            {
                // Standart OpenAI / vLLM / normalize edilmiş Gemini sözleşmesi: Reasoning, OutputTokenCount içindedir.
                visibleTextTokens = serverOutput - reasoningTokens;
                billedOutputTokens = serverOutput;
            }
            else
            {
                // Bazı özel proxy'lerde OutputTokenCount yalnız görünür metni sayıp reasoning'i ayırabilir.
                visibleTextTokens = serverOutput;
                billedOutputTokens = serverOutput + reasoningTokens;
            }
        }
        else
        {
            visibleTextTokens = estimatedTextTokens;
            reasoningTokens = Math.Max(0L, explicitReasoning ?? 0L);
            billedOutputTokens = visibleTextTokens + reasoningTokens;
        }

        var totalTokens = rawTotal.HasValue
            ? Math.Max(rawTotal.Value, inputTokens + billedOutputTokens)
            : inputTokens + billedOutputTokens;

        return new ResolvedUsageMetrics(
            inputTokens,
            cachedInputTokens,
            billedOutputTokens,
            visibleTextTokens,
            reasoningTokens,
            totalTokens);
    }

    private static StreamingChatResult RecordTelemetryAndBuildResult(
        string stageName,
        ChatResponse? response,
        string fullText,
        ResolvedUsageMetrics metrics,
        bool isStreaming,
        long activeTtfcMs,
        long activeTtftMs,
        long e2eTtftMs,
        double activeTotalMs,
        double e2eTotalMs,
        ResilienceAttemptTracker tracker,
        Activity? activity)
    {
        var activeTotalSec = Math.Max(0.001, activeTotalMs / 1000.0);
        var activeStreamingMs = activeTtftMs >= 0 ? Math.Max(0.0, activeTotalMs - activeTtftMs) : 0.0;
        var streamingDurationSec = isStreaming && activeStreamingMs >= 100.0
            ? (activeStreamingMs / 1000.0)
            : activeTotalSec;

        var textTps = metrics.VisibleTextTokens / streamingDurationSec;
        var overallTps = metrics.BilledOutputTokens / activeTotalSec;

        if (!isStreaming)
        {
            GlossatorDiagnostics.TimeToFirstTokenHistogram.Record(activeTtftMs);
        }

        GlossatorDiagnostics.TokensPerSecondHistogram.Record(textTps);
        GlossatorDiagnostics.StreamedTokensCounter.Add(metrics.VisibleTextTokens);
        GlossatorDiagnostics.RecordStageUsage(
            stageName,
            metrics.InputTokens,
            metrics.BilledOutputTokens,
            metrics.VisibleTextTokens,
            metrics.CachedInputTokens,
            metrics.ReasoningTokens);

        activity?.SetTag("gen_ai.response.streaming", isStreaming);
        if (activeTtfcMs >= 0) activity?.SetTag("gen_ai.response.ttfc_ms", activeTtfcMs);
        activity?.SetTag("gen_ai.response.ttft_ms", activeTtftMs);
        activity?.SetTag("gen_ai.response.tps", textTps);
        activity?.SetTag("gen_ai.response.overall_throughput", overallTps);
        if (metrics.BilledOutputTokens > 0) activity?.SetTag("gen_ai.usage.output_tokens", metrics.BilledOutputTokens);
        activity?.SetTag("gen_ai.usage.text_tokens", metrics.VisibleTextTokens);
        activity?.SetTag("gen_ai.usage.reasoning_tokens", metrics.ReasoningTokens);
        activity?.SetTag("gen_ai.usage.input_tokens", metrics.InputTokens);
        if (metrics.CachedInputTokens > 0) activity?.SetTag("gen_ai.usage.cache_read_tokens", metrics.CachedInputTokens);
        activity?.SetTag("gen_ai.usage.total_tokens", metrics.TotalTokens);

        if (tracker.RetryCount > 0)
        {
            activity?.SetTag("gen_ai.retry.count", tracker.RetryCount);
            activity?.SetTag("gen_ai.retry.wait_ms", (long)Math.Round(tracker.TotalRetryWait.TotalMilliseconds));
            activity?.SetTag("gen_ai.response.e2e_ttft_ms", e2eTtftMs);
            activity?.SetTag("gen_ai.response.e2e_duration_ms", (long)Math.Round(e2eTotalMs));
        }

        return new StreamingChatResult(
            Response: response,
            Text: fullText,
            TimeToFirstTokenMs: activeTtftMs,
            TokensPerSecond: textTps,
            OutputTokens: metrics.VisibleTextTokens,
            BilledOutputTokens: metrics.BilledOutputTokens,
            OverallThroughput: overallTps,
            InputTokens: metrics.InputTokens,
            CachedInputTokens: metrics.CachedInputTokens,
            ReasoningTokens: metrics.ReasoningTokens,
            TimeToFirstChunkMs: activeTtfcMs,
            EndToEndDurationMs: (long)Math.Round(e2eTotalMs));
    }
}
