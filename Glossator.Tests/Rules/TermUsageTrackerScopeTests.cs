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
        Assert.Equal(TermFormatDecision.Hybrid, decision1); // 1. kullanım -> Hibrit

        var decision2 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.Hybrid, decision2); // 2. kullanım -> Hibrit

        var decision3 = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.PureTurkish, decision3); // 3. kullanım -> Saf Türkçe

        // Yeni Bölüme Geçiş (Scope Reset)
        tracker.BeginNewScope("ch02");
        Assert.Equal("ch02", tracker.CurrentScopeId);

        // Act & Assert (Bölüm 2: Sayaç sıfırlanmış olmalı, tekrar hibrit başlamalı!)
        var decisionChapter2FirstUse = tracker.RecordUsage(entry);
        Assert.Equal(TermFormatDecision.Hybrid, decisionChapter2FirstUse);
    }
}
