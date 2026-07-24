using Bardie.Module.Channel.Certificates;
using Bardie.Module.Channel.Channel;
using Bardie.Orchestrator.Source;
using Bardie.Orchestrator.Source.Catalog;
using Bardie.Orchestrator.Source.Ports;
using Kithara.Infrastructure.Neck;
using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kithara.Tests;

public class NeckStrunaFifoTests
{
    [Fact]
    public async Task Ensure_and_remove_struna_fifo()
    {
        var root = Path.Combine(Path.GetTempPath(), "kithara-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var encoder = new StrunaEncoderSupervisor(
                Options.Create(new NeckOptions { StrunaFifoRoot = root }),
                LoggerFactory.Create(b => { }),
                NullLogger<StrunaEncoderSupervisor>.Instance);
            var neck = new Neck(
                Options.Create(new NeckOptions { StrunaFifoRoot = root }),
                new ThrowingDbContextFactory(),
                CreateUnusedSourceOrchestrator(),
                encoder,
                NullLogger<Neck>.Instance);

            var strunaId = Guid.NewGuid();
            var path = await neck.EnsureStrunaFifoAsync(strunaId);
            Assert.Equal(neck.GetStrunaFifoPath(strunaId), path);
            Assert.True(File.Exists(path));

            // Idempotent
            var again = await neck.EnsureStrunaFifoAsync(strunaId);
            Assert.Equal(path, again);

            await neck.RemoveStrunaFifoAsync(strunaId);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SourceModuleOrchestrator CreateUnusedSourceOrchestrator() =>
        new(
            new SourceModuleCatalog(),
            new UnusedBlobStorage(),
            new UnusedChannelFactory(),
            new UnloadedCertificateStore(),
            new ConfigurationBuilder().Build(),
            NullLogger<SourceModuleOrchestrator>.Instance);

    private sealed class ThrowingDbContextFactory : IDbContextFactory<KitharaDbContext>
    {
        public KitharaDbContext CreateDbContext() =>
            throw new InvalidOperationException("DB not used in FIFO unit test.");

        public Task<KitharaDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB not used in FIFO unit test.");
    }

    private sealed class UnusedBlobStorage : IBlobStorage
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

    private sealed class UnusedChannelFactory : IModuleGrpcChannelFactory
    {
        public Grpc.Net.Client.GrpcChannel CreateChannel(
            string address,
            System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null,
            bool trustRemoteServerCertificate = false,
            bool ownsClientCertificate = false,
            string? expectedServerIdentity = null) =>
            throw new InvalidOperationException();
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
}
