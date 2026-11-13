namespace Glossator.Tests.AI;

using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Glossator.AI.RateLimiting;
using Microsoft.Extensions.AI;
using Xunit;

public class RateLimitingChatClientTests
{
    private sealed class TestChatClient : IChatClient
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test Response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test Update");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task GetResponseAsync_WhenPermitAvailable_CallsInnerClient()
    {
        var inner = new TestChatClient();
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 5,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

        using var client = new RateLimitingChatClient(inner, limiter);
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
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        using var client = new RateLimitingChatClient(inner, limiter);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };

        // 1. çağrı permit alır
        await client.GetResponseAsync(messages);

        // 2. çağrıda limit biter ve kuyruk 0 olduğu için reddedilir
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(messages));
        Assert.Contains("RateLimiter", ex.Message);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task BuilderExtension_WiresMiddlewareSuccessfullyAndEnforcesRateLimit()
    {
        // Arrange
        var inner = new TestChatClient();
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });

        using var client = inner.AsBuilder()
            .UseRateLimiting(limiter)
            .Build();

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello Builder")]);

        // Assert
        Assert.Equal("Test Response", response.Text);
        Assert.Equal(1, inner.CallCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second")]));
    }

    [Fact]
    public void UseRateLimiting_WithNullBuilderOrLimiter_ThrowsArgumentNullException()
    {
        // Arrange
        var inner = new TestChatClient();
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1)
        });

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            ((ChatClientBuilder)null!).UseRateLimiting(limiter));
        Assert.Throws<ArgumentNullException>(() =>
            inner.AsBuilder().UseRateLimiting(null!));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenLimitExhausted_ThrowsInvalidOperationException()
    {
        var inner = new TestChatClient();
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        using var client = new RateLimitingChatClient(inner, limiter);
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };

        // 1. stream çağrısı permit alır
        await foreach (var _ in client.GetStreamingResponseAsync(messages)) { }

        // 2. stream çağrısında limit bittiği için fırlatmalıdır
        var stream = client.GetStreamingResponseAsync(messages);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream) { }
        });

        Assert.Contains("RateLimiter", ex.Message);
    }

    [Fact]
    public async Task RateLimiting_UnderConcurrentRequests_ThrottlesProperly()
    {
        var inner = new TestChatClient();
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 5,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0 // Sıfır kuyruk: Limit dolduğunda anında IsAcquired=false döner
        });

        using var client = new RateLimitingChatClient(inner, limiter);

        // 10 istek paralel gönderilir (5 başarılı, 5 reddedilmeli)
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Contention")]);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);
        var successes = results.Count(r => r);
        var rejections = results.Count(r => !r);

        Assert.Equal(5, successes);
        Assert.Equal(5, rejections);
        Assert.Equal(5, inner.CallCount);
    }
}
