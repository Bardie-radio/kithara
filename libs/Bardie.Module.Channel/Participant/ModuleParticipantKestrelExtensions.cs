using System.Security.Cryptography.X509Certificates;
using Bardie.Module.Channel.Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bardie.Module.Channel.Participant;

public static class ModuleParticipantKestrelExtensions
{
    /// <summary>
    /// Binds HTTPS HTTP/2 for module work RPCs. Presents the module work-port server cert and
    /// requires a client certificate chained to the mesh CA with the configured host identity
    /// (<see cref="ModuleParticipantOptions.ExpectedHostClientIdentity"/>) — MESH-CHN-001 inbound pin.
    /// </summary>
    public static ListenOptions UseBardieModuleWorkGrpc(this ListenOptions listenOptions)
    {
        var store = listenOptions.ApplicationServices.GetRequiredService<IModuleParticipantCertificateStore>();
        var participantOptions = listenOptions.ApplicationServices
            .GetRequiredService<IOptions<ModuleParticipantOptions>>()
            .Value;
        var expectedHost = participantOptions.ExpectedHostClientIdentity?.Trim() ?? string.Empty;

        if (!store.IsServerMaterialLoaded)
        {
            store.EnsureServerCertificateAsync().GetAwaiter().GetResult();
        }

        if (!store.IsClientMaterialLoaded)
        {
            // CA may already be on disk from a prior Register; load if present.
            store.EnsureLoadedAsync().GetAwaiter().GetResult();
        }

        listenOptions.Protocols = HttpProtocols.Http2;
        listenOptions.UseHttps(https =>
        {
            // Independent PEM load — never Export the store's live ServerCertificate.
            https.ServerCertificate = store.OpenListenerServerCertificate();
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) =>
            {
                if (!store.IsClientMaterialLoaded || certificate is null)
                {
                    // Before first Register, CA may be missing — reject work callers.
                    return false;
                }

                if (string.IsNullOrWhiteSpace(expectedHost))
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(store.CaCertificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (!chain.Build(certificate))
                {
                    return false;
                }

                // MESH-CHN-001: reject other mesh modules dialing this work-port (CA alone is not enough).
                return CertificateIdentity.IsHostClient(certificate, expectedHost);
            };
        });

        return listenOptions;
    }

    /// <summary>
    /// Configures Kestrel for a module process: optional plain HTTP health port + work gRPC TLS port.
    /// </summary>
    public static void ConfigureBardieModuleParticipantListeners(
        this KestrelServerOptions kestrel,
        int httpPort = 8080,
        int workGrpcPort = 5001)
    {
        kestrel.ListenAnyIP(httpPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
        });

        kestrel.ListenAnyIP(workGrpcPort, listen => listen.UseBardieModuleWorkGrpc());
    }
}
