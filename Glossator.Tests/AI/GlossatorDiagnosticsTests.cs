using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Glossator.AI.Diagnostics;
using Xunit;

namespace Glossator.Tests.AI;

[Collection("GlobalDiagnosticsAndConsole")]
public class GlossatorDiagnosticsTests
{
    [Fact]
    public void StageUsageStats_ReasoningTokens_ComputesDeltaAndClampsNegativeToZero()
    {
        // Arrange
        var positive = new StageUsageStats
        {
            BilledOutputTokens = 450,
            VisibleOutputTokens = 150
        };
        var negative = new StageUsageStats
        {
            BilledOutputTokens = 100,
            VisibleOutputTokens = 180
        };

        // Act & Assert
        Assert.Equal(300, positive.ReasoningTokens);
        Assert.Equal(0, negative.ReasoningTokens);
    }

    [Fact]
    public void SessionMetrics_RecordAndReset_AccumulatesAndClearsAllCountersAccurately()
    {
        // Arrange
        GlossatorDiagnostics.ResetSessionStats();

        // Act
        GlossatorDiagnostics.RecordStageUsage("Translator", 1000, 350, 150);
        GlossatorDiagnostics.RecordStageUsage("Translator", 500, 150, 100);
        GlossatorDiagnostics.RecordStageUsage("   ", 200, 50, 50);
        GlossatorDiagnostics.RecordPatchesApplied(3);
        GlossatorDiagnostics.RecordPatchesApplied(2);
        GlossatorDiagnostics.RecordRejection();
        GlossatorDiagnostics.RecordSectionCompleted();
        GlossatorDiagnostics.RecordChapterCompleted();

        // Assert before reset
        var snapshot = GlossatorDiagnostics.GetStageStatsSnapshot();
        Assert.Equal(2, snapshot["Translator"].Calls);
        Assert.Equal(1500, snapshot["Translator"].InputTokens);
        Assert.Equal(500, snapshot["Translator"].BilledOutputTokens);
        Assert.Equal(250, snapshot["Translator"].VisibleOutputTokens);
        Assert.Equal(250, snapshot["Translator"].ReasoningTokens);
        Assert.Equal(1, snapshot["Other"].Calls);

        Assert.Equal(5, GlossatorDiagnostics.SessionPatchesApplied);
        Assert.Equal(1, GlossatorDiagnostics.SessionRejections);
        Assert.Equal(1, GlossatorDiagnostics.SessionSectionsCompleted);
        Assert.Equal(1, GlossatorDiagnostics.SessionChaptersCompleted);

        // Act reset
        GlossatorDiagnostics.ResetSessionStats();

        // Assert after reset
        Assert.Empty(GlossatorDiagnostics.GetStageStatsSnapshot());
        Assert.Equal(0, GlossatorDiagnostics.SessionPatchesApplied);
        Assert.Equal(0, GlossatorDiagnostics.SessionRejections);
        Assert.Equal(0, GlossatorDiagnostics.SessionSectionsCompleted);
        Assert.Equal(0, GlossatorDiagnostics.SessionChaptersCompleted);
    }

    [Fact]
    public void Instruments_EmitMeasurableActivitiesAndCountersWhenListenersAttached()
    {
        // Arrange
        Activity? capturedActivity = null;
        long sectionsDelta = 0;

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => capturedActivity = act
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GlossatorDiagnostics.SourceName &&
                instrument.Name == "glossator.sections.translated")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
        {
            if (inst.Name == "glossator.sections.translated")
                sectionsDelta += value;
        });
        meterListener.Start();

        // Act
        using (var act = GlossatorDiagnostics.ActivitySource.StartActivity("DiagUnitTestActivity"))
        {
            act?.SetTag("book.id", "unit-test");
        }
        GlossatorDiagnostics.SectionsTranslatedCounter.Add(2);
        meterListener.RecordObservableInstruments();

        // Assert
        Assert.NotNull(capturedActivity);
        Assert.Equal("DiagUnitTestActivity", capturedActivity.OperationName);
        Assert.Equal("unit-test", capturedActivity.GetTagItem("book.id"));
        Assert.Equal(2, sectionsDelta);
    }
}
