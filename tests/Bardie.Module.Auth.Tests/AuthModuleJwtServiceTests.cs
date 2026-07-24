using System.Text.Json;
using Bardie.Module.Channel.Manifest;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bardie.Module.Auth.Tests;

public class AuthModuleJwtServiceTests
{
    [Fact]
    public void ExportJwksJson_round_trips_rsa_public_parameters()
    {
        using var service = CreateService(out var keyDir);
        try
        {
            using var doc = JsonDocument.Parse(service.ExportJwksJson());
            var key = doc.RootElement.GetProperty("keys")[0];
            Assert.Equal("RSA", key.GetProperty("kty").GetString());
            Assert.Equal("sig", key.GetProperty("use").GetString());
            Assert.Equal("RS256", key.GetProperty("alg").GetString());
            Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("kid").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("n").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("e").GetString()));
        }
        finally
        {
            TryDeleteDirectory(keyDir);
        }
    }

    [Fact]
    public void MintTokens_then_TryValidateRefresh_returns_subject()
    {
        using var service = CreateService(out var keyDir);
        try
        {
            var (_, refresh, expiresIn) = service.MintTokens("alice", mustRotateCredentials: true, roles: ["admin"]);
            Assert.True(expiresIn > 0);

            var (ok, subject, mustRotate, roles) = service.TryValidateRefresh(refresh);
            Assert.True(ok);
            Assert.Equal("alice", subject);
            Assert.True(mustRotate);
            Assert.Contains("admin", roles);
        }
        finally
        {
            TryDeleteDirectory(keyDir);
        }
    }

    [Fact]
    public void TryValidateRefresh_rejects_access_token()
    {
        using var service = CreateService(out var keyDir);
        try
        {
            var (access, _, _) = service.MintTokens("bob", mustRotateCredentials: false);
            var (ok, subject, _, _) = service.TryValidateRefresh(access);
            Assert.False(ok);
            Assert.Null(subject);
        }
        finally
        {
            TryDeleteDirectory(keyDir);
        }
    }

    private static AuthModuleJwtService CreateService(out string keyDir)
    {
        keyDir = Path.Combine(Path.GetTempPath(), "bardie-auth-jwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDir);

        var options = Options.Create(new AuthModuleJwtOptions
        {
            Audience = "bardie.test",
            AccessTokenMinutes = 5,
            RefreshTokenDays = 1,
            SigningKeyPath = Path.Combine(keyDir, "jwt.pem"),
        });

        var manifest = new ModuleManifest
        {
            Slug = "bes",
            Kind = "auth",
            OtelServiceName = "bardie.auth.bes",
        };

        return new AuthModuleJwtService(options, manifest);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
