using Bardie.Module.Channel.Hosting;
using Kithara.Features.Library;
using Kithara.Features.Streams;
using Kithara.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kithara.Features.Modules;

public static class ModuleRegistryServiceCollectionExtensions
{
    public static IServiceCollection AddModuleRegistry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ModuleRegistryOptions>(options =>
        {
            configuration.GetSection(ModuleRegistryOptions.SectionName).Bind(options);
            var json = configuration["BARDIE_JOIN_SECRETS"];
            if (!string.IsNullOrWhiteSpace(json))
            {
                options.JoinSecrets = JoinSecretsConfiguration.Parse(json);
            }
        });

        services.AddSingleton<InMemoryModuleRegistry>();
        services.AddSingleton<ModuleRegistryOperations>();
        services.AddSingleton<ManagedPermissionGate>();
        services.AddHostedService<ModuleRegistryJanitor>();
        services.AddGrpc(options =>
        {
            options.Interceptors.Add<ModuleChannelBootstrapInterceptor>();
        });

        return services;
    }

    public static WebApplication MapModuleRegistry(this WebApplication app)
    {
        app.MapGrpcService<ModuleRegistryService>();
        app.MapGrpcService<BlobStorageService>();
        app.MapGrpcService<LibraryService>();
        return app;
    }
}

public static class HealthEndpointExtensions
{
    public static WebApplication MapKitharaHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var payload = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                    }),
                };
                await context.Response.WriteAsJsonAsync(payload).ConfigureAwait(false);
            },
        });

        return app;
    }
}

public sealed class ModuleTlsHealthCheck : IHealthCheck
{
    private readonly Bardie.Module.Channel.Certificates.IModuleCertificateStore _store;

    public ModuleTlsHealthCheck(Bardie.Module.Channel.Certificates.IModuleCertificateStore store)
    {
        _store = store;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_store.IsLoaded)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Module TLS material loaded."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("Module TLS material is not loaded."));
    }
}
