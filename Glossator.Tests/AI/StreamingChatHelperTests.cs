using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Glossator.AI.Clients;
using Glossator.AI.Diagnostics;
using Glossator.AI.Models;
using Glossator.AI.Streaming;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class StreamingChatHelperTests
{
    private sealed class StreamingMockChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _chunks;
        private readonly int _delayMs;

        public StreamingMockChatClient(IReadOnlyList<string> chunks, int delayMs = 0)
        {
            _chunks = chunks;
            _delayMs = delayMs;
        }

        public ChatClientMetadata Metadata => new("StreamingMock");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var fullText = string.Concat(_chunks);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, fullText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _chunks)
            {
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, cancellationToken);
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NonStreamingOnlyChatClient : IChatClient
    {
        private readonly string _responseText;

        public NonStreamingOnlyChatClient(string responseText)
        {
            _responseText = responseText;
        }

        public ChatClientMetadata Metadata => new("NonStreamingOnly");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _responseText)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Streaming is intentionally unsupported in this mock");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CustomUpdatesChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public ChatClientMetadata Metadata => new("CustomUpdatesMock");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task StreamResponseAsync_WithStreamingClient_AggregatesTextAndCalculatesMetrics()
    {
        // Arrange
        var chunks = new[]
        {
            "[BLOK_001] ",
            "Yapay zeka modelleri ",
            "canlı akış ile ",
            "hızlı yanıt üretir. ",
            "[/BLOK_001]"
        };
        var client = new StreamingMockChatClient(chunks);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        using var activitySource = new ActivitySource("Glossator.Tests.StreamingHelper");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.StreamingHelper",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var testActivity = activitySource.StartActivity("TestStreamingActivity");
        Assert.NotNull(testActivity);

        var progressLogs = new List<PipelineStepLog>();

        // Act
        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            messages,
            stageName: "TranslatorTest",
            attempt: 1,
            activity: testActivity,
            stepLogger: log => progressLogs.Add(log));

        // Assert
        var expectedText = string.Concat(chunks);
        Assert.Equal(expectedText, result.Text);
        Assert.True(result.TimeToFirstTokenMs >= 0);
        Assert.True(result.TokensPerSecond > 0);
        Assert.True(result.OutputTokens > 0);
        Assert.NotEmpty(progressLogs);
        Assert.All(progressLogs, log => Assert.True(log.IsTransient));

        Assert.Equal(true, testActivity.GetTagItem("gen_ai.response.streaming"));
        Assert.NotNull(testActivity.GetTagItem("gen_ai.response.ttft_ms"));
        Assert.NotNull(testActivity.GetTagItem("gen_ai.response.tps"));
    }

    [Fact]
    public async Task StreamResponseAsync_WithMultipleCumulativeUsageChunks_DoesNotSumUsage()
    {
        // Arrange: 5 chunk, her biri kümülatif olarak 2100 input token bildiriyor
        var updates = new List<ChatResponseUpdate>();
        for (int i = 0; i < 5; i++)
        {
            var update = new ChatResponseUpdate(ChatRole.Assistant, $"chunk{i} ");
            update.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = 2100,
                OutputTokenCount = (i + 1) * 50,
                TotalTokenCount = 2100 + (i + 1) * 50
            }));
            updates.Add(update);
        }

        var client = new CustomUpdatesChatClient(updates);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Usage test") };

        using var activitySource = new ActivitySource("Glossator.Tests.Cumulative");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.Cumulative",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var testActivity = activitySource.StartActivity("TestUsageActivity");
        Assert.NotNull(testActivity);

        // Act
        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            messages,
            stageName: "UsageTest",
            activity: testActivity);

        // Assert
        Assert.NotNull(result.Response?.Usage);
        Assert.Equal(2100, result.Response.Usage.InputTokenCount);
        Assert.Equal(250, result.BilledOutputTokens);
        Assert.Equal(2350, result.Response.Usage.TotalTokenCount);

        Assert.Equal(2100L, testActivity.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(250L, testActivity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(2350L, testActivity.GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Fact]
    public async Task StreamResponseAsync_WithExplicitReasoningTokenCount_ComputesVisibleVsBilledTokens()
    {
        // Arrange
        var usage = new UsageDetails
        {
            InputTokenCount = 500,
            OutputTokenCount = 820,
            TotalTokenCount = 1320,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["OutputTokenDetails.ReasoningTokenCount"] = 700
            }
        };
        var update = new ChatResponseUpdate(ChatRole.Assistant, "Visible reply text.");
        update.Contents.Add(new UsageContent(usage));
        var client = new CustomUpdatesChatClient([update]);

        using var activitySource = new ActivitySource("Glossator.Tests.Reasoning");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.Reasoning",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("ReasoningActivity");
        Assert.NotNull(activity);

        // Act
        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            [new ChatMessage(ChatRole.User, "Reason")],
            stageName: "Reviewer",
            activity: activity);

        // Assert
        Assert.Equal(820, result.BilledOutputTokens);
        Assert.Equal(120, result.OutputTokens); // 820 - 700 reasoning
        Assert.Equal(700L, activity.GetTagItem("gen_ai.usage.reasoning_tokens"));
        Assert.Equal(120L, activity.GetTagItem("gen_ai.usage.text_tokens"));
        Assert.Equal(820L, activity.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Theory]
    [InlineData(100, 100, 1, "Fatura:")]
    [InlineData(100, 350, 2, "Fatura:")]
    public void StreamingChatResult_FormatSummary_FormatsBilledAndAttemptConditionally(
        long outputTokens,
        long billedTokens,
        int attempt,
        string billedMarker)
    {
        // Arrange
        var result = new StreamingChatResult(null, "text", 250, 42.5, outputTokens, billedTokens, 50.0);

        // Act
        var summary = result.FormatSummary(attempt);

        // Assert
        if (billedTokens - outputTokens > 100)
            Assert.Contains(billedMarker, summary);
        else
            Assert.DoesNotContain(billedMarker, summary);

        if (attempt > 1)
            Assert.Contains($"Deneme: {attempt}", summary);
        else
            Assert.DoesNotContain("Deneme:", summary);
    }

    [Fact]
    public async Task StreamResponseAsync_WhenStreamingNotImplemented_FallsBackGracefullyToGetResponseAsync()
    {
        // Arrange
        const string expectedText = "[BLOK_001] Bu bir yedek yanıt metnidir. [/BLOK_001]";
        var client = new NonStreamingOnlyChatClient(expectedText);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Fallback Test") };

        // Act
        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            messages,
            stageName: "FallbackTest",
            attempt: 1);

        // Assert
        Assert.Equal(expectedText, result.Text);
        Assert.NotNull(result.Response);
        Assert.True(result.OutputTokens >= 1);
        Assert.True(result.OverallThroughput > 0);
    }

    [Fact]
    public async Task StreamResponseAsync_WhenCancelledMidStream_PropagatesOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var client = new CancelAfterFirstChunkClient(cts);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await StreamingChatHelper.StreamResponseAsync(
                client,
                [new ChatMessage(ChatRole.User, "Hi")],
                stageName: "CancelTest",
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task StreamResponseAsync_ThrowsOnNullClientOrMessages()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StreamingChatHelper.StreamResponseAsync(null!, [new(ChatRole.User, "Hi")], "Test"));

        var client = new StreamingMockChatClient(["ok"]);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            StreamingChatHelper.StreamResponseAsync(client, null!, "Test"));
    }

    private sealed class CancelAfterFirstChunkClient(CancellationTokenSource cts) : IChatClient
    {
        public ChatClientMetadata Metadata => new("CancelMidStream");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first ");
            cts.Cancel();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task StreamResponseAsync_WithServerZeroReasoningTokenCount_UsesServerOutputTokensInsteadOfCharsHeuristic()
    {
        GlossatorDiagnostics.ResetSessionStats();

        // Kısa/yoğun Türkçe yanıt (~40 krk -> chars/4=10, ancak sunucu 25 çıktı + 0 reasoning bildiriyor)
        var usage = new UsageDetails
        {
            InputTokenCount = 8027,
            OutputTokenCount = 25,
            ReasoningTokenCount = 0,
            TotalTokenCount = 8052
        };
        var update = new ChatResponseUpdate(ChatRole.Assistant, "Kısa Türkçe metin.");
        update.Contents.Add(new UsageContent(usage));
        var client = new CustomUpdatesChatClient([update]);

        using var activitySource = new ActivitySource("Glossator.Tests.ZeroReasoning");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.ZeroReasoning",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("ZeroReasoningActivity");
        Assert.NotNull(activity);

        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            [new ChatMessage(ChatRole.User, "Translate")],
            stageName: "Translator",
            activity: activity);

        Assert.Equal(8027, result.InputTokens);
        Assert.Equal(25, result.OutputTokens);
        Assert.Equal(25, result.BilledOutputTokens);
        Assert.Equal(0, result.ReasoningTokens);
        Assert.Equal(0L, activity.GetTagItem("gen_ai.usage.reasoning_tokens"));
        Assert.Equal(25L, activity.GetTagItem("gen_ai.usage.text_tokens"));
        Assert.Equal(25L, activity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(0, GlossatorDiagnostics.GetStageStatsSnapshot()["Translator"].ReasoningTokens);
    }

    [Fact]
    public async Task StreamResponseAsync_WithFirstClassReasoningAndCachedInputTokens_SeparatesTtfcFromTtftAndRecordsCacheHit()
    {
        GlossatorDiagnostics.ResetSessionStats();

        var client = new ReasoningDelayThenTextClient(
            preTextDelayMs: 65,
            finalUsage: new UsageDetails
            {
                InputTokenCount = 10276,
                CachedInputTokenCount = 3072,
                OutputTokenCount = 553,
                ReasoningTokenCount = 304,
                TotalTokenCount = 10829
            });

        using var activitySource = new ActivitySource("Glossator.Tests.CacheAndTtfc");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.CacheAndTtfc",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("CacheAndTtfcActivity");
        Assert.NotNull(activity);

        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            [new ChatMessage(ChatRole.User, "Review")],
            stageName: "Reviewer",
            activity: activity);

        Assert.Equal(10276, result.InputTokens);
        Assert.Equal(3072, result.CachedInputTokens);
        Assert.Equal(304, result.ReasoningTokens);
        Assert.Equal(249, result.OutputTokens);
        Assert.Equal(553, result.BilledOutputTokens);
        Assert.True(result.TimeToFirstChunkMs >= 0);
        Assert.True(result.TimeToFirstTokenMs > result.TimeToFirstChunkMs,
            $"Expected visible TTFT ({result.TimeToFirstTokenMs}ms) > first reasoning chunk TTFC ({result.TimeToFirstChunkMs}ms)");
        Assert.Equal(3072L, activity.GetTagItem("gen_ai.usage.cache_read_tokens"));
        Assert.Equal(304L, activity.GetTagItem("gen_ai.usage.reasoning_tokens"));
        Assert.Contains("Cache: 3", result.FormatSummary());

        var stats = GlossatorDiagnostics.GetStageStatsSnapshot()["Reviewer"];
        Assert.Equal(3072, stats.CachedInputTokens);
        Assert.Equal(304, stats.ReasoningTokens);
        Assert.Equal(249, stats.VisibleOutputTokens);
    }

    [Fact]
    public async Task StreamResponseAsync_WithResilience503Retry_ExcludesRetryBackoffFromActiveModelTtft()
    {
        var attemptCount = 0;
        var transientClient = new TransientThenSuccessStreamingClient(
            onAttempt: attempt =>
            {
                attemptCount = attempt;
                if (attempt == 1)
                {
                    throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
                }
            });

        var resilientClient = new ResilienceChatClient(
            transientClient,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(200));

        using var activitySource = new ActivitySource("Glossator.Tests.RetryTtft");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests.RetryTtft",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("RetryTtftActivity");
        Assert.NotNull(activity);

        var result = await StreamingChatHelper.StreamResponseAsync(
            resilientClient,
            [new ChatMessage(ChatRole.User, "Retry TTFT")],
            stageName: "Translator",
            activity: activity);

        var retryWaitMs = Convert.ToInt64(activity.GetTagItem("gen_ai.retry.wait_ms"));
        var e2eTtftMs = Convert.ToInt64(activity.GetTagItem("gen_ai.response.e2e_ttft_ms"));

        Assert.Equal(2, attemptCount);
        Assert.Equal(1, activity.GetTagItem("gen_ai.retry.count"));
        Assert.True(retryWaitMs >= 160, $"Expected retry wait >=160ms, observed {retryWaitMs}ms");
        Assert.True(e2eTtftMs >= retryWaitMs, $"Expected e2e TTFT ({e2eTtftMs}ms) >= retry wait ({retryWaitMs}ms)");
        Assert.True(result.TimeToFirstTokenMs + 80 <= e2eTtftMs,
            $"Expected active TTFT ({result.TimeToFirstTokenMs}ms) to exclude 200ms retry sleep from e2e TTFT ({e2eTtftMs}ms)");
    }

    private sealed class ReasoningDelayThenTextClient(int preTextDelayMs, UsageDetails finalUsage) : IChatClient
    {
        public ChatClientMetadata Metadata => new("ReasoningDelayMock");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty);
            await Task.Delay(preTextDelayMs, cancellationToken);

            var final = new ChatResponseUpdate(ChatRole.Assistant, "{\"isApproved\": true, \"patches\": []}");
            final.Contents.Add(new UsageContent(finalUsage));
            yield return final;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TransientThenSuccessStreamingClient(Action<int> onAttempt) : IChatClient
    {
        private int _attempt;
        public ChatClientMetadata Metadata => new("TransientRetryMock");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var current = ++_attempt;
            onAttempt(current);
            return StreamSuccessAsync(cancellationToken);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> StreamSuccessAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "[BLOK_001] Tamam. [/BLOK_001]");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
