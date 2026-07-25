using Bardie.Module.Channel.Manifest;
using Bardie.Modules.V1;
using Bardie.Source.V1;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bardie.Module.Source.Tests;

public class SourceModuleLibTests
{
    [Fact]
    public void CanonicalPcm_mvp_profile_is_s16le_48k_stereo()
    {
        Assert.Equal(48_000, CanonicalPcm.SampleRate);
        Assert.Equal(2, CanonicalPcm.Channels);
        Assert.Equal(sizeof(short), CanonicalPcm.BytesPerSample);
        Assert.Equal(CanonicalPcm.BytesPerSample * CanonicalPcm.Channels, CanonicalPcm.BytesPerFrame);
        Assert.Equal(CanonicalPcm.SampleRate * CanonicalPcm.BytesPerFrame, CanonicalPcm.BytesPerSecond);
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var adapter = new StubSource(new ModuleManifest { Slug = "magpie", Kind = "source" });
        var response = await adapter.Health(new HealthRequest(), context: null!);
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task PauseTrack_without_capability_is_failed_precondition()
    {
        var adapter = new StubSource(new ModuleManifest
        {
            Slug = "starling",
            Kind = "source",
            Capabilities = ["search", "play"],
        });

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => adapter.PauseTrack(new PauseTrackRequest { TrackJobId = "x" }, context: null!));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task PrefetchTrack_without_capability_is_failed_precondition()
    {
        var adapter = new StubSource(new ModuleManifest
        {
            Slug = "starling",
            Kind = "source",
            Capabilities = ["search", "play"],
        });

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => adapter.PrefetchTrack(new PrefetchTrackRequest { TrackRef = "x" }, context: null!));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task PrefetchTrack_with_capability_without_override_is_unimplemented()
    {
        var adapter = new StubSource(new ModuleManifest
        {
            Slug = "magpie",
            Kind = "source",
            Capabilities = ["search", "play", "prefetch"],
        });

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => adapter.PrefetchTrack(new PrefetchTrackRequest { TrackRef = "x" }, context: null!));
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task PauseTrack_with_capability_without_registry_is_unimplemented()
    {
        var adapter = new StubSource(new ModuleManifest
        {
            Slug = "magpie",
            Kind = "source",
            Capabilities = ["search", "play", "pause"],
        });

        var ex = await Assert.ThrowsAsync<RpcException>(
            () => adapter.PauseTrack(new PauseTrackRequest { TrackJobId = "x" }, context: null!));
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task PauseResumeStop_use_registry_defaults()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 4 }));
        var job = registry.Create("s1", "ref", "/tmp/a.pcm");
        Assert.Equal(TrackState.Preparing, job.State);
        var adapter = new StubSource(
            new ModuleManifest
            {
                Slug = "magpie",
                Kind = "source",
                Capabilities = ["play", "pause"],
            },
            registry);

        var paused = await adapter.PauseTrack(new PauseTrackRequest { TrackJobId = job.TrackJobId }, null!);
        Assert.True(paused.Ok);
        Assert.Equal(TrackState.Paused, job.State);

        var resumed = await adapter.ResumeTrack(new ResumeTrackRequest { TrackJobId = job.TrackJobId }, null!);
        Assert.True(resumed.Ok);
        Assert.Equal(TrackState.Running, job.State);

        var stopped = await adapter.StopTrack(new StopTrackRequest { TrackJobId = job.TrackJobId }, null!);
        Assert.True(stopped.Ok);
        Assert.True(job.Cancellation.IsCancellationRequested);

        var again = await adapter.StopTrack(new StopTrackRequest { TrackJobId = job.TrackJobId }, null!);
        Assert.True(again.Ok);

        var missing = await adapter.StopTrack(new StopTrackRequest { TrackJobId = "no-such-job" }, null!);
        Assert.True(missing.Ok);
    }

    [Fact]
    public async Task StopTracksForStruna_cancels_matching_jobs()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 4 }));
        var a = registry.Create("s1", "ref-a", "/tmp/a.pcm");
        var other = registry.Create("s2", "ref-b", "/tmp/b.pcm");
        var adapter = new StubSource(
            new ModuleManifest { Slug = "magpie", Kind = "source", Capabilities = ["play"] },
            registry);

        var response = await adapter.StopTracksForStruna(
            new StopTracksForStrunaRequest { StrunaId = "s1" },
            null!);
        Assert.True(response.Ok);
        Assert.Equal(1, response.StoppedCount);
        Assert.True(a.Cancellation.IsCancellationRequested);
        Assert.Equal(TrackState.Preparing, a.State);
        Assert.True(registry.TryGet(a.TrackJobId, out _));
        Assert.True(registry.TryGet(other.TrackJobId, out _));
    }

    [Fact]
    public void TrackJobRegistry_create_replaces_sibling_on_same_struna()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 1 }));
        var first = registry.Create("s1", "ref-a", "/tmp/a.pcm");
        var second = registry.Create("s1", "ref-b", "/tmp/b.pcm");
        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(first.IsActive);
        Assert.True(registry.TryGet(first.TrackJobId, out _));
        Assert.True(registry.TryGet(second.TrackJobId, out _));
        Assert.Equal(TrackState.Preparing, second.State);
        Assert.Equal(1, registry.CountActive());
    }

    [Fact]
    public void SearchFieldsCustomizer_defaults_to_title()
    {
        var customizer = new SourceSearchFieldsRegisterRequestCustomizer(
            Options.Create(new SourceModuleOptions()));
        var request = new RegisterRequest();
        customizer.Customize(request, new ModuleManifest { Slug = "magpie", Kind = "source" });

        Assert.Equal(RegisterRequest.DetailsOneofCase.Source, request.DetailsCase);
        Assert.Single(request.Source.SearchFields);
        Assert.Equal("title", request.Source.SearchFields[0].Name);
        Assert.True(request.Source.SearchFields[0].Required);
    }

    [Fact]
    public void SearchFieldsCustomizer_prefers_manifest_over_options()
    {
        var manifest = ModuleManifestLoader.LoadFromJson("""
            {
              "slug": "magpie",
              "kind": "source",
              "capabilities": [],
              "source": {
                "searchFields": [
                  { "name": "title", "required": true },
                  { "name": "artist", "required": false }
                ]
              }
            }
            """);

        var customizer = new SourceSearchFieldsRegisterRequestCustomizer(
            Options.Create(new SourceModuleOptions
            {
                SearchFields = [new SourceSearchFieldOptions { Name = "owner", Required = false }],
            }));
        var request = new RegisterRequest();
        customizer.Customize(request, manifest);

        Assert.Equal(2, request.Source.SearchFields.Count);
        Assert.Equal("title", request.Source.SearchFields[0].Name);
        Assert.Equal("artist", request.Source.SearchFields[1].Name);
        Assert.False(request.Source.SearchFields[1].Required);
    }

    [Fact]
    public void TrackJobRegistry_create_get_remove()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions()));
        var job = registry.Create("struna-1", "ref", "/tmp/a.pcm");
        Assert.Equal(TrackState.Preparing, job.State);
        Assert.True(registry.TryGet(job.TrackJobId, out var found));
        Assert.Equal(job.TrackJobId, found!.TrackJobId);
        Assert.True(registry.TryRemove(job.TrackJobId, out _));
        Assert.False(registry.TryGet(job.TrackJobId, out _));
    }

    [Fact]
    public void TrackJobRegistry_enforces_parallel_limit_across_strunas()
    {
        var registry = new TrackJobRegistry(Options.Create(new SourceModuleOptions { MaxParallelJobs = 1 }));
        registry.Create("s1", "ref-a", "/tmp/a.pcm");
        var ex = Assert.Throws<InvalidOperationException>(() => registry.Create("s2", "ref-b", "/tmp/b.pcm"));
        Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModuleBlobKeys_for_object()
    {
        Assert.Equal("tunes/magpie/abc", ModuleBlobKeys.ForObject("Magpie", "abc"));
        Assert.Throws<ArgumentException>(() => ModuleBlobKeys.ForObject("magpie", "../x"));
    }

    [Fact]
    public async Task FifoAudioSink_writes_to_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "bardie-fifo-sink-" + Guid.NewGuid().ToString("N") + ".pcm");
        await File.WriteAllBytesAsync(path, []);
        try
        {
            var sink = new FifoAudioSink();
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            await using var input = new MemoryStream(payload);
            await sink.WriteAsync(path, input);

            var written = await File.ReadAllBytesAsync(path);
            Assert.Equal(payload, written);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FifoAudioSink_honours_pause_predicate()
    {
        var path = Path.Combine(Path.GetTempPath(), "bardie-fifo-pause-" + Guid.NewGuid().ToString("N") + ".pcm");
        await File.WriteAllBytesAsync(path, []);
        var paused = true;
        var sink = new FifoAudioSink();
        var payload = new byte[] { 9, 8, 7 };
        await using var input = new MemoryStream(payload);

        var write = sink.WriteAsync(path, input, CancellationToken.None, () => paused);
        await Task.Delay(80);
        Assert.False(write.IsCompleted);
        paused = false;
        await write;

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        File.Delete(path);
    }

    [Fact]
    public void MapStartFailure_maps_common_exceptions()
    {
        var exhausted = SourceModuleRpc.MapStartFailure(new InvalidOperationException("full"));
        Assert.Equal(StatusCode.ResourceExhausted, exhausted.StatusCode);

        var invalid = SourceModuleRpc.MapStartFailure(new ArgumentException("bad"));
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);
    }

    private sealed class StubSource : SourceModuleBase
    {
        public StubSource(ModuleManifest manifest, ITrackJobRegistry? jobs = null)
            : base(manifest, jobs)
        {
        }
    }
}
