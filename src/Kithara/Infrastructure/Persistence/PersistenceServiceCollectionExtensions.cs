using Bardie.Harness.Auth.Ports;
using Bardie.Harness.Source.Ports;
using Kithara.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers EF <see cref="KitharaDbContext"/> and auth persistence ports.</summary>
    public static IServiceCollection AddKitharaDb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = (configuration["DbProvider"] ?? "sqlite").Trim().ToLowerInvariant();
        var connectionString = configuration["DbConnectionString"]
            ?? configuration.GetConnectionString("Kithara")
            ?? "Data Source=kithara.db";

        services.AddDbContextFactory<KitharaDbContext>(options =>
        {
            switch (provider)
            {
                case "postgres":
                case "postgresql":
                    options.UseNpgsql(connectionString);
                    break;
                default:
                    options.UseSqlite(connectionString);
                    break;
            }
        });

        services.AddSingleton<IAuthPersistence, EfAuthPersistence>();
        return services;
    }

    /// <summary>
    /// Binds options, selects the blob driver from config, and registers the BlobStorage gRPC façade.
    /// </summary>
    public static IServiceCollection AddKitharaBlobStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BlobStorageOptions>(options =>
        {
            configuration.GetSection(BlobStorageOptions.SectionName).Bind(options);
            var path = configuration["BARDIE_STORAGE_PATH"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                options.Path = path.Trim();
            }

            var driverOverride = configuration["BARDIE_STORAGE_DRIVER"];
            if (!string.IsNullOrWhiteSpace(driverOverride))
            {
                options.Driver = driverOverride.Trim();
            }
        });

        var driver = (configuration["BARDIE_STORAGE_DRIVER"]
                ?? configuration[$"{BlobStorageOptions.SectionName}:Driver"]
                ?? "local")
            .Trim()
            .ToLowerInvariant();

        switch (driver)
        {
            case "local":
                services.AddSingleton<IBlobStorage, LocalBlobStorage>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported BlobStorage:Driver '{driver}'. Phase 3 supports 'local' only.");
        }

        services.AddSingleton<BlobStorageService>();
        return services;
    }

    public static async Task MigrateKitharaDatabaseAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KitharaDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
