namespace Bardie.Module.Source;

/// <summary>
/// Shared Bardie vocabulary for source-module <c>RegisterRequest.capabilities</c>.
/// Mesh contract treats capabilities as open strings; ModuleChannel does not interpret them.
/// Modules gate optional RPCs here; the Source Orchestrator gates host dials on the same values.
/// </summary>
public static class WellKnownSourceCapabilities
{
    /// <summary>Implements <c>Search</c>; eligible for <c>/api/search</c> fan-out.</summary>
    public const string Search = "search";

    /// <summary>Implements <c>StartTrack</c> / <c>StopTrack</c> (PCM to Struna FIFO).</summary>
    public const string Play = "play";

    /// <summary>Implements <c>PauseTrack</c> / <c>ResumeTrack</c> without tearing down the job.</summary>
    public const string Pause = "pause";

    /// <summary>Implements <c>PrefetchTrack</c> (warm blob cache; no FIFO write).</summary>
    public const string Prefetch = "prefetch";
}
