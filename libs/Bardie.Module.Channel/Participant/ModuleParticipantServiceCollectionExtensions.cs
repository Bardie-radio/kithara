using Bardie.Module.Channel.Manifest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bardie.Module.Channel.Participant;

public static class ModuleParticipantServiceCollectionExtensions
{
    /// <summary>
    /// Registers module-side ModuleChannel helpers: manifest, TLS store, dial factory, Register/Heartbeat hosted service.
    /// Does not register host-side CA issuance (<see cref="AddModuleChannel"/>).
    /// </summary>
    public static IServiceCollection AddModuleParticipant(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ModuleParticipantOptions>? configure = null,
        string? contentRoot = null)
    {
        if (configuration is not null)
        {
            services.Configure<ModuleParticipantOptions>(configuration.GetSection(ModuleParticipantOptions.SectionName));
            ApplyEnvironmentOverrides(services, configuration);
        }
        else
        {
            services.AddOptions<ModuleParticipantOptions>();
        }

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModuleParticipantOptions>>().Value;
            var path = ModuleManifestLoader.ResolvePath(options.ManifestPath, contentRoot);
            var manifest = ModuleManifestLoader.LoadFromFile(path);
            return ModuleManifestLoader.ApplyEnvironmentOverlays(manifest, configuration);
        });

        services.TryAddSingleton<FileModuleParticipantCertificateStore>();
        services.TryAddSingleton<IModuleParticipantCertificateStore>(sp =>
            sp.GetRequiredService<FileModuleParticipantCertificateStore>());
        services.TryAddSingleton<IModuleParticipantChannelFactory, ModuleParticipantChannelFactory>();
        services.AddHostedService<ModuleRegistrationHostedService>();

        return services;
    }

    private static void ApplyEnvironmentOverrides(IServiceCollection services, IConfiguration configuration)
    {
        services.PostConfigure<ModuleParticipantOptions>(options =>
        {
            var address = configuration["MODULE_HOST_GRPC_ADDRESS"]
                ?? configuration["ModuleParticipant:HostGrpcAddress"];
            if (!string.IsNullOrWhiteSpace(address))
            {
                options.HostGrpcAddress = NormalizeGrpcAddress(address);
            }

            var join = configuration["JOIN_SECRET"]
                ?? configuration["ModuleParticipant:JoinSecret"];
            if (!string.IsNullOrWhiteSpace(join))
            {
                options.JoinSecret = join;
            }

            var advertise = configuration["GRPC_ADVERTISE_ADDRESS"]
                ?? configuration["ModuleParticipant:GrpcAdvertiseAddress"];
            if (!string.IsNullOrWhiteSpace(advertise))
            {
                options.GrpcAdvertiseAddress = advertise;
            }

            var manifestPath = configuration["MODULE_MANIFEST_PATH"]
                ?? configuration["ModuleParticipant:ManifestPath"];
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                options.ManifestPath = manifestPath;
            }

            var tlsPath = configuration["MODULE_TLS_DATA_PATH"]
                ?? configuration["ModuleParticipant:TlsDataPath"];
            if (!string.IsNullOrWhiteSpace(tlsPath))
            {
                options.TlsDataPath = tlsPath;
            }

            var workPort = configuration["MODULE_WORK_GRPC_PORT"]
                ?? configuration["ModuleParticipant:WorkGrpcPort"];
            if (int.TryParse(workPort, out var port) && port > 0)
            {
                options.WorkGrpcPort = port;
            }

            var enable = configuration["ModuleParticipant:EnableRegistration"];
            if (bool.TryParse(enable, out var parsed))
            {
                options.EnableRegistration = parsed;
            }

            var expectedHost = configuration["MODULE_EXPECTED_HOST_IDENTITY"]
                ?? configuration["ModuleParticipant:ExpectedHostClientIdentity"];
            if (!string.IsNullOrWhiteSpace(expectedHost))
            {
                options.ExpectedHostClientIdentity = expectedHost.Trim();
            }
        });
    }

    /// <summary>
    /// Ensures a scheme for gRPC dial addresses. Compose often sets <c>host:5000</c> without <c>https://</c>.
    /// </summary>
    public static string NormalizeGrpcAddress(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("dns:///", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return "https://" + trimmed;
    }
}
