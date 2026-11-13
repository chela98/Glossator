using System.Net;
using System.Text;
using Glossator.AI.Services;
using Xunit;

namespace Glossator.Tests.AI;

public class ProviderDiscoveryServiceTests
{
    [Fact]
    public async Task PingAsync_WhenModelsEndpointSucceeds_ReturnsLatencyAndSortedModels()
    {
        string? capturedAuth = null;
        string? capturedUrl = null;

        var handler = new StubHttpHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedUrl = req.RequestUri?.ToString();
            return JsonResponse(HttpStatusCode.OK, """
                {
                  "object": "list",
                  "data": [
                    { "id": "gpt-4o" },
                    { "id": "gpt-5-mini" },
                    { "id": "gpt-4o" },
                    { "id": "o3-mini" }
                  ]
                }
                """);
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.PingAsync("https://api.openai.com/v1/", "sk-test-key");

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.False(result.UsedChatProbeFallback);
        Assert.Equal("https://api.openai.com/v1/models", capturedUrl);
        Assert.Equal("Bearer sk-test-key", capturedAuth);
        Assert.Equal(["gpt-4o", "gpt-5-mini", "o3-mini"], result.DiscoveredModels);
        Assert.Contains("3 model", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchModelsAsync_NormalizesGeminiModelsPrefixAndOllamaSchema()
    {
        var geminiHandler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.OK, """
            {
              "object": "list",
              "data": [
                { "id": "models/gemini-3.8-flash" },
                { "id": "models/gemini-2.5-flash" },
                { "id": "gemini-2.5-pro" }
              ]
            }
            """));

        using var geminiClient = new HttpClient(geminiHandler);
        using var geminiService = new ProviderDiscoveryService(geminiClient);

        var geminiResult = await geminiService.FetchModelsAsync(
            "https://generativelanguage.googleapis.com/v1beta/openai",
            "AIza-test");

        Assert.True(geminiResult.Success);
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro", "gemini-3.8-flash"], geminiResult.Models);

        var ollamaHandler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.OK, """
            {
              "models": [
                { "name": "qwen2.5:14b" },
                { "name": "llama3.2:latest" }
              ]
            }
            """));

        using var ollamaClient = new HttpClient(ollamaHandler);
        using var ollamaService = new ProviderDiscoveryService(ollamaClient);

        var ollamaResult = await ollamaService.FetchModelsAsync("http://localhost:11434/v1", string.Empty);

        Assert.True(ollamaResult.Success);
        Assert.Equal(["llama3.2:latest", "qwen2.5:14b"], ollamaResult.Models);
    }

    [Fact]
    public async Task PingAsync_OnLocalEndpointWithEmptyKey_UsesDummyLocalKey()
    {
        string? capturedAuth = null;
        var handler = new StubHttpHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return JsonResponse(HttpStatusCode.OK, """{ "data": [ { "id": "qwen2.5:14b" } ] }""");
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.PingAsync("http://localhost:11434/v1", string.Empty);

        Assert.True(result.Success);
        Assert.Equal("Bearer dummy-local-key", capturedAuth);
    }

    [Fact]
    public async Task PingAsync_WhenModelsReturns404_FallsBackToChatCompletionsAndRetriesMaxCompletionTokens()
    {
        var requests = new List<(string Method, string Url, string Body)>();

        var handler = new StubHttpHandler(async req =>
        {
            var body = req.Content != null ? await req.Content.ReadAsStringAsync() : string.Empty;
            requests.Add((req.Method.Method, req.RequestUri!.ToString(), body));

            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.NotFound, """{ "error": { "message": "Not Found" } }""");
            }

            if (body.Contains("\"max_tokens\"", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.BadRequest, """
                    { "error": { "message": "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead." } }
                    """);
            }

            return JsonResponse(HttpStatusCode.OK, """{ "id": "chatcmpl-1", "choices": [] }""");
        });

        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.PingAsync("https://proxy.example.com/v1", "sk-proxy", "deepseek-flash");

        Assert.True(result.Success);
        Assert.True(result.UsedChatProbeFallback);
        Assert.Equal(3, requests.Count);
        Assert.Equal("GET", requests[0].Method);
        Assert.EndsWith("/models", requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\"", requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"max_completion_tokens\"", requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("Chat testi OK", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PingAsync_WhenModelsReturns404AndNoDefaultModel_ExplainsMissingDefaultModel()
    {
        var handler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, "{}"));
        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.PingAsync("https://proxy.example.com/v1", "sk-proxy", defaultModel: null);

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("Varsayılan Model", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PingAsync_AndFetchModelsAsync_ReportUnauthorizedAndValidationErrorsCleanly()
    {
        var unauthHandler = new StubHttpHandler(_ =>
            JsonResponse(HttpStatusCode.Unauthorized, """{ "error": { "message": "Invalid API Key" } }"""));
        using var httpClient = new HttpClient(unauthHandler);
        using var service = new ProviderDiscoveryService(httpClient);

        var unauthPing = await service.PingAsync("https://api.openai.com/v1", "sk-bad", "gpt-4o");
        Assert.False(unauthPing.Success);
        Assert.Equal(401, unauthPing.StatusCode);
        Assert.Contains("401", unauthPing.Message, StringComparison.Ordinal);

        var invalidUrl = await service.FetchModelsAsync("not-a-url", "sk-ok");
        Assert.False(invalidUrl.Success);
        Assert.Null(invalidUrl.StatusCode);

        var placeholderKey = await service.FetchModelsAsync("https://api.openai.com/v1", "YOUR_OPENAI_KEY");
        Assert.False(placeholderKey.Success);
        Assert.Contains("YOUR_", placeholderKey.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchModelsAsync_WhenModelsUnsupported_AllowsManualEntryWithClearMessage()
    {
        var handler = new StubHttpHandler(_ => JsonResponse(HttpStatusCode.NotFound, "{}"));
        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.FetchModelsAsync("https://proxy.example.com/v1", "sk-proxy");

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Empty(result.Models);
        Assert.Contains("elle yazabilirsiniz", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PingAsync_WhenNetworkFails_ReturnsGracefulFailure()
    {
        var handler = new StubHttpHandler((Func<HttpRequestMessage, Task<HttpResponseMessage>>)(_ =>
            throw new HttpRequestException("Connection refused")));
        using var httpClient = new HttpClient(handler);
        using var service = new ProviderDiscoveryService(httpClient);

        var result = await service.PingAsync("https://unreachable.local.test/v1", "sk-test");

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> syncHandler)
            : this(req => Task.FromResult(syncHandler(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
