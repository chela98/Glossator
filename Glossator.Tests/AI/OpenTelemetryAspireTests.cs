namespace Glossator.Tests.AI;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Glossator.AI.Diagnostics;
using Xunit;

public class OpenTelemetryAspireTests
{
    [Fact]
    public void OpenTelemetry_GlossatorDiagnostics_RecordsActivitiesAndMetricsAccurately()
    {
        // 1. Arrange & Listen: Activity Listener (Distributed Tracing)
        Activity? capturedActivity = null;
        using var actListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GlossatorDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "LocalizeSection")
                {
                    capturedActivity = activity;
                }
            }
        };
        ActivitySource.AddActivityListener(actListener);

        // 2. Arrange & Listen: Meter Listener (Metrics)
        var recordedMetrics = new System.Collections.Concurrent.ConcurrentBag<(string InstrumentName, long Value)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == GlossatorDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            recordedMetrics.Add((instrument.Name, measurement));
        });
        meterListener.Start();

        // 3. Act: İşlem adımı başlat, etiket ekle ve sayaç artır
        using (var activity = GlossatorDiagnostics.ActivitySource.StartActivity("LocalizeSection"))
        {
            activity?.SetTag("glossator.chapter", "ch01");
            activity?.SetTag("glossator.segments", 15);

            GlossatorDiagnostics.SectionsTranslatedCounter.Add(1);
            GlossatorDiagnostics.ReviewerPatchesCounter.Add(2);
        }

        meterListener.RecordObservableInstruments();

        // 4. Assert: Distributed Tracing Doğrulaması
        Assert.NotNull(capturedActivity);
        Assert.Equal("LocalizeSection", capturedActivity.OperationName);
        Assert.Equal("ch01", capturedActivity.GetTagItem("glossator.chapter"));
        Assert.Equal(15, capturedActivity.GetTagItem("glossator.segments"));

        // 5. Assert: Metrik Doğrulaması
        Assert.Contains(recordedMetrics, m => m.InstrumentName == "glossator.sections.translated" && m.Value == 1);
        Assert.Contains(recordedMetrics, m => m.InstrumentName == "glossator.reviewer.patches_applied" && m.Value == 2);
    }
}
