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
}
