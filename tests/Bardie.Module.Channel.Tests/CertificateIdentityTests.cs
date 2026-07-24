using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bardie.Module.Channel.Certificates;
using Xunit;

namespace Bardie.Module.Channel.Tests;

public class CertificateIdentityTests
{
    [Fact]
    public void Matches_cn()
    {
        using var cert = CreateSelfSigned("magpie", dnsNames: ["magpie", "localhost"]);
        Assert.True(CertificateIdentity.Matches(cert, "magpie"));
        Assert.True(CertificateIdentity.Matches(cert, "MAGPIE"));
        Assert.False(CertificateIdentity.Matches(cert, "bes"));
    }

    [Fact]
    public void Matches_san_when_cn_differs()
    {
        using var cert = CreateSelfSigned("localhost", dnsNames: ["localhost", "magpie"]);
        Assert.True(CertificateIdentity.Matches(cert, "magpie"));
    }

    [Fact]
    public void IsHostClient_requires_configured_identity()
    {
        using var host = CreateSelfSigned("my-host", dnsNames: ["my-host", "localhost"]);
        using var module = CreateSelfSigned("magpie", dnsNames: ["magpie"]);
        Assert.True(CertificateIdentity.IsHostClient(host, "my-host"));
        Assert.False(CertificateIdentity.IsHostClient(module, "my-host"));
        Assert.False(CertificateIdentity.IsHostClient(host, ""));
        Assert.False(CertificateIdentity.IsHostClient(null, "my-host"));
    }

    private static X509Certificate2 CreateSelfSigned(string cn, string[] dnsNames)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={cn}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        request.CertificateExtensions.Add(san.Build());
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
