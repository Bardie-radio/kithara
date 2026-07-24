using System.Collections.Concurrent;
using Bardie.Source.V1;
using Microsoft.Extensions.Options;

namespace Bardie.Module.Source;

/// <summary>In-flight track job tracked by a source module.</summary>
public sealed class TrackJob
{
    public required string TrackJobId { get; init; }
    public required string StrunaId { get; init; }
    public required string TrackRef { get; init; }
    public required string AudioEndpoint { get; init; }
    public TrackState State { get; set; } = TrackState.Preparing;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? ErrorMessage { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsActive =>
        !Cancellation.IsCancellationRequested
        && State is TrackState.Preparing or TrackState.Running or TrackState.Paused;

    public void MarkPreparing() => State = TrackState.Preparing;

    public void MarkPaused() => State = TrackState.Paused;

    public void MarkRunning() => State = TrackState.Running;

    public void MarkEnded() => State = TrackState.Ended;

    public void MarkFailed(string message)
    {
        State = TrackState.Error;
        ErrorMessage = message;
    }
}

public interface ITrackJobRegistry
{
    TrackJob Create(string strunaId, string trackRef, string audioEndpoint);
    bool TryGet(string trackJobId, out TrackJob? job);
    bool TryRemove(string trackJobId, out TrackJob? job);
    IReadOnlyCollection<TrackJob> List();
    bool TryStop(string trackJobId);
    int TryStopAllForStruna(string strunaId);
    bool TryPause(string trackJobId);
    bool TryResume(string trackJobId);
    int CountActive();
}

/// <summary>Concurrent track-job registry for Stop / Pause / Resume / TrackStatus.</summary>
public sealed class TrackJobRegistry : ITrackJobRegistry
{
    private readonly ConcurrentDictionary<string, TrackJob> _jobs = new(StringComparer.Ordinal);
    private readonly SourceModuleOptions _options;

    public TrackJobRegistry(IOptions<SourceModuleOptions> options)
    {
        _options = options.Value;
    }

    public TrackJob Create(string strunaId, string trackRef, string audioEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEndpoint);

        // Orphan / replace: cancel prior jobs for this Struna. Do NOT remove here —
        // removing races TrackStatus into synthetic Ended while RunJobAsync is still preparing.
        List()
            .Where(j => string.Equals(j.StrunaId, strunaId, StringComparison.Ordinal))
            .ToList()
            .ForEach(j => j.Cancellation.Cancel());

        var max = _options.MaxParallelJobs;
        if (max > 0 && CountActive() >= max)
        {
            throw new InvalidOperationException($"Parallel track-job limit reached ({max}).");
        }

        var job = new TrackJob
        {
            TrackJobId = Guid.NewGuid().ToString("N"),
            StrunaId = strunaId,
            TrackRef = trackRef,
            AudioEndpoint = audioEndpoint,
            State = TrackState.Preparing,
        };

        if (!_jobs.TryAdd(job.TrackJobId, job))
        {
            throw new InvalidOperationException("Failed to register track job id.");
        }

        return job;
    }

    public bool TryGet(string trackJobId, out TrackJob? job) =>
        _jobs.TryGetValue(trackJobId, out job);

    public bool TryRemove(string trackJobId, out TrackJob? job)
    {
        if (_jobs.TryRemove(trackJobId, out var removed))
        {
            job = removed;
            return true;
        }

        job = null;
        return false;
    }

    public IReadOnlyCollection<TrackJob> List() => _jobs.Values.ToArray();

    public int CountActive() => _jobs.Values.Count(j => j.IsActive);

    public bool TryStop(string trackJobId)
    {
        if (!TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    public int TryStopAllForStruna(string strunaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);
        // Cancel only — do not MarkEnded here. Marking Ended while WriteAsync is still
        // winding down makes Neck clear now-playing while PCM/MP3 is still audible.
        var matching = List()
            .Where(j =>
                string.Equals(j.StrunaId, strunaId, StringComparison.Ordinal) && j.IsActive)
            .ToList();
        matching.ForEach(j => j.Cancellation.Cancel());
        return matching.Count;
    }

    public bool TryPause(string trackJobId)
    {
        if (!TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        job.MarkPaused();
        return true;
    }

    public bool TryResume(string trackJobId)
    {
        if (!TryGet(trackJobId, out var job) || job is null)
        {
            return false;
        }

        if (job.State == TrackState.Paused)
        {
            job.MarkRunning();
        }

        return true;
    }
}
