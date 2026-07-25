using Bardie.Logos.Channel;
using Bardie.Harness.Source.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Bardie.Harness.Source;

public static class SourceHarnessServiceCollectionExtensions
{
    /// <summary>
    /// Registers source harness catalog, façade, and ModuleChannel with mTLS on by default.
    /// Host must register <see cref="Ports.IBlobStorage"/>.
    /// Does not double-register ModuleChannel when <see cref="Bardie.Harness.Auth.AuthHarnessServiceCollectionExtensions.AddAuthModuleHarness"/> already did.
    /// </summary>
    public static IServiceCollection AddSourceModuleHarness(
        this IServiceCollection services,
        Action<ModuleChannelOptions>? configureModuleChannel = null,
        bool registerModuleChannel = true)
    {
        if (registerModuleChannel)
        {
            services.AddModuleChannel(configure: options =>
            {
                options.UseMtls = true;
                configureModuleChannel?.Invoke(options);
            });
        }
        else if (configureModuleChannel is not null)
        {
            services.Configure(configureModuleChannel);
        }

        services.AddSingleton<ISourceModuleCatalog, SourceModuleCatalog>();
        services.AddSingleton<SourceModuleHarness>();
        return services;
    }
}
