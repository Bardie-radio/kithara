using Bardie.Logos.Channel.Participant;
using Bardie.Logos.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bardie.Logos.Hosting.Tests;

public class BardieComposeParticipantEnvTests
{
    [Fact]
    public void Apply_maps_bardie_compose_aliases_onto_participant_options()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KITHARA_GRPC_ADDRESS"] = "kithara:5000",
                ["BARDIE_JOIN_SECRET"] = "join-secret",
                ["BARDIE_GRPC_ADVERTISE_ADDRESS"] = "https://bes:5001",
                ["BARDIE_GRPC_TLS_DATA_PATH"] = "/data/mtls",
                ["BARDIE_WORK_GRPC_PORT"] = "5101",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<ModuleParticipantOptions>();
        BardieComposeParticipantEnv.Apply(services, configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ModuleParticipantOptions>>().Value;

        Assert.Equal("https://kithara:5000", options.HostGrpcAddress);
        Assert.Equal("join-secret", options.JoinSecret);
        Assert.Equal("https://bes:5001", options.GrpcAdvertiseAddress);
        Assert.Equal("/data/mtls", options.TlsDataPath);
        Assert.Equal(5101, options.WorkGrpcPort);
    }

    [Fact]
    public void Apply_falls_back_to_generic_module_env_names()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MODULE_HOST_GRPC_ADDRESS"] = "https://host:5000",
                ["JOIN_SECRET"] = "generic-join",
                ["GRPC_ADVERTISE_ADDRESS"] = "dns:///module:5001",
                ["MODULE_TLS_DATA_PATH"] = "data/mtls",
                ["MODULE_WORK_GRPC_PORT"] = "5202",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<ModuleParticipantOptions>();
        BardieComposeParticipantEnv.Apply(services, configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ModuleParticipantOptions>>().Value;

        Assert.Equal("https://host:5000", options.HostGrpcAddress);
        Assert.Equal("generic-join", options.JoinSecret);
        Assert.Equal("dns:///module:5001", options.GrpcAdvertiseAddress);
        Assert.Equal("data/mtls", options.TlsDataPath);
        Assert.Equal(5202, options.WorkGrpcPort);
    }
}
