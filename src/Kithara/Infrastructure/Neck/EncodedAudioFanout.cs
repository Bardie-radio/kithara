using System.Threading.Channels;

namespace Kithara.Infrastructure.Neck;

/// <summary>
/// Live MP3 fan-out from one in-process encoder to N Stream Server listeners.
/// Slow listeners drop oldest chunks (broadcast radio semantics).
/// </summary>
public sealed class EncodedAudioFanout
{
    private readonly ConcurrentListenerSet _listeners = new();

    public int ListenerCount => _listeners.Count;

    /// <summary>Subscribe to encoded chunks until cancelled or the session ends.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _listeners.Add(channel.Writer);
        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            _listeners.Remove(channel.Writer);
        }
    }

    public void Publish(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        var copy = chunk.ToArray();
        _listeners.Broadcast(copy);
    }

    public void Complete() => _listeners.CompleteAll();

    private sealed class ConcurrentListenerSet
    {
        private readonly object _gate = new();
        private List<ChannelWriter<byte[]>> _writers = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _writers.Count;
                }
            }
        }

        public void Add(ChannelWriter<byte[]> writer)
        {
            lock (_gate)
            {
                var next = new List<ChannelWriter<byte[]>>(_writers.Count + 1);
                next.AddRange(_writers);
                next.Add(writer);
                _writers = next;
            }
        }

        public void Remove(ChannelWriter<byte[]> writer)
        {
            lock (_gate)
            {
                var next = new List<ChannelWriter<byte[]>>(_writers.Count);
                foreach (var existing in _writers)
                {
                    if (!ReferenceEquals(existing, writer))
                    {
                        next.Add(existing);
                    }
                }

                _writers = next;
            }
        }

        public void Broadcast(byte[] chunk)
        {
            List<ChannelWriter<byte[]>> snapshot;
            lock (_gate)
            {
                snapshot = _writers;
            }

            foreach (var writer in snapshot)
            {
                writer.TryWrite(chunk);
            }
        }

        public void CompleteAll()
        {
            List<ChannelWriter<byte[]>> snapshot;
            lock (_gate)
            {
                snapshot = _writers;
                _writers = [];
            }

            foreach (var writer in snapshot)
            {
                writer.TryComplete();
            }
        }
    }
}

/// <summary>Per-Struna encode session: silence feeder + in-process MP3 encoder + fan-out.</summary>
public sealed class StrunaEncodeSession : IAsyncDisposable
{
    public Guid StrunaId { get; }
    public string Slug { get; }
    public string FifoPath { get; }
    public EncodedAudioFanout Fanout { get; }
    public string StreamTitle { get; set; } = string.Empty;

    private readonly SilenceFeeder _silence;
    private readonly FfmpegMp3PcmEncoder _encoder;
    private int _disposed;

    public StrunaEncodeSession(
        Guid strunaId,
        string slug,
        string fifoPath,
        SilenceFeeder silence,
        FfmpegMp3PcmEncoder encoder,
        EncodedAudioFanout fanout)
    {
        StrunaId = strunaId;
        Slug = slug;
        FifoPath = fifoPath;
        _silence = silence;
        _encoder = encoder;
        Fanout = fanout;
    }

    public void SetSilence(bool enabled) => _silence.SetEnabled(enabled);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Drop the reader first so silence WriteAsync is not stuck on a full FIFO.
        await _encoder.DisposeAsync().ConfigureAwait(false);
        await _silence.DisposeAsync().ConfigureAwait(false);
        Fanout.Complete();
    }
}
