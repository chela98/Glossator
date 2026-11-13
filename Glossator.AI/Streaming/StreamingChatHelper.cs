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
    double OverallThroughput = 0)
{
    /// <summary>
    /// Kullanıcı dostu, kafa karışıklığını önleyen zengin özet dizesi döner.
    /// Örn: "98,2 tok/s | Çıktı: ~414 tok | Fatura: 4.312 tok | TTFT: 43019ms"
    /// </summary>
    public string FormatSummary(int attempt = 1)
    {
        var billed = BilledOutputTokens > 0 ? BilledOutputTokens : OutputTokens;
        var ttftStr = TimeToFirstTokenMs >= 0 ? $" | TTFT: {TimeToFirstTokenMs}ms" : "";
        var attemptStr = attempt > 1 ? $" | Deneme: {attempt}" : "";

        if (billed > OutputTokens && (billed - OutputTokens) > 100)
        {
            return $"{TokensPerSecond:F1} tok/s | Çıktı: ~{OutputTokens} tok | Fatura: {billed:N0} tok{ttftStr}{attemptStr}";
        }

        return $"{TokensPerSecond:F1} tok/s | ~{OutputTokens} tok{ttftStr}{attemptStr}";
    }
}

/// <summary>
/// Server-Sent Events (SSE) tabanlı canlı token akışını (Streaming) yöneten,
/// ilk token varış süresini (TTFT) ve saniye başına token hızını (TPS) hesaplayan,
/// terminal donma hissini önlemek için canlı ilerleme sinyali yayan telemetri yardımcısı.
/// </summary>
public static class StreamingChatHelper
{
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
        long ttftMs = -1;
        var sb = new StringBuilder();
        var updates = new List<ChatResponseUpdate>();
        int estimatedTokenCount = 0;
        long lastReportMs = 0;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // İlk sinyal: İsteğin sunucuya iletildiğini ve bekleme durumuna geçildiğini bildir
        stepLogger?.Invoke(new PipelineStepLog(
            Stage: stageName,
            Summary: "Bağlantı kuruluyor / Yanıt bekleniyor...",
            Attempt: attempt,
            IsTransient: true));

        // TTFT (ilk token varışı) geciktiğinde (sunucu yoğunluğu, 503 retry, model reasoning)
        // terminalin kilitlenmiş gibi görünmesini önleyen saniye sayacı (heartbeat ticker)
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(heartbeatCts.Token).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref ttftMs) >= 0)
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
                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    var nowMs = stopwatch.ElapsedMilliseconds;
                    if (ttftMs < 0)
                    {
                        ttftMs = nowMs;
                        heartbeatCts.Cancel();
                        GlossatorDiagnostics.TimeToFirstTokenHistogram.Record(ttftMs);
                        activity?.SetTag("gen_ai.response.ttft_ms", ttftMs);
                    }

                    sb.Append(text);
                    estimatedTokenCount += Math.Max(1, (text.Length + 2) / 4);

                    // Canlı terminal / log bildirimi (donma hissini engellemek için ~250-300ms aralıklarla)
                    var streamingElapsedMs = nowMs - ttftMs;
                    if (nowMs - lastReportMs >= 300)
                    {
                        string tpsDisplay;
                        if (streamingElapsedMs >= 200)
                        {
                            var streamingSec = streamingElapsedMs / 1000.0;
                            var currentTps = estimatedTokenCount / streamingSec;
                            tpsDisplay = $"{currentTps:F1} tok/s";
                        }
                        else
                        {
                            tpsDisplay = "başlatılıyor...";
                        }

                        stepLogger?.Invoke(new PipelineStepLog(
                            Stage: stageName,
                            Summary: $"Canlı Token Akışı: ~{estimatedTokenCount} token ({tpsDisplay} | TTFT: {ttftMs}ms)",
                            Attempt: attempt,
                            IsTransient: true));

                        lastReportMs = nowMs;
                    }
                }
            }
        }
        catch (NotImplementedException)
        {
            heartbeatCts.Cancel();
            // İstemci GetStreamingResponseAsync desteklemiyorsa (örn: temel test mock'ları) GetResponseAsync'e güvenli geri dönüş
            var nonStreamingResp = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var nonStreamText = nonStreamingResp.Text?.Trim() ?? string.Empty;
            var estimatedInTokens = messages.Sum(m => Math.Max(1L, (m.Text?.Length ?? 0) / 4));
            var nonStreamInTokens = nonStreamingResp.Usage?.InputTokenCount ?? estimatedInTokens;
            var billedOutTokens = nonStreamingResp.Usage?.OutputTokenCount ?? Math.Max(1, nonStreamText.Length / 4);
            var textOutTokens = Math.Max(1, nonStreamText.Length / 4);
            var durationSec = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            var tps = textOutTokens / durationSec;
            var fallbackOverallTps = billedOutTokens / durationSec;

            GlossatorDiagnostics.RecordStageUsage(stageName, nonStreamInTokens, billedOutTokens, textOutTokens);

            if (nonStreamingResp.Usage != null)
            {
                activity?.SetTag("gen_ai.usage.input_tokens", nonStreamingResp.Usage.InputTokenCount);
                activity?.SetTag("gen_ai.usage.output_tokens", nonStreamingResp.Usage.OutputTokenCount);
                activity?.SetTag("gen_ai.usage.total_tokens", nonStreamingResp.Usage.TotalTokenCount);
            }

            return new StreamingChatResult(
                Response: nonStreamingResp,
                Text: nonStreamText,
                TimeToFirstTokenMs: stopwatch.ElapsedMilliseconds,
                TokensPerSecond: tps,
                OutputTokens: textOutTokens,
                BilledOutputTokens: billedOutTokens,
                OverallThroughput: fallbackOverallTps);
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
        // updates.ToChatResponse() akış boyunca gelen her SSE chunk'ındaki UsageContent nesnesini toplar (+=).
        // Ancak Gemini ve OpenAI akışlarında her chunk'taki Usage incremental değil, kümülatiftir (o ana kadarki toplam).
        // Bu nedenle akıştaki son geçerli UsageContent nesnesini alarak 16x-40x şişmeyi önlüyoruz.
        var lastUsage = updates
            .SelectMany(u => u.Contents.OfType<UsageContent>())
            .LastOrDefault()?.Details
            ?? composedResponse?.Usage;

        if (composedResponse != null && lastUsage != null)
        {
            composedResponse.Usage = lastUsage;
        }

        var fullText = sb.ToString();
        var textTokens = Math.Max(1L, fullText.Length > 0 ? (fullText.Length + 2) / 4 : estimatedTokenCount);
        var estimatedInputTokens = messages.Sum(m => Math.Max(1L, (m.Text?.Length ?? 0) / 4));
        var inputTokens = lastUsage?.InputTokenCount ?? estimatedInputTokens;
        var rawOutputTokens = lastUsage?.OutputTokenCount ?? textTokens;
        var totalTokens = lastUsage?.TotalTokenCount ?? (inputTokens + rawOutputTokens);

        // Gemini ve OpenAI reasoning token tespiti:
        // 1) OpenAI / MEAI AdditionalCounts["OutputTokenDetails.ReasoningTokenCount"]
        // 2) Gemini total_tokens - prompt_tokens farkı (total > input + visible_output)
        long explicitReasoningTokens = 0;
        if (lastUsage?.AdditionalCounts != null &&
            lastUsage.AdditionalCounts.TryGetValue("OutputTokenDetails.ReasoningTokenCount", out var rCount))
        {
            explicitReasoningTokens = rCount;
        }

        var billedOutputTokens = Math.Max(rawOutputTokens, totalTokens > inputTokens ? (totalTokens - inputTokens) : rawOutputTokens);
        if (explicitReasoningTokens > 0 && billedOutputTokens < textTokens + explicitReasoningTokens)
        {
            billedOutputTokens = textTokens + explicitReasoningTokens;
        }

        var reasoningTokens = explicitReasoningTokens > 0
            ? explicitReasoningTokens
            : Math.Max(0, billedOutputTokens - textTokens);
        var visibleTextTokens = explicitReasoningTokens > 0
            ? Math.Max(1, billedOutputTokens - explicitReasoningTokens)
            : textTokens;

        var totalDurationSec = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        var streamingDurationMs = ttftMs > 0 ? (stopwatch.ElapsedMilliseconds - ttftMs) : 0;
        // Eğer tüm tokenlar tek parça geldiyse veya akış süresi 100ms altındaysa toplam süreyi baz al
        var streamingDurationSec = (ttftMs > 0 && streamingDurationMs >= 100)
            ? (streamingDurationMs / 1000.0)
            : totalDurationSec;

        // Metin Akış Hızı (Text TPS): Canlı ekrana basılan çeviri metninin hızı (~100 tok/s)
        var textTps = visibleTextTokens / streamingDurationSec;

        // Genel Model Hızı (Overall Throughput): Düşünme (reasoning) dahil faturanın tüm süreye oranı (~90 tok/s)
        var overallTps = billedOutputTokens / totalDurationSec;

        // Telemetri ve OpenTelemetry Metrik Kayıtları
        GlossatorDiagnostics.TokensPerSecondHistogram.Record(textTps);
        GlossatorDiagnostics.StreamedTokensCounter.Add(visibleTextTokens);
        GlossatorDiagnostics.RecordStageUsage(stageName, inputTokens, billedOutputTokens, visibleTextTokens);

        activity?.SetTag("gen_ai.response.streaming", true);
        activity?.SetTag("gen_ai.response.ttft_ms", ttftMs >= 0 ? ttftMs : stopwatch.ElapsedMilliseconds);
        activity?.SetTag("gen_ai.response.tps", textTps);
        activity?.SetTag("gen_ai.response.overall_throughput", overallTps);
        if (billedOutputTokens > 0) activity?.SetTag("gen_ai.usage.output_tokens", billedOutputTokens);
        activity?.SetTag("gen_ai.usage.text_tokens", visibleTextTokens);
        activity?.SetTag("gen_ai.usage.reasoning_tokens", reasoningTokens);
        activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity?.SetTag("gen_ai.usage.total_tokens", totalTokens);

        return new StreamingChatResult(
            Response: composedResponse,
            Text: fullText,
            TimeToFirstTokenMs: ttftMs >= 0 ? ttftMs : stopwatch.ElapsedMilliseconds,
            TokensPerSecond: textTps,
            OutputTokens: visibleTextTokens,
            BilledOutputTokens: billedOutputTokens,
            OverallThroughput: overallTps);
    }
}
