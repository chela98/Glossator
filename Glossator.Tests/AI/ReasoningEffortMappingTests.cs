using System.ClientModel.Primitives;
using Glossator.AI.Configuration;
using Glossator.AI.Extensions;
using Glossator.AI.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Xunit;

namespace Glossator.Tests.AI;

public class ReasoningEffortMappingTests
{
#pragma warning disable OPENAI001
    [Theory]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("minimal", "minimal")]
    [InlineData("Minimal", "minimal")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("XHigh", "xhigh")]
    public void MapReasoningEffortLevel_MapsKnownTiersToExpectedWireJson(string input, string expectedWireValue)
    {
        var mapped = AiServiceExtensions.MapReasoningEffortLevel(input);
        var options = new ChatCompletionOptions
        {
            ReasoningEffortLevel = mapped
        };

        var json = ModelReaderWriter.Write(options).ToString();
        Assert.Contains($"\"reasoning_effort\":\"{expectedWireValue}\"", json, StringComparison.Ordinal);
    }
#pragma warning restore OPENAI001

    [Fact]
    public void MapReasoningEffortLevel_PreservesCustomTierString()
    {
#pragma warning disable OPENAI001
        var mapped = AiServiceExtensions.MapReasoningEffortLevel("  custom-tier  ");
        var options = new ChatCompletionOptions { ReasoningEffortLevel = mapped };
        var json = ModelReaderWriter.Write(options).ToString();
        Assert.Contains("\"reasoning_effort\":\"custom-tier\"", json, StringComparison.Ordinal);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "", "dummy-local-key")]
    [InlineData("http://127.0.0.1:11434/v1", "YOUR_LOCAL_KEY", "dummy-local-key")]
    [InlineData("https://api.openai.com/v1", "sk-real-123", "sk-real-123")]
    [InlineData("https://api.openai.com/v1", null, "")]
    public void ResolveEffectiveApiKey_HandlesLocalEndpointsCleanly(string endpoint, string? apiKey, string expected)
    {
        var resolved = AiServiceExtensions.ResolveEffectiveApiKey(apiKey, endpoint);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void AddGlossatorAiClients_RegistersProviderDiscoveryService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new GlossatorAiOptions()));
        services.AddGlossatorAiClients();

        using var sp = services.BuildServiceProvider();
        var discovery = sp.GetService<IProviderDiscoveryService>();

        Assert.NotNull(discovery);
    }

    [Fact]
    public async Task ReasoningEffortChatClient_InjectsEffortInResponseAndStreamingWithoutMutatingCallerOptions()
    {
        var captureClient = new CapturingChatClient();
        var client = new Glossator.AI.Clients.ReasoningEffortChatClient(captureClient, "low");
        var callerOptions = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Ping")], callerOptions);
        await foreach (var _ in client.GetStreamingResponseAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Stream")], callerOptions))
        {
        }

        Assert.Null(callerOptions.AdditionalProperties);
        Assert.Equal(2, captureClient.CapturedOptions.Count);
        foreach (var opt in captureClient.CapturedOptions)
        {
            Assert.NotNull(opt);
            Assert.Equal("low", opt.AdditionalProperties?["reasoning_effort"]);
            var raw = Assert.IsType<ChatCompletionOptions>(opt.RawRepresentationFactory?.Invoke(client));
#pragma warning disable OPENAI001
            Assert.Equal(ChatReasoningEffortLevel.Low, raw.ReasoningEffortLevel);
#pragma warning restore OPENAI001
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatOptions?> CapturedOptions { get; } = [];
        public ChatClientMetadata Metadata => new("Capture");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedOptions.Add(options);
            return Task.FromResult(new ChatResponse([new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedOptions.Add(options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
