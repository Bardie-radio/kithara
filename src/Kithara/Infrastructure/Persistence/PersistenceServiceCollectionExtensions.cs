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
        var (provider, connectionString) = ResolveDatabase(configuration);

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
    /// Resolves EF provider + connection string.
    /// Prefer Jellyfin-style <c>POSTGRES_HOST</c> / <c>POSTGRES_USER</c> / … when set (Compose).
    /// SQLite is allowed only outside Production (Development / tests).
    /// Production requires Postgres via <c>POSTGRES_HOST</c> or <c>DbProvider=postgres</c> + connection string.
    /// </summary>
    public static (string Provider, string ConnectionString) ResolveDatabase(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var postgresHost = FirstNonEmpty(
            configuration["POSTGRES_HOST"],
            configuration["BARDIE_POSTGRES_HOST"]);

        if (!string.IsNullOrWhiteSpace(postgresHost))
        {
            var user = FirstNonEmpty(
                configuration["POSTGRES_USER"],
                configuration["BARDIE_POSTGRES_USER"]) ?? "kithara";
            var password = FirstNonEmpty(
                configuration["POSTGRES_PASSWORD"],
                configuration["BARDIE_POSTGRES_PASSWORD"]);
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "POSTGRES_PASSWORD is required when POSTGRES_HOST is set.");
            }

            var database = FirstNonEmpty(
                configuration["POSTGRES_DB"],
                configuration["BARDIE_POSTGRES_DB"]) ?? "kithara";
            var port = FirstNonEmpty(
                configuration["POSTGRES_PORT"],
                configuration["BARDIE_POSTGRES_PORT"]) ?? "5432";

            var connectionString =
                $"Host={postgresHost.Trim()};Port={port.Trim()};Database={database.Trim()};Username={user.Trim()};Password={password}";
            return ("postgres", connectionString);
        }

        var provider = (configuration["DbProvider"] ?? "sqlite").Trim().ToLowerInvariant();
        var connectionStringFallback = configuration["DbConnectionString"]
            ?? configuration.GetConnectionString("Kithara");

        if (IsProduction(configuration))
        {
            if (provider is "postgres" or "postgresql")
            {
                if (string.IsNullOrWhiteSpace(connectionStringFallback))
                {
                    throw new InvalidOperationException(
                        "Production postgres requires POSTGRES_HOST (+ PASSWORD) or DbConnectionString.");
                }

                return (provider, connectionStringFallback);
            }

            throw new InvalidOperationException(
                "Production requires Postgres (POSTGRES_HOST + POSTGRES_PASSWORD). SQLite is Development-only.");
        }

        return (provider, connectionStringFallback ?? "Data Source=kithara.db");
    }

    private static bool IsProduction(IConfiguration configuration)
    {
        var env = FirstNonEmpty(
            configuration["ASPNETCORE_ENVIRONMENT"],
            configuration["DOTNET_ENVIRONMENT"]);
        return string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
