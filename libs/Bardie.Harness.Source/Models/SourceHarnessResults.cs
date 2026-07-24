namespace Bardie.Harness.Source.Models;

/// <summary>One hit from a source module Search, tagged with the module slug.</summary>
public sealed record SourceSearchHit(
    string ModuleSlug,
    string TrackRef,
    string Title,
    string Artist,
    string ExternalId,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Aggregated Search across one or more modules.</summary>
public sealed record SourceSearchResult(
    bool Ok,
    IReadOnlyList<SourceSearchHit> Hits,
    string? FailureReason);

/// <summary>Outcome of StartTrack.</summary>
public sealed record StartTrackResult(
    bool Ok,
    string? ModuleSlug,
    string? TrackJobId,
    string? FailureReason);

/// <summary>Outcome of Stop / Pause / Resume.</summary>
public sealed record TrackControlResult(
    bool Ok,
    string? FailureReason);
