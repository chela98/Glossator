using System.Diagnostics;
using System.Runtime.CompilerServices;
using Glossator.AI.Diagnostics;
using Glossator.AI.Models;
using Glossator.AI.Streaming;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

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
        var client = new StreamingMockChatClient(chunks, delayMs: 1);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        using var activitySource = new ActivitySource("Glossator.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var testActivity = activitySource.StartActivity("TestStreamingActivity");

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
        Assert.NotNull(result);
        var expectedText = string.Concat(chunks);
        Assert.Equal(expectedText, result.Text);
        Assert.True(result.TimeToFirstTokenMs >= 0, "TTFT should be non-negative");
        Assert.True(result.TokensPerSecond >= 0, "TPS should be non-negative");
        Assert.True(result.OutputTokens > 0, "Output tokens should be greater than zero");
        Assert.NotEmpty(progressLogs);
        Assert.All(progressLogs, log => Assert.True(log.IsTransient, "Intermediate streaming progress logs must be marked as IsTransient"));

        if (testActivity != null)
        {
            var streamingTag = testActivity.GetTagItem("gen_ai.response.streaming");
            Assert.Equal(true, streamingTag);
            Assert.NotNull(testActivity.GetTagItem("gen_ai.response.ttft_ms"));
            Assert.NotNull(testActivity.GetTagItem("gen_ai.response.tps"));
        }
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

        using var activitySource = new ActivitySource("Glossator.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Glossator.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var testActivity = activitySource.StartActivity("TestUsageActivity");

        // Act
        var result = await StreamingChatHelper.StreamResponseAsync(
            client,
            messages,
            stageName: "UsageTest",
            activity: testActivity);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Response?.Usage);
        // Eskiden 5 * 2100 = 10500 oluyordu. Artık son chunk'ın gerçek kümülatif değeri (2100) korunmalı!
        Assert.Equal(2100, result.Response.Usage.InputTokenCount);
        Assert.Equal(250, result.BilledOutputTokens);
        Assert.Equal(2350, result.Response.Usage.TotalTokenCount);

        if (testActivity != null)
        {
            Assert.Equal(2100L, testActivity.GetTagItem("gen_ai.usage.input_tokens"));
            Assert.Equal(250L, testActivity.GetTagItem("gen_ai.usage.output_tokens"));
            Assert.Equal(2350L, testActivity.GetTagItem("gen_ai.usage.total_tokens"));
        }
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
        Assert.NotNull(result);
        Assert.Equal(expectedText, result.Text);
        Assert.NotNull(result.Response);
        Assert.True(result.TimeToFirstTokenMs >= 0);
        Assert.True(result.TokensPerSecond >= 0);
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
}
