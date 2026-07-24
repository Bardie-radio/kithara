using System.Security.Cryptography.X509Certificates;

namespace Bardie.Module.Channel.Certificates;

/// <summary>
/// Peer identity helpers for MESH-CHN-001 bilateral mTLS pinning.
/// Host / module names are caller-supplied — this library does not hardcode a product host slug.
/// </summary>
public static class CertificateIdentity
{
    /// <summary>
    /// True when <paramref name="certificate"/> presents <paramref name="expectedHostIdentity"/>
    /// (CN or DNS SAN). Used by module work-ports to accept only the paired host.
    /// </summary>
    public static bool IsHostClient(X509Certificate2? certificate, string expectedHostIdentity)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(expectedHostIdentity))
        {
            return false;
        }

        return Matches(certificate, expectedHostIdentity);
    }

    /// <summary>
    /// True when CN or any DNS SAN equals <paramref name="expectedIdentity"/> (ordinal ignore-case).
    /// Used to pin host→module dials to the registered module slug.
    /// </summary>
    public static bool Matches(X509Certificate2 certificate, string expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);

        var expected = expectedIdentity.Trim();
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.IsNullOrWhiteSpace(cn)
            && string.Equals(cn.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var ext in certificate.Extensions)
        {
            if (ext is not X509SubjectAlternativeNameExtension san)
            {
                continue;
            }

            foreach (var dns in san.EnumerateDnsNames())
            {
                if (string.Equals(dns, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
