using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Glossator.AI.Diagnostics;

namespace Glossator.App.Infrastructure;

public sealed record TelemetrySnapshot(
    int ActiveWorkers,
    long SectionsTranslated,
    long ChaptersTranslated,
    long PatchesApplied,
    long Rejections,
    long RecoveryAttempts,
    long StreamedTokens,
    double LastTtftMs,
    double LastTps);

/// <summary>
/// GlossatorDiagnostics OpenTelemetry Meter ölçümlerini canlı olarak uygulama içinde dinleyen
/// ve Docker Aspire OTLP sunucu erişilebilirliğini kontrol eden hafif gözlemlenebilirlik köprüsü.
/// </summary>
public sealed class StudioTelemetryMonitor : IDisposable
{
    private readonly MeterListener _listener;

    private int _activeWorkers;
    private long _sectionsTranslated;
    private long _chaptersTranslated;
    private long _patchesApplied;
    private long _rejections;
    private long _recoveryAttempts;
    private long _streamedTokens;
    private long _lastTtftBits;
    private long _lastTpsBits;
    private long _lastNotifyTicks;

    public event Action<TelemetrySnapshot>? SnapshotChanged;

    public StudioTelemetryMonitor()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == GlossatorDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<int>(OnIntMeasurement);
        _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _listener.Start();
    }

    public void ResetSessionCounters()
    {
        Interlocked.Exchange(ref _sectionsTranslated, 0);
        Interlocked.Exchange(ref _chaptersTranslated, 0);
        Interlocked.Exchange(ref _patchesApplied, 0);
        Interlocked.Exchange(ref _rejections, 0);
        Interlocked.Exchange(ref _recoveryAttempts, 0);
        Interlocked.Exchange(ref _streamedTokens, 0);
        Interlocked.Exchange(ref _lastTtftBits, 0);
        Interlocked.Exchange(ref _lastTpsBits, 0);
        Notify(force: true);
    }

    public TelemetrySnapshot GetSnapshot() => new(
        Math.Max(0, Volatile.Read(ref _activeWorkers)),
        Interlocked.Read(ref _sectionsTranslated),
        Interlocked.Read(ref _chaptersTranslated),
        Interlocked.Read(ref _patchesApplied),
        Interlocked.Read(ref _rejections),
        Interlocked.Read(ref _recoveryAttempts),
        Interlocked.Read(ref _streamedTokens),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastTtftBits)),
        BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastTpsBits)));

    public static async Task<bool> CheckOtlpReachableAsync(string endpointUrl, CancellationToken ct = default)
    {
        try
        {
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
                return false;

            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(900));
            await tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 4317, timeoutCts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void OnIntMeasurement(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == "glossator.workers.active")
        {
            Interlocked.Add(ref _activeWorkers, measurement);
            Notify(force: false);
        }
    }

    private void OnLongMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "glossator.sections.translated":
                Interlocked.Add(ref _sectionsTranslated, measurement);
                break;
            case "glossator.chapters.translated":
                Interlocked.Add(ref _chaptersTranslated, measurement);
                break;
            case "glossator.reviewer.patches_applied":
                Interlocked.Add(ref _patchesApplied, measurement);
                break;
            case "glossator.reviewer.rejections":
                Interlocked.Add(ref _rejections, measurement);
                break;
            case "glossator.recovery.attempts":
                Interlocked.Add(ref _recoveryAttempts, measurement);
                break;
            case "glossator.genai.tokens_streamed":
                Interlocked.Add(ref _streamedTokens, measurement);
                break;
            default:
                return;
        }

        Notify(force: false);
    }

    private void OnDoubleMeasurement(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "glossator.genai.ttft":
                Interlocked.Exchange(ref _lastTtftBits, BitConverter.DoubleToInt64Bits(measurement));
                break;
            case "glossator.genai.tps":
                Interlocked.Exchange(ref _lastTpsBits, BitConverter.DoubleToInt64Bits(measurement));
                break;
            default:
                return;
        }

        Notify(force: false);
    }

    private void Notify(bool force)
    {
        var now = Environment.TickCount64;
        var prev = Interlocked.Read(ref _lastNotifyTicks);
        if (!force && now - prev < 200) return;
        Interlocked.Exchange(ref _lastNotifyTicks, now);
        SnapshotChanged?.Invoke(GetSnapshot());
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
