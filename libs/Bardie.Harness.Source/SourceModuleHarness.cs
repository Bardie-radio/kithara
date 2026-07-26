using Bardie.Logos.Channel.Certificates;
using Bardie.Logos.Channel.Channel;
using Bardie.Logos.Channel.Participant;
using Bardie.Module.Source;
using Bardie.Harness.Source.Catalog;
using Bardie.Harness.Source.Models;
using Bardie.Harness.Source.Ports;
using Bardie.Source.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bardie.Harness.Source;

/// <summary>
/// Source module harness: catalog lookup, capability gates, and per-call SourceModule dials.
/// </summary>
public sealed class SourceModuleHarness
{
    private readonly ISourceModuleCatalog _catalog;
    private readonly IBlobStorage _blobStorage;
    private readonly IModuleGrpcChannelFactory _channelFactory;
    private readonly IModuleCertificateStore _certificateStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SourceModuleHarness> _logger;

    public SourceModuleHarness(
        ISourceModuleCatalog catalog,
        IBlobStorage blobStorage,
        IModuleGrpcChannelFactory channelFactory,
        IModuleCertificateStore certificateStore,
        IConfiguration configuration,
        ILogger<SourceModuleHarness> logger)
    {
        _catalog = catalog;
        _blobStorage = blobStorage;
        _channelFactory = channelFactory;
        _certificateStore = certificateStore;
        _configuration = configuration;
        _logger = logger;
    }

    public ISourceModuleCatalog Catalog => _catalog;

    public IBlobStorage BlobStorage => _blobStorage;

    public IModuleGrpcChannelFactory ChannelFactory => _channelFactory;

    public IReadOnlyCollection<SourceModuleRegistration> GetSources() => _catalog.List();

    /// <summary>
    /// Search one module (when <paramref name="moduleSlug"/> is set) or fan out across
    /// registered sources that advertise <see cref="WellKnownSourceCapabilities.Search"/>.
    /// </summary>
    public async Task<SourceSearchResult> SearchAsync(
        IReadOnlyDictionary<string, string> fields,
        string? moduleSlug = null,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var targets = ResolveSearchTargets(moduleSlug);
        if (targets.Count == 0)
        {
            var reason = string.IsNullOrWhiteSpace(moduleSlug)
                ? "No source modules with search capability are registered."
                : $"Source module '{moduleSlug}' is not registered or lacks search capability.";
            return new SourceSearchResult(false, [], reason);
        }

        var hits = new List<SourceSearchHit>();
        string? lastFailure = null;

        foreach (var module in targets)
        {
            try
            {
                using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
                var client = new SourceModule.SourceModuleClient(channel);
                var request = new SearchRequest { Limit = limit };
                foreach (var (key, value) in fields)
                {
                    request.Fields[key] = value;
                }

                var response = await client.SearchAsync(request, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                foreach (var result in response.Results)
                {
                    hits.Add(new SourceSearchHit(
                        module.Slug,
                        result.TrackRef,
                        result.Title,
                        result.Artist,
                        result.ExternalId,
                        result.Metadata.ToDictionary(
                            static p => p.Key,
                            static p => p.Value,
                            StringComparer.Ordinal)));
                }
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                lastFailure = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
                _logger.LogWarning(
                    ex,
                    "Search failed for source module {Slug} at {Address}",
                    module.Slug,
                    module.GrpcAdvertiseAddress);
            }
        }

        if (hits.Count == 0 && lastFailure is not null && targets.Count == 1)
        {
            return new SourceSearchResult(false, [], lastFailure);
        }

        return new SourceSearchResult(true, hits, null);
    }

    /// <summary>Start a track job on a play-capable module; <paramref name="audioEndpoint"/> is the session FIFO path.</summary>
    public async Task<StartTrackResult> StartTrackAsync(
        string moduleSlug,
        string strunaId,
        string trackRef,
        string audioEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEndpoint);

        if (!TryGetCapable(moduleSlug, WellKnownSourceCapabilities.Play, out var module, out var failure))
        {
            return new StartTrackResult(false, null, null, failure);
        }

        try
        {
            using var channel = CreateModuleChannel(module!.GrpcAdvertiseAddress, module.Slug);
            var client = new SourceModule.SourceModuleClient(channel);
            var response = await client.StartTrackAsync(
                    new StartTrackRequest
                    {
                        StrunaId = strunaId,
                        TrackRef = trackRef,
                        AudioEndpoint = audioEndpoint,
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new StartTrackResult(true, module.Slug, response.TrackJobId, null);
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "StartTrack failed for source module {Slug}", moduleSlug);
            var detail = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
            return new StartTrackResult(false, module!.Slug, null, detail);
        }
    }

    public Task<TrackControlResult> StopTrackAsync(
        string moduleSlug,
        string trackJobId,
        CancellationToken cancellationToken = default) =>
        ControlTrackAsync(
            moduleSlug,
            trackJobId,
            WellKnownSourceCapabilities.Play,
            async (client, jobId, ct) =>
            {
                var response = await client.StopTrackAsync(
                        new StopTrackRequest { TrackJobId = jobId },
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                return response.Ok;
            },
            cancellationToken);

    /// <summary>Cancels every in-flight track job for a Struna on a play-capable module.</summary>
    public async Task<TrackControlResult> StopTracksForStrunaAsync(
        string moduleSlug,
        string strunaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(strunaId);

        if (!TryGetCapable(moduleSlug, WellKnownSourceCapabilities.Play, out var module, out var failure))
        {
            return new TrackControlResult(false, failure);
        }

        try
        {
            using var channel = CreateModuleChannel(module!.GrpcAdvertiseAddress, module.Slug);
            var client = new SourceModule.SourceModuleClient(channel);
            var response = await client.StopTracksForStrunaAsync(
                    new StopTracksForStrunaRequest { StrunaId = strunaId },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new TrackControlResult(response.Ok, response.Ok ? null : "Module rejected StopTracksForStruna.");
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "StopTracksForStruna failed for source module {Slug}", moduleSlug);
            var detail = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
            return new TrackControlResult(false, detail);
        }
    }

    /// <summary>Ask a prefetch-capable module to warm its blob cache for a track_ref (no FIFO write).</summary>
    public async Task<TrackControlResult> PrefetchTrackAsync(
        string moduleSlug,
        string trackRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackRef);

        if (!TryGetCapable(moduleSlug, WellKnownSourceCapabilities.Prefetch, out var module, out var failure))
        {
            return new TrackControlResult(false, failure);
        }

        try
        {
            using var channel = CreateModuleChannel(module!.GrpcAdvertiseAddress, module.Slug);
            var client = new SourceModule.SourceModuleClient(channel);
            var response = await client.PrefetchTrackAsync(
                    new PrefetchTrackRequest { TrackRef = trackRef },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new TrackControlResult(response.Ok, response.Ok ? null : "Module rejected PrefetchTrack.");
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "PrefetchTrack failed for source module {Slug}", moduleSlug);
            var detail = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
            return new TrackControlResult(false, detail);
        }
    }

    public Task<TrackControlResult> PauseTrackAsync(
        string moduleSlug,
        string trackJobId,
        CancellationToken cancellationToken = default) =>
        ControlTrackAsync(
            moduleSlug,
            trackJobId,
            WellKnownSourceCapabilities.Pause,
            async (client, jobId, ct) =>
            {
                var response = await client.PauseTrackAsync(
                        new PauseTrackRequest { TrackJobId = jobId },
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                return response.Ok;
            },
            cancellationToken);

    public Task<TrackControlResult> ResumeTrackAsync(
        string moduleSlug,
        string trackJobId,
        CancellationToken cancellationToken = default) =>
        ControlTrackAsync(
            moduleSlug,
            trackJobId,
            WellKnownSourceCapabilities.Pause,
            async (client, jobId, ct) =>
            {
                var response = await client.ResumeTrackAsync(
                        new ResumeTrackRequest { TrackJobId = jobId },
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                return response.Ok;
            },
            cancellationToken);

    /// <summary>Server-stream TrackStatus events until the module ends the stream or the token cancels.</summary>
    public async IAsyncEnumerable<TrackStatusEvent> TrackStatusAsync(
        string moduleSlug,
        string trackJobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackJobId);

        if (!TryGetCapable(moduleSlug, WellKnownSourceCapabilities.Play, out var module, out _))
        {
            yield break;
        }

        using var channel = CreateModuleChannel(module!.GrpcAdvertiseAddress, module.Slug);
        var client = new SourceModule.SourceModuleClient(channel);
        using var call = client.TrackStatus(
            new TrackStatusRequest { TrackJobId = trackJobId },
            cancellationToken: cancellationToken);

        await foreach (var evt in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Ordered playable sources for quickplay when the client omits <c>module</c>.
    /// Uses <c>BARDIE_SOURCE_PRIORITY</c> when set; otherwise slug order.
    /// </summary>
    public IReadOnlyList<SourceModuleRegistration> OrderPlayableSources()
    {
        var playable = _catalog.List()
            .Where(HasCapability(WellKnownSourceCapabilities.Play))
            .Where(HasCapability(WellKnownSourceCapabilities.Search))
            .ToArray();

        return OrderByPriority(playable);
    }

    private async Task<TrackControlResult> ControlTrackAsync(
        string moduleSlug,
        string trackJobId,
        string requiredCapability,
        Func<SourceModule.SourceModuleClient, string, CancellationToken, Task<bool>> invoke,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackJobId);

        if (!TryGetCapable(moduleSlug, requiredCapability, out var module, out var failure))
        {
            return new TrackControlResult(false, failure);
        }

        try
        {
            using var channel = CreateModuleChannel(module!.GrpcAdvertiseAddress, module.Slug);
            var client = new SourceModule.SourceModuleClient(channel);
            var ok = await invoke(client, trackJobId, cancellationToken).ConfigureAwait(false);
            return new TrackControlResult(ok, ok ? null : "Module rejected the control request.");
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "Track control ({Capability}) failed for source module {Slug}",
                requiredCapability,
                moduleSlug);
            var detail = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
            return new TrackControlResult(false, detail);
        }
    }

    private IReadOnlyList<SourceModuleRegistration> ResolveSearchTargets(string? moduleSlug)
    {
        if (!string.IsNullOrWhiteSpace(moduleSlug))
        {
            if (_catalog.TryGet(moduleSlug.Trim(), out var single)
                && single is not null
                && HasCapability(WellKnownSourceCapabilities.Search)(single))
            {
                return [single];
            }

            return [];
        }

        return OrderByPriority(
            _catalog.List().Where(HasCapability(WellKnownSourceCapabilities.Search)).ToArray());
    }

    private bool TryGetCapable(
        string moduleSlug,
        string capability,
        out SourceModuleRegistration? module,
        out string? failure)
    {
        if (!_catalog.TryGet(moduleSlug.Trim(), out module) || module is null)
        {
            failure = $"Source module '{moduleSlug}' is not registered.";
            return false;
        }

        if (!HasCapability(capability)(module))
        {
            failure = $"Source module '{moduleSlug}' lacks '{capability}' capability.";
            module = null;
            return false;
        }

        failure = null;
        return true;
    }

    private IReadOnlyList<SourceModuleRegistration> OrderByPriority(
        IReadOnlyCollection<SourceModuleRegistration> modules)
    {
        var priority = _configuration["BARDIE_SOURCE_PRIORITY"];
        if (string.IsNullOrWhiteSpace(priority))
        {
            return modules.OrderBy(m => m.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var order = priority.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return modules
            .OrderBy(m =>
            {
                var index = Array.FindIndex(
                    order,
                    s => string.Equals(s, m.Slug, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Grpc.Net.Client.GrpcChannel CreateModuleChannel(string advertiseAddress, string moduleSlug)
    {
        var address = ModuleParticipantServiceCollectionExtensions.NormalizeGrpcAddress(advertiseAddress);
        if (!_certificateStore.IsLoaded)
        {
            return _channelFactory.CreateChannel(address);
        }

        // MESH-CHN-001: pin work-port server cert CN/SAN to the registered module slug.
        return _channelFactory.CreateChannel(
            address,
            _certificateStore.OpenOutboundClientIdentity(),
            trustRemoteServerCertificate: false,
            ownsClientCertificate: true,
            expectedServerIdentity: moduleSlug);
    }

    private static Func<SourceModuleRegistration, bool> HasCapability(string capability) =>
        module => module.Capabilities.Any(c =>
            string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
}
