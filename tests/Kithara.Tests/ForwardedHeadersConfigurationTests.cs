using Kithara.Infrastructure.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kithara.Tests;

public sealed class ForwardedHeadersConfigurationTests
{
    [Fact]
    public void Unset_means_no_proxy()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.False(ForwardedHeadersConfiguration.IsEnabled(config));

        var options = new ForwardedHeadersOptions();
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);
        ForwardedHeadersConfiguration.Apply(options, config);

        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.Contains(System.Net.IPAddress.Loopback, options.KnownProxies);
    }

    [Fact]
    public void Apply_honors_operator_knobs_from_compose()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BARDIE_FORWARDED_HEADERS_FORWARD_LIMIT"] = "2",
                ["BARDIE_FORWARDED_HEADERS_CLEAR_KNOWN"] = "true",
                ["BARDIE_FORWARDED_HEADERS_KNOWN_PROXIES"] = "10.0.0.2",
                ["BARDIE_FORWARDED_HEADERS_KNOWN_NETWORKS"] = "10.0.0.0/8",
            })
            .Build();

        Assert.True(ForwardedHeadersConfiguration.IsEnabled(config));

        var options = new ForwardedHeadersOptions();
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);
        ForwardedHeadersConfiguration.Apply(options, config);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(2, options.ForwardLimit);
        Assert.DoesNotContain(System.Net.IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(options.KnownProxies, a => a.ToString() == "10.0.0.2");
        Assert.Single(options.KnownIPNetworks);
    }

    [Fact]
    public void Clear_known_false_keeps_loopback_trust()
    {
        var options = new ForwardedHeadersOptions();
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BARDIE_FORWARDED_HEADERS_FORWARD_LIMIT"] = "1",
                ["BARDIE_FORWARDED_HEADERS_CLEAR_KNOWN"] = "false",
            })
            .Build();

        ForwardedHeadersConfiguration.Apply(options, config);

        Assert.Contains(System.Net.IPAddress.Loopback, options.KnownProxies);
        Assert.Equal(1, options.ForwardLimit);
    }
}
