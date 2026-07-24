using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bardie.Module.Channel.Certificates;

public sealed class FileModuleCertificateStore : IModuleCertificateStore, IDisposable
{
    private readonly ModuleChannelOptions _options;
    private readonly ILogger<FileModuleCertificateStore> _logger;
    private readonly object _gate = new();
    private X509Certificate2? _ca;
    private X509Certificate2? _server;
    private string? _caPem;
    private string? _serverCertPath;
    private string? _serverKeyPath;
    private bool _disposed;

    public FileModuleCertificateStore(
        IOptions<ModuleChannelOptions> options,
        ILogger<FileModuleCertificateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsLoaded => _ca is not null && _server is not null;

    public X509Certificate2 ServerCertificate =>
        _server ?? throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");

    public X509Certificate2 CaCertificate =>
        _ca ?? throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");

    public string CaCertificatePem =>
        _caPem ?? throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");

    public string CaThumbprint => CaCertificate.Thumbprint;

    public X509Certificate2 OpenOutboundClientIdentity()
    {
        if (_serverCertPath is null || _serverKeyPath is null || !IsLoaded)
        {
            throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");
        }

        // Fresh PEM load — never Export the Kestrel-bound ServerCertificate; dispose after the dial.
        return CertificateMaterial.LoadPemPair(_serverCertPath, _serverKeyPath);
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded)
        {
            return;
        }

        await Task.Run(() =>
        {
            lock (_gate)
            {
                if (IsLoaded)
                {
                    return;
                }

                Directory.CreateDirectory(_options.TlsDataPath);
                var caCertPath = Path.Combine(_options.TlsDataPath, "ca.crt.pem");
                var caKeyPath = Path.Combine(_options.TlsDataPath, "ca.key.pem");
                _serverCertPath = Path.Combine(_options.TlsDataPath, "server.crt.pem");
                _serverKeyPath = Path.Combine(_options.TlsDataPath, "server.key.pem");

                if (File.Exists(caCertPath) && File.Exists(caKeyPath))
                {
                    _ca = CertificateMaterial.LoadPemPair(caCertPath, caKeyPath);
                    CertificateMaterial.RestrictKeyFilePermissions(caKeyPath);
                    _logger.LogInformation("Loaded module CA from {Path}", caCertPath);
                }
                else
                {
                    _ca = CreateSelfSignedCa();
                    CertificateMaterial.WritePemPair(caCertPath, caKeyPath, _ca);
                    _logger.LogInformation("Generated module CA at {Path}", caCertPath);
                }

                if (File.Exists(_serverCertPath) && File.Exists(_serverKeyPath))
                {
                    _server = CertificateMaterial.LoadPemPair(_serverCertPath, _serverKeyPath);
                    CertificateMaterial.RestrictKeyFilePermissions(_serverKeyPath);
                    _logger.LogInformation("Loaded module gRPC server cert from {Path}", _serverCertPath);
                }
                else
                {
                    _server = CreateServerCertificate(_ca);
                    CertificateMaterial.WritePemPair(_serverCertPath, _serverKeyPath, _server);
                    // Reload so in-memory handle matches on-disk PEM (outbound dials load from the same files).
                    _server.Dispose();
                    _server = CertificateMaterial.LoadPemPair(_serverCertPath, _serverKeyPath);
                    _logger.LogInformation("Generated module gRPC server cert at {Path}", _serverCertPath);
                }

                _caPem = CertificateMaterial.ExportCertificatePem(_ca);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetPresharedClientMaterial(string slug, out X509Certificate2? certificate)
    {
        certificate = null;
        var dir = ResolvePresharedSlugDir(slug);
        if (dir is null)
        {
            return false;
        }

        var certPath = Path.Combine(dir, "client.crt.pem");
        var keyPath = Path.Combine(dir, "client.key.pem");
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return false;
        }

        certificate = CertificateMaterial.LoadPemPair(certPath, keyPath);
        return true;
    }

    public bool TryGetPresharedClientExpiry(string slug, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (!TryGetPresharedClientMaterial(slug, out var cert) || cert is null)
        {
            return false;
        }

        using (cert)
        {
            expiresAt = cert.NotAfter.ToUniversalTime();
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server?.Dispose();
        _ca?.Dispose();
    }

    private string? ResolvePresharedSlugDir(string slug)
    {
        if (string.IsNullOrWhiteSpace(_options.PresharedDir))
        {
            return null;
        }

        var safe = SanitizeSlug(slug);
        return Path.Combine(_options.PresharedDir, safe);
    }

    internal X509Certificate2 CreateClientCertificate(string slug, TimeSpan lifetime, out RSA privateKey)
    {
        if (_ca is null)
        {
            throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");
        }

        privateKey = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={SanitizeSlug(slug)}");
        var request = new CertificateRequest(subject, privateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")],
                true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.Add(lifetime);
        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(_ca, notBefore, notAfter, serial);
    }

    private X509Certificate2 CreateSelfSignedCa()
    {
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            "CN=Bardie Module CA",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(10);
        var cert = request.CreateSelfSigned(notBefore, notAfter);
        return CertificateMaterial.PersistWithPrivateKey(cert, rsa);
    }

    private X509Certificate2 CreateServerCertificate(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var cn = _options.ServerDnsNames
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Select(static n => n.Trim())
            .FirstOrDefault() ?? "localhost";
        var request = new CertificateRequest(
            $"CN={cn}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [
                    new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth — Kestrel module gRPC
                    new Oid("1.3.6.1.5.5.7.3.2"), // clientAuth — host→module dials
                ],
                true));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in _options.ServerDnsNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(dns))
            {
                san.AddDnsName(dns.Trim());
            }
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(5);
        var serial = RandomNumberGenerator.GetBytes(16);
        var issued = request.Create(ca, notBefore, notAfter, serial);
        return CertificateMaterial.PersistWithPrivateKey(issued, rsa);
    }

    internal static string ExportCertificatePem(X509Certificate2 cert) =>
        CertificateMaterial.ExportCertificatePem(cert);

    internal static string ExportPrivateKeyPem(RSA key) => key.ExportPkcs8PrivateKeyPem();

    internal static string SanitizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("Module slug is required.", nameof(slug));
        }

        var trimmed = slug.Trim().ToLowerInvariant();
        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                throw new ArgumentException($"Invalid module slug '{slug}'.", nameof(slug));
            }
        }

        return trimmed;
    }
}
