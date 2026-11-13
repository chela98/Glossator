using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Glossator.AI.Models;
using Glossator.AI.Workflows;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class BookLocalizationPipelineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteGlossaryRepository _repository;
    private readonly TermUsageTracker _tracker;
    private readonly TermGlossingPostProcessor _postProcessor;

    public BookLocalizationPipelineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"glossator_pipeline_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(_dbPath);
        factory.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new SqliteGlossaryRepository(factory);
        _tracker = new TermUsageTracker("ch01");
        _postProcessor = new TermGlossingPostProcessor(_repository, _tracker);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch { }
    }

    private sealed class MockQueuedClient(Queue<Func<IReadOnlyList<ChatMessage>, string>> handlers) : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, string>> _handlers = handlers;
        public int CallCount { get; private set; }
        public List<string> UserPrompts { get; } = [];
        public ChatClientMetadata Metadata => new("MockPipelineClient");

        public MockQueuedClient(params string[] staticResponses)
            : this(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(staticResponses.Select(r => (Func<IReadOnlyList<ChatMessage>, string>)(_ => r))))
        {
        }

        public static MockQueuedClient FromHandlers(params Func<IReadOnlyList<ChatMessage>, string>[] handlers)
            => new(new Queue<Func<IReadOnlyList<ChatMessage>, string>>(handlers));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = messages.ToList();
            UserPrompts.Add(list.LastOrDefault()?.Text ?? string.Empty);
            var text = _handlers.Count > 0 ? _handlers.Dequeue()(list) : string.Empty;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = messages.ToList();
            UserPrompts.Add(list.LastOrDefault()?.Text ?? string.Empty);
            var text = _handlers.Count > 0 ? _handlers.Dequeue()(list) : string.Empty;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ListLogger<T> : ILogger<T>, IDisposable
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private static HtmlSection CreateTwoBlockSection()
    {
        var dom = new HtmlParser().ParseDocument("<html><body><h1>Title</h1><p>One</p><p>Two</p></body></html>");
        var ps = dom.QuerySelectorAll("p");
        return new HtmlSection
        {
            SectionIndex = 1,
            SectionKey = "sec_1",
            Title = "Consensus Intro",
            Segments =
            [
                new HtmlSegment { Index = 1, TagName = "p", OriginalInnerHtml = "Consensus algorithms coordinate Vector Store.", MaskedText = "Consensus algorithms coordinate Vector Store.", DomElement = ps[0] },
                new HtmlSegment { Index = 2, TagName = "p", OriginalInnerHtml = "They tolerate faults.", MaskedText = "They tolerate faults.", DomElement = ps[1] }
            ]
        };
    }

    [Fact]
    public async Task AnalyzeStyleAsync_AndBeginChapterScope_CachesStyleGuideAndResetsPerChapter()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Vector Store",
            TurkishTerm = "Vektör Deposu",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        var styleClient = new MockQueuedClient("Net teknik üslup.", "Bölüm 2 üslubu.");
        var transClient = new MockQueuedClient(
            "[BLOK_001]\nBölüm 1 Vektör Deposu (Vector Store).\n[/BLOK_001]\n[BLOK_002]\nHata toleransı.\n[/BLOK_002]",
            "[BLOK_001]\nBölüm 2 Vektör Deposu (Vector Store).\n[/BLOK_001]\n[BLOK_002]\nHata toleransı.\n[/BLOK_002]");
        var revClient = new MockQueuedClient(
            """{"isApproved": true, "patches": []}""",
            """{"isApproved": true, "patches": []}""");

        var pipeline = new BookLocalizationPipeline(
            styleClient, transClient, revClient,
            _postProcessor, "STYLE", "TRANS", "REV");

        // Act: 1. analiz ve bölüm 1 çevirisi (AnalyzeStyleAsync önbelleğe alındığı için Ch1 içinde tekrar çağrılmamalı)
        var style = await pipeline.AnalyzeStyleAsync("Sample chapter content");
        await pipeline.TranslateSectionAsync(CreateTwoBlockSection());

        // Yeni bölüm -> önbellek ve tracker sıfırlanmalı
        pipeline.BeginChapterScope("ch02");
        await pipeline.TranslateSectionAsync(CreateTwoBlockSection());

        // Assert
        Assert.Equal("Net teknik üslup.", style);
        Assert.Equal(2, styleClient.CallCount);
        Assert.Contains("Net teknik üslup.", transClient.UserPrompts[0]);
        Assert.Contains("Vektör Deposu (Vector Store)", transClient.UserPrompts[0]);
        Assert.Contains("Bölüm 2 üslubu.", transClient.UserPrompts[1]);
        Assert.Contains("Vektör Deposu (Vector Store)", transClient.UserPrompts[1]);
    }

    [Fact]
    public async Task TranslateSectionAsync_WhenWorkflowReturnsEmpty_ExecutesDirectTranslatorFallback()
    {
        // Arrange: İlk workflow çağrısında fırlat (CyclicWorkflow "" dönsün), fallback Translator kurtarsın
        var styleClient = new MockQueuedClient();
        var transClient = MockQueuedClient.FromHandlers(
            _ => throw new InvalidOperationException("Primary workflow hiccup"),
            _ => "[BLOK_001]\nYedek çeviri 1.\n[/BLOK_001]\n[BLOK_002]\nYedek çeviri 2.\n[/BLOK_002]");
        var revClient = new MockQueuedClient();

        var activities = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => activities.Add(act.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        var pipeline = new BookLocalizationPipeline(
            styleClient, transClient, revClient,
            _postProcessor, "STYLE", "TRANS", "REV");
        pipeline.SetStyleGuide("Hazır Kılavuz");

        var section = CreateTwoBlockSection();

        // Act
        var result = await pipeline.TranslateSectionAsync(section);

        // Assert
        Assert.Equal(2, transClient.CallCount);
        Assert.Equal("Yedek çeviri 1.", result.Segments[0].TranslatedText);
        Assert.Equal("Yedek çeviri 2.", result.Segments[1].TranslatedText);
        Assert.Contains("DirectTranslatorFallback", activities);
    }

    [Fact]
    public async Task TranslateSectionAsync_WhenBlockMissingFromOutput_TriggersMicroRecoveryAndFillsDeltaBlock()
    {
        // Arrange: Attempt 1 & 2'de C# pre-filter 0 maliyetle reddeder; Attempt 3'te pre-filter devreden çıkar ve Reviewer onaylar -> MicroRecovery devreye girer
        var styleClient = new MockQueuedClient();
        var transClient = new MockQueuedClient(
            "[BLOK_001]\nBirinci blok tamam.\n[/BLOK_001]",
            "[BLOK_001]\nBirinci blok tamam.\n[/BLOK_001]",
            "[BLOK_001]\nBirinci blok tamam.\n[/BLOK_001]",
            "[BLOK_002]\nKurtarılan ikinci blok.\n[/BLOK_002]");
        var revClient = new MockQueuedClient("""{"isApproved": true, "patches": []}""");

        var logs = new List<PipelineStepLog>();
        var logger = new ListLogger<BookLocalizationPipeline>();
        var activities = new List<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Glossator.AI.Diagnostics.GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => activities.Add(act.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        var pipeline = new BookLocalizationPipeline(
            styleClient, transClient, revClient,
            _postProcessor, "STYLE", "TRANS", "REV",
            logger: logger);
        pipeline.SetStyleGuide("Hazır Kılavuz");
        pipeline.StepProgress += log => logs.Add(log);

        var section = CreateTwoBlockSection();

        // Act
        var result = await pipeline.TranslateSectionAsync(section);

        // Assert
        Assert.Equal(4, transClient.CallCount);
        Assert.Equal("Birinci blok tamam.", result.Segments[0].TranslatedText);
        Assert.Equal("Kurtarılan ikinci blok.", result.Segments[1].TranslatedText);
        Assert.Contains("MicroRecovery", activities);
        Assert.Contains(logs, l => l.Stage == "RecoverySuccess");
        Assert.Contains(logger.Messages, m => m.Contains("2/2 blocks translated"));
    }

    [Fact]
    public async Task RegisterCachedSectionAsync_UpdatesPostProcessorTrackerForSubsequentSections()
    {
        // Arrange
        await _repository.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Vector Store",
            TurkishTerm = "Vektör Deposu",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        var dummy = new MockQueuedClient();
        var pipeline = new BookLocalizationPipeline(dummy, dummy, dummy, _postProcessor, "S", "T", "R");

        // Act
        await pipeline.RegisterCachedSectionAsync("Cached intro about Vector Store.");

        // Assert
        Assert.Equal(1, _tracker.GetCurrentCount("Vector Store"));
    }
}
