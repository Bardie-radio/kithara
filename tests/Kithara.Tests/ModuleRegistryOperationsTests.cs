using Bardie.Harness.Auth;
using Bardie.Module.Channel;
using Bardie.Module.Channel.Certificates;
using Bardie.Modules.V1;
using Bardie.Harness.Source;
using Bardie.Harness.Source.Catalog;
using Grpc.Core;
using Kithara.Features.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kithara.Tests;

public class ModuleRegistryOperationsTests
{
    [Fact]
    public void Join_secret_rejects_unknown_slug_and_wrong_secret()
    {
        var secrets = JoinSecretsConfiguration.Parse("""{"magpie":"s3cret"}""");
        Assert.False(JoinSecretsConfiguration.Validate(secrets, "bes", "s3cret"));
        Assert.False(JoinSecretsConfiguration.Validate(secrets, "magpie", "wrong"));
        Assert.True(JoinSecretsConfiguration.Validate(secrets, "magpie", "s3cret"));
    }

    [Fact]
    public async Task Auto_register_returns_private_key_pem_and_projects_source_catalog()
    {
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Auto,
            """{"magpie":"s3cret"}""");

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var sourceCatalog = provider.GetRequiredService<ISourceModuleCatalog>();
        var authCatalog = provider.GetRequiredService<Bardie.Harness.Auth.Catalog.IAuthModuleCatalog>();
        var registry = provider.GetRequiredService<InMemoryModuleRegistry>();

        var response = ops.Register(new RegisterRequest
        {
            Slug = "magpie",
            JoinSecret = "s3cret",
            Kind = WellKnownModuleKinds.Source,
            GrpcAdvertiseAddress = "magpie:5001",
            Capabilities = { "search", "play", "pause" },
            Source = new SourceRegisterDetails
            {
                SearchFields =
                {
                    new Bardie.Modules.V1.SearchFieldDescriptor { Name = "title", Required = true },
                },
            },
        });

        Assert.False(string.IsNullOrWhiteSpace(response.ClientPrivateKeyPem));
        Assert.False(string.IsNullOrWhiteSpace(response.ClientCertificatePem));
        Assert.False(string.IsNullOrWhiteSpace(response.CaCertificatePem));
        Assert.True(registry.TryGet("magpie", out _));
        Assert.True(sourceCatalog.TryGet("magpie", out var source));
        Assert.Contains(source!.SearchFields, f => f.Name == "title" && f.Required);
        Assert.False(authCatalog.TryGet("magpie", out _));
    }

    [Fact]
    public async Task Preshared_register_omits_private_key_on_wire_and_projects_auth_catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "kithara-reg-" + Guid.NewGuid().ToString("N"));
        var tls = Path.Combine(root, "tls");
        var preshared = Path.Combine(root, "preshared");

        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Preshared,
            """{"bes":"auth-secret"}""",
            tls,
            preshared);

        var store = provider.GetRequiredService<IModuleCertificateStore>();
        var issuer = provider.GetRequiredService<IModuleCertificateIssuer>();
        issuer.ProvisionPresharedClientCertificate("bes");
        Assert.True(store.TryGetPresharedClientExpiry("bes", out _));

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var authCatalog = provider.GetRequiredService<Bardie.Harness.Auth.Catalog.IAuthModuleCatalog>();
        var jwksJson = CreateTestJwksJson();

        var response = ops.Register(new RegisterRequest
        {
            Slug = "bes",
            JoinSecret = "auth-secret",
            Kind = WellKnownModuleKinds.Auth,
            GrpcAdvertiseAddress = "bes:5001",
            Capabilities = { "seedAdmin", "updateBinding" },
            Auth = new AuthRegisterDetails { JwksJson = jwksJson },
        });

        Assert.True(string.IsNullOrEmpty(response.ClientPrivateKeyPem));
        Assert.True(string.IsNullOrEmpty(response.ClientCertificatePem));
        Assert.False(string.IsNullOrWhiteSpace(response.CaCertificatePem));
        Assert.True(authCatalog.TryGet("bes", out var auth));
        Assert.Equal(jwksJson, auth!.JwksJson);
    }

    [Fact]
    public async Task Auth_register_fails_closed_when_jwks_unavailable()
    {
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Auto,
            """{"bes":"auth-secret"}""");

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var authCatalog = provider.GetRequiredService<Bardie.Harness.Auth.Catalog.IAuthModuleCatalog>();
        var registry = provider.GetRequiredService<InMemoryModuleRegistry>();

        var ex = Assert.Throws<RpcException>(() => ops.Register(new RegisterRequest
        {
            Slug = "bes",
            JoinSecret = "auth-secret",
            Kind = WellKnownModuleKinds.Auth,
            GrpcAdvertiseAddress = "bes:5001",
            Capabilities = { "seedAdmin", "updateBinding" },
            Auth = new AuthRegisterDetails { JwksUri = "https://bes.invalid/.well-known/jwks.json" },
        }));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.False(authCatalog.TryGet("bes", out _));
        Assert.False(registry.TryGet("bes", out _));
    }

    [Fact]
    public async Task Preshared_register_fails_closed_when_client_material_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "kithara-reg-miss-" + Guid.NewGuid().ToString("N"));
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Preshared,
            """{"ghost":"secret"}""",
            Path.Combine(root, "tls"),
            Path.Combine(root, "preshared"));

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var ex = Assert.Throws<RpcException>(() => ops.Register(new RegisterRequest
        {
            Slug = "ghost",
            JoinSecret = "secret",
            Kind = WellKnownModuleKinds.Auth,
            GrpcAdvertiseAddress = "ghost:1",
        }));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task Wrong_join_secret_is_permission_denied()
    {
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Auto,
            """{"magpie":"s3cret"}""");

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var ex = Assert.Throws<RpcException>(() => ops.Register(new RegisterRequest
        {
            Slug = "magpie",
            JoinSecret = "nope",
            Kind = WellKnownModuleKinds.Source,
            GrpcAdvertiseAddress = "magpie:5001",
        }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_requires_matching_mtls_slug()
    {
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Auto,
            """{"magpie":"s3cret"}""");

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        ops.Register(new RegisterRequest
        {
            Slug = "magpie",
            JoinSecret = "s3cret",
            Kind = WellKnownModuleKinds.Source,
            GrpcAdvertiseAddress = "magpie:5001",
        });

        var bare = Assert.Throws<RpcException>(() =>
            ops.Heartbeat(new HeartbeatRequest { Slug = "magpie" }, presentedCertSlug: null));
        Assert.Equal(StatusCode.Unauthenticated, bare.StatusCode);

        var ok = ops.Heartbeat(new HeartbeatRequest { Slug = "magpie" }, presentedCertSlug: "magpie");
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Client_kind_stays_registry_only()
    {
        await using var provider = await BuildProviderAsync(
            ModuleChannelBootstrapMode.Auto,
            """{"plume":"ui-secret"}""");

        var ops = provider.GetRequiredService<ModuleRegistryOperations>();
        var sourceCatalog = provider.GetRequiredService<ISourceModuleCatalog>();
        var authCatalog = provider.GetRequiredService<Bardie.Harness.Auth.Catalog.IAuthModuleCatalog>();
        var registry = provider.GetRequiredService<InMemoryModuleRegistry>();

        ops.Register(new RegisterRequest
        {
            Slug = "plume",
            JoinSecret = "ui-secret",
            Kind = WellKnownModuleKinds.Client,
            GrpcAdvertiseAddress = "plume:5001",
            Client = new ClientRegisterDetails { AuthMode = "user-aware" },
        });

        Assert.True(registry.TryGet("plume", out _));
        Assert.False(sourceCatalog.TryGet("plume", out _));
        Assert.False(authCatalog.TryGet("plume", out _));
    }

    private static async Task<ServiceProvider> BuildProviderAsync(
        ModuleChannelBootstrapMode mode,
        string joinSecretsJson,
        string? tlsPath = null,
        string? presharedPath = null)
    {
        tlsPath ??= Path.Combine(Path.GetTempPath(), "kithara-tls-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddModuleChannel(configure: options =>
        {
            options.TlsDataPath = tlsPath;
            options.BootstrapMode = mode;
            options.UseMtls = true;
            if (!string.IsNullOrEmpty(presharedPath))
            {
                options.PresharedDir = presharedPath;
            }
        });
        services.AddAuthModuleHarness();
        services.AddSourceModuleHarness();
        services.AddMemoryCache();
        services.AddHttpClient(nameof(Kithara.Features.Auth.AuthModuleJwksKeyProvider));
        services.AddSingleton<Kithara.Features.Auth.AuthModuleJwksKeyProvider>();
        services.AddSingleton<InMemoryModuleRegistry>();
        services.Configure<ModuleRegistryOptions>(o =>
        {
            o.JoinSecrets = JoinSecretsConfiguration.Parse(joinSecretsJson);
        });
        services.AddSingleton<ModuleRegistryOperations>();

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IModuleCertificateStore>();
        await store.EnsureLoadedAsync();
        return provider;
    }

    private static string CreateTestJwksJson()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var jwk = new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = "test",
            ["n"] = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(parameters.Modulus!),
            ["e"] = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(parameters.Exponent!),
        };
        return System.Text.Json.JsonSerializer.Serialize(new { keys = new[] { jwk } });
    }
}
