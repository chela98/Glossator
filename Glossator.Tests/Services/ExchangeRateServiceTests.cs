using System.Net;
using System.Text;
using Glossator.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glossator.Tests.Services;

public class ExchangeRateServiceTests
{
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public List<Uri?> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            lock (RequestedUris)
            {
                RequestedUris.Add(request.RequestUri);
            }
            return handler(request, cancellationToken);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
        new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenPrimaryFrankfurterSucceeds_ReturnsLiveQuoteWithoutCallingBackup()
    {
        // Arrange
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("frankfurter", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    """{"amount":1.0,"base":"USD","date":"2026-11-13","rates":{"TRY":42.3456}}"""));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.InternalServerError, "{}"));
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act
        var quote = await service.GetUsdToTryRateAsync();

        // Assert
        Assert.True(quote.IsLive);
        Assert.Equal("Frankfurter (ECB)", quote.Source);
        Assert.Equal(42.3456, quote.Rate, precision: 4);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("frankfurter", handler.RequestedUris.Single()!.Host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenPrimaryFailsAndBackupSucceeds_FallsBackToExchangeRateApi()
    {
        // Arrange
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("frankfurter", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "upstream down"));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"result":"success","base_code":"USD","rates":{"TRY":41.952}}"""));
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act
        var quote = await service.GetUsdToTryRateAsync();

        // Assert
        Assert.True(quote.IsLive);
        Assert.Equal("ExchangeRate-API", quote.Source);
        Assert.Equal(41.952, quote.Rate, precision: 4);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenBackupReturnsErrorResult_UsesConfiguredFallbackRate()
    {
        // Arrange
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("frankfurter", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("Network unreachable");
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"result":"error","error-type":"quota-reached","rates":{"TRY":99.9}}"""));
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act
        var quote = await service.GetUsdToTryRateAsync(fallbackRate: 39.25);

        // Assert
        Assert.False(quote.IsLive);
        Assert.Equal("Yerel Yedek", quote.Source);
        Assert.Equal(39.25, quote.Rate, precision: 4);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rates":{}}""")]
    [InlineData("""{"rates":{"TRY":0}}""")]
    [InlineData("""{"rates":{"TRY":-15.2}}""")]
    [InlineData("""{"rates":{"TRY":"invalid"}}""")]
    [InlineData("not-valid-json")]
    public async Task GetUsdToTryRateAsync_WhenAllProvidersReturnInvalidPayloads_FallsBackSafely(string payload)
    {
        // Arrange
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, payload)));

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act
        var quote = await service.GetUsdToTryRateAsync(fallbackRate: double.NaN);

        // Assert
        Assert.False(quote.IsLive);
        Assert.Equal("Yerel Yedek", quote.Source);
        Assert.Equal(ExchangeRateService.DefaultFallbackRate, quote.Rate, precision: 4);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenPrimaryTimesOut_FailsOverToBackupWithoutThrowing()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri!.Host.Contains("frankfurter", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            return JsonResponse(HttpStatusCode.OK, """{"result":"success","rates":{"TRY":43.10}}""");
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(
            NullLogger<ExchangeRateService>.Instance,
            httpClient,
            requestTimeout: TimeSpan.FromMilliseconds(40));

        // Act
        var quote = await service.GetUsdToTryRateAsync();

        // Assert
        Assert.True(quote.IsLive);
        Assert.Equal("ExchangeRate-API", quote.Source);
        Assert.Equal(43.10, quote.Rate, precision: 4);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenCallerCancels_ThrowsOperationCanceledException()
    {
        // Arrange
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"rates":{"TRY":42.0}}""")));

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetUsdToTryRateAsync(cancellationToken: cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WithCacheTtlAndForceRefresh_MinimizesHttpCallsDeterministically()
    {
        // Arrange
        var fakeTime = new MutableTimeProvider(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));
        double[] sequence = [42.10, 42.80, 43.25];
        var index = 0;

        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var nextRate = sequence[Math.Min(index++, sequence.Length - 1)];
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                System.Text.Json.JsonSerializer.Serialize(new { rates = new { TRY = nextRate } })));
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(
            NullLogger<ExchangeRateService>.Instance,
            httpClient,
            cacheDuration: TimeSpan.FromHours(6),
            timeProvider: fakeTime);

        // Act 1: İlk çağrı API'den çeker
        var first = await service.GetUsdToTryRateAsync();
        // Act 2: TTL süresi dolmadan ikinci çağrı önbellekten döner
        fakeTime.Advance(TimeSpan.FromHours(2));
        var second = await service.GetUsdToTryRateAsync();
        // Act 3: forceRefresh=true önbelleği aşarak güncel değeri alır
        var forced = await service.GetUsdToTryRateAsync(forceRefresh: true);
        // Act 4: TTL dolunca otomatik tazelenir
        fakeTime.Advance(TimeSpan.FromHours(7));
        var expired = await service.GetUsdToTryRateAsync();

        // Assert
        Assert.Equal(42.10, first.Rate, precision: 4);
        Assert.Equal(42.10, second.Rate, precision: 4);
        Assert.Equal(42.80, forced.Rate, precision: 4);
        Assert.Equal(43.25, expired.Rate, precision: 4);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_UnderConcurrentColdCache_FetchesUpstreamOnlyOnce()
    {
        // Arrange
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((_, _) => tcs.Task);

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act: 10 eşzamanlı çağrı başlat, ardından upstream cevabını serbest bırak
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => service.GetUsdToTryRateAsync())
            .ToArray();

        tcs.SetResult(JsonResponse(HttpStatusCode.OK, """{"rates":{"TRY":42.75}}"""));
        var quotes = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, handler.CallCount);
        Assert.All(quotes, q =>
        {
            Assert.True(q.IsLive);
            Assert.Equal(42.75, q.Rate, precision: 4);
        });
    }

    [Fact]
    public async Task GetUsdToTryRateAsync_WhenOfflineThenOnline_DoesNotCacheFailureAndRecoversImmediately()
    {
        // Arrange
        var isOnline = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            if (!isOnline)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "offline"));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"rates":{"TRY":42.90}}"""));
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ExchangeRateService(NullLogger<ExchangeRateService>.Instance, httpClient);

        // Act 1: Çevrimdışı yedek
        var offlineQuote = await service.GetUsdToTryRateAsync(fallbackRate: 39.50);
        // Act 2: Ağ geri geldiğinde hemen canlı kur çekilir
        isOnline = true;
        var onlineQuote = await service.GetUsdToTryRateAsync(fallbackRate: 39.50);

        // Assert
        Assert.False(offlineQuote.IsLive);
        Assert.Equal(39.50, offlineQuote.Rate, precision: 4);
        Assert.True(onlineQuote.IsLive);
        Assert.Equal(42.90, onlineQuote.Rate, precision: 4);
    }
}
