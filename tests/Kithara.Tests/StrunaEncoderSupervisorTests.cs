using System.Runtime.InteropServices;
using Kithara.Infrastructure.Neck;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;

namespace Kithara.Tests;

public class StrunaEncoderSupervisorTests
{
    [Fact]
    public async Task Start_silence_encoder_produces_mp3_chunks_then_stop()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            throw SkipException.ForSkip("Struna encoder FIFO integration requires a Unix host.");
        }

        // Magpie-style: BARDIE_FFMPEG_ROOT / Debian paths (FFmpeg.AutoGen 6.1 → libavcodec.so.60).
        var ffmpegRoot = Environment.GetEnvironmentVariable("BARDIE_FFMPEG_ROOT");
        if (string.IsNullOrWhiteSpace(ffmpegRoot))
        {
            foreach (var dir in new[]
                     {
                         "/usr/lib/x86_64-linux-gnu",
                         "/usr/lib/aarch64-linux-gnu",
                     })
            {
                if (File.Exists(Path.Combine(dir, "libavcodec.so.60")))
                {
                    ffmpegRoot = dir;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(ffmpegRoot))
        {
            var artifacts = FindArtifactsLibDir();
            if (artifacts is not null)
            {
                ffmpegRoot = artifacts;
            }
        }

        if (string.IsNullOrWhiteSpace(ffmpegRoot)
            || !File.Exists(Path.Combine(ffmpegRoot, "libavcodec.so.60")))
        {
            throw SkipException.ForSkip(
                "Need libavcodec.so.60 (FFmpeg.AutoGen 6.1.x). Set BARDIE_FFMPEG_ROOT or use Docker test stage.");
        }

        var root = Path.Combine(Path.GetTempPath(), "kithara-encode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fifo = Path.Combine(root, "test.pcm");
        MkFifo(fifo);

        var logs = new ListLogger();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new ListLoggerProvider(logs)));
        await using var encoder = new StrunaEncoderSupervisor(
            Options.Create(new NeckOptions
            {
                StrunaFifoRoot = root,
                Mp3BitrateKbps = 128,
                FfmpegRootPath = ffmpegRoot,
            }),
            loggerFactory,
            loggerFactory.CreateLogger<StrunaEncoderSupervisor>());

        var strunaId = Guid.NewGuid();
        await encoder.StartAsync(strunaId, "encode-test", fifo);

        Assert.True(encoder.TryGetSession(strunaId, out var session));
        Assert.NotNull(session);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var total = 0;
        try
        {
            await foreach (var chunk in session!.Fanout.SubscribeAsync(cts.Token))
            {
                total += chunk.Length;
                if (total >= 512)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — still assert below.
        }

        await encoder.StopAsync(strunaId);
        if (total == 0)
        {
            Assert.Fail(
                "Expected MP3 bytes from silence→libav encoder. Logs:\n"
                + string.Join('\n', logs.Messages));
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindArtifactsLibDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "ffmpeg-n6.1-linux64-gpl-shared", "lib");
            if (File.Exists(Path.Combine(candidate, "libavcodec.so.60")))
            {
                return candidate;
            }

            // Legacy extract name from earlier AutoGen 7.1 attempts.
            candidate = Path.Combine(dir.FullName, "artifacts", "ffmpeg-n7.1-linux64-gpl-shared", "lib");
            if (File.Exists(Path.Combine(candidate, "libavcodec.so.60")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void MkFifo(string path)
    {
        var rc = NativeMkFifo(path, 0x1B6);
        if (rc != 0)
        {
            throw new IOException($"mkfifo failed errno={Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "mkfifo")]
    private static extern int NativeMkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mode);

    private sealed class ListLoggerProvider(ListLogger sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => sink;
        public void Dispose() { }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"[{logLevel}] {formatter(state, exception)}";
            if (exception is not null)
            {
                line += " :: " + exception;
            }

            Messages.Add(line);
        }
    }
}
