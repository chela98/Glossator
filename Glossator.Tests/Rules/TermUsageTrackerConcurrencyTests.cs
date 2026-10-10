using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Xunit;

namespace Glossator.Tests.Rules;

public class TermUsageTrackerConcurrencyTests
{
    [Fact]
    public async Task TermUsageTracker_UnderConcurrentCalls_MaintainsAccurateCount()
    {
        var tracker = new TermUsageTracker("concurrent_chapter.html");
        var term = new GlossaryEntry
        {
            EnglishTerm = "Multi-Agent Systems",
            TurkishTerm = "Çok Ajanlı Sistemler",
            Policy = GlossingPolicy.ProgressiveGloss
        };

        const int workerCount = 50;

        var tasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            return tracker.RecordUsage(term);
        })).ToArray();

        var decisions = await Task.WhenAll(tasks);

        // Toplam sayacın tam olarak 50 olduğunu doğrula (Hiçbir race condition kaybolmadı)
        Assert.Equal(50, tracker.GetCurrentCount("Multi-Agent Systems"));

        // İlk 2 kullanım Hybrid, sonraki 48 kullanım PureTurkish olmalı
        var hybridCount = decisions.Count(d => d == TermFormatDecision.Hybrid);
        var turkishCount = decisions.Count(d => d == TermFormatDecision.PureTurkish);

        Assert.Equal(2, hybridCount);
        Assert.Equal(48, turkishCount);
    }
}
