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

    [Fact]
    public async Task GetResponseAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Zaten iptal edilmiş token

        var inner = new MockChatClient((msgs, opts, ct) =>
        {
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
}
