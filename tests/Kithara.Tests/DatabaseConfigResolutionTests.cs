using Kithara.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kithara.Tests;

public sealed class DatabaseConfigResolutionTests
{
    [Fact]
    public void ResolveDatabase_uses_postgres_host_knobs_like_jellyfin()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["DbProvider"] = "sqlite",
                ["DbConnectionString"] = "Data Source=ignored.db",
                ["POSTGRES_HOST"] = "db",
                ["POSTGRES_USER"] = "kithara",
                ["POSTGRES_PASSWORD"] = "secret",
                ["POSTGRES_DB"] = "kithara",
                ["POSTGRES_PORT"] = "5432",
            })
            .Build();

        var (provider, cs) = PersistenceServiceCollectionExtensions.ResolveDatabase(config);

        Assert.Equal("postgres", provider);
        Assert.Equal(
            "Host=db;Port=5432;Database=kithara;Username=kithara;Password=secret",
            cs);
    }

    [Fact]
    public void ResolveDatabase_requires_password_when_host_set()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["POSTGRES_HOST"] = "db",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            PersistenceServiceCollectionExtensions.ResolveDatabase(config));
    }

    [Fact]
    public void ResolveDatabase_allows_sqlite_outside_production()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["DbProvider"] = "sqlite",
                ["DbConnectionString"] = "Data Source=kithara.db",
            })
            .Build();

        var (provider, cs) = PersistenceServiceCollectionExtensions.ResolveDatabase(config);

        Assert.Equal("sqlite", provider);
        Assert.Equal("Data Source=kithara.db", cs);
    }

    [Fact]
    public void ResolveDatabase_rejects_sqlite_in_production()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["DbProvider"] = "sqlite",
                ["DbConnectionString"] = "Data Source=/data/db/kithara.db",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PersistenceServiceCollectionExtensions.ResolveDatabase(config));
        Assert.Contains("Production requires Postgres", ex.Message, StringComparison.Ordinal);
    }
}
