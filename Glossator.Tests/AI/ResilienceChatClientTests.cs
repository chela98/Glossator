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

        public MockChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        {
            _handler = handler;
        }

        public ChatClientMetadata Metadata => new("Mock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return _handler(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
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
}
