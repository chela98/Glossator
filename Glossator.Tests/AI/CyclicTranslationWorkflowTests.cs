using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Glossator.AI.Configuration;
using Glossator.AI.Models;
using Glossator.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class CyclicTranslationWorkflowTests
{
    private sealed class QueuedChatClient(Queue<Func<IReadOnlyList<ChatMessage>, string>> handlers) : IChatClient
    {
        public int CallCount { get; private set; }
        public List<string> CapturedUserPrompts { get; } = [];

        public ChatClientMetadata Metadata => new("QueuedMock");

        public QueuedChatClient(params string[] staticResponses)
            : this(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(staticResponses.Select(r => (Func<IReadOnlyList<ChatMessage>, string>)(_ => r))))
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = messages.ToList();
            CapturedUserPrompts.Add(list.LastOrDefault()?.Text ?? string.Empty);
            var reply = handlers.Count > 0 ? handlers.Dequeue()(list) : string.Empty;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, reply)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var list = messages.ToList();
            CapturedUserPrompts.Add(list.LastOrDefault()?.Text ?? string.Empty);
            var reply = handlers.Count > 0 ? handlers.Dequeue()(list) : string.Empty;
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ExecuteAsync_WhenStyleGuideEmptyAndReviewerApprovesFirstPass_RunsStyleTranslatorAndReviewerOnceEach()
    {
        // Arrange
        var styleClient = new QueuedChatClient("Akıcı ve teknik dil.");
        var transClient = new QueuedChatClient("[BLOK_001]\nDağıtık sistemler dayanıklıdır.\n[/BLOK_001]");
        var revClient = new QueuedChatClient("""{"isApproved": true, "patches": []}""");

        var logs = new List<PipelineStepLog>();
        var workflow = new CyclicTranslationWorkflow(
            styleClient, transClient, revClient,
            "STYLE_SYS", "TRANS_SYS", "REV_SYS",
            stepLogger: log => logs.Add(log));

        // Act
        var output = await workflow.ExecuteAsync(
            "[BLOK_001]\nDistributed systems are resilient.\n[/BLOK_001]",
            doNotTranslateTerms: [],
            glossaryTerms: null,
            styleGuide: string.Empty);

        // Assert
        Assert.Equal("[BLOK_001]\nDağıtık sistemler dayanıklıdır.\n[/BLOK_001]", output);
        Assert.Equal(1, styleClient.CallCount);
        Assert.Equal(1, transClient.CallCount);
        Assert.Equal(1, revClient.CallCount);
        Assert.Contains(logs, l => l.Stage == "StyleAnalyst");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReviewerRejectsThenApproves_LoopsBackToTranslatorWithFeedback()
    {
        // Arrange
        var styleClient = new QueuedChatClient("SHOULD_NOT_CALL");
        var transClient = new QueuedChatClient(
            "[BLOK_001]\nİlk taslak çeviri.\n[/BLOK_001]",
            "[BLOK_001]\nDüzeltilmiş nihai çeviri.\n[/BLOK_001]");
        var revClient = new QueuedChatClient(
            """{"isApproved": false, "feedback": "Terminoloji tutarsız", "patches": []}""",
            """{"isApproved": true, "patches": []}""");

        var workflow = new CyclicTranslationWorkflow(
            styleClient, transClient, revClient,
            "STYLE_SYS", "TRANS_SYS", "REV_SYS");

        // Act
        var output = await workflow.ExecuteAsync(
            "[BLOK_001]\nSource paragraph.\n[/BLOK_001]",
            doNotTranslateTerms: [],
            styleGuide: "Hazır Kılavuz");

        // Assert
        Assert.Equal(0, styleClient.CallCount);
        Assert.Equal(2, transClient.CallCount);
        Assert.Equal(2, revClient.CallCount);
        Assert.Contains("Terminoloji tutarsız", transClient.CapturedUserPrompts[1]);
        Assert.Equal("[BLOK_001]\nDüzeltilmiş nihai çeviri.\n[/BLOK_001]", output);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReviewerRejectsThreeTimes_StopsAtMaxAttemptsAndReturnsBestEffortDraft()
    {
        // Arrange
        var styleClient = new QueuedChatClient();
        var transClient = new QueuedChatClient(
            "[BLOK_001]\nDeneme 1\n[/BLOK_001]",
            "[BLOK_001]\nDeneme 2\n[/BLOK_001]",
            "[BLOK_001]\nDeneme 3\n[/BLOK_001]");
        var revClient = new QueuedChatClient(
            """{"isApproved": false, "feedback": "Red 1", "patches": []}""",
            """{"isApproved": false, "feedback": "Red 2", "patches": []}""",
            """{"isApproved": false, "feedback": "Red 3", "patches": []}""");

        var workflow = new CyclicTranslationWorkflow(
            styleClient, transClient, revClient,
            "STYLE_SYS", "TRANS_SYS", "REV_SYS");

        // Act
        var output = await workflow.ExecuteAsync(
            "[BLOK_001]\nSource.\n[/BLOK_001]",
            doNotTranslateTerms: [],
            styleGuide: "Hazır Kılavuz");

        // Assert: 3. reddin ardından döngüye girmemeli ve 3. taslağı döndürmeli
        Assert.Equal(3, transClient.CallCount);
        Assert.Equal(3, revClient.CallCount);
        Assert.Equal("[BLOK_001]\nDeneme 3\n[/BLOK_001]", output);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ThrowsOperationCanceledExceptionImmediately()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var dummy = new QueuedChatClient("OK");
        var workflow = new CyclicTranslationWorkflow(dummy, dummy, dummy, "S", "T", "R");

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await workflow.ExecuteAsync(
                "[BLOK_001]\nTest\n[/BLOK_001]",
                [],
                styleGuide: "Hazır",
                cancellationToken: cts.Token));
        Assert.Equal(0, dummy.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUnexpectedExceptionOccursInsideWorkflow_ReturnsEmptyStringWithoutCrashing()
    {
        // Arrange
        var throwingClient = new QueuedChatClient(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(
            [_ => throw new InvalidOperationException("Simulated workflow crash")]));

        var workflow = new CyclicTranslationWorkflow(
            throwingClient, throwingClient, throwingClient,
            "S", "T", "R");

        // Act
        var result = await workflow.ExecuteAsync(
            "[BLOK_001]\nTest\n[/BLOK_001]",
            [],
            styleGuide: "Hazır");

        // Assert
        Assert.Equal(string.Empty, result);
        Assert.Equal(1, throwingClient.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFatalUnauthorized401Occurs_PropagatesInsteadOfMaskingAsEmpty()
    {
        var authErrorClient = new QueuedChatClient(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(
            [_ => throw new ClientResultException("Unauthorized API key", new MockStatusResponse(401))]));

        var workflow = new CyclicTranslationWorkflow(
            authErrorClient, authErrorClient, authErrorClient,
            "S", "T", "R");

        await Assert.ThrowsAsync<ClientResultException>(() =>
            workflow.ExecuteAsync(
                "[BLOK_001]\nTest\n[/BLOK_001]",
                [],
                styleGuide: "Hazır"));
        Assert.Equal(1, authErrorClient.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelledInsideExecutor_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var cancellingClient = new QueuedChatClient(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(
            [
                _ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            ]));

        var workflow = new CyclicTranslationWorkflow(
            cancellingClient, cancellingClient, cancellingClient,
            "S", "T", "R");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.ExecuteAsync(
                "[BLOK_001]\nTest\n[/BLOK_001]",
                [],
                styleGuide: "Hazır",
                cancellationToken: cts.Token));
    }

    [Fact]
    public void GlossatorAiOptions_DefaultsToSensitiveTelemetryDisabled()
    {
        var options = new GlossatorAiOptions();
        Assert.False(options.EnableSensitiveTelemetry);
    }

    private sealed class MockStatusResponse(int status) : PipelineResponse
    {
        private sealed class EmptyHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
            public override bool TryGetValue(string name, out string? value) { value = null; return false; }
            public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
        }

        private readonly EmptyHeaders _headers = new();
        public override int Status => status;
        public override string ReasonPhrase => string.Empty;
        protected override PipelineResponseHeaders HeadersCore => _headers;
        public override BinaryData Content => BinaryData.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);
        public override void Dispose() { }
    }
}
