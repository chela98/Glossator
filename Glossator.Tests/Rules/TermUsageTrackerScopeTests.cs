using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Xunit;

namespace Glossator.Tests.Rules;

public class TermUsageTrackerScopeTests
{
    [Fact]
    public void BeginNewScope_ResetsCounterAcrossChapters_AllowsHybridFormatAgain()
    {
        // Arrange
        var tracker = new TermUsageTracker("ch01");
        var entry = new GlossaryEntry
        {
            EnglishTerm = "autonomous agents",
            TurkishTerm = "otonom ajanlar",
            Policy = GlossingPolicy.ProgressiveGloss
        };

        // Act & Assert (Bölüm 1)
        var decision1 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.Hybrid, decision1);
        Assert.Equal(1, tracker.GetCurrentCount("autonomous agents"));

        var decision2 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.PureTurkish, decision2);
        Assert.Equal(2, tracker.GetCurrentCount("autonomous agents"));

        // Bölüm değişimi -> sayaç sıfırlanır
        tracker.BeginNewScope("ch02");
        Assert.Equal("ch02", tracker.CurrentScopeId);
        Assert.Equal(0, tracker.GetCurrentCount("autonomous agents"));

        // Bölüm 2 ilk kullanım -> yeniden Hibrit olmalı
        var chapter2Decision = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.Hybrid, chapter2Decision);
        Assert.Equal(1, tracker.GetCurrentCount("autonomous agents"));
    }

    [Fact]
    public async Task RecordUsage_WhenCalledConcurrentlyWithScopeSwitches_IsDeterministicAndThreadSafe()
    {
        // Arrange
        var tracker = new TermUsageTracker("chapter_01");
        var term = new GlossaryEntry
        {
            EnglishTerm = "pipeline",
            TurkishTerm = "boru hattı",
            Policy = GlossingPolicy.ProgressiveGloss
        };

        const int workerCount = 4;
        const int iterationsPerWorker = 250;
        using var startBarrier = new Barrier(workerCount + 1);

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                var hybridCount = 0;
                startBarrier.SignalAndWait();
                for (var i = 0; i < iterationsPerWorker; i++)
                {
                    if (tracker.RecordUsage(term) == TermFormatDecision.Hybrid)
                    {
                        hybridCount++;
                    }
                }
                return hybridCount;
            }))
            .ToArray();

        var scopeSwitcher = Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            for (var s = 2; s <= 15; s++)
            {
                tracker.BeginNewScope($"chapter_{s:D2}");
            }
        });

        // Act
        var hybridCounts = await Task.WhenAll(workers);
        await scopeSwitcher;

        // Assert: Çarpışma veya istisna olmadan en az ilk scope'un Hibrit kararı üretilmiş olmalı
        Assert.InRange(hybridCounts.Sum(), 1, 15);

        tracker.BeginNewScope("chapter_final");
        Assert.Equal(0, tracker.GetCurrentCount("pipeline"));
        Assert.Equal(TermFormatDecision.Hybrid, tracker.RecordUsage(term));
        Assert.Equal(1, tracker.GetCurrentCount("pipeline"));
    }
}
