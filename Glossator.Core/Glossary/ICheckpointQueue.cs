using System.Threading.Channels;
using Glossator.Core.Models.Checkpoint;

namespace Glossator.Core.Glossary;

/// <summary>
/// Eşzamanlı worker'lardan gelen checkpoint kayıtlarını toplayıp
/// arka plandaki tek yazıcıya (single-writer) aktaran thread-safe kuyruk sözleşmesi.
/// </summary>
public interface ICheckpointQueue
{
    ValueTask EnqueueAsync(SegmentCheckpoint checkpoint, CancellationToken cancellationToken = default);
    ChannelReader<SegmentCheckpoint> Reader { get; }
    void Complete();
}

/// <summary>
/// BoundedChannel tabanlı sırt-basıncı (backpressure) destekli checkpoint kuyruğu.
/// </summary>
public sealed class CheckpointQueue : ICheckpointQueue
{
    private readonly Channel<SegmentCheckpoint> _channel;

    public CheckpointQueue(int capacity = 5000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            SingleWriter = false, // Çoklu worker paralel yazar
            SingleReader = true,  // Tek bir arka plan iş parçacığı SQLite'a yazar
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateBounded<SegmentCheckpoint>(options);
    }

    public ValueTask EnqueueAsync(SegmentCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(checkpoint, cancellationToken);

    public ChannelReader<SegmentCheckpoint> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
