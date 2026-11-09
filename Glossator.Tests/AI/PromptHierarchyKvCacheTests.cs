using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

public class PromptHierarchyKvCacheTests
{
    private sealed class CapturingMockChatClient(Func<IEnumerable<ChatMessage>, string> responseFactory) : IChatClient
    {
        public List<ChatMessage> LastReceivedMessages { get; } = [];

        public ChatClientMetadata Metadata => new("CapturingMock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastReceivedMessages.Clear();
            LastReceivedMessages.AddRange(messages);

            var text = responseFactory(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ReviewerExecutor_ConstructsUserPrompt_WithOptimizedKvCacheHierarchy()
    {
        // Arrange
        var mockClient = new CapturingMockChatClient(_ => """{"isApproved": true, "patches": []}""");
        var executor = new ReviewerExecutor(mockClient, "SYSTEM_PROMPT_STATIC_RULES");

        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001] In this chapter, we discuss agentic systems.",
            CurrentTranslation: "[BLOK_001] Bu bölümde otonom sistemleri ele alıyoruz.",
            DoNotTranslateTerms: ["AutoGen", "LangChain"],
            StyleGuide: "Samimi ve doğrudan O'Reilly mühendis mentor üslubu.",
            GlossaryTerms: new Dictionary<string, string> { ["Agent"] = "Ajan" },
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        // Null context is acceptable since ReviewerExecutor doesn't use context during basic prompt generation
        await executor.HandleAsync(state, null!);

        // Assert
        Assert.Equal(2, mockClient.LastReceivedMessages.Count);
        Assert.Equal(ChatRole.System, mockClient.LastReceivedMessages[0].Role);
        Assert.Equal("SYSTEM_PROMPT_STATIC_RULES", mockClient.LastReceivedMessages[0].Text);

        Assert.Equal(ChatRole.User, mockClient.LastReceivedMessages[1].Role);
        var userPrompt = mockClient.LastReceivedMessages[1].Text!;

        var idxStyle = userPrompt.IndexOf("[YAZAR ÜSLUP KILAVUZU]:", StringComparison.Ordinal);
        var idxGlossary = userPrompt.IndexOf("[TERİM SÖZLÜĞÜ - RESMİ TÜRKÇE KARŞILIKLAR]:", StringComparison.Ordinal);
        var idxProtected = userPrompt.IndexOf("[DOKUNULMAYACAK MARKA, PROTOKOL VE KISALTMALAR]:", StringComparison.Ordinal);
        var idxOriginal = userPrompt.IndexOf("[ORİJİNAL İNGİLİZCE METİN]:", StringComparison.Ordinal);
        var idxDraft = userPrompt.IndexOf("[DEĞERLENDİRİLECEK ÇEVİRİ TASLAĞI]:", StringComparison.Ordinal);

        // KV Cache Doğrulaması:
        // 1. Tüm başlıklar eksiksiz mevcut olmalı
        Assert.True(idxStyle >= 0, "Yazar üslup kılavuzu başlığı bulunamadı");
        Assert.True(idxGlossary >= 0, "Terim sözlüğü başlığı bulunamadı");
        Assert.True(idxProtected >= 0, "Dokunulmaz terimler başlığı bulunamadı");
        Assert.True(idxOriginal >= 0, "Orijinal metin başlığı bulunamadı");
        Assert.True(idxDraft >= 0, "Çeviri taslağı başlığı bulunamadı");

        // 2. Sıralama katı KV Cache Prefix hiyerarşisine uymalı:
        // En başta sabit / bölüm bağlamı, en sonda değişken payload
        Assert.True(idxStyle < idxGlossary, "Üslup kılavuzu terim sözlüğünden önce gelmelidir (bölüm bazlı önbellek)");
        Assert.True(idxGlossary < idxProtected, "Terim sözlüğü dokunulmaz terimlerden önce gelmelidir");
        Assert.True(idxProtected < idxOriginal, "Dokunulmazlar listesi dinamik orijinal metinden önce gelmelidir");
        Assert.True(idxOriginal < idxDraft, "Orijinal metin çeviri taslağından önce gelmelidir (tekrar denemelerinde önbellek)");

        // 3. Eski hatalı anti-pattern doğrulaması: Orijinal metin ASLA en başta olmamalıdır!
        Assert.False(userPrompt.TrimStart().StartsWith("[ORİJİNAL İNGİLİZCE METİN]:"),
            "KV Cache hatası: Dinamik orijinal metin kullanıcı mesajının en başında olamaz!");
    }

    [Fact]
    public async Task TranslatorExecutor_ConstructsUserPrompt_WithOptimizedKvCacheHierarchy()
    {
        // Arrange
        var mockClient = new CapturingMockChatClient(_ => "[BLOK_001] Bu bölümde otonom sistemleri ele alıyoruz.");
        var executor = new TranslatorExecutor(mockClient, "SYSTEM_PROMPT_TRANSLATOR_RULES");

        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001] In this chapter, we discuss agentic systems.",
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: ["AutoGen", "LangChain"],
            StyleGuide: "Samimi ve doğrudan O'Reilly mühendis mentor üslubu.",
            GlossaryTerms: new Dictionary<string, string> { ["Agent"] = "Ajan" },
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        await executor.HandleAsync(state, null!);

        // Assert
        Assert.Equal(2, mockClient.LastReceivedMessages.Count);
        Assert.Equal(ChatRole.System, mockClient.LastReceivedMessages[0].Role);
        Assert.Equal(ChatRole.User, mockClient.LastReceivedMessages[1].Role);

        var userPrompt = mockClient.LastReceivedMessages[1].Text!;

        var idxStyle = userPrompt.IndexOf("[YAZAR ÜSLUP KILAVUZU]:", StringComparison.Ordinal);
        var idxGlossary = userPrompt.IndexOf("[TERİM SÖZLÜĞÜ - ZORUNLU TÜRKÇE KARŞILIKLAR]:", StringComparison.Ordinal);
        var idxProtected = userPrompt.IndexOf("[DOKUNULMAYACAK MARKA, PROTOKOL VE KISALTMALAR]:", StringComparison.Ordinal);
        var idxText = userPrompt.IndexOf("[ÇEVRİLECEK METİN]:", StringComparison.Ordinal);

        // KV Cache Sıralama Doğrulaması:
        Assert.True(idxStyle >= 0, "Yazar üslup kılavuzu başlığı bulunamadı");
        Assert.True(idxGlossary >= 0, "Terim sözlüğü başlığı bulunamadı");
        Assert.True(idxProtected >= 0, "Dokunulmaz terimler başlığı bulunamadı");
        Assert.True(idxText >= 0, "Çevrilecek metin başlığı bulunamadı");

        Assert.True(idxStyle < idxGlossary, "Üslup kılavuzu terim sözlüğünden önce gelmelidir");
        Assert.True(idxGlossary < idxProtected, "Terim sözlüğü dokunulmazlar listesinden önce gelmelidir");
        Assert.True(idxProtected < idxText, "Dokunulmazlar listesi çevrilecek metinden önce gelmelidir");

        // Dinamik metin prompt'un en sonunda yer almalıdır
        Assert.True(userPrompt.EndsWith(state.OriginalText),
            "Çevrilecek metin prompt'un en sonunda yer almalıdır");
    }

    [Fact]
    public async Task ReviewerExecutor_WhenPatchDropsPlaceholder_GatekeeperRejectsPatchAndPreservesTranslation()
    {
        // Arrange: Reviewer onaylıyor ama yaması ⟦PH_000001⟧ yer tutucusunu yutuyor!
        var maliciousPatchJson = """
        {
            "is_approved": true,
            "patches": [
                {
                    "block_id": 1,
                    "revised_text": "Bu metinde yer tutucu silindi."
                }
            ]
        }
        """;

        var mockClient = new CapturingMockChatClient(_ => maliciousPatchJson);
        var executor = new ReviewerExecutor(mockClient, "SYSTEM_RULES");

        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001] Bu kod örneği: ⟦PH_000001⟧ içerir.",
            CurrentTranslation: "[BLOK_001] Bu kod örneği: ⟦PH_000001⟧ içerir.",
            DoNotTranslateTerms: [],
            StyleGuide: "Standart",
            GlossaryTerms: new Dictionary<string, string>(),
            IsApproved: false,
            Feedback: null,
            AttemptCount: 1);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert: Gatekeeper yama ihlalini yakalamalı ve orijinal ⟦PH_000001⟧ içeren çeviriyi korumalıdır
        Assert.NotNull(result);
        Assert.True(result.IsApproved);
        Assert.Contains("⟦PH_000001⟧", result.CurrentTranslation);
    }
}
