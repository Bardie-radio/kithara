using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Bardie.Module.Channel.Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace Bardie.Module.Channel.Channel;

/// <summary>Creates outbound gRPC channels that trust the module CA (and optionally present a client cert).</summary>
public interface IModuleGrpcChannelFactory
{
    /// <summary>
    /// Dial a peer.
    /// When <paramref name="expectedServerIdentity"/> is set (host→module work dials), the remote
    /// server cert CN/SAN must match that identity (registered module slug) — MESH-CHN-001 slug pin.
    /// When unset and <paramref name="trustRemoteServerCertificate"/> is false (default), the remote
    /// server cert must chain to the mesh CA. When <paramref name="trustRemoteServerCertificate"/> is
    /// true and no expected identity is set, any present server cert is accepted (legacy / tests).
    /// </summary>
    /// <param name="ownsClientCertificate">
    /// When true, the channel disposes <paramref name="clientCertificate"/> when the channel is disposed
    /// (use with short-lived <see cref="IModuleCertificateStore.OpenOutboundClientIdentity"/> copies).
    /// </param>
    GrpcChannel CreateChannel(
        string address,
        X509Certificate2? clientCertificate = null,
        bool trustRemoteServerCertificate = false,
        bool ownsClientCertificate = false,
        string? expectedServerIdentity = null);
}

public sealed class ModuleGrpcChannelFactory : IModuleGrpcChannelFactory
{
    private readonly IModuleCertificateStore _store;
    private readonly ModuleChannelOptions _options;

    public ModuleGrpcChannelFactory(
        IModuleCertificateStore store,
        IOptions<ModuleChannelOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public GrpcChannel CreateChannel(
        string address,
        X509Certificate2? clientCertificate = null,
        bool trustRemoteServerCertificate = false,
        bool ownsClientCertificate = false,
        string? expectedServerIdentity = null)
    {
        if (!_options.UseMtls)
        {
            return GrpcChannel.ForAddress(address);
        }

        if (!_store.IsLoaded)
        {
            throw new InvalidOperationException("TLS material is not loaded. Call EnsureLoadedAsync first.");
        }

        var expected = string.IsNullOrWhiteSpace(expectedServerIdentity)
            ? null
            : expectedServerIdentity.Trim();

        var sockets = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null)
                    {
                        return false;
                    }

                    using var presented = new X509Certificate2(cert);

                    // MESH-CHN-001: pin work-port identity to registered module slug (self-signed OK).
                    if (expected is not null)
                    {
                        return CertificateIdentity.Matches(presented, expected);
                    }

                    if (trustRemoteServerCertificate)
                    {
                        return true;
                    }

                    return ValidateAgainstCa(presented);
                },
            },
        };

        if (clientCertificate is not null)
        {
            sockets.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
            sockets.SslOptions.LocalCertificateSelectionCallback =
                (_, _, localCertificates, _, _) =>
                    localCertificates.Count > 0 ? localCertificates[0]! : clientCertificate;
        }

        HttpMessageHandler handler = sockets;
        if (ownsClientCertificate && clientCertificate is not null)
        {
            handler = new OwnedClientCertificateHandler(sockets, clientCertificate);
        }

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    private bool ValidateAgainstCa(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_store.CaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    /// <summary>Disposes a short-lived client cert when the gRPC channel (and handler) is disposed.</summary>
    private sealed class OwnedClientCertificateHandler : DelegatingHandler
    {
        private readonly X509Certificate2 _clientCertificate;

        public OwnedClientCertificateHandler(HttpMessageHandler inner, X509Certificate2 clientCertificate)
            : base(inner)
        {
            _clientCertificate = clientCertificate;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clientCertificate.Dispose();
                InnerHandler?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
