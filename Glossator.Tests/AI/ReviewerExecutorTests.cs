using System.Diagnostics;
using System.Runtime.CompilerServices;
using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

public class ReviewerExecutorTests
{
    private sealed class MockReviewerChatClient : IChatClient
    {
        private readonly Queue<string> _responses;

        public MockReviewerChatClient(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        public ChatClientMetadata Metadata => new("MockReviewer");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void ParseReviewerResponse_WithUnescapedHtmlAttributes_ParsesSuccessfully()
    {
        // Arrange: Kullanıcının loglarında parser'ı patlatan gerçek JSON çıktısı
        var rawJsonWithUnescapedHtml = """
            {
              "isApproved": true,
              "feedback": "Çeviri genel olarak başarılı.",
              "patches": [
                {
                  "blockId": "153",
                  "reason": "Quantifies fiili düzeltildi.",
                  "fixedText": "EyeLevel.ai (<span class=\"customlink\"><a href=\"https://oreil.ly/V_roj\">Warfield, 2024</a></span>) tarafından yapılan araştırma."
                }
              ]
            }
            """;

        // Act
        var result = ReviewerExecutor.ParseReviewerResponse(rawJsonWithUnescapedHtml);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsApproved);
        Assert.NotNull(result.Patches);
        Assert.Single(result.Patches);
        Assert.Equal("153", result.Patches[0].BlockId);
        Assert.Contains("Warfield, 2024", result.Patches[0].FixedText);
    }

    [Fact]
    public async Task HandleAsync_WhenJsonCorrupted_ExecutesSelfRepairSuccessfully()
    {
        // Arrange: İlk yanıt bozuk JSON, ikinci yanıt (repair çağrısı) düzeltilmiş temiz JSON
        var brokenJson = "{ \"isApproved\": true, \"feedback\": \"Eksik tırnak ";
        var repairedJson = """
            {
              "isApproved": true,
              "feedback": "Onarılmış redaksiyon.",
              "patches": [
                {
                  "blockId": "001",
                  "reason": "Üslup rötuşu",
                  "fixedText": "Düzeltilmiş ilk blok."
                }
              ]
            }
            """;

        var mockClient = new MockReviewerChatClient([brokenJson, repairedJson]);
        var executor = new ReviewerExecutor(mockClient, "Prompt");

        var originalText = "[BLOK_001]\nİlk blok orijinal.\n[/BLOK_001]";
        var currentTranslation = "[BLOK_001]\nİlk blok çeviri.\n[/BLOK_001]";

        var state = new TranslationReviewState(
            OriginalText: originalText,
            CurrentTranslation: currentTranslation,
            DoNotTranslateTerms: [],
            StyleGuide: "",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsApproved);
        Assert.Contains("Düzeltilmiş ilk blok", result.CurrentTranslation);
    }

    [Fact]
    public async Task HandleAsync_WhenRepairAlsoFails_FallsBackToTranslatorDraftWithoutCrashing()
    {
        // Arrange: Hem ilk yanıt hem de repair yanıtı tamamen bozuk serbest metin
        var brokenJson1 = "Ben bir yapay zekayım ve JSON yerine bu metni yazıyorum.";
        var brokenJson2 = "Hala JSON yazamıyorum üzgünüm.";

        var mockClient = new MockReviewerChatClient([brokenJson1, brokenJson2]);
        var executor = new ReviewerExecutor(mockClient, "Prompt");

        var originalText = "[BLOK_001]\nOrijinal metin.\n[/BLOK_001]";
        var translatorDraft = "[BLOK_001]\nÇevirmenin kusursuz taslağı.\n[/BLOK_001]";

        var state = new TranslationReviewState(
            OriginalText: originalText,
            CurrentTranslation: translatorDraft,
            DoNotTranslateTerms: [],
            StyleGuide: "",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert: Translator'a DÖNÜLMEMELİ! Taslak doğrudan onaylanmalı.
        Assert.NotNull(result);
        Assert.True(result.IsApproved);
        Assert.Equal(translatorDraft, result.CurrentTranslation);
        Assert.Null(result.Feedback);
    }

    [Fact]
    public async Task OpenTelemetryPipeline_WithResilienceChatClient_CapturesExactSingleGeminiUsage()
    {
        var capturedActivities = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name.Contains("Microsoft.Extensions.AI"),
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => capturedActivities.Add(act)
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var inner = new MockStreamingUsageChatClient();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Glossator.AI.Clients.ResilienceChatClient>.Instance;

        var pipeline = inner
            .AsBuilder()
            .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
            .Use(c => new Glossator.AI.Clients.ResilienceChatClient(c, logger))
            .Build();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in pipeline.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Test")]))
        {
            updates.Add(u);
        }

        Assert.NotEmpty(capturedActivities);
        var otelSpan = capturedActivities.Last();
        var inTok = otelSpan.GetTagItem("gen_ai.usage.input_tokens");
        var outTok = otelSpan.GetTagItem("gen_ai.usage.output_tokens");

        // Gemini sends cumulative 1000 input tokens on 3 chunks; OpenTelemetry must see 1000 (not 3000 and not null!)
        Assert.Equal(1000L, Convert.ToInt64(inTok));
        // Gemini sends total=1450, input=1000, output=150 (300 hidden thinking tokens) -> normalized output=450!
        Assert.Equal(450L, Convert.ToInt64(outTok));
    }

    private sealed class MockStreamingUsageChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("MockGemini", new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"), "gemini-2.5-flash");
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            for (int i = 1; i <= 3; i++)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = "gemini-2.5-flash",
                    Contents =
                    [
                        new TextContent($"Parça {i} "),
                        new UsageContent(new UsageDetails
                        {
                            InputTokenCount = 1000,
                            OutputTokenCount = i * 50,
                            TotalTokenCount = 1000 + (i * 50) + 300 // +300 thinking tokens
                        })
                    ]
                };
            }
        }
    }
}
