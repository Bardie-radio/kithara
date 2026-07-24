using System.Collections.Concurrent;
using System.Diagnostics;

namespace Kithara.Infrastructure.Neck;

/// <summary>Shared ActivitySource for Neck lifecycle spans (auto-instrumentation is blind here).</summary>
public static class NeckActivity
{
    public static ActivitySource Source { get; } = new("bardie.kithara.neck");
}

/// <summary>
/// Owns per-Struna in-process MP3 encoders + silence feeders + encoded fan-out for Stream Server.
/// Skip / queue shift never restarts the encoder — only module Stop/Start.
/// </summary>
public sealed class StrunaEncoderSupervisor : IAsyncDisposable
{
    private readonly NeckOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StrunaEncoderSupervisor> _logger;
    private readonly ConcurrentDictionary<Guid, StrunaEncodeSession> _sessions = new();
    private int _disposed;

    public StrunaEncoderSupervisor(
        Microsoft.Extensions.Options.IOptions<NeckOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<StrunaEncoderSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool TryGetSession(Guid strunaId, out StrunaEncodeSession? session) =>
        _sessions.TryGetValue(strunaId, out session);

    public bool TryGetSessionBySlug(string slug, out StrunaEncodeSession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        foreach (var candidate in _sessions.Values)
        {
            if (string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase))
            {
                session = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Encode-alive: attach silence feeder + start in-process MP3 encoder reading <paramref name="fifoPath"/>.
    /// Idempotent if a session already exists for <paramref name="strunaId"/>.
    /// </summary>
    public async Task StartAsync(
        Guid strunaId,
        string slug,
        string fifoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(fifoPath);

        if (_sessions.ContainsKey(strunaId))
        {
            return;
        }

        using var activity = NeckActivity.Source.StartActivity("neck.encoder.start");
        activity?.SetTag("struna.id", strunaId.ToString("D"));
        activity?.SetTag("struna.slug", slug);

        cancellationToken.ThrowIfCancellationRequested();

        var silenceLogger = _loggerFactory.CreateLogger($"Kithara.Infrastructure.Neck.SilenceFeeder[{slug}]");
        var silence = new SilenceFeeder(fifoPath, strunaId, slug, silenceLogger);
        silence.SetEnabled(true);

        // Let silence open the FIFO (RDWR) before the encoder attaches as reader.
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        var fanout = new EncodedAudioFanout();
        var encoderLogger = _loggerFactory.CreateLogger($"Kithara.Infrastructure.Neck.Mp3Encoder[{slug}]");
        FfmpegMp3PcmEncoder encoder;
        try
        {
            encoder = new FfmpegMp3PcmEncoder(
                fifoPath,
                strunaId,
                slug,
                _options.Mp3BitrateKbps,
                fanout,
                _options.FfmpegRootPath,
                encoderLogger);
        }
        catch
        {
            await silence.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var session = new StrunaEncodeSession(strunaId, slug, fifoPath, silence, encoder, fanout);
        if (!_sessions.TryAdd(strunaId, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Encode-alive started for Struna {Id} ({Slug})", strunaId, slug);
    }

    public async Task StopAsync(Guid strunaId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(strunaId, out var session))
        {
            return;
        }

        using var activity = NeckActivity.Source.StartActivity("neck.encoder.stop");
        activity?.SetTag("struna.id", strunaId.ToString("D"));
        activity?.SetTag("struna.slug", session.Slug);

        await session.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("Encode session torn down for Struna {Id}", strunaId);
    }

    public void SetSilence(Guid strunaId, bool enabled)
    {
        if (_sessions.TryGetValue(strunaId, out var session))
        {
            session.SetSilence(enabled);
        }
    }

    public void SetStreamTitle(Guid strunaId, string? title)
    {
        if (_sessions.TryGetValue(strunaId, out var session))
        {
            session.StreamTitle = title ?? string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var id in _sessions.Keys.ToArray())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }
}
