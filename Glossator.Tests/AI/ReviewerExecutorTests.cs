using System.Diagnostics;
using System.Runtime.CompilerServices;
using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class ReviewerExecutorTests
{
    private sealed class MockReviewerChatClient : IChatClient
    {
        private readonly Queue<string> _responses;

        public int CallCount { get; private set; }

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
            CallCount++;
            var text = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
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
    public async Task HandleAsync_WhenCSharpPrefilterDetectsMissingBlockOrMask_RejectsAtZeroLlmCost()
    {
        // Arrange
        var client = new MockReviewerChatClient(["{ \"isApproved\": true, \"patches\": [] }"]);
        var logs = new List<PipelineStepLog>();
        var executor = new ReviewerExecutor(client, "SYSTEM", log => logs.Add(log));

        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nFirst ⟦PH_000001⟧\n[/BLOK_001]\n\n[BLOK_002]\nSecond [[CODE_01]]\n[/BLOK_002]",
            CurrentTranslation: "[BLOK_001]\nİlk taslak (yer tutucu eksik)\n[/BLOK_001]",
            DoNotTranslateTerms: [],
            StyleGuide: "Teknik",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert: LLM kesinlikle çağrılmamalı (0 maliyet), deneme sayısı artmalı ve eksikler bildirilmeli
        Assert.Equal(0, client.CallCount);
        Assert.False(result.IsApproved);
        Assert.Equal(2, result.AttemptCount);
        Assert.NotNull(result.Feedback);
        Assert.Contains("BLOK_002", result.Feedback);
        Assert.Contains("⟦PH_000001⟧", result.Feedback);
        Assert.Contains(logs, l => l.Summary.Contains("C# Ön Filtresi"));
    }

    [Fact]
    public async Task HandleAsync_WhenMaxAttemptsReachedAndLlmRejects_ForceApprovesBestEffortDraft()
    {
        // Arrange
        var client = new MockReviewerChatClient([
            """{"isApproved": false, "feedback": "Üslup biraz zayıf", "patches": []}"""
        ]);
        var logs = new List<PipelineStepLog>();
        var executor = new ReviewerExecutor(client, "SYSTEM", log => logs.Add(log));

        var draft = "[BLOK_001]\nKabul edilecek taslak.\n[/BLOK_001]";
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nOriginal text.\n[/BLOK_001]",
            CurrentTranslation: draft,
            DoNotTranslateTerms: [],
            StyleGuide: "Teknik",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 3);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.True(result.IsApproved);
        Assert.Equal(draft, result.CurrentTranslation);
        Assert.Null(result.Feedback);
        Assert.Contains(logs, l => l.Summary.Contains("3. denemede de redaksiyon onay alamadı"));
    }

    [Fact]
    public async Task HandleAsync_WhenApprovedWithValidPatches_NormalizesBlockIdsAndStripsNestedBlockTags()
    {
        // Arrange: Biri "BLOK_025" önekli ve içeriğinde iç içe [BLOK_025] taşıyan, diğeri sayısal "26" iki yama
        var patchJson = """
            {
              "isApproved": true,
              "feedback": "İki blok iyileştirildi",
              "patches": [
                {
                  "blockId": "BLOK_025",
                  "reason": "Akıcılık",
                  "fixedText": "[BLOK_025]\nDüzeltilmiş yirmi beşinci blok.\n[/BLOK_025]"
                },
                {
                  "blockId": 26,
                  "reason": "Terim",
                  "fixedText": "Düzeltilmiş yirmi altıncı blok."
                }
              ]
            }
            """;

        var client = new MockReviewerChatClient([patchJson]);
        var executor = new ReviewerExecutor(client, "SYSTEM");

        var state = new TranslationReviewState(
            OriginalText: "[BLOK_025]\nOriginal 25\n[/BLOK_025]\n[BLOK_026]\nOriginal 26\n[/BLOK_026]",
            CurrentTranslation: "[BLOK_025 | TAG: p]\nEski 25\n[/BLOK_025]\n[BLOK_026]\nEski 26\n[/BLOK_026]",
            DoNotTranslateTerms: [],
            StyleGuide: "Standart",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.True(result.IsApproved);
        Assert.Contains("[BLOK_025]\nDüzeltilmiş yirmi beşinci blok.\n[/BLOK_025]", result.CurrentTranslation);
        Assert.Contains("[BLOK_026]\nDüzeltilmiş yirmi altıncı blok.\n[/BLOK_026]", result.CurrentTranslation);
        // İç içe çift [BLOK_025] oluşmamalı
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.CurrentTranslation, @"\[/BLOK_025\]"));
    }

    [Fact]
    public async Task HandleAsync_WhenPatchDropsPlaceholder_GatekeeperRejectsPatchAndPreservesTranslation()
    {
        // Arrange: Reviewer onaylıyor ama yaması ⟦PH_000001⟧ yer tutucusunu yutuyor
        var maliciousPatchJson = """
            {
              "isApproved": true,
              "patches": [
                {
                  "blockId": "001",
                  "reason": "Hatalı yama",
                  "fixedText": "Bu metinde yer tutucu silindi."
                }
              ]
            }
            """;

        var client = new MockReviewerChatClient([maliciousPatchJson]);
        var logs = new List<PipelineStepLog>();
        var executor = new ReviewerExecutor(client, "SYSTEM", log => logs.Add(log));

        var draft = "[BLOK_001]\nBu kod örneği: ⟦PH_000001⟧ içerir.\n[/BLOK_001]";
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nThis code sample contains ⟦PH_000001⟧.\n[/BLOK_001]",
            CurrentTranslation: draft,
            DoNotTranslateTerms: [],
            StyleGuide: "Standart",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert: Gatekeeper ihlali yakalayıp yamayı reddetmeli ve tam taslağı korumalı
        Assert.True(result.IsApproved);
        Assert.Equal(draft, result.CurrentTranslation);
        Assert.Contains(logs, l => l.Summary.Contains("Yama İhlali Engellendi"));
    }

    [Fact]
    public void ParseReviewerResponse_WhenJsonHasRawNewlineInsideString_FallsBackToRegexParser()
    {
        // Arrange: System.Text.Json kaçışsız ham satır sonunu reddeder -> TryRegexParseReviewerResponse devreye girmeli
        var brokenForStrictJson = "{\n  \"isApproved\": true,\n  \"feedback\": \"Satır 1\nSatır 2\",\n  \"patches\": [\n    { \"blockId\": \"007\", \"reason\": \"test\", \"fixedText\": \"Temiz\nmetin\" }\n  ]\n}";

        // Act
        var parsed = ReviewerExecutor.ParseReviewerResponse(brokenForStrictJson);

        // Assert
        Assert.NotNull(parsed);
        Assert.True(parsed.IsApproved);
        var patch = Assert.Single(parsed.Patches!);
        Assert.Equal("007", patch.BlockId);
        Assert.Contains("Temiz", patch.FixedText);
    }

    [Fact]
    public void ParseReviewerResponse_WhenJsonIsTruncatedWithoutClosingBrace_ReturnsNullSoSelfRepairCanRun()
    {
        // Arrange
        var truncatedJson = "{\"isApproved\": true, \"feedback\": \"Kesilmiş";

        // Act
        var parsed = ReviewerExecutor.ParseReviewerResponse(truncatedJson);

        // Assert
        Assert.Null(parsed);
    }

    [Fact]
    public async Task HandleAsync_WhenSelfRepairRuns_IsolatesRepairActivitySoPrimaryReviewerTagsArePreserved()
    {
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => stoppedActivities.Add(act)
        };
        ActivitySource.AddActivityListener(listener);

        var client = new UsageTrackedQueueClient(
            ("{\"broken\":", new UsageDetails { InputTokenCount = 9100, OutputTokenCount = 220, ReasoningTokenCount = 50 }),
            ("""{"isApproved": true, "patches": []}""", new UsageDetails { InputTokenCount = 320, OutputTokenCount = 45, ReasoningTokenCount = 0 }));

        var executor = new ReviewerExecutor(client, "REV_SYS");
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nSource.\n[/BLOK_001]",
            CurrentTranslation: "[BLOK_001]\nKaynak.\n[/BLOK_001]",
            DoNotTranslateTerms: [],
            StyleGuide: "Teknik",
            GlossaryTerms: null,
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        var result = await executor.HandleAsync(state, null!);

        Assert.True(result.IsApproved);
        var primarySpan = Assert.Single(stoppedActivities, a => a.OperationName == "ReviewerAgent");
        var repairSpan = Assert.Single(stoppedActivities, a => a.OperationName == "ReviewerJsonRepair");
        Assert.Equal(9100L, primarySpan.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(320L, repairSpan.GetTagItem("gen_ai.usage.input_tokens"));
    }

    private sealed class UsageTrackedQueueClient(params (string Text, UsageDetails Usage)[] responses) : IChatClient
    {
        private readonly Queue<(string Text, UsageDetails Usage)> _queue = new(responses);
        public ChatClientMetadata Metadata => new("UsageTrackedMock");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var (text, usage) = _queue.Dequeue();
            var update = new ChatResponseUpdate(ChatRole.Assistant, text);
            update.Contents.Add(new UsageContent(usage));
            yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
