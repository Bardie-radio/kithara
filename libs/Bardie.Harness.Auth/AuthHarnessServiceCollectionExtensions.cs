using Bardie.Harness.Auth.Catalog;
using Bardie.Logos.Channel;
using Microsoft.Extensions.DependencyInjection;

namespace Bardie.Harness.Auth;

public static class AuthHarnessServiceCollectionExtensions
{
    /// <summary>
    /// Registers auth harness catalog, façade, and ModuleChannel with mTLS on by default.
    /// Host must register <see cref="Ports.IAuthPersistence"/>.
    /// </summary>
    public static IServiceCollection AddAuthModuleHarness(
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

        services.AddSingleton<IAuthModuleCatalog, AuthModuleCatalog>();
        services.AddSingleton<AuthModuleHarness>();
        return services;
    }
}
