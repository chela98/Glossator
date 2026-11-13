using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glossator.AI.Executors;
using Glossator.AI.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class StyleAnalystAndTranslatorExecutorTests
{
    private sealed class CapturingChatClient(Func<IReadOnlyList<ChatMessage>, string> factory) : IChatClient
    {
        public int CallCount { get; private set; }
        public List<List<ChatMessage>> CapturedCalls { get; } = [];

        public ChatClientMetadata Metadata => new("CapturingMock");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = messages.ToList();
            CapturedCalls.Add(list);
            var reply = factory(list);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, reply)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = messages.ToList();
            CapturedCalls.Add(list);
            var reply = factory(list);
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task StyleAnalystExecutor_WhenStyleGuideAlreadyPresent_BypassesLlmAtZeroCost()
    {
        // Arrange
        var client = new CapturingChatClient(_ => "SHOULD_NOT_BE_CALLED");
        var executor = new StyleAnalystExecutor(client, "SYSTEM_STYLE");
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nSome text\n[/BLOK_001]",
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: [],
            StyleGuide: "Hazır Üslup Kılavuzu",
            GlossaryTerms: null);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.Equal(0, client.CallCount);
        Assert.Equal("Hazır Üslup Kılavuzu", result.StyleGuide);
    }

    [Fact]
    public async Task StyleAnalystExecutor_WhenInputExceeds2500Chars_SlicesSafelyAtWhitespaceAndSurrogateBoundaries()
    {
        // Arrange: 2499 karakter + surrogate pair ("🚀" = 2 char) -> tam sınırda bölünürse high surrogate bozulmamalı
        var longText = new string('a', 1200) + " " + new string('b', 1298) + "🚀 trailing words past limit " + new string('z', 200);
        var client = new CapturingChatClient(_ => "  Akademik ve net teknik dil.  ");
        var logs = new List<PipelineStepLog>();
        var executor = new StyleAnalystExecutor(client, "SYSTEM_STYLE", log => logs.Add(log));

        var state = new TranslationReviewState(
            OriginalText: longText,
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: [],
            StyleGuide: "");

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.Equal(1, client.CallCount);
        Assert.Equal("Akademik ve net teknik dil.", result.StyleGuide);
        var sentUserPrompt = client.CapturedCalls[0][1].Text!;
        Assert.DoesNotContain("trailing words past limit", sentUserPrompt);
        // Kesilen örnek geçerli UTF-16 olmalı (tek başına kalmış surrogate karakter içermemeli)
        Assert.False(char.IsHighSurrogate(sentUserPrompt[^1]));
        Assert.Contains(logs, l => l.Stage == "StyleAnalyst" && !l.IsTransient);
    }

    [Fact]
    public async Task StyleAnalystExecutor_WhenLlmReturnsWhitespace_FallsBackToDefaultStyleGuide()
    {
        // Arrange
        var client = new CapturingChatClient(_ => "   \n  ");
        var executor = new StyleAnalystExecutor(client, "SYSTEM_STYLE");
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nText\n[/BLOK_001]",
            CurrentTranslation: string.Empty,
            DoNotTranslateTerms: [],
            StyleGuide: string.Empty);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.Contains("O'Reilly", result.StyleGuide);
    }

    [Fact]
    public async Task TranslatorExecutor_WhenFeedbackIsPresent_InjectsCorrectionDirectiveIntoPrompt()
    {
        // Arrange
        var client = new CapturingChatClient(_ => "  [BLOK_001]\nDüzeltilmiş çeviri.\n[/BLOK_001]  ");
        var executor = new TranslatorExecutor(client, "SYSTEM_TRANS");
        var state = new TranslationReviewState(
            OriginalText: "[BLOK_001]\nOriginal text.\n[/BLOK_001]",
            CurrentTranslation: "[BLOK_001]\nEski çeviri.\n[/BLOK_001]",
            DoNotTranslateTerms: ["API"],
            StyleGuide: "Teknik ton",
            Feedback: "[[CODE_01]] eksik bırakılmış!",
            AttemptCount: 2);

        // Act
        var result = await executor.HandleAsync(state, null!);

        // Assert
        Assert.Equal("[BLOK_001]\nDüzeltilmiş çeviri.\n[/BLOK_001]", result.CurrentTranslation);
        var userPrompt = client.CapturedCalls[0][1].Text!;
        Assert.Contains("[ÖNCEKİ DENETİMDEKİ HATALAR - DÜZELTME EMRİ]:", userPrompt);
        Assert.Contains("[[CODE_01]] eksik bırakılmış!", userPrompt);
    }
}
