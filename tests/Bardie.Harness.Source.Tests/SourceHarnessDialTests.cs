using Bardie.Logos.Channel;
using Bardie.Logos.Channel.Certificates;
using Bardie.Logos.Channel.Channel;
using Bardie.Module.Source;
using Bardie.Harness.Source;
using Bardie.Harness.Source.Catalog;
using Bardie.Harness.Source.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Bardie.Harness.Source.Tests;

public class SourceHarnessDialTests
{
    [Fact]
    public void AddSourceModuleHarness_registers_catalog_and_module_channel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IBlobStorage, FakeBlobStorage>();
        services.AddSourceModuleHarness(options =>
        {
            options.TlsDataPath = Path.Combine(Path.GetTempPath(), "src-harness-mtls-" + Guid.NewGuid().ToString("N"));
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ISourceModuleCatalog>());
        Assert.NotNull(provider.GetRequiredService<SourceModuleHarness>());
        Assert.NotNull(provider.GetRequiredService<IModuleCertificateStore>());
    }

    [Fact]
    public async Task SearchAsync_fails_when_no_search_capable_modules()
    {
        var orch = CreateHarness(catalog =>
        {
            catalog.Upsert(new SourceModuleRegistration
            {
                Slug = "starling",
                GrpcAdvertiseAddress = "https://starling:5001",
                Capabilities = [WellKnownSourceCapabilities.Play],
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });
        });

        var result = await orch.SearchAsync(new Dictionary<string, string> { ["title"] = "x" });
        Assert.False(result.Ok);
        Assert.Empty(result.Hits);
        Assert.Contains("search", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTrackAsync_fails_when_module_lacks_play()
    {
        var orch = CreateHarness(catalog =>
        {
            catalog.Upsert(new SourceModuleRegistration
            {
                Slug = "index-only",
                GrpcAdvertiseAddress = "https://index:5001",
                Capabilities = [WellKnownSourceCapabilities.Search],
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });
        });

        var result = await orch.StartTrackAsync(
            "index-only",
            Guid.NewGuid().ToString("D"),
            "track-ref",
            "/tmp/fake.pcm");

        Assert.False(result.Ok);
        Assert.Contains("play", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PauseTrackAsync_fails_when_module_lacks_pause()
    {
        var orch = CreateHarness(catalog =>
        {
            catalog.Upsert(new SourceModuleRegistration
            {
                Slug = "magpie",
                GrpcAdvertiseAddress = "https://magpie:5001",
                Capabilities =
                [
                    WellKnownSourceCapabilities.Search,
                    WellKnownSourceCapabilities.Play,
                ],
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });
        });

        var result = await orch.PauseTrackAsync("magpie", "job-1");
        Assert.False(result.Ok);
        Assert.Contains("pause", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrefetchTrackAsync_fails_when_module_lacks_prefetch()
    {
        var orch = CreateHarness(catalog =>
        {
            catalog.Upsert(new SourceModuleRegistration
            {
                Slug = "magpie",
                GrpcAdvertiseAddress = "https://magpie:5001",
                Capabilities =
                [
                    WellKnownSourceCapabilities.Search,
                    WellKnownSourceCapabilities.Play,
                ],
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });
        });

        var result = await orch.PrefetchTrackAsync("magpie", "ref-1");
        Assert.False(result.Ok);
        Assert.Contains("prefetch", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrderPlayableSources_honours_BARDIE_SOURCE_PRIORITY()
    {
        var config = new PriorityConfiguration("catbird,magpie");

        var orch = CreateHarness(
            catalog =>
            {
                var now = DateTimeOffset.UtcNow;
                catalog.Upsert(Reg("magpie", now));
                catalog.Upsert(Reg("catbird", now));
                catalog.Upsert(Reg("starling", now, playOnly: true));
            },
            config);

        var ordered = orch.OrderPlayableSources();
        Assert.Equal(["catbird", "magpie"], ordered.Select(m => m.Slug).ToArray());
    }

    private static SourceModuleRegistration Reg(string slug, DateTimeOffset now, bool playOnly = false) =>
        new()
        {
            Slug = slug,
            GrpcAdvertiseAddress = $"https://{slug}:5001",
            Capabilities = playOnly
                ? [WellKnownSourceCapabilities.Play]
                :
                [
                    WellKnownSourceCapabilities.Search,
                    WellKnownSourceCapabilities.Play,
                ],
            RegisteredAt = now,
            LastHeartbeatAt = now,
            ExpiresAt = now.AddMinutes(5),
        };

    private static SourceModuleHarness CreateHarness(
        Action<ISourceModuleCatalog> seed,
        IConfiguration? configuration = null)
    {
        var catalog = new SourceModuleCatalog();
        seed(catalog);
        configuration ??= new ConfigurationBuilder().Build();
        return new SourceModuleHarness(
            catalog,
            new FakeBlobStorage(),
            new ThrowingChannelFactory(),
            new UnloadedCertificateStore(),
            configuration,
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None))
                .CreateLogger<SourceModuleHarness>());
    }

    private sealed class FakeBlobStorage : IBlobStorage
    {
        public Task<long> PutAsync(
            string key,
            Stream content,
            string? contentType = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task<BlobReadResult?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<BlobReadResult?>(null);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingChannelFactory : IModuleGrpcChannelFactory
    {
        public Grpc.Net.Client.GrpcChannel CreateChannel(
            string address,
            System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null,
            bool trustRemoteServerCertificate = false,
            bool ownsClientCertificate = false,
            string? expectedServerIdentity = null) =>
            throw new InvalidOperationException("Dial not expected in capability-gate unit tests.");
    }

    private sealed class UnloadedCertificateStore : IModuleCertificateStore
    {
        public bool IsLoaded => false;
        public string CaThumbprint => string.Empty;
        public string CaCertificatePem => string.Empty;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 CaCertificate =>
            throw new InvalidOperationException();
        public System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCertificate =>
            throw new InvalidOperationException();
        public System.Security.Cryptography.X509Certificates.X509Certificate2 OpenOutboundClientIdentity() =>
            throw new InvalidOperationException();
        public bool TryGetPresharedClientMaterial(
            string slug,
            out System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
        {
            certificate = null;
            return false;
        }

        public bool TryGetPresharedClientExpiry(string slug, out DateTimeOffset expiresAt)
        {
            expiresAt = default;
            return false;
        }

        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PriorityConfiguration : IConfiguration
    {
        private readonly string _priority;

        public PriorityConfiguration(string priority) => _priority = priority;

        public string? this[string key]
        {
            get => string.Equals(key, "BARDIE_SOURCE_PRIORITY", StringComparison.Ordinal) ? _priority : null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) =>
            new EmptySection(key, this[key]);

        private sealed class EmptySection : IConfigurationSection
        {
            public EmptySection(string key, string? value)
            {
                Key = key;
                Value = value;
                Path = key;
            }

            public string? this[string key]
            {
                get => null;
                set => throw new NotSupportedException();
            }

            public string Key { get; }
            public string Path { get; }
            public string? Value { get; set; }
            public IEnumerable<IConfigurationSection> GetChildren() => [];
            public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
            public IConfigurationSection GetSection(string key) => new EmptySection(key, null);
        }
    }
}
