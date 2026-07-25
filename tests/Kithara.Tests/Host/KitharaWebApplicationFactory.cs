using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Catalog;
using Bardie.Logos.Channel;
using Kithara.Features.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Kithara.Tests.Host;

/// <summary>Shared host + fake Bes for META-QA-001 (serial collection).</summary>
public sealed class HostE2EFixture : IAsyncLifetime
{
    public KitharaWebApplicationFactory Factory { get; } = new();

    public FakeAuthAdapterHost FakeAuth { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        FakeAuth = await FakeAuthAdapterHost.StartAsync().ConfigureAwait(false);
        Client = Factory.CreateClient();
        await Factory.RegisterFakeAuthAsync(FakeAuth).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await FakeAuth.DisposeAsync().ConfigureAwait(false);
        await Factory.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class KitharaWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "kithara-host-" + Guid.NewGuid().ToString("N"));

    public string DataRoot => _root;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "kithara.db");
        var tlsPath = Path.Combine(_root, "mtls");
        var audioPath = Path.Combine(_root, "audio");
        var blobsPath = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(tlsPath);
        Directory.CreateDirectory(audioPath);
        Directory.CreateDirectory(blobsPath);

        builder.UseEnvironment("Development");
        builder.UseSetting("DbProvider", "sqlite");
        builder.UseSetting("DbConnectionString", $"Data Source={dbPath}");
        builder.UseSetting("BARDIE_GRPC_TLS_DATA_PATH", tlsPath);
        builder.UseSetting("BARDIE_STRUNA_FIFO_PATH", audioPath);
        builder.UseSetting("BARDIE_STORAGE_PATH", blobsPath);
        builder.UseSetting("BARDIE_STORAGE_DRIVER", "local");
        builder.UseSetting("BARDIE_JOIN_SECRETS", """{"bes":"test-join","magpie":"test-join"}""");
        builder.UseSetting("ModuleChannel:UseMtls", "false");
        builder.UseSetting("ModuleChannel:TlsDataPath", tlsPath);
        builder.UseSetting("GuestJwt:SigningKey", Convert.ToBase64String(new byte[32]));

        builder.ConfigureTestServices(services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var descriptor = services[i];
                if (descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(InviteBootstrapHostedService))
                {
                    services.RemoveAt(i);
                }
            }

            services.PostConfigure<ModuleChannelOptions>(options =>
            {
                options.UseMtls = false;
                options.TlsDataPath = tlsPath;
                options.ServerDnsNames = ["kithara", "localhost"];
            });
        });
    }

    public async Task RegisterFakeAuthAsync(FakeAuthAdapterHost fake)
    {
        var catalog = Services.GetRequiredService<IAuthModuleCatalog>();
        var now = DateTimeOffset.UtcNow;
        catalog.Upsert(new AuthModuleRegistration
        {
            Slug = fake.Slug,
            GrpcAdvertiseAddress = fake.GrpcAddress,
            Capabilities = [WellKnownAuthCapabilities.UpdateBinding],
            JwksJson = fake.JwksJson,
            RegisteredAt = now,
            LastHeartbeatAt = now,
            ExpiresAt = now.AddHours(1),
        });

        var jwks = Services.GetRequiredService<AuthModuleJwksKeyProvider>();
        await jwks.RequireSigningKeysForModuleAsync(fake.Slug).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
