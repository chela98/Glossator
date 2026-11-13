using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glossator.AI.Diagnostics;
using Glossator.AI.Extensions;
using Glossator.Cli.Orchestration;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Checkpoint;
using Glossator.Core.Models.Epub;
using Glossator.Core.Models.Glossary;
using Glossator.Core.Parsing;
using Glossator.Core.Rules;
using Glossator.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Glossator.Tests.CLI;

[Collection("GlobalDiagnosticsAndConsole")]
public class ParallelLocalizationEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ParallelLocalizationEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossator_engine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "engine.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private sealed class ConcurrentRoleChatClient(Func<IReadOnlyList<ChatMessage>, string> responder) : IChatClient
    {
        private int _callCount;
        public int CallCount => _callCount;
        public ConcurrentQueue<string> CapturedUserPrompts { get; } = new();

        public ConcurrentRoleChatClient(params string[] queuedResponses)
            : this(CreateQueuedResponder(queuedResponses))
        {
        }

        private static Func<IReadOnlyList<ChatMessage>, string> CreateQueuedResponder(string[] responses)
        {
            var queue = new ConcurrentQueue<string>(responses);
            return _ => queue.TryDequeue(out var next) ? next : string.Empty;
        }

        public ChatClientMetadata Metadata => new("EngineMock");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var list = messages.ToList();
            CapturedUserPrompts.Enqueue(list.LastOrDefault()?.Text ?? string.Empty);
            var text = responder(list);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            var list = messages.ToList();
            CapturedUserPrompts.Enqueue(list.LastOrDefault()?.Text ?? string.Empty);
            var text = responder(list);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private async Task<(ServiceProvider Provider, SqliteGlossaryRepository Repo, CheckpointStore Store)> BuildServiceProviderAsync(
        ConcurrentRoleChatClient styleClient,
        ConcurrentRoleChatClient transClient,
        ConcurrentRoleChatClient revClient,
        Action<IServiceCollection>? configureServices = null)
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        await factory.InitializeDatabaseAsync();
        var repo = new SqliteGlossaryRepository(factory);

        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(factory);
        services.AddSingleton(repo);
        services.AddSingleton<CheckpointStore>();
        services.AddScoped(_ => new TermUsageTracker("default"));
        services.AddScoped<TermGlossingPostProcessor>();
        services.AddKeyedSingleton<IChatClient>(AiServiceExtensions.KeyStyleAnalyst, styleClient);
        services.AddKeyedSingleton<IChatClient>(AiServiceExtensions.KeyTranslator, transClient);
        services.AddKeyedSingleton<IChatClient>(AiServiceExtensions.KeyReviewer, revClient);
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return (provider, repo, provider.GetRequiredService<CheckpointStore>());
    }

    private static Task RunWithTestConsoleAsync(Func<Task> action) =>
        RunWithTestConsoleCaptureAsync(action);

    private static async Task<string> RunWithTestConsoleCaptureAsync(Func<Task> action)
    {
        var originalConsole = AnsiConsole.Console;
        using var testConsole = new TestConsole();
        testConsole.Profile.Width = 240;
        AnsiConsole.Console = testConsole;
        try
        {
            await action();
            return testConsole.Output;
        }
        finally
        {
            AnsiConsole.Console = originalConsole;
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    [Fact]
    public async Task RunAsync_WhenSelectedChaptersEmpty_ReturnsImmediatelyWithoutCallingServices()
    {
        // Arrange
        var styleClient = new ConcurrentRoleChatClient(_ => "STYLE");
        var transClient = new ConcurrentRoleChatClient(_ => "TRANS");
        var revClient = new ConcurrentRoleChatClient(_ => "REV");
        var (sp, _, _) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;
        var engine = new ParallelLocalizationEngine(sp);
        var package = new EpubPackage { Title = "Empty Book", OpfDirectory = _tempDir };

        // Act
        await engine.RunAsync(package, [], concurrency: 2, isFresh: false, requestedSectionCount: null, isTestMode: false);

        // Assert
        Assert.Equal(0, styleClient.CallCount);
        Assert.Equal(0, transClient.CallCount);
        Assert.Equal(0, revClient.CallCount);
    }

    [Fact]
    public async Task RunAsync_CompletesChapter_WritesStrictXhtmlCreatesDoneDeletesPartAndPersistsCheckpoints()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "complete_book");
        Directory.CreateDirectory(opfDir);
        var chapterPath = Path.Combine(opfDir, "ch01.xhtml");
        File.WriteAllText(chapterPath, """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Ch 1</title></head>
              <body>
                <h1>Chapter One</h1>
                <p>Distributed systems coordinate state reliably over networks.</p>
              </body>
            </html>
            """);
        // Önceden kalmış .part dosyasının başarıyla silindiğini doğrulamak için bir .part bırak
        var expectedOut = Path.Combine(opfDir, "ch01_tr.xhtml");
        File.WriteAllText(expectedOut + ".part", "stale partial");

        var styleClient = new ConcurrentRoleChatClient(_ => "Akıcı mühendislik dili.");
        var transClient = new ConcurrentRoleChatClient(_ =>
            "[BLOK_000]\nBölüm Bir\n[/BLOK_000]\n[BLOK_001]\nDağıtık sistemler ağ üzerinden durumu güvenle koordine eder.\n[/BLOK_001]");
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;
        var engine = new ParallelLocalizationEngine(sp);

        var chapterItem = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "Engine Book", OpfDirectory = opfDir, Spine = [chapterItem] };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [chapterItem], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert
        Assert.True(File.Exists(expectedOut));
        Assert.True(File.Exists(expectedOut + ".done"));
        Assert.False(File.Exists(expectedOut + ".part"));

        var xhtml = File.ReadAllText(expectedOut);
        Assert.Contains("lang=\"tr\"", xhtml);
        Assert.Contains("Dağıtık sistemler ağ üzerinden durumu güvenle koordine eder.", xhtml);

        var checkpoints = await store.GetCompletedCheckpointsAsync("Engine Book", "ch01.xhtml");
        Assert.True(checkpoints.ContainsKey(-1)); // StyleGuide checkpoint
        Assert.True(checkpoints.ContainsKey(0));
        Assert.True(checkpoints.ContainsKey(1));
    }

    [Fact]
    public async Task RunAsync_WhenFreshFlagSet_ClearsPreviousCheckpointsAndDonePartMarkersBeforeTranslating()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "fresh_book");
        Directory.CreateDirectory(opfDir);
        var chapterPath = Path.Combine(opfDir, "ch01.xhtml");
        File.WriteAllText(chapterPath, "<html><body><p>Original text paragraph needing translation.</p></body></html>");

        var outPath = Path.Combine(opfDir, "ch01_tr.xhtml");
        File.WriteAllText(outPath + ".done", "old-done");
        File.WriteAllText(outPath + ".part", "old-part");

        var styleClient = new ConcurrentRoleChatClient(_ => "Yeni üslup");
        var transClient = new ConcurrentRoleChatClient(_ => "[BLOK_000]\nTaze çevrilmiş paragraf.\n[/BLOK_000]");
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        var (_, segs) = HtmlSegmenter.SegmentFile(chapterPath);
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "FreshBook",
            ChapterFile = "ch01.xhtml",
            SegmentIndex = -1,
            OriginalHash = "h",
            TagName = "styleguide",
            OriginalText = "StyleGuide",
            TranslatedText = "Eski üslup"
        });
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "FreshBook",
            ChapterFile = "ch01.xhtml",
            SegmentIndex = segs[0].Index,
            OriginalHash = SegmentCheckpoint.ComputeHash(segs[0].MaskedText),
            TagName = segs[0].TagName,
            OriginalText = segs[0].MaskedText,
            TranslatedText = "Eski çeviri"
        });
        await store.FlushAsync();

        var engine = new ParallelLocalizationEngine(sp);
        var chapterItem = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "FreshBook", OpfDirectory = opfDir, Spine = [chapterItem] };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [chapterItem], concurrency: 1, isFresh: true, requestedSectionCount: null, isTestMode: false));

        // Assert: Eski checkpoint temizlendiği için StyleAnalyst & Translator mutlaka yeniden çalışmalı
        Assert.Equal(1, styleClient.CallCount);
        Assert.Equal(1, transClient.CallCount);
        Assert.Contains("Taze çevrilmiş paragraf.", File.ReadAllText(outPath));
    }

    [Fact]
    public async Task RunAsync_WhenCheckpointCachedForFirstSection_SkipsLlmAndRehydratesProgressiveTermTracker()
    {
        // Arrange: Bölüm 2 kısımdan oluşsun; ilk kısım önbellekte olsun, ikincisi yeniden çevrilsin.
        // Her iki kısım da ProgressiveGloss "Vector Store" terimi içersin -> 2. kısımda Saf Türkçe beklenir!
        var opfDir = Path.Combine(_tempDir, "resume_book");
        Directory.CreateDirectory(opfDir);
        var chapterPath = Path.Combine(opfDir, "ch01.xhtml");
        File.WriteAllText(chapterPath, """
            <html><body>
              <h1>First Part</h1>
              <p>A Vector Store holds embeddings reliably.</p>
              <h1>Second Part</h1>
              <p>Querying the Vector Store executes rapidly.</p>
            </body></html>
            """);

        var styleClient = new ConcurrentRoleChatClient(_ => "SHOULD_NOT_BE_CALLED");
        var transClient = new ConcurrentRoleChatClient(_ =>
            "[BLOK_002]\nİkinci Kısım\n[/BLOK_002]\n[BLOK_003]\nVektör Deposu hızlı sorgulanır.\n[/BLOK_003]");
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, repo, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        await repo.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Vector Store",
            TurkishTerm = "Vektör Deposu",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        var (_, segs) = HtmlSegmenter.SegmentFile(chapterPath);
        var groups = HtmlSectionGrouper.Group(segs, "ch01");
        Assert.Equal(2, groups.Count);

        // StyleGuide (-1) ve 1. Section'daki tüm segmentler için checkpoint kaydet
        await store.EnqueueAsync(new SegmentCheckpoint
        {
            BookId = "ResumeBook",
            ChapterFile = "ch01.xhtml",
            SegmentIndex = -1,
            OriginalHash = "h",
            TagName = "styleguide",
            OriginalText = "StyleGuide",
            TranslatedText = "Önbellekli Kılavuz"
        });
        foreach (var seg in groups[0].Segments)
        {
            await store.EnqueueAsync(new SegmentCheckpoint
            {
                BookId = "ResumeBook",
                ChapterFile = "ch01.xhtml",
                SegmentIndex = seg.Index,
                OriginalHash = SegmentCheckpoint.ComputeHash(seg.MaskedText),
                TagName = seg.TagName,
                OriginalText = seg.MaskedText,
                TranslatedText = $"ÖNBELLEK_{seg.Index}"
            });
        }
        await store.FlushAsync();

        var engine = new ParallelLocalizationEngine(sp);
        var chapterItem = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "ResumeBook", OpfDirectory = opfDir, Spine = [chapterItem] };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [chapterItem], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert
        Assert.Equal(0, styleClient.CallCount); // StyleGuide checkpoint'ten alındı
        Assert.Equal(1, transClient.CallCount); // Yalnızca 2. kısım için Translator çağrıldı
        Assert.True(transClient.CapturedUserPrompts.TryDequeue(out var prompt));
        // Önbellekteki 1. kısım rehydrate edildiği için 2. kısımda parantezsiz Saf Türkçe istenmeli
        Assert.Contains("Vector Store => Vektör Deposu", prompt);
        Assert.DoesNotContain("Vector Store => Vektör Deposu (Vector Store)", prompt);

        var outText = File.ReadAllText(Path.Combine(opfDir, "ch01_tr.xhtml"));
        Assert.Contains("ÖNBELLEK_0", outText);
        Assert.Contains("ÖNBELLEK_1", outText);
        Assert.Contains("Vektör Deposu hızlı sorgulanır.", outText);
        Assert.True(File.Exists(Path.Combine(opfDir, "ch01_tr.xhtml.done")));
    }

    [Fact]
    public async Task RunAsync_WhenTestModeLimitsToTwoSections_TranslatesMaxTwoSectionsAndKeepsPartWithoutDone()
    {
        // Arrange: 3 bölümlük (section) dosyada isTestMode: true yalnızca ilk 2 section'ı işler -> 3. section çevrilmez
        var opfDir = Path.Combine(_tempDir, "testmode_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), """
            <html><body>
              <h1>Section One</h1>
              <p>First body content block.</p>
              <h1>Section Two</h1>
              <p>Second body content block.</p>
              <h1>Section Three</h1>
              <p>Third body block should remain untranslated in test mode.</p>
            </body></html>
            """);

        var styleClient = new ConcurrentRoleChatClient(_ => "Teknik");
        var transClient = new ConcurrentRoleChatClient(
            "[BLOK_000]\nBirinci Kısım\n[/BLOK_000]\n[BLOK_001]\nİlk gövde bloğu çevrildi.\n[/BLOK_001]",
            "[BLOK_002]\nİkinci Kısım\n[/BLOK_002]\n[BLOK_003]\nİkinci gövde bloğu çevrildi.\n[/BLOK_003]");
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, _) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;
        var engine = new ParallelLocalizationEngine(sp);

        var chapterItem = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "TestModeBook", OpfDirectory = opfDir, Spine = [chapterItem] };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [chapterItem], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: true));

        // Assert: Kısmi/test modunda nihai _tr.xhtml yazılmaz, yalnızca .part dosyası üretilir
        Assert.Equal(2, transClient.CallCount);
        var baseOut = Path.Combine(opfDir, "ch01_tr.xhtml");
        Assert.False(File.Exists(baseOut));
        Assert.True(File.Exists(baseOut + ".part"));
        Assert.False(File.Exists(baseOut + ".done"));
        var outContent = File.ReadAllText(baseOut + ".part");
        Assert.Contains("İlk gövde bloğu çevrildi.", outContent);
        Assert.Contains("İkinci gövde bloğu çevrildi.", outContent);
        Assert.Contains("Third body block should remain untranslated in test mode.", outContent);
    }

    [Fact]
    public async Task RunAsync_WithParallelConcurrencyTwo_IsolatesTermTrackerScopesPerChapter()
    {
        // Arrange: 2 bölüm paralel çalıştırılır; her ikisinde de ProgressiveGloss "Embedding" geçer -> her iki chapter da Hibrit form almalı!
        var opfDir = Path.Combine(_tempDir, "parallel_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), "<html><body><p>Chapter one Embedding guide.</p></body></html>");
        File.WriteAllText(Path.Combine(opfDir, "ch02.xhtml"), "<html><body><p>Chapter two Embedding guide.</p></body></html>");

        var styleClient = new ConcurrentRoleChatClient(_ => "Teknik üslup.");
        var transClient = new ConcurrentRoleChatClient(_ => "[BLOK_000]\nGömme (Embedding) rehberi.\n[/BLOK_000]");
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, repo, _) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        await repo.AddOrUpdateTermAsync(new GlossaryEntry
        {
            EnglishTerm = "Embedding",
            TurkishTerm = "Gömme",
            Policy = GlossingPolicy.ProgressiveGloss,
            IsReviewed = true
        });

        var engine = new ParallelLocalizationEngine(sp);
        var ch1 = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var ch2 = new EpubItem { Id = "ch2", Href = "ch02.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "ParallelBook", OpfDirectory = opfDir, Spine = [ch1, ch2] };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [ch1, ch2], concurrency: 2, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert: Her iki bölüm de kendi scoped TermUsageTracker'ını kullanarak Hibrit terim eşlemesi üretmiş olmalı
        Assert.True(File.Exists(Path.Combine(opfDir, "ch01_tr.xhtml.done")));
        Assert.True(File.Exists(Path.Combine(opfDir, "ch02_tr.xhtml.done")));
        var prompts = transClient.CapturedUserPrompts.ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.All(prompts, p => Assert.Contains("Embedding => Gömme (Embedding)", p));
    }

    [Fact]
    public async Task RunAsync_WithLiveExchangeRateService_UsesFetchedRateInSessionCostReport()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "rate_live_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), "<html><body><p>Cost report test paragraph.</p></body></html>");

        using var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rates":{"TRY":45.00}}""", Encoding.UTF8, "application/json")
            });
        using var httpClient = new HttpClient(handler);
        using var rateService = new ExchangeRateService(httpClient: httpClient);

        var styleClient = new ConcurrentRoleChatClient(_ => "Üslup özeti.");
        var transClient = new ConcurrentRoleChatClient(_ =>
        {
            GlossatorDiagnostics.RecordStageUsage("Translator", 1_000_000, 0, 0);
            return "[BLOK_000]\nMaliyet raporu test paragrafı.\n[/BLOK_000]";
        });
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, _) = await BuildServiceProviderAsync(styleClient, transClient, revClient, services =>
        {
            services.AddSingleton<IExchangeRateService>(rateService);
        });
        await using var _sp = sp;

        var engine = new ParallelLocalizationEngine(sp);
        var ch1 = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "LiveRateBook", OpfDirectory = opfDir, Spine = [ch1] };

        // Act
        var output = await RunWithTestConsoleCaptureAsync(() =>
            engine.RunAsync(package, [ch1], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert
        var expectedUsd = ComputeDefaultGeminiUsdCost(GlossatorDiagnostics.GetStageStatsSnapshot());
        var expectedTry = expectedUsd * 45.00;
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "${0:F4}", expectedUsd), output);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "~{0:F2}", expectedTry), output);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "1 USD = {0:F4}", 45.00), output);
        Assert.Contains("Frankfurter (ECB) - Canlı API", output);
    }

    private static double ComputeDefaultGeminiUsdCost(IReadOnlyDictionary<string, StageUsageStats> snapshot)
    {
        double total = 0;
        foreach (var s in snapshot.Values)
        {
            var inTok = Interlocked.Read(ref s.InputTokens);
            var billedOut = Interlocked.Read(ref s.BilledOutputTokens);
            total += (inTok / 1_000_000.0 * 0.30) + (billedOut / 1_000_000.0 * 2.50);
        }

        return total;
    }

    [Fact]
    public async Task RunAsync_WhenExchangeRateApisFail_FallsBackToConfiguredStudioRateInSessionCostReport()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "rate_fallback_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), "<html><body><p>Fallback cost test paragraph.</p></body></html>");

        using var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        using var rateService = new ExchangeRateService(httpClient: httpClient);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:UsdToTryRate"] = "41.50"
            })
            .Build();

        var styleClient = new ConcurrentRoleChatClient(_ => "Üslup özeti.");
        var transClient = new ConcurrentRoleChatClient(_ =>
        {
            GlossatorDiagnostics.RecordStageUsage("Translator", 1_000_000, 0, 0);
            return "[BLOK_000]\nYedek kur test paragrafı.\n[/BLOK_000]";
        });
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, _) = await BuildServiceProviderAsync(styleClient, transClient, revClient, services =>
        {
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<IExchangeRateService>(rateService);
        });
        await using var _sp = sp;

        var engine = new ParallelLocalizationEngine(sp);
        var ch1 = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "FallbackRateBook", OpfDirectory = opfDir, Spine = [ch1] };

        // Act
        var output = await RunWithTestConsoleCaptureAsync(() =>
            engine.RunAsync(package, [ch1], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert
        var expectedUsd = ComputeDefaultGeminiUsdCost(GlossatorDiagnostics.GetStageStatsSnapshot());
        var expectedTry = expectedUsd * 41.50;
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "${0:F4}", expectedUsd), output);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "~{0:F2}", expectedTry), output);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "1 USD = {0:F4}", 41.50), output);
        Assert.Contains("Yerel Yedek - Yedek Kur", output);
    }

    [Fact]
    public async Task RunAsync_WhenBookTitleLegalAddressAndEmailPreservedVerbatim_CompletesWithDoneAndNoPartStuck()
    {
        // Arrange: copyright-page01 ve preface01'de .part takılmasına yol açan gerçek segment kalıpları
        var opfDir = Path.Combine(_tempDir, "copyright_contact_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "copyright-page01.html"), """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Agentic GraphRAG</title></head>
              <body>
                <h1>Agentic GraphRAG</h1>
                <p>Published by <span class="publishername">O’Reilly Media, Inc.</span></p>
              </body>
            </html>
            """);
        File.WriteAllText(Path.Combine(opfDir, "preface01.html"), """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Preface</title></head>
              <body>
                <h1>How to Contact Us</h1>
                <ul class="simplelist">
                  <li>O’Reilly Media, Inc.</li>
                  <li>141 Stony Circle, Suite 195</li>
                  <li>Santa Rosa, CA 95401</li>
                  <li><a class="email" href="mailto:support@oreilly.com"><em>support@oreilly.com</em></a></li>
                </ul>
              </body>
            </html>
            """);

        var styleClient = new ConcurrentRoleChatClient(_ => "Teknik üslup.");
        var transClient = new ConcurrentRoleChatClient(messages =>
        {
            var prompt = messages.LastOrDefault()?.Text ?? string.Empty;
            if (prompt.Contains("BLOK_000") && prompt.Contains("Agentic GraphRAG"))
            {
                return """
                    [BLOK_000]
                    Agentic GraphRAG
                    [/BLOK_000]
                    [BLOK_001]
                    Yayımlayan: <span class="publishername">O’Reilly Media, Inc.</span>
                    [/BLOK_001]
                    """;
            }

            return """
                [BLOK_000]
                Bizimle Nasıl İletişime Geçebilirsiniz
                [/BLOK_000]
                [BLOK_001]
                O’Reilly Media, Inc.
                [/BLOK_001]
                [BLOK_002]
                141 Stony Circle, Suite 195
                [/BLOK_002]
                [BLOK_003]
                Santa Rosa, CA 95401
                [/BLOK_003]
                [BLOK_004]
                <a class="email" href="mailto:support@oreilly.com"><em>support@oreilly.com</em></a>
                [/BLOK_004]
                """;
        });
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        var engine = new ParallelLocalizationEngine(sp);
        var chCopyright = new EpubItem { Id = "copy", Href = "copyright-page01.html", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var chPreface = new EpubItem { Id = "pref", Href = "preface01.html", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Auxiliary };
        var package = new EpubPackage
        {
            Title = "Agentic GraphRAG",
            Author = "Anthony Alcaraz and Sam Julien",
            OpfDirectory = opfDir,
            Spine = [chCopyright, chPreface]
        };

        // Act
        await RunWithTestConsoleAsync(() =>
            engine.RunAsync(package, [chCopyright, chPreface], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false));

        // Assert: Her iki dosya da .done üretmeli, .part bırakmamalı ve tüm segmentler 'Completed' kaydedilmeli
        var outCopy = Path.Combine(opfDir, "copyright-page01_tr.html");
        var outPref = Path.Combine(opfDir, "preface01_tr.html");

        Assert.True(File.Exists(outCopy + ".done"));
        Assert.False(File.Exists(outCopy + ".part"));
        Assert.True(File.Exists(outPref + ".done"));
        Assert.False(File.Exists(outPref + ".part"));

        var copyCheckpoints = await store.GetCompletedCheckpointsAsync("Agentic GraphRAG", "copyright-page01.html");
        Assert.Equal("Completed", copyCheckpoints[0].StageStatus);

        var prefCheckpoints = await store.GetCompletedCheckpointsAsync("Agentic GraphRAG", "preface01.html");
        Assert.Equal(5, prefCheckpoints.Keys.Count(k => k >= 0));
    }

    [Fact]
    public async Task RunAsync_WhenCancelledMidChapter_FlushesCompletedSectionsViaFinally()
    {
        // Arrange: 2 kesitli bölüm; 1. kesit tamamlanır, 2. kesitte iptal tetiklenir
        var opfDir = Path.Combine(_tempDir, "cancel_flush_book");
        Directory.CreateDirectory(opfDir);
        File.WriteAllText(Path.Combine(opfDir, "ch01.xhtml"), """
            <html><body>
              <h1>Part 1</h1>
              <p>First section text completes before cancel.</p>
              <h1>Part 2</h1>
              <p>Second section triggers cancellation.</p>
            </body></html>
            """);

        using var cts = new CancellationTokenSource();
        var styleClient = new ConcurrentRoleChatClient(_ => "Teknik üslup.");
        var transCall = 0;
        var transClient = new ConcurrentRoleChatClient(_ =>
        {
            if (Interlocked.Increment(ref transCall) == 1)
            {
                return "[BLOK_000]\nKısım 1\n[/BLOK_000]\n[BLOK_001]\nİlk kesit iptalden önce tamamlandı.\n[/BLOK_001]";
            }

            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var revClient = new ConcurrentRoleChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, _, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        var engine = new ParallelLocalizationEngine(sp);
        var chapterItem = new EpubItem { Id = "ch1", Href = "ch01.xhtml", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage { Title = "CancelFlushBook", OpfDirectory = opfDir, Spine = [chapterItem] };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunWithTestConsoleAsync(() =>
                engine.RunAsync(package, [chapterItem], concurrency: 1, isFresh: false, requestedSectionCount: null, isTestMode: false, cts.Token)));

        // Assert: 1. kesit checkpoint'leri finally bloğu sayesinde SQLite'a flush edilmiş olmalı, .done üretilmemeli
        var checkpoints = await store.GetCompletedCheckpointsAsync("CancelFlushBook", "ch01.xhtml");
        Assert.True(checkpoints.ContainsKey(-1));
        Assert.True(checkpoints.ContainsKey(0));
        Assert.True(checkpoints.ContainsKey(1));
        Assert.False(File.Exists(Path.Combine(opfDir, "ch01_tr.xhtml.done")));
        Assert.False(File.Exists(Path.Combine(opfDir, "ch01_tr.xhtml")));
    }
}
