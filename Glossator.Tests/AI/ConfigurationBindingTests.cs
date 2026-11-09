namespace Glossator.Tests.AI;

using System.Text;
using Glossator.AI.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

public class ConfigurationBindingTests
{
    [Fact]
    public void ConfigurationBuilder_WhenReadingAppSettingsJson_BindsAiOptionsAndOtelCorrectly()
    {
        // Arrange: appsettings.json içeriğini temsil eden JSON
        var json = """
        {
          "GlossatorAi": {
            "ActiveProvider": "Custom",
            "Providers": {
              "Custom": {
                "Endpoint": "https://api.openai.com/v1",
                "ApiKey": "sk-custom-test-key",
                "DefaultModel": "gpt-4o"
              }
            },
            "Agents": {
              "StyleAnalyst": { "Provider": "Custom", "ModelId": "gpt-4o-mini" },
              "Translator": { "Provider": "Custom", "ModelId": "gpt-4o" },
              "Reviewer": { "Provider": "Custom", "ModelId": "gpt-4o-mini" }
            }
          },
          "OpenTelemetry": {
            "Endpoint": "http://localhost:4317",
            "ServiceName": "Glossator.Cli.Test"
          }
        }
        """;

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        // Act: Disk I/O yerine MemoryStream ile izole ve hızlı konfigürasyon inşası
        var config = new ConfigurationBuilder()
            .AddJsonStream(memoryStream)
            .Build();

        var aiSection = config.GetSection("GlossatorAi");
        var aiOptions = aiSection.Get<GlossatorAiOptions>();

        var otelEndpoint = config["OpenTelemetry:Endpoint"];
        var otelService = config["OpenTelemetry:ServiceName"];

        // Assert: Temel Sağlayıcı Doğrulaması
        Assert.NotNull(aiOptions);
        Assert.Equal("Custom", aiOptions.ActiveProvider);
        Assert.True(aiOptions.Providers.ContainsKey("Custom"));
        Assert.Equal("https://api.openai.com/v1", aiOptions.Providers["Custom"].Endpoint);
        Assert.Equal("sk-custom-test-key", aiOptions.Providers["Custom"].ApiKey);
        Assert.Equal("gpt-4o", aiOptions.Providers["Custom"].DefaultModel);

        // Assert: Ajanlar Hiyerarşisi Doğrulaması
        Assert.NotNull(aiOptions.Agents);
        Assert.Equal(3, aiOptions.Agents.Count);
        Assert.True(aiOptions.Agents.ContainsKey("Translator"));
        Assert.Equal("Custom", aiOptions.Agents["Translator"].Provider);
        Assert.Equal("gpt-4o", aiOptions.Agents["Translator"].ModelId);

        // OpenTelemetry Assert
        Assert.Equal("http://localhost:4317", otelEndpoint);
        Assert.Equal("Glossator.Cli.Test", otelService);
    }
}
