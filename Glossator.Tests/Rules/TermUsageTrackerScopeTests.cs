namespace Glossator.Tests.Rules;

using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Xunit;

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
        Assert.Equal(TermFormatDecision.Hybrid, decision1); // 1. kullanım -> Hibrit

        var decision2 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.PureTurkish, decision2); // 2. kullanım -> Saf Türkçe

        var decision3 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.PureTurkish, decision3); // 3. kullanım -> Saf Türkçe

        // Yeni Bölüme Geçiş (Scope Reset)
        tracker.BeginNewScope("ch02");
        Assert.Equal("ch02", tracker.CurrentScopeId);

        // Act & Assert (Bölüm 2: Sayaç sıfırlanmış olmalı, tekrar hibrit başlamalı!)
        var decisionChapter2FirstUse = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.Hybrid, decisionChapter2FirstUse);
    }

    [Fact]
    public async Task BeginNewScope_WhileConcurrentWorkersRecording_DoesNotThrowOrCorruptState()
    {
        var tracker = new TermUsageTracker("chapter_01");
        var term = new GlossaryEntry
        {
            EnglishTerm = "pipeline",
            TurkishTerm = "boru hattı",
            Policy = GlossingPolicy.ProgressiveGloss
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var workerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                tracker.RecordUsage(term);
            }
        });

        var scopeSwitchTask = Task.Run(async () =>
        {
            for (int i = 2; i <= 10; i++)
            {
                tracker.BeginNewScope($"chapter_{i:D2}");
                await Task.Delay(15);
            }
            cts.Cancel();
        });

        await Task.WhenAll(workerTask, scopeSwitchTask);

        // Son scope için sayaç sıfırlanıp 1 yapıldığında Hibrit döndüğünü doğrula
        tracker.BeginNewScope("chapter_final");
        var decision = tracker.RecordUsage(term);
        Assert.Equal(TermFormatDecision.Hybrid, decision);
    }
}
