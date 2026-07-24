using Bardie.Module.Channel.Manifest;
using Bardie.Source.V1;
using Grpc.Core;

namespace Bardie.Module.Source;

/// <summary>
/// Thin SourceModule base: health, pause/prefetch capability gates, default Stop/Pause/Resume/TrackStatus
/// against <see cref="ITrackJobRegistry"/>, and job-not-found helpers.
/// Concrete Search / StartTrack stay in the module.
/// </summary>
public abstract class SourceModuleBase : SourceModule.SourceModuleBase
{
    private readonly ITrackJobRegistry? _jobs;

    protected ModuleManifest Manifest { get; }

    /// <summary>Job registry when the module uses shared Stop / Pause / Resume / TrackStatus defaults.</summary>
    protected ITrackJobRegistry? Jobs => _jobs;

    protected SourceModuleBase(ModuleManifest manifest, ITrackJobRegistry? jobs = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _jobs = jobs;
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) =>
        Task.FromResult(new HealthResponse { Ok = true });

    public override Task<StopTrackResponse> StopTrack(StopTrackRequest request, ServerCallContext context)
    {
        var jobs = RequireJobs();
        // Idempotent: job may already be gone after natural end, sibling replace, or StopTracksForStruna.
        jobs.TryStop(request.TrackJobId);
        return Task.FromResult(new StopTrackResponse { Ok = true });
    }

    public override Task<StopTracksForStrunaResponse> StopTracksForStruna(
        StopTracksForStrunaRequest request,
        ServerCallContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StrunaId);
        var jobs = RequireJobs();
        var stopped = jobs.TryStopAllForStruna(request.StrunaId);
        return Task.FromResult(new StopTracksForStrunaResponse
        {
            Ok = true,
            StoppedCount = stopped,
        });
    }

    public override Task<PrefetchTrackResponse> PrefetchTrack(
        PrefetchTrackRequest request,
        ServerCallContext context)
    {
        if (!HasCapability(WellKnownSourceCapabilities.Prefetch))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Module '{Manifest.Slug}' does not advertise '{WellKnownSourceCapabilities.Prefetch}'."));
        }

        return PrefetchTrackCoreAsync(request, context);
    }

    public override Task<PauseTrackResponse> PauseTrack(PauseTrackRequest request, ServerCallContext context)
    {
        if (!HasCapability(WellKnownSourceCapabilities.Pause))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Module '{Manifest.Slug}' does not advertise '{WellKnownSourceCapabilities.Pause}'."));
        }

        return PauseTrackCoreAsync(request, context);
    }

    public override Task<ResumeTrackResponse> ResumeTrack(ResumeTrackRequest request, ServerCallContext context)
    {
        if (!HasCapability(WellKnownSourceCapabilities.Pause))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Module '{Manifest.Slug}' does not advertise '{WellKnownSourceCapabilities.Pause}'."));
        }

        return ResumeTrackCoreAsync(request, context);
    }

    public override Task TrackStatus(
        TrackStatusRequest request,
        IServerStreamWriter<TrackStatusEvent> responseStream,
        ServerCallContext context)
    {
        var jobs = RequireJobs();
        return TrackStatusStreaming.WriteEventsAsync(
            jobs,
            request.TrackJobId,
            responseStream,
            context.CancellationToken);
    }

    /// <summary>Override when the module advertises <c>prefetch</c>.</summary>
    protected virtual Task<PrefetchTrackResponse> PrefetchTrackCoreAsync(
        PrefetchTrackRequest request,
        ServerCallContext context) =>
        throw new RpcException(new Status(
            StatusCode.Unimplemented,
            $"Module '{Manifest.Slug}' does not implement PrefetchTrack."));

    /// <summary>Override when the module advertises <c>pause</c> but does not use the shared registry.</summary>
    protected virtual Task<PauseTrackResponse> PauseTrackCoreAsync(
        PauseTrackRequest request,
        ServerCallContext context)
    {
        var jobs = RequireJobs();
        if (!jobs.TryPause(request.TrackJobId))
        {
            throw JobNotFound(request.TrackJobId);
        }

        return Task.FromResult(new PauseTrackResponse { Ok = true });
    }

    /// <summary>Override when the module advertises <c>pause</c> but does not use the shared registry.</summary>
    protected virtual Task<ResumeTrackResponse> ResumeTrackCoreAsync(
        ResumeTrackRequest request,
        ServerCallContext context)
    {
        var jobs = RequireJobs();
        if (!jobs.TryResume(request.TrackJobId))
        {
            throw JobNotFound(request.TrackJobId);
        }

        return Task.FromResult(new ResumeTrackResponse { Ok = true });
    }

    protected bool HasCapability(string capability) =>
        Manifest.Capabilities.Any(c =>
            string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));

    public static RpcException JobNotFound(string trackJobId) =>
        new(new Status(StatusCode.NotFound, $"Track job '{trackJobId}' was not found."));

    private ITrackJobRegistry RequireJobs() =>
        _jobs ?? throw new RpcException(new Status(
            StatusCode.Unimplemented,
            "Track job registry is not wired for this module."));
}
