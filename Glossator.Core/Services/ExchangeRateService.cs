using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glossator.Core.Services;

/// <summary>
/// Bir USD → TRY döviz kuru sorgusunun sonucunu, veri kaynağını ve canlı API durumunu temsil eder.
/// </summary>
public sealed record ExchangeRateQuote(
    double Rate,
    string Source,
    bool IsLive,
    DateTimeOffset FetchedAtUtc);

/// <summary>
/// Güncel USD → TRY döviz kurunu harici API'lerden önbellekli ve yedekli olarak sağlayan servis sözleşmesi.
/// </summary>
public interface IExchangeRateService
{
    /// <summary>
    /// Güncel USD/TRY kurunu döndürür. Geçerli önbellekteki canlı kur süresi dolmamışsa API'ye istek atmaz;
    /// tüm harici kaynaklar başarısız olursa sağlanan yedek kuru veya varsayılan sabit kuru döndürür.
    /// </summary>
    Task<ExchangeRateQuote> GetUsdToTryRateAsync(
        double? fallbackRate = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Anahtarsız (keyless) kamusal döviz API'lerinden (Frankfurter ECB ve Open ExchangeRate-API)
/// güncel USD/TRY kurunu çeken, bellek içi TTL önbellekli ve yedeklemeli servis.
/// </summary>
public sealed class ExchangeRateService : IExchangeRateService, IDisposable
{
    public const double DefaultFallbackRate = 38.00;
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(4);

    private sealed record RateProvider(string Name, string Url, bool RequireSuccessResult);

    private static readonly RateProvider[] Providers =
    [
        new("Frankfurter (ECB)", "https://api.frankfurter.dev/v1/latest?base=USD&symbols=TRY", RequireSuccessResult: false),
        new("ExchangeRate-API", "https://open.er-api.com/v6/latest/USD", RequireSuccessResult: true)
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<ExchangeRateService> _logger;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private volatile ExchangeRateQuote? _cachedQuote;

    public ExchangeRateService(
        ILogger<ExchangeRateService>? logger = null,
        HttpClient? httpClient = null,
        TimeSpan? cacheDuration = null,
        TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<ExchangeRateService>.Instance;
        _cacheDuration = cacheDuration is { Ticks: > 0 } ? cacheDuration.Value : DefaultCacheDuration;
        _requestTimeout = requestTimeout is { Ticks: > 0 } ? requestTimeout.Value : DefaultRequestTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }
    }

    public async Task<ExchangeRateQuote> GetUsdToTryRateAsync(
        double? fallbackRate = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!forceRefresh && TryGetCachedLiveQuote(out var cached))
        {
            return cached;
        }

        await _fetchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryGetCachedLiveQuote(out cached))
            {
                return cached;
            }

            foreach (var provider in Providers)
            {
                var quote = await TryFetchFromProviderAsync(provider, cancellationToken).ConfigureAwait(false);
                if (quote != null)
                {
                    _cachedQuote = quote;
                    _logger.LogInformation(
                        "Fetched live USD/TRY exchange rate {Rate:F4} from '{Source}'.",
                        quote.Rate,
                        quote.Source);
                    return quote;
                }
            }

            var resolvedFallback = ResolveValidFallback(fallbackRate);
            _logger.LogWarning(
                "All live exchange rate providers failed. Falling back to USD/TRY rate {Rate:F4}.",
                resolvedFallback);

            return new ExchangeRateQuote(
                Rate: resolvedFallback,
                Source: "Yerel Yedek",
                IsLive: false,
                FetchedAtUtc: _timeProvider.GetUtcNow());
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private bool TryGetCachedLiveQuote(out ExchangeRateQuote quote)
    {
        var current = _cachedQuote;
        if (current != null &&
            current.IsLive &&
            (_timeProvider.GetUtcNow() - current.FetchedAtUtc) < _cacheDuration)
        {
            quote = current;
            return true;
        }

        quote = null!;
        return false;
    }

    private async Task<ExchangeRateQuote?> TryFetchFromProviderAsync(
        RateProvider provider,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_requestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, provider.Url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!request.Headers.UserAgent.Any())
            {
                request.Headers.UserAgent.ParseAdd("Glossator/1.0");
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Exchange rate provider '{Provider}' returned HTTP {StatusCode}.",
                    provider.Name,
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(attemptCts.Token)
                .ConfigureAwait(false);

            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: attemptCts.Token)
                .ConfigureAwait(false);

            if (!TryExtractUsdToTryRate(doc.RootElement, provider.RequireSuccessResult, out var rate))
            {
                _logger.LogWarning(
                    "Exchange rate provider '{Provider}' returned an invalid or missing USD/TRY rate.",
                    provider.Name);
                return null;
            }

            return new ExchangeRateQuote(
                Rate: rate,
                Source: provider.Name,
                IsLive: true,
                FetchedAtUtc: _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Exchange rate request to '{Provider}' timed out after {TimeoutMs}ms.",
                provider.Name,
                _requestTimeout.TotalMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch USD/TRY rate from '{Provider}'.", provider.Name);
            return null;
        }
    }

    internal static bool TryExtractUsdToTryRate(JsonElement root, bool requireSuccessResult, out double rate)
    {
        rate = 0;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (requireSuccessResult &&
            root.TryGetProperty("result", out var resultProp) &&
            !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!root.TryGetProperty("rates", out var ratesProp) ||
            ratesProp.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!ratesProp.TryGetProperty("TRY", out var tryProp) ||
            tryProp.ValueKind != JsonValueKind.Number ||
            !tryProp.TryGetDouble(out var parsed))
        {
            return false;
        }

        if (!double.IsFinite(parsed) || parsed <= 0)
        {
            return false;
        }

        rate = parsed;
        return true;
    }

    public static double ResolveValidFallback(double? fallbackRate) =>
        fallbackRate is double value && double.IsFinite(value) && value > 0
            ? value
            : DefaultFallbackRate;

    public void Dispose()
    {
        _fetchLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
