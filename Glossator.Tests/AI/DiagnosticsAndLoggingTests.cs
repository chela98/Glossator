namespace Glossator.Tests.AI;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Glossator.AI.Diagnostics;
using Glossator.AI.Models;
using Glossator.AI.Workflows;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

public class DiagnosticsAndLoggingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteGlossaryRepository _repository;

    public DiagnosticsAndLoggingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_diag_test_{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _connectionFactory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(_connectionFactory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp directory
        }
    }

    private sealed class MockChatClient(Func<IEnumerable<ChatMessage>, string> responseFactory) : IChatClient
    {
        public ChatClientMetadata Metadata => new("Mock");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = responseFactory(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void GlossatorDiagnostics_InitializesTelemetrySourcesAndCounters()
    {
        // Assert: Statik tanımlar
        Assert.Equal("Glossator", GlossatorDiagnostics.SourceName);
        Assert.NotNull(GlossatorDiagnostics.ActivitySource);
        Assert.NotNull(GlossatorDiagnostics.Meter);
        Assert.NotNull(GlossatorDiagnostics.SectionsTranslatedCounter);
        Assert.NotNull(GlossatorDiagnostics.ChaptersTranslatedCounter);
        Assert.NotNull(GlossatorDiagnostics.ReviewerPatchesCounter);
        Assert.NotNull(GlossatorDiagnostics.ReviewerRejectionsCounter);
        Assert.NotNull(GlossatorDiagnostics.RecoveryAttemptsCounter);
        Assert.NotNull(GlossatorDiagnostics.ActiveWorkersCounter);

        // Assert: Gerçek Metrik Kaydı Testi
        var recorded = new System.Collections.Concurrent.ConcurrentBag<(string Name, long Value)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (inst, listener) =>
            {
                if (inst.Meter.Name == GlossatorDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(inst);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, val, tags, state) =>
        {
            recorded.Add((inst.Name, val));
        });
        meterListener.Start();

        GlossatorDiagnostics.ChaptersTranslatedCounter.Add(1);
        GlossatorDiagnostics.RecoveryAttemptsCounter.Add(3);

        Assert.Contains(recorded, r => r.Name == "glossator.chapters.translated" && r.Value == 1);
        Assert.Contains(recorded, r => r.Name == "glossator.recovery.attempts" && r.Value == 3);
    }

    [Fact]
    public async Task BookLocalizationPipeline_EmitsStructuredLogsAndTracksActivities()
    {
        // Arrange
        var testLogger = new TestLogger<BookLocalizationPipeline>();
        var translatorMock = new MockChatClient(msgs => "[BLOK_001] Merhaba dünya!");
        var reviewerMock = new MockChatClient(msgs => """{"is_approved": true, "patches": []}""");
        var postProcessor = new TermGlossingPostProcessor(_repository, new TermUsageTracker("test"));

        var pipeline = new BookLocalizationPipeline(
            translatorMock,
            translatorMock,
            reviewerMock,
            postProcessor,
            stylePrompt: "Style Prompt",
            translatorPrompt: "Translator Prompt",
            reviewerPrompt: "Reviewer Prompt",
            logger: testLogger);

        var section = new HtmlSection
        {
            SectionIndex = 0,
            Title = "Giriş",
            Segments = [new HtmlSegment { Index = 1, TagName = "p", MaskedText = "Hello world!" }]
        };

        var recordedActivities = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => recordedActivities.Add(a.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        var result = await pipeline.TranslateSectionAsync(section);

        // Assert
        Assert.NotNull(result);
        Assert.True(testLogger.LoggedMessages.Count > 0, "Pipeline should emit structured log entries to ILogger");
        Assert.Contains(testLogger.LoggedMessages, m => m.Contains("PreProcessor"));
        Assert.Contains(testLogger.LoggedMessages, m => m.Contains("PostProcessor"));
        Assert.Contains("PreProcessor", recordedActivities);
        Assert.Contains("TranslateLogicalSection", recordedActivities);
    }

    [Fact]
    public async Task CyclicTranslationWorkflow_WhenCancelled_ThrowsOperationCanceledExceptionImmediately()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockClient = new MockChatClient(_ => "Mock");
        var workflow = new CyclicTranslationWorkflow(
            mockClient,
            mockClient,
            mockClient,
            "Style Prompt",
            "Translator Prompt",
            "Reviewer Prompt");

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            workflow.ExecuteAsync("Test text", [], null, "", cts.Token));
    }
}
