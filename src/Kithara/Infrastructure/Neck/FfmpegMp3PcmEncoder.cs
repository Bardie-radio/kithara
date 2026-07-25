using System.Diagnostics;
using System.Runtime.InteropServices;
using Bardie.Module.Source;
using FFmpeg.AutoGen;

namespace Kithara.Infrastructure.Neck;

/// <summary>
/// In-process PCM → MP3 encoder via FFmpeg.AutoGen (libmp3lame / AV_CODEC_ID_MP3).
/// Reads <see cref="CanonicalPcm"/> from the Struna FIFO and publishes raw MP3 frames.
/// Native load matches Magpie's <c>FfmpegPcmTranscoder</c> (set <c>ffmpeg.RootPath</c>, no CLI).
/// </summary>
public sealed class FfmpegMp3PcmEncoder : IAsyncDisposable
{
    private static readonly object InitLock = new();
    private static bool _nativeInitialized;

    private readonly string _fifoPath;
    private readonly Guid _strunaId;
    private readonly int _bitrateKbps;
    private readonly EncodedAudioFanout _fanout;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposed;

    public FfmpegMp3PcmEncoder(
        string fifoPath,
        Guid strunaId,
        string slug,
        int bitrateKbps,
        EncodedAudioFanout fanout,
        string? ffmpegRootPath,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fifoPath);
        _fifoPath = fifoPath;
        _strunaId = strunaId;
        _bitrateKbps = Math.Clamp(bitrateKbps, 64, 320);
        _fanout = fanout ?? throw new ArgumentNullException(nameof(fanout));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        EnsureNativeLoaded(ffmpegRootPath, logger);
        // META-OTEL-002: capture before Task.Run — Activity.Current dies when the create/start span ends.
        var linkContext = NeckActivity.CaptureLinkContext();
        _loop = Task.Run(() => RunUnsafe(slug, linkContext, _cts.Token), CancellationToken.None);
    }

    private static void EnsureNativeLoaded(string? configuredRoot, ILogger logger)
    {
        if (_nativeInitialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_nativeInitialized)
            {
                return;
            }

            var root = ResolveFfmpegRoot(configuredRoot);
            if (!string.IsNullOrWhiteSpace(root))
            {
                ffmpeg.RootPath = root;
                logger.LogInformation("FFmpeg.AutoGen RootPath={Root}", root);
            }

            // Surface missing sonames instead of opaque NotSupportedException stubs.
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            DynamicallyLoadedBindings.Initialize();

            try
            {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                logger.LogInformation("FFmpeg native ready: {Version}", ffmpeg.av_version_info());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "FFmpeg.AutoGen failed after Initialize — need FFmpeg 6.1 shared libs (libavcodec.so.60). Check BARDIE_FFMPEG_ROOT.",
                    ex);
            }

            _nativeInitialized = true;
        }
    }

    private static string? ResolveFfmpegRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        foreach (var candidate in new[]
                 {
                     "/usr/lib/x86_64-linux-gnu",
                     "/usr/lib/aarch64-linux-gnu",
                     "/usr/lib",
                     "/usr/local/lib",
                 })
        {
            if (Directory.Exists(candidate)
                && Directory.EnumerateFiles(candidate, "libavcodec.so*").Any())
            {
                return candidate;
            }
        }

        return null;
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
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("MP3 encoder for Struna {Id} did not stop within timeout", _strunaId);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MP3 encoder ended for Struna {Id}", _strunaId);
        }

        // Fan-out completes only on StrunaEncodeSession dispose — keep listeners across loop restarts.
        _cts.Dispose();
    }

    private void RunUnsafe(string slug, ActivityContext linkContext, CancellationToken cancellationToken)
    {
        const int maxRestarts = 8;
        for (var attempt = 1; attempt <= maxRestarts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                unsafe
                {
                    Run(slug, linkContext, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogError(
                    ex,
                    "MP3 encoder crashed for Struna {Id} (attempt {Attempt}/{Max})",
                    _strunaId,
                    attempt,
                    maxRestarts);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _logger.LogWarning(
                "MP3 encoder loop ended for Struna {Id}; restarting (attempt {Attempt}/{Max})",
                _strunaId,
                attempt,
                maxRestarts);
            Thread.Sleep(200);
        }
    }

    private unsafe void Run(string slug, ActivityContext linkContext, CancellationToken cancellationToken)
    {
        using var activity = NeckActivity.StartLinked("neck.encoder.run", linkContext);
        activity?.SetTag("struna.id", _strunaId.ToString("D"));
        activity?.SetTag("struna.slug", slug);

        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        FileStream? fifo = null;

        try
        {
            var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MP3);
            if (codec is null)
            {
                throw new InvalidOperationException("No MP3 encoder found (AV_CODEC_ID_MP3).");
            }

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx is null)
            {
                throw new InvalidOperationException("avcodec_alloc_context3 failed.");
            }

            codecCtx->bit_rate = _bitrateKbps * 1000L;
            codecCtx->sample_rate = CanonicalPcm.SampleRate;
            codecCtx->sample_fmt = PickSampleFormat(codec);
            ffmpeg.av_channel_layout_default(&codecCtx->ch_layout, CanonicalPcm.Channels);
            codecCtx->time_base = new AVRational { num = 1, den = CanonicalPcm.SampleRate };

            var open = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (open < 0)
            {
                throw new InvalidOperationException($"avcodec_open2 failed: {Error(open)}");
            }

            var frameSamples = codecCtx->frame_size > 0 ? codecCtx->frame_size : 1152;
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame is null || packet is null)
            {
                throw new InvalidOperationException("av_frame_alloc / av_packet_alloc failed.");
            }

            frame->nb_samples = frameSamples;
            frame->format = (int)codecCtx->sample_fmt;
            frame->ch_layout = codecCtx->ch_layout;
            frame->sample_rate = codecCtx->sample_rate;

            var bufRc = ffmpeg.av_frame_get_buffer(frame, 0);
            if (bufRc < 0)
            {
                throw new InvalidOperationException($"av_frame_get_buffer failed: {Error(bufRc)}");
            }

            // Reader end — silence feeder holds RDWR so this does not deadlock.
            fifo = new FileStream(
                _fifoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: frameSamples * CanonicalPcm.BytesPerFrame,
                FileOptions.SequentialScan);

            _logger.LogInformation(
                "MP3 encoder attached to FIFO {Path} for Struna {Id} (bitrate={Bitrate}k)",
                _fifoPath,
                _strunaId,
                _bitrateKbps);

            var interleaved = new byte[frameSamples * CanonicalPcm.BytesPerFrame];
            long pts = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var filled = 0;
                while (filled < interleaved.Length && !cancellationToken.IsCancellationRequested)
                {
                    var n = fifo.Read(interleaved, filled, interleaved.Length - filled);
                    if (n <= 0)
                    {
                        // Writer closed — keep waiting while Struna is alive (silence reopens / continues).
                        Thread.Sleep(20);
                        continue;
                    }

                    filled += n;
                }

                if (cancellationToken.IsCancellationRequested || filled < interleaved.Length)
                {
                    break;
                }

                var makeWritable = ffmpeg.av_frame_make_writable(frame);
                if (makeWritable < 0)
                {
                    throw new InvalidOperationException($"av_frame_make_writable failed: {Error(makeWritable)}");
                }

                CopyPcmToFrame(frame, interleaved, frameSamples, codecCtx->sample_fmt);
                frame->pts = pts;
                pts += frameSamples;

                EncodeFrame(codecCtx, frame, packet);
            }

            // Drain
            EncodeFrame(codecCtx, null, packet);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "MP3 encoder failed for Struna {Id}", _strunaId);
            }
        }
        finally
        {
            fifo?.Dispose();
            if (packet is not null)
            {
                var p = packet;
                ffmpeg.av_packet_free(&p);
            }

            if (frame is not null)
            {
                var f = frame;
                ffmpeg.av_frame_free(&f);
            }

            if (codecCtx is not null)
            {
                var c = codecCtx;
                ffmpeg.avcodec_free_context(&c);
            }

            // Do not Complete fan-out here — session dispose owns listener lifetime.
        }
    }

    private unsafe void EncodeFrame(AVCodecContext* codecCtx, AVFrame* frame, AVPacket* packet)
    {
        var send = ffmpeg.avcodec_send_frame(codecCtx, frame);
        if (send < 0 && send != ffmpeg.AVERROR_EOF)
        {
            throw new InvalidOperationException($"avcodec_send_frame failed: {Error(send)}");
        }

        while (true)
        {
            var receive = ffmpeg.avcodec_receive_packet(codecCtx, packet);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (receive < 0)
            {
                throw new InvalidOperationException($"avcodec_receive_packet failed: {Error(receive)}");
            }

            var span = new ReadOnlySpan<byte>(packet->data, packet->size);
            _fanout.Publish(span.ToArray());
            ffmpeg.av_packet_unref(packet);
        }
    }

    private static unsafe AVSampleFormat PickSampleFormat(AVCodec* codec)
    {
        // Prefer packed/planar s16 when the encoder accepts it (libmp3lame is typically S16P).
#pragma warning disable CS0618 // sample_fmts obsolete → avcodec_get_supported_config; fine for MVP
        if (codec->sample_fmts is not null)
        {
            for (var i = 0; codec->sample_fmts[i] != AVSampleFormat.AV_SAMPLE_FMT_NONE; i++)
            {
                if (codec->sample_fmts[i] == AVSampleFormat.AV_SAMPLE_FMT_S16)
                {
                    return AVSampleFormat.AV_SAMPLE_FMT_S16;
                }
            }

            for (var i = 0; codec->sample_fmts[i] != AVSampleFormat.AV_SAMPLE_FMT_NONE; i++)
            {
                if (codec->sample_fmts[i] == AVSampleFormat.AV_SAMPLE_FMT_S16P)
                {
                    return AVSampleFormat.AV_SAMPLE_FMT_S16P;
                }
            }

            return codec->sample_fmts[0];
        }
#pragma warning restore CS0618

        return AVSampleFormat.AV_SAMPLE_FMT_S16P;
    }

    private static unsafe void CopyPcmToFrame(
        AVFrame* frame,
        byte[] interleaved,
        int frameSamples,
        AVSampleFormat format)
    {
        fixed (byte* src = interleaved)
        {
            if (format == AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                Buffer.MemoryCopy(
                    src,
                    frame->data[0],
                    interleaved.Length,
                    interleaved.Length);
                return;
            }

            if (format == AVSampleFormat.AV_SAMPLE_FMT_S16P)
            {
                // De-interleave stereo s16le → planar.
                var left = (short*)frame->data[0];
                var right = (short*)frame->data[1];
                var samples = (short*)src;
                for (var i = 0; i < frameSamples; i++)
                {
                    left[i] = samples[i * 2];
                    right[i] = samples[(i * 2) + 1];
                }

                return;
            }

            if (format is AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                var samples = (short*)src;
                if (format == AVSampleFormat.AV_SAMPLE_FMT_FLT)
                {
                    var dst = (float*)frame->data[0];
                    for (var i = 0; i < frameSamples * CanonicalPcm.Channels; i++)
                    {
                        dst[i] = samples[i] / 32768f;
                    }
                }
                else
                {
                    var left = (float*)frame->data[0];
                    var right = (float*)frame->data[1];
                    for (var i = 0; i < frameSamples; i++)
                    {
                        left[i] = samples[i * 2] / 32768f;
                        right[i] = samples[(i * 2) + 1] / 32768f;
                    }
                }

                return;
            }

            throw new NotSupportedException($"Unsupported encoder sample format: {format}");
        }
    }

    private static unsafe string Error(int code)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(code, buffer, (ulong)size);
        return Marshal.PtrToStringUTF8((nint)buffer) ?? code.ToString();
    }
}
