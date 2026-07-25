using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bardie.Module.Source.Debug;

/// <summary>
/// Shared proof-track identity for source-module FIFO / protocol smoke
/// (<c>StartTrack</c> → session PCM without a real media backend).
/// </summary>
public static class DevProofTrack
{
    public const string TrackRef = "sine";
    public const string ExternalId = "sine";
    public const string Title = "Source module sine (PCM proof)";
    public const string Artist = "bardie.dev";

    /// <summary>True for <c>sine</c>, <c>{slug}:sine</c>, or plain query equal to the proof ref.</summary>
    public static bool Matches(string? queryOrRef, string? moduleSlug = null)
    {
        if (string.IsNullOrWhiteSpace(queryOrRef))
        {
            return false;
        }

        var trimmed = queryOrRef.Trim();
        if (string.Equals(trimmed, TrackRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(moduleSlug)
            && string.Equals(
                trimmed,
                $"{moduleSlug.Trim()}:{TrackRef}",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colon = trimmed.IndexOf(':');
        return colon > 0
            && string.Equals(trimmed[(colon + 1)..], TrackRef, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Options for <see cref="SinePcmGenerator"/>.</summary>
public sealed class SinePcmOptions
{
    public const string SectionName = "SinePcm";

    public double FrequencyHz { get; set; } = 440;

    public double DurationSeconds { get; set; } = 30;
}

/// <summary>
/// Canonical PCM sine for StartTrack → FIFO protocol checks (see <see cref="CanonicalPcm"/>).
/// </summary>
public sealed class SinePcmGenerator
{
    public const int SampleRate = CanonicalPcm.SampleRate;
    public const int Channels = CanonicalPcm.Channels;

    private readonly SinePcmOptions _options;

    public SinePcmGenerator(IOptions<SinePcmOptions> options)
    {
        _options = options.Value;
    }

    public Stream CreateStream(CancellationToken cancellationToken = default)
    {
        var frequency = _options.FrequencyHz <= 0 ? 440 : _options.FrequencyHz;
        var duration = _options.DurationSeconds <= 0 ? 30 : _options.DurationSeconds;
        var totalSamples = (int)(CanonicalPcm.SampleRate * duration);
        var memory = new MemoryStream(totalSamples * CanonicalPcm.BytesPerFrame);
        var phase = 0.0;
        var phaseIncrement = 2.0 * Math.PI * frequency / CanonicalPcm.SampleRate;

        for (var i = 0; i < totalSamples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = (short)(Math.Sin(phase) * short.MaxValue * 0.2);
            phase += phaseIncrement;
            if (phase > 2.0 * Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }

            memory.WriteByte((byte)(sample & 0xFF));
            memory.WriteByte((byte)((sample >> 8) & 0xFF));
            memory.WriteByte((byte)(sample & 0xFF));
            memory.WriteByte((byte)((sample >> 8) & 0xFF));
        }

        memory.Position = 0;
        return memory;
    }
}

public static class SourceModuleDebugServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SinePcmGenerator"/> + <see cref="SinePcmOptions"/> for protocol smoke.
    /// Call only from Debug / test hosts that reference this package.
    /// </summary>
    public static IServiceCollection AddSourceModuleDevFixtures(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SinePcmOptions>(configuration.GetSection(SinePcmOptions.SectionName));
        services.AddSingleton<SinePcmGenerator>();
        return services;
    }
}
