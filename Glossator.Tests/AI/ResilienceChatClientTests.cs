using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using Glossator.AI.Clients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glossator.Tests.AI;

public class ResilienceChatClientTests
{
    private sealed class MockChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _handler;
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? _streamingHandler;

        public MockChatClient(
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler,
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? streamingHandler = null)
        {
            _handler = handler;
            _streamingHandler = streamingHandler;
        }

        public ChatClientMetadata Metadata => new("Mock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return _handler(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_streamingHandler != null)
            {
                return _streamingHandler(messages, options, cancellationToken);
            }
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task GetResponseAsync_WhenTransient429Occurs_RetriesAndSucceeds()
    {
        // Arrange
        var attemptCount = 0;
        var inner = new MockChatClient((msgs, opts, ct) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                throw new InvalidOperationException("API Rate limit (429): Quota exceeded");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Başarılı Çeviri")));
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        // Assert
        Assert.Equal(2, attemptCount);
        Assert.Equal("Başarılı Çeviri", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WhenNonTransientErrorOccurs_ThrowsImmediatelyWithoutRetries()
    {
        // Arrange
        var attemptCount = 0;
        var inner = new MockChatClient((msgs, opts, ct) =>
        {
            attemptCount++;
            throw new ArgumentException("Invalid arguments provided");
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal(1, attemptCount);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    public async Task GetResponseAsync_WhenHttpRequestExceptionHasNonTransientStatusCode_ThrowsWithoutRetries(
        System.Net.HttpStatusCode statusCode)
    {
        // Arrange
        var attemptCount = 0;
        var inner = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            throw new HttpRequestException($"HTTP {(int)statusCode}", null, statusCode);
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task GetResponseAsync_WhenHttpRequestExceptionHasNoStatusCode_RetriesSocketFailure()
    {
        // Arrange
        var attemptCount = 0;
        var inner = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                throw new HttpRequestException("Name or service not known");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered")));
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        // Assert
        Assert.Equal(2, attemptCount);
        Assert.Equal("Recovered", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WhenCancelled_ThrowsOperationCanceledExceptionWithoutRetry()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var attemptCount = 0;

        var inner = new MockChatClient((msgs, opts, ct) =>
        {
            attemptCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], cancellationToken: cts.Token));
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenTransient429Occurs_RetriesAndSucceeds()
    {
        var attemptCount = 0;
        var inner = new MockChatClient(
            handler: (_, _, _) => Task.FromResult(new ChatResponse([])),
            streamingHandler: (msgs, opts, ct) =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    throw new InvalidOperationException("API Rate limit (429): Quota exceeded");
                }

                async IAsyncEnumerable<ChatResponseUpdate> Stream()
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "Akış Başarılı");
                }

                return Stream();
            });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")]))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("Akış Başarılı", updates[0].Text);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task GetResponseAsync_WhenTransientErrorsExceedMaxRetries_ThrowsUnderlyingException()
    {
        var attemptCount = 0;
        var inner = new MockChatClient((msgs, opts, ct) =>
        {
            attemptCount++;
            throw new HttpRequestException("503 Service Unavailable");
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Contains("503", ex.Message);
        Assert.Equal(3, attemptCount); // maxRetries = 3 kez denendi ve fırlatıldı
    }

    [Fact]
    public async Task GetResponseAsync_WhenTimeoutOrHttp429StatusOccurs_RetriesAndSucceeds()
    {
        // Arrange
        var attemptCount = 0;
        var inner = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            if (attemptCount == 1)
                throw new TimeoutException("Socket read timed out");
            if (attemptCount == 2)
                throw new HttpRequestException("Rate limit", null, System.Net.HttpStatusCode.TooManyRequests);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        });

        var client = new ResilienceChatClient(
            inner,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Ping")]);

        // Assert
        Assert.Equal(3, attemptCount);
        Assert.Equal("OK", response.Text);
    }

    [Fact]
    public async Task OpenTelemetryPipeline_WithResilienceChatClient_CapturesExactSingleGeminiUsage()
    {
        // Arrange
        var capturedActivities = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name.Contains("Microsoft.Extensions.AI"),
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => capturedActivities.Add(act)
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var inner = new MockStreamingUsageChatClient();
        var pipeline = inner
            .AsBuilder()
            .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
            .Use(c => new ResilienceChatClient(c, NullLogger<ResilienceChatClient>.Instance))
            .Build();

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in pipeline.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Test")]))
        {
            updates.Add(u);
        }

        // Assert: 3 içerik parçası (UsageContent sıyrılmış) + 1 normalize edilmiş final UsageContent chunk'ı = 4
        Assert.Equal(4, updates.Count);
        Assert.All(updates.Take(3), u => Assert.DoesNotContain(u.Contents, c => c is UsageContent));
        var finalUsage = Assert.IsType<UsageContent>(Assert.Single(updates[3].Contents));
        Assert.Equal(300L, finalUsage.Details.AdditionalCounts!["OutputTokenDetails.ReasoningTokenCount"]);
        Assert.NotEmpty(capturedActivities);
        var otelSpan = capturedActivities.Last();
        Assert.Equal(1000L, Convert.ToInt64(otelSpan.GetTagItem("gen_ai.usage.input_tokens")));
        Assert.Equal(450L, Convert.ToInt64(otelSpan.GetTagItem("gen_ai.usage.output_tokens")));
    }

    private sealed class MockStreamingUsageChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("MockGemini", new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"), "gemini-2.5-flash");
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            for (int i = 1; i <= 3; i++)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = "gemini-2.5-flash",
                    Contents =
                    [
                        new TextContent($"Parça {i} "),
                        new UsageContent(new UsageDetails
                        {
                            InputTokenCount = 1000,
                            OutputTokenCount = i * 50,
                            TotalTokenCount = 1000 + (i * 50) + 300
                        })
                    ]
                };
            }
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenHttp502BadGatewayOccurs_RetriesAndSucceeds()
    {
        var attemptCount = 0;
        var mockClient = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                throw new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "502 Sonrası Başarılı")));
        });

        var client = new ResilienceChatClient(
            mockClient,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Test")]);

        Assert.Equal("502 Sonrası Başarılı", response.Text);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task GetResponseAsync_WhenTaskCanceledWrapsTimeoutWithoutCallerCancel_RetriesAndSucceeds()
    {
        var attemptCount = 0;
        var mockClient = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                throw new TaskCanceledException("HttpClient network timeout", new TimeoutException("Request timed out"));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Timeout Sonrası Başarılı")));
        });

        var client = new ResilienceChatClient(
            mockClient,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Test")]);

        Assert.Equal("Timeout Sonrası Başarılı", response.Text);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task GetResponseAsync_WhenClientResultExceptionHasRetryAfterMs_HonorsHeaderAndRetries()
    {
        var attemptCount = 0;
        var mockClient = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                var raw = new MockPipelineResponse(429, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["retry-after-ms"] = "75"
                });
                throw new ClientResultException("Too Many Requests", raw);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Retry-After Sonrası Başarılı")));
        });

        var client = new ResilienceChatClient(
            mockClient,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        var sw = Stopwatch.StartNew();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Test")]);
        sw.Stop();

        Assert.Equal("Retry-After Sonrası Başarılı", response.Text);
        Assert.Equal(2, attemptCount);
        Assert.True(sw.ElapsedMilliseconds >= 55, $"Expected Retry-After delay (>=55ms), observed {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GetResponseAsync_WhenClientResult400HasQuotaText_ThrowsWithoutRetrying()
    {
        var attemptCount = 0;
        var mockClient = new MockChatClient((_, _, _) =>
        {
            attemptCount++;
            var raw = new MockPipelineResponse(400);
            throw new ClientResultException("Invalid prompt: Quota word mentioned in BadRequest", raw);
        });

        var client = new ResilienceChatClient(
            mockClient,
            NullLogger<ResilienceChatClient>.Instance,
            maxRetries: 3,
            initialDelay: TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAsync<ClientResultException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Test")]));

        Assert.Equal(1, attemptCount);
    }

    private sealed class MockPipelineResponse(int status, Dictionary<string, string>? headers = null) : PipelineResponse
    {
        private readonly MockPipelineResponseHeaders _headers = new(headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        public override int Status => status;
        public override string ReasonPhrase => string.Empty;
        protected override PipelineResponseHeaders HeadersCore => _headers;
        public override BinaryData Content => BinaryData.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);
        public override void Dispose() { }
    }

    private sealed class MockPipelineResponseHeaders(Dictionary<string, string> headers) : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => headers.GetEnumerator();
        public override bool TryGetValue(string name, out string? value) => headers.TryGetValue(name, out value);
        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            if (headers.TryGetValue(name, out var v))
            {
                values = [v];
                return true;
            }
            values = null;
            return false;
        }
    }
}
