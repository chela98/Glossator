using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Glossator.AI.Configuration;
using Glossator.AI.Extensions;

namespace Glossator.AI.Services;

/// <summary>
/// OpenAI uyumlu sağlayıcı uç noktalarına ping atar ve mevcut modelleri otomatik listeler.
/// </summary>
public interface IProviderDiscoveryService
{
    /// <summary>
    /// Belirtilen sağlayıcı uç noktasının erişilebilirliğini, anahtar geçerliliğini ve gecikme süresini (ms) ölçer.
    /// <c>GET /models</c> başarılıysa keşfedilen modelleri de sonuç içinde döndürür.
    /// </summary>
    Task<ProviderPingResult> PingAsync(
        string endpoint,
        string? apiKey,
        string? defaultModel = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sağlayıcının <c>GET /models</c> uç noktasından kullanılabilir model kimliklerini çeker.
    /// </summary>
    Task<ProviderModelsResult> FetchModelsAsync(
        string endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sağlayıcı bağlantı / ping testi sonucu.
/// </summary>
public sealed record ProviderPingResult(
    bool Success,
    int? StatusCode,
    long LatencyMs,
    string Message,
    IReadOnlyList<string> DiscoveredModels,
    bool UsedChatProbeFallback = false);

/// <summary>
/// Sağlayıcı model keşfi (<c>/models</c>) sonucu.
/// </summary>
public sealed record ProviderModelsResult(
    bool Success,
    int? StatusCode,
    long LatencyMs,
    IReadOnlyList<string> Models,
    string Message);

/// <summary>
/// OpenAI uyumlu HTTP uç noktaları (OpenAI, Gemini, Ollama, OpenRouter, vLLM vb.) için
/// hafif ping ve <c>/models</c> keşif servisi.
/// </summary>
public sealed class ProviderDiscoveryService : IProviderDiscoveryService, IDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ProviderDiscoveryService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            })
            {
                Timeout = DefaultRequestTimeout
            };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<ProviderPingResult> PingAsync(
        string endpoint,
        string? apiKey,
        string? defaultModel = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateInputs(endpoint, apiKey, out var baseUri, out var effectiveKey, out var validationError))
        {
            return new ProviderPingResult(false, null, 0, validationError, []);
        }

        try
        {
            var modelsResponse = await SendModelsGetAsync(baseUri!, effectiveKey, cancellationToken).ConfigureAwait(false);
            if (modelsResponse.IsSuccess)
            {
                var models = ParseModelIds(modelsResponse.Body);
                var message = models.Count > 0
                    ? $"Bağlandı ({modelsResponse.LatencyMs} ms · {models.Count} model)"
                    : $"Bağlandı ({modelsResponse.LatencyMs} ms)";

                return new ProviderPingResult(true, modelsResponse.StatusCode, modelsResponse.LatencyMs, message, models);
            }

            if (modelsResponse.StatusCode is (int)HttpStatusCode.NotFound
                or (int)HttpStatusCode.MethodNotAllowed
                or (int)HttpStatusCode.NotImplemented)
            {
                if (string.IsNullOrWhiteSpace(defaultModel))
                {
                    return new ProviderPingResult(
                        false,
                        modelsResponse.StatusCode,
                        modelsResponse.LatencyMs,
                        "Sağlayıcı /models listesini desteklemiyor (404). Ping testi için Varsayılan Model girin.",
                        []);
                }

                return await ProbeChatCompletionAsync(baseUri!, effectiveKey, defaultModel.Trim(), cancellationToken).ConfigureAwait(false);
            }

            return new ProviderPingResult(
                false,
                modelsResponse.StatusCode,
                modelsResponse.LatencyMs,
                FormatHttpError(modelsResponse.StatusCode, modelsResponse.Body),
                []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderPingResult(false, null, 0, FormatTransportError(ex), []);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderModelsResult> FetchModelsAsync(
        string endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateInputs(endpoint, apiKey, out var baseUri, out var effectiveKey, out var validationError))
        {
            return new ProviderModelsResult(false, null, 0, [], validationError);
        }

        try
        {
            var response = await SendModelsGetAsync(baseUri!, effectiveKey, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                var errorMessage = response.StatusCode is (int)HttpStatusCode.NotFound
                    or (int)HttpStatusCode.MethodNotAllowed
                    or (int)HttpStatusCode.NotImplemented
                    ? $"Bu sağlayıcı otomatik model listelemeyi (/models) desteklemiyor ({response.StatusCode}). Model adını elle yazabilirsiniz."
                    : FormatHttpError(response.StatusCode, response.Body);

                return new ProviderModelsResult(false, response.StatusCode, response.LatencyMs, [], errorMessage);
            }

            var models = ParseModelIds(response.Body);
            var message = models.Count > 0
                ? $"{models.Count} model çekildi ({response.LatencyMs} ms)."
                : $"Sağlayıcı yanıt verdi ({response.LatencyMs} ms) ancak model listesi boş döndü.";

            return new ProviderModelsResult(true, response.StatusCode, response.LatencyMs, models, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderModelsResult(false, null, 0, [], FormatTransportError(ex));
        }
    }

    internal static IReadOnlyList<string> ParseModelIds(string jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;
        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
            {
                CollectModelArray(dataArray, uniqueIds);
            }
            else if (root.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
            {
                CollectModelArray(modelsArray, uniqueIds);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            CollectModelArray(root, uniqueIds);
        }

        return uniqueIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectModelArray(JsonElement arrayElement, HashSet<string> sink)
    {
        foreach (var item in arrayElement.EnumerateArray())
        {
            string? rawId = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String => idProp.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String => nameProp.GetString(),
                _ => null
            };

            var normalized = NormalizeModelId(rawId);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                sink.Add(normalized);
            }
        }
    }

    private static string? NormalizeModelId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        var trimmed = rawId.Trim();
        const string modelsPrefix = "models/";
        return trimmed.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[modelsPrefix.Length..]
            : trimmed;
    }

    private static bool TryValidateInputs(
        string? endpoint,
        string? apiKey,
        out Uri? normalizedBaseUri,
        out string effectiveKey,
        out string errorMessage)
    {
        normalizedBaseUri = null;
        effectiveKey = string.Empty;
        errorMessage = string.Empty;

        var trimmedEndpoint = endpoint?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmedEndpoint) ||
            !Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            errorMessage = "Geçerli bir Endpoint URL'si girin (örn: https://api.openai.com/v1).";
            return false;
        }

        if (!GlossatorAiOptions.IsLocalEndpoint(trimmedEndpoint) &&
            (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = "API anahtarı eksik veya örnek değer ('YOUR_...') içeriyor.";
            return false;
        }

        normalizedBaseUri = new Uri(trimmedEndpoint + "/");
        effectiveKey = AiServiceExtensions.ResolveEffectiveApiKey(apiKey, trimmedEndpoint);
        return true;
    }

    private async Task<HttpProbeResponse> SendModelsGetAsync(
        Uri baseUri,
        string effectiveKey,
        CancellationToken cancellationToken)
    {
        var targetUri = new Uri(baseUri, "models");
        using var request = CreateJsonRequest(HttpMethod.Get, targetUri, effectiveKey);
        return await ExecuteTimedRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderPingResult> ProbeChatCompletionAsync(
        Uri baseUri,
        string effectiveKey,
        string modelId,
        CancellationToken cancellationToken)
    {
        var targetUri = new Uri(baseUri, "chat/completions");
        var response = await SendChatProbePayloadAsync(targetUri, effectiveKey, modelId, useCompletionTokensField: false, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == (int)HttpStatusCode.BadRequest &&
            response.Body.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
        {
            response = await SendChatProbePayloadAsync(targetUri, effectiveKey, modelId, useCompletionTokensField: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (response.IsSuccess)
        {
            return new ProviderPingResult(
                true,
                response.StatusCode,
                response.LatencyMs,
                $"Bağlandı · Chat testi OK ({response.LatencyMs} ms · /models kapalı)",
                [],
                UsedChatProbeFallback: true);
        }

        return new ProviderPingResult(
            false,
            response.StatusCode,
            response.LatencyMs,
            FormatHttpError(response.StatusCode, response.Body),
            [],
            UsedChatProbeFallback: true);
    }

    private async Task<HttpProbeResponse> SendChatProbePayloadAsync(
        Uri targetUri,
        string effectiveKey,
        string modelId,
        bool useCompletionTokensField,
        CancellationToken cancellationToken)
    {
        var payload = useCompletionTokensField
            ? new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["messages"] = new[] { new { role = "user", content = "ping" } },
                ["max_completion_tokens"] = 1
            }
            : new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["messages"] = new[] { new { role = "user", content = "ping" } },
                ["max_tokens"] = 1
            };

        using var request = CreateJsonRequest(HttpMethod.Post, targetUri, effectiveKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await ExecuteTimedRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri targetUri, string effectiveKey)
    {
        var request = new HttpRequestMessage(method, targetUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(effectiveKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveKey);
        }

        return request;
    }

    private async Task<HttpProbeResponse> ExecuteTimedRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return new HttpProbeResponse((int)response.StatusCode, response.IsSuccessStatusCode, sw.ElapsedMilliseconds, body);
    }

    private static string FormatHttpError(int statusCode, string body)
    {
        if (statusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return $"Yetkilendirme başarısız ({statusCode}): API anahtarını kontrol edin.";
        }

        if (statusCode == (int)HttpStatusCode.NotFound)
        {
            return "Uç nokta bulunamadı (404): URL sonunda '/v1' olup olmadığını kontrol edin.";
        }

        var detail = ExtractErrorMessage(body);
        return string.IsNullOrWhiteSpace(detail)
            ? $"HTTP {statusCode} hatası döndü."
            : $"HTTP {statusCode}: {detail}";
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var errorObj))
                {
                    if (errorObj.ValueKind == JsonValueKind.Object &&
                        errorObj.TryGetProperty("message", out var msgProp) &&
                        msgProp.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(msgProp.GetString(), 140);
                    }

                    if (errorObj.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(errorObj.GetString(), 140);
                    }
                }

                if (doc.RootElement.TryGetProperty("message", out var topMsg) &&
                    topMsg.ValueKind == JsonValueKind.String)
                {
                    return Truncate(topMsg.GetString(), 140);
                }
            }
        }
        catch (JsonException)
        {
            // Yanıt JSON değilse ham metnin kısa bir özetini döndür
        }

        return Truncate(body, 140);
    }

    private static string FormatTransportError(Exception ex) => ex switch
    {
        TaskCanceledException => "Bağlantı zaman aşımına uğradı.",
        JsonException => "Sağlayıcı geçerli bir OpenAI JSON yanıtı döndürmedi.",
        HttpRequestException httpEx => $"Bağlantı kurulamadı: {Truncate(httpEx.InnerException?.Message ?? httpEx.Message, 140)}",
        _ => $"Beklenmeyen hata: {Truncate(ex.Message, 140)}"
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength - 1), "…");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record HttpProbeResponse(int StatusCode, bool IsSuccess, long LatencyMs, string Body);
}
