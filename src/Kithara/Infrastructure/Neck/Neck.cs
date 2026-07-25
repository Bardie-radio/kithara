using System.Collections.Concurrent;
using Bardie.Harness.Source;
using Kithara.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kithara.Infrastructure.Neck;

/// <summary>
/// Stream lifecycle (Neck): alive Strunas, session FIFOs, encode supervisor, source track jobs,
/// TrackStatus → now-playing / queue advance.
/// Split across partials: Strunas, Playback, Queue, Grants, Fifo, Models.
/// </summary>
public sealed partial class Neck
{
    private readonly string _fifoRoot;
    private readonly IDbContextFactory<KitharaDbContext> _dbFactory;
    private readonly SourceModuleHarness _orch;
    private readonly StrunaEncoderSupervisor _encoder;
    private readonly ConcurrentDictionary<Guid, ActiveTrackJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, NowPlayingSnapshot> _nowPlaying = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _statusWatchers = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _strunaGates = new();
    private readonly ILogger<Neck> _logger;

    /// <summary>
    /// Caps TrackStatus reconnects after disconnect / premature Ended without a terminal event
    /// (NECK-JOB-001). Beyond this, Neck clears the job so the next play does not need an orphan sweep.
    /// </summary>
    private const int MaxStatusResubscribeAttempts = 8;

    private static readonly TimeSpan StatusResubscribeDelay = TimeSpan.FromMilliseconds(250);

    public Neck(
        IOptions<NeckOptions> options,
        IDbContextFactory<KitharaDbContext> dbFactory,
        SourceModuleHarness orch,
        StrunaEncoderSupervisor encoder,
        ILogger<Neck> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _orch = orch ?? throw new ArgumentNullException(nameof(orch));
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var path = options.Value.StrunaFifoRoot;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fifoRoot = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.Combine(_fifoRoot, "strunas"));
    }

    private SemaphoreSlim Gate(Guid strunaId) =>
        _strunaGates.GetOrAdd(strunaId, static _ => new SemaphoreSlim(1, 1));

    private static string CreateSecret(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        Span<byte> bytes = stackalloc byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
