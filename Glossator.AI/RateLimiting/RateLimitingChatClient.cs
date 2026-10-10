using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;

namespace Glossator.AI.RateLimiting;

/// <summary>
/// Tüm worker'ların LLM çağrılarını merkezi bir TokenBucketRateLimiter altında denetleyen
/// ve API kota aşımlarını proaktif olarak engelleyen ara yazılım istemcisi.
/// </summary>
public sealed class RateLimitingChatClient(
    IChatClient innerClient,
    RateLimiter rateLimiter) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("Merkezi RateLimiter kuyruğundan izin alınamadı (İstek reddedildi).");
        }

        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
}

public static class RateLimitingChatClientBuilderExtensions
{
    /// <summary>
    /// ChatClientBuilder boru hattına merkezi RateLimiter ara yazılımını bağlar.
    /// </summary>
    public static ChatClientBuilder UseRateLimiting(this ChatClientBuilder builder, RateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(rateLimiter);

        return builder.Use(inner => new RateLimitingChatClient(inner, rateLimiter));
    }
}
