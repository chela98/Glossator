using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Glossator.AI.RateLimiting;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

public class RateLimitingChatClientTests
{
    private sealed class TestChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test Response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test Update");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task GetResponseAsync_WhenPermitAvailable_CallsInnerClient()
    {
        var inner = new TestChatClient();
        var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 5,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

        var client = new RateLimitingChatClient(inner, limiter);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Equal("Test Response", response.Text);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_WhenLimitExhausted_ThrowsInvalidOperationException()
    {
        var inner = new TestChatClient();
        var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        var client = new RateLimitingChatClient(inner, limiter);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };

        // 1. çağrı permit alır
        await client.GetResponseAsync(messages);

        // 2. çağrıda limit biter ve kuyruk 0 olduğu için reddedilir
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(messages));
        Assert.Contains("RateLimiter", ex.Message);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public void BuilderExtension_WiresMiddlewareSuccessfully()
    {
        var inner = new TestChatClient();
        var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

        var client = inner.AsBuilder()
            .UseRateLimiting(limiter)
            .Build();

        Assert.NotNull(client);
    }
}
