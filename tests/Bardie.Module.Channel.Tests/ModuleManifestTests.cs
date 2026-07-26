using Bardie.Logos.Channel.Manifest;
using Bardie.Logos.Channel.Participant;
using Bardie.Modules.V1;
using Xunit;

namespace Bardie.Logos.Channel.Tests;

public class ModuleManifestTests
{
    [Fact]
    public void Load_generic_manifest_and_build_core_register_request()
    {
        const string json = """
            {
              "slug": "widget",
              "kind": "custom",
              "displayName": "Widget",
              "otelServiceName": "example.widget",
              "capabilities": ["ping"]
            }
            """;

        var manifest = ModuleManifestLoader.LoadFromJson(json);
        Assert.Equal("widget", manifest.Slug);
        Assert.Equal("custom", manifest.Kind);
        Assert.Equal("example.widget", manifest.OtelServiceName);
        Assert.Contains("ping", manifest.Capabilities);

        var request = manifest.BuildRegisterRequest(
            joinSecret: "join-secret",
            advertiseAddress: "https://widget:5001");

        Assert.Equal("widget", request.Slug);
        Assert.Equal("custom", request.Kind);
        Assert.Equal("join-secret", request.JoinSecret);
        Assert.Equal("https://widget:5001", request.GrpcAdvertiseAddress);
        Assert.Contains("ping", request.Capabilities);
        Assert.Equal(RegisterRequest.DetailsOneofCase.None, request.DetailsCase);
    }

    [Fact]
    public void Extensions_preserve_unknown_json_without_interpreting_it()
    {
        var manifest = ModuleManifestLoader.LoadFromJson("""
            {
              "slug": "widget",
              "kind": "custom",
              "capabilities": [],
              "source": { "searchFields": [ { "name": "title", "required": true } ] }
            }
            """);

        Assert.NotNull(manifest.Extensions);
        Assert.True(manifest.Extensions.ContainsKey("source"));
    }

    [Fact]
    public void Customizer_applies_kind_specific_oneof()
    {
        var manifest = ModuleManifestLoader.LoadFromJson("""
            { "slug": "widget", "kind": "custom", "capabilities": [] }
            """);

        var request = manifest.BuildRegisterRequest(
            "secret",
            "https://widget:5001",
            [new StubCustomizer()]);

        Assert.Equal("""{"keys":[]}""", request.Auth.JwksJson);
    }

    [Fact]
    public void ApplyEnvironmentOverlays_honours_slug_override()
    {
        var manifest = ModuleManifestLoader.LoadFromJson("""
            { "slug": "widget", "kind": "custom", "capabilities": [] }
            """);

        Environment.SetEnvironmentVariable(ModuleManifestLoader.SlugOverrideEnvironmentVariable, "widget-alt");
        try
        {
            ModuleManifestLoader.ApplyEnvironmentOverlays(manifest);
            Assert.Equal("widget-alt", manifest.Slug);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModuleManifestLoader.SlugOverrideEnvironmentVariable, null);
        }
    }

    private sealed class StubCustomizer : IModuleRegisterRequestCustomizer
    {
        public void Customize(RegisterRequest request, ModuleManifest manifest) =>
            request.Auth = new AuthRegisterDetails { JwksJson = """{"keys":[]}""" };
    }
}
