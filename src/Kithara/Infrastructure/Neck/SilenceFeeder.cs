using System.Diagnostics;

namespace Kithara.Infrastructure.Neck;

/// <summary>
/// Writes zero PCM (s16le / 48 kHz / stereo) into a Struna FIFO when no module is feeding,
/// so FFmpeg never starves across silence gaps / pause / between tracks.
/// Keeps the write end open for the encoder life even while disabled (avoids FIFO EOF).
/// </summary>
public sealed class SilenceFeeder : IAsyncDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerFrame = sizeof(short) * Channels;

    /// <summary>~20 ms of silence per write.</summary>
    private const int FramesPerChunk = SampleRate / 50;

    private readonly string _fifoPath;
    private readonly Guid _strunaId;
    private readonly string _slug;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private bool _enabled = true;
    private int _disposed;

    public SilenceFeeder(string fifoPath, Guid strunaId, string slug, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fifoPath);
        _fifoPath = fifoPath;
        _strunaId = strunaId;
        _slug = slug;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loop = RunAsync(_cts.Token);
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
        }

        using var activity = NeckActivity.Source.StartActivity("neck.silence");
        activity?.SetTag("struna.id", _strunaId.ToString("D"));
        activity?.SetTag("struna.slug", _slug);
        activity?.SetTag("silence.enabled", enabled);
        _logger.LogDebug(
            "Silence feeder for Struna {Id} ({Slug}) enabled={Enabled}",
            _strunaId,
            _slug,
            enabled);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            // Bound wait — FIFO open/write must not hang host shutdown.
            await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Silence feeder for Struna {Id} did not stop within timeout", _strunaId);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silence feeder ended for Struna {Id}", _strunaId);
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var activity = NeckActivity.Source.StartActivity("neck.silence.attach");
        activity?.SetTag("struna.id", _strunaId.ToString("D"));
        activity?.SetTag("struna.slug", _slug);

        // O_RDWR on Linux FIFOs does not block waiting for a peer — keeps the write end
        // alive for FFmpeg even while silence writes are paused (module writer attached).
        await using var fifo = new FileStream(
            _fifoPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: FramesPerChunk * BytesPerFrame,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _logger.LogInformation(
            "Silence feeder attached to FIFO {Path} for Struna {Id}",
            _fifoPath,
            _strunaId);

        var zeros = new byte[FramesPerChunk * BytesPerFrame];
        var pace = TimeSpan.FromMilliseconds(1000.0 * FramesPerChunk / SampleRate);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsEnabled)
            {
                await Task.Delay(pace, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await fifo.WriteAsync(zeros, cancellationToken).ConfigureAwait(false);
            // Soft pacing so we do not spin when FFmpeg drains faster than realtime.
            await Task.Delay(pace, cancellationToken).ConfigureAwait(false);
        }
    }
}
