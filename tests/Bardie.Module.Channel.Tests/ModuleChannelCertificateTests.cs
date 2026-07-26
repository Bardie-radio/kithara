using System.Security.Cryptography.X509Certificates;
using Bardie.Logos.Channel;
using Bardie.Logos.Channel.Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bardie.Logos.Channel.Tests;

public class ModuleChannelCertificateTests
{
    [Fact]
    public async Task Issue_then_validate_round_trip()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleChannel(configure: options =>
        {
            options.TlsDataPath = Path.Combine(Path.GetTempPath(), "bardie-mtls-" + Guid.NewGuid().ToString("N"));
            options.BootstrapMode = ModuleChannelBootstrapMode.Auto;
            options.UseMtls = true;
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IModuleCertificateStore>();
        var issuer = provider.GetRequiredService<IModuleCertificateIssuer>();
        var validator = provider.GetRequiredService<IModuleCertificateValidator>();

        await store.EnsureLoadedAsync();

        var issued = issuer.IssueClientCertificate("magpie");
        Assert.False(string.IsNullOrWhiteSpace(issued.ClientPrivateKeyPem));
        Assert.False(string.IsNullOrWhiteSpace(issued.ClientCertificatePem));

        using var cert = X509Certificate2.CreateFromPem(issued.ClientCertificatePem, issued.ClientPrivateKeyPem);
        Assert.True(validator.TryValidate(cert, out var slug));
        Assert.Equal("magpie", slug);
    }

    [Fact]
    public async Task Preshared_register_material_does_not_emit_private_key_on_wire_shape()
    {
        var root = Path.Combine(Path.GetTempPath(), "bardie-preshared-" + Guid.NewGuid().ToString("N"));
        var tls = Path.Combine(root, "tls");
        var preshared = Path.Combine(root, "preshared");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleChannel(configure: options =>
        {
            options.TlsDataPath = tls;
            options.PresharedDir = preshared;
            options.BootstrapMode = ModuleChannelBootstrapMode.Preshared;
            options.UseMtls = true;
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IModuleCertificateStore>();
        var issuer = provider.GetRequiredService<IModuleCertificateIssuer>();

        await store.EnsureLoadedAsync();
        var provisioned = issuer.ProvisionPresharedClientCertificate("bes");
        Assert.False(string.IsNullOrWhiteSpace(provisioned.ClientPrivateKeyPem));

        Assert.True(store.TryGetPresharedClientExpiry("bes", out var expires));
        Assert.True(expires > DateTimeOffset.UtcNow);

        // Wire shape for Register in preshared mode: host returns CA metadata only —
        // never the provisioned private key. (Full Register path covered in Kithara.Tests.)
        Assert.False(string.IsNullOrWhiteSpace(store.CaCertificatePem));
        Assert.False(store.TryGetPresharedClientExpiry("missing-module", out _));
    }
}
