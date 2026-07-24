using System.Diagnostics;

namespace Bardie.Module.Source;

/// <summary>Writes PCM bytes to a Kithara-owned session FIFO (<c>audio_endpoint</c>).</summary>
public interface IFifoAudioSink
{
    /// <summary>
    /// Opens <paramref name="audioEndpoint"/> for write and copies <paramref name="pcm"/> until EOF or cancel.
    /// Blocks until a reader attaches on a real FIFO (Unix <c>mkfifo</c>).
    /// When <paramref name="isPaused"/> returns true, writing pauses until it returns false.
    /// Writes are paced to s16le / 48 kHz / stereo realtime.
    /// </summary>
    Task WriteAsync(
        string audioEndpoint,
        Stream pcm,
        CancellationToken cancellationToken = default,
        Func<bool>? isPaused = null);
}

public sealed class FifoAudioSink : IFifoAudioSink
{
    private const int BufferSize = 16 * 1024;
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerSecond = SampleRate * Channels * sizeof(short);
    private static readonly TimeSpan PausePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PaceSlack = TimeSpan.FromMilliseconds(30);

    public async Task WriteAsync(
        string audioEndpoint,
        Stream pcm,
        CancellationToken cancellationToken = default,
        Func<bool>? isPaused = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEndpoint);
        ArgumentNullException.ThrowIfNull(pcm);

        await using var fifo = new FileStream(
            audioEndpoint,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        var clock = Stopwatch.StartNew();
        long bytesWritten = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (isPaused?.Invoke() == true)
            {
                await Task.Delay(PausePoll, cancellationToken).ConfigureAwait(false);
                // Don't let pause inflate realtime debt when we resume.
                clock.Restart();
                bytesWritten = 0;
            }

            var read = await pcm.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await fifo.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesWritten += read;

            // Pace to s16le / 48 kHz / stereo so Magpie Ended ≈ audible end (not "dump then drain").
            var expected = TimeSpan.FromSeconds(bytesWritten / (double)BytesPerSecond);
            var ahead = expected - clock.Elapsed - PaceSlack;
            if (ahead > TimeSpan.Zero)
            {
                await Task.Delay(ahead, cancellationToken).ConfigureAwait(false);
            }
        }

        await fifo.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
