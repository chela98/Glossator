namespace Glossator.Tests.Rules;

using Glossator.Core.Models.Glossary;
using Glossator.Core.Rules;
using Xunit;

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

        // İlk kullanım Hybrid, sonraki 49 kullanım PureTurkish olmalı
        var hybridCount = decisions.Count(d => d == TermFormatDecision.Hybrid);
        var turkishCount = decisions.Count(d => d == TermFormatDecision.PureTurkish);

        Assert.Equal(1, hybridCount);
        Assert.Equal(49, turkishCount);
    }

    [Fact]
    public async Task TermUsageTracker_UnderMultipleConcurrentTermsAndPolicies_EvaluatesCorrectly()
    {
        var tracker = new TermUsageTracker("mixed_chapter.html");

        var progressiveTerm = new GlossaryEntry
        {
            EnglishTerm = "Neural Network",
            TurkishTerm = "Yapay Sinir Ağı",
            Policy = GlossingPolicy.ProgressiveGloss
        };
        var englishOnlyTerm = new GlossaryEntry
        {
            EnglishTerm = "LLM",
            Policy = GlossingPolicy.AlwaysEnglish
        };
        var turkishOnlyTerm = new GlossaryEntry
        {
            EnglishTerm = "Database",
            TurkishTerm = "Veritabanı",
            Policy = GlossingPolicy.AlwaysTurkish
        };

        const int iterations = 30;

        var tasks = Enumerable.Range(0, iterations).Select(_ => Task.Run(() =>
        {
            var d1 = tracker.RecordUsage(progressiveTerm);
            var d2 = tracker.RecordUsage(englishOnlyTerm);
            var d3 = tracker.RecordUsage(turkishOnlyTerm);

            Assert.Equal(TermFormatDecision.EnglishOnly, d2);
            Assert.Equal(TermFormatDecision.PureTurkish, d3);
            return d1;
        })).ToArray();

        var progressiveDecisions = await Task.WhenAll(tasks);

        Assert.Equal(iterations, tracker.GetCurrentCount("Neural Network"));
        Assert.Equal(1, progressiveDecisions.Count(d => d == TermFormatDecision.Hybrid));
        Assert.Equal(iterations - 1, progressiveDecisions.Count(d => d == TermFormatDecision.PureTurkish));
    }
}
