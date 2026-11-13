using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Glossator.AI.Extensions;
using Glossator.App.Engine;
using Glossator.App.Infrastructure;
using Glossator.Core.Data;
using Glossator.Core.Glossary;
using Glossator.Core.Models.Epub;
using Glossator.Core.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Glossator.App.Tests;

[Collection("GlobalDiagnosticsAndConsole")]
public sealed class StudioLocalizationEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public StudioLocalizationEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossator_studio_engine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "studio_engine.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private sealed class StubChatClient(Func<IReadOnlyList<ChatMessage>, string> responder) : IChatClient
    {
        public ChatClientMetadata Metadata => new("StudioEngineMock");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = responder(messages.ToList());
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = responder(messages.ToList());
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private async Task<(ServiceProvider Provider, CheckpointStore Store)> BuildServiceProviderAsync(
        StubChatClient styleClient,
        StubChatClient transClient,
        StubChatClient revClient)
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

        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<CheckpointStore>());
    }

    [Fact]
    public async Task RunAsync_WhenBookTitleLegalNameAddressAndEmailPreserved_CompletesChapterWithoutPart()
    {
        // Arrange
        var opfDir = Path.Combine(_tempDir, "studio_ok");
        Directory.CreateDirectory(opfDir);

        File.WriteAllText(Path.Combine(opfDir, "copyright-page01.html"), """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Agentic GraphRAG</title></head>
              <body>
                <h1>Agentic GraphRAG</h1>
                <p>Copyright © 2026 Anthony Alcaraz and Sam Julien.</p>
              </body>
            </html>
            """);

        File.WriteAllText(Path.Combine(opfDir, "preface01.html"), """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Preface</title></head>
              <body>
                <h1>O’Reilly Online Learning</h1>
                <ul class="simplelist">
                  <li>O’Reilly Media, Inc.</li>
                  <li>141 Stony Circle, Suite 195</li>
                  <li>Santa Rosa, CA 95401</li>
                  <li><a class="email" href="mailto:support@oreilly.com"><em>support@oreilly.com</em></a></li>
                </ul>
              </body>
            </html>
            """);

        // Eski yarım çalışma kalıntılarını bırak
        File.WriteAllText(Path.Combine(opfDir, "copyright-page01_tr.html.part"), "stale");
        File.WriteAllText(Path.Combine(opfDir, "preface01_tr.html.part"), "stale");

        var styleClient = new StubChatClient(_ => "Teknik üslup.");
        var transClient = new StubChatClient(messages =>
        {
            var prompt = messages.LastOrDefault()?.Text ?? string.Empty;
            if (prompt.Contains("Agentic GraphRAG"))
            {
                return """
                    [BLOK_000]
                    Agentic GraphRAG
                    [/BLOK_000]
                    [BLOK_001]
                    Telif Hakkı © 2026 Anthony Alcaraz ve Sam Julien.
                    [/BLOK_001]
                    """;
            }

            return """
                [BLOK_000]
                O’Reilly Online Learning
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
        var revClient = new StubChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        var engine = new StudioLocalizationEngine(sp);
        var logs = new ConcurrentQueue<string>();
        var states = new ConcurrentQueue<StudioProgressUpdate>();
        engine.LogMessageReceived += msg => logs.Enqueue(msg);
        engine.ProgressChanged += upd => states.Enqueue(upd);

        var ch1 = new EpubItem { Id = "copy", Href = "copyright-page01.html", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var ch2 = new EpubItem { Id = "pref", Href = "preface01.html", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Auxiliary };
        var package = new EpubPackage
        {
            Title = "Agentic GraphRAG",
            Author = "Anthony Alcaraz",
            OpfDirectory = opfDir,
            Spine = [ch1, ch2]
        };

        // Act
        await engine.RunAsync(package, [ch1, ch2], concurrency: 1, isFresh: false);

        // Assert
        var outCopy = Path.Combine(opfDir, "copyright-page01_tr.html");
        var outPref = Path.Combine(opfDir, "preface01_tr.html");

        Assert.True(File.Exists(outCopy + ".done"));
        Assert.False(File.Exists(outCopy + ".part"));
        Assert.True(File.Exists(outPref + ".done"));
        Assert.False(File.Exists(outPref + ".part"));
        Assert.DoesNotContain(logs, l => l.Contains("kısmi (.part)", StringComparison.OrdinalIgnoreCase));

        var copyCheckpoints = await store.GetCompletedCheckpointsAsync("Agentic GraphRAG", "copyright-page01.html");
        Assert.Equal("Completed", copyCheckpoints[0].StageStatus);
        var prefCheckpoints = await store.GetCompletedCheckpointsAsync("Agentic GraphRAG", "preface01.html");
        Assert.Equal(5, prefCheckpoints.Keys.Count(k => k >= 0));
    }

    [Fact]
    public async Task RunAsync_WhenProseSentenceRemainsUntranslated_StillMarksPartialAndMissingFallback()
    {
        // Arrange: Çevirmen doğal İngilizce düz yazı cümlesini olduğu gibi geri dönerse hala yakalanmalı
        var opfDir = Path.Combine(_tempDir, "studio_untranslated_prose");
        Directory.CreateDirectory(opfDir);

        File.WriteAllText(Path.Combine(opfDir, "ch01.html"), """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Chapter 1</title></head>
              <body>
                <p>Distributed systems coordinate state reliably over networks.</p>
              </body>
            </html>
            """);

        var styleClient = new StubChatClient(_ => "Teknik üslup.");
        var transClient = new StubChatClient(_ => """
            [BLOK_000]
            Distributed systems coordinate state reliably over networks.
            [/BLOK_000]
            """);
        var revClient = new StubChatClient(_ => """{"isApproved": true, "patches": []}""");

        var (sp, store) = await BuildServiceProviderAsync(styleClient, transClient, revClient);
        await using var _sp = sp;

        var engine = new StudioLocalizationEngine(sp);
        var logs = new ConcurrentQueue<string>();
        engine.LogMessageReceived += msg => logs.Enqueue(msg);

        var ch1 = new EpubItem { Id = "ch1", Href = "ch01.html", MediaType = "application/xhtml+xml", Role = EpubDocumentRole.Chapter };
        var package = new EpubPackage
        {
            Title = "Test Book",
            OpfDirectory = opfDir,
            Spine = [ch1]
        };

        // Act
        await engine.RunAsync(package, [ch1], concurrency: 1, isFresh: false);

        // Assert
        var outCh1 = Path.Combine(opfDir, "ch01_tr.html");
        Assert.False(File.Exists(outCh1 + ".done"));
        Assert.True(File.Exists(outCh1 + ".part"));
        Assert.Contains(logs, l => l.Contains("1 çevrilmemiş blok", StringComparison.OrdinalIgnoreCase));

        var checkpoints = await store.GetCompletedCheckpointsAsync("Test Book", "ch01.html");
        Assert.False(checkpoints.ContainsKey(0));
    }
}
