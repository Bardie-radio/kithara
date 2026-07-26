using Bardie.Logos.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Bardie.Logos.Hosting.Tests;

public class ModuleHostingPortsTests
{
    [Fact]
    public void ResolveWorkPort_prefers_bardie_env_over_module_and_config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BARDIE_WORK_GRPC_PORT"] = "6001",
                ["MODULE_WORK_GRPC_PORT"] = "6002",
                ["ModuleParticipant:WorkGrpcPort"] = "6003",
            })
            .Build();

        Assert.Equal(6001, ModuleHostingPorts.ResolveWorkPort(configuration));
    }

    [Fact]
    public void ResolveWorkPort_falls_back_through_aliases_then_default()
    {
        var moduleOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MODULE_WORK_GRPC_PORT"] = "6002",
                ["ModuleParticipant:WorkGrpcPort"] = "6003",
            })
            .Build();
        Assert.Equal(6002, ModuleHostingPorts.ResolveWorkPort(moduleOnly));

        var configOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleParticipant:WorkGrpcPort"] = "6003",
            })
            .Build();
        Assert.Equal(6003, ModuleHostingPorts.ResolveWorkPort(configOnly));

        var empty = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Equal(ModuleHostingPorts.DefaultWorkGrpcPort, ModuleHostingPorts.ResolveWorkPort(empty));
    }

    [Fact]
    public void ResolveHttpPort_prefers_bardie_env_over_module_and_config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BARDIE_HTTP_PORT"] = "9080",
                ["MODULE_HTTP_PORT"] = "9081",
                ["ModuleParticipant:HttpPort"] = "9082",
            })
            .Build();

        Assert.Equal(9080, ModuleHostingPorts.ResolveHttpPort(configuration));
    }

    [Fact]
    public void ResolveHttpPort_falls_back_through_aliases_then_default()
    {
        var moduleOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MODULE_HTTP_PORT"] = "9081",
            })
            .Build();
        Assert.Equal(9081, ModuleHostingPorts.ResolveHttpPort(moduleOnly));

        var empty = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Equal(ModuleHostingPorts.DefaultHttpPort, ModuleHostingPorts.ResolveHttpPort(empty));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nope")]
    public void ResolvePorts_ignore_invalid_values(string invalid)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BARDIE_WORK_GRPC_PORT"] = invalid,
                ["BARDIE_HTTP_PORT"] = invalid,
            })
            .Build();

        Assert.Equal(ModuleHostingPorts.DefaultWorkGrpcPort, ModuleHostingPorts.ResolveWorkPort(configuration));
        Assert.Equal(ModuleHostingPorts.DefaultHttpPort, ModuleHostingPorts.ResolveHttpPort(configuration));
    }
}
