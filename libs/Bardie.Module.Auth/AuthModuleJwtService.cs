using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Bardie.Module.Channel.Manifest;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Bardie.Module.Auth;

public sealed class AuthModuleJwtOptions
{
    public const string SectionName = "AuthModuleJwt";

    public string Audience { get; set; } = "bardie.kithara";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
    public string? SigningKeyPath { get; set; }
}

public sealed class AuthModuleJwtService : IDisposable
{
    public const string TokenUseClaim = "token_use";
    public const string AccessTokenUse = "access";
    public const string RefreshTokenUse = "refresh";
    public const string MustRotateClaim = "must_rotate_credentials";
    public const string ProviderClaim = "bardie_provider";

    private readonly AuthModuleJwtOptions _options;
    private readonly ModuleManifest _manifest;
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _securityKey;
    private readonly SigningCredentials _credentials;
    private readonly string _keyId;
    private bool _disposed;

    public AuthModuleJwtService(IOptions<AuthModuleJwtOptions> options, ModuleManifest manifest)
    {
        _options = options.Value;
        _manifest = manifest;
        _rsa = RSA.Create(2048);
        LoadOrCreateKey();
        _keyId = Convert.ToHexString(SHA256.HashData(_rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        _securityKey = new RsaSecurityKey(_rsa) { KeyId = _keyId };
        _credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.RsaSha256);
    }

    public string Issuer => string.IsNullOrWhiteSpace(_manifest.OtelServiceName)
        ? $"bardie.auth.{_manifest.Slug}"
        : _manifest.OtelServiceName;

    public string ExportJwksJson()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        var jwk = new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = _keyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent!),
        };

        return JsonSerializer.Serialize(new { keys = new[] { jwk } });
    }

    public (string AccessToken, string RefreshToken, long ExpiresIn) MintTokens(
        string subject,
        bool mustRotateCredentials,
        IEnumerable<string>? roles = null)
    {
        var now = DateTime.UtcNow;
        var accessExpires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
        var refreshExpires = now.AddDays(Math.Max(1, _options.RefreshTokenDays));

        var accessClaims = BuildBaseClaims(subject, AccessTokenUse, mustRotateCredentials, roles);
        // Roles travel on refresh too so remint does not invent privileges (AUTH-ROLE-001).
        var refreshClaims = BuildBaseClaims(subject, RefreshTokenUse, mustRotateCredentials, roles);

        var access = CreateToken(accessClaims, now, accessExpires);
        var refresh = CreateToken(refreshClaims, now, refreshExpires);
        var expiresIn = (long)(accessExpires - now).TotalSeconds;
        return (access, refresh, expiresIn);
    }

    public (bool Ok, string? Subject, bool MustRotate, IReadOnlyList<string> Roles) TryValidateRefresh(
        string refreshToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _securityKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            RoleClaimType = ClaimTypes.Role,
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(refreshToken, parameters, out _);
            var use = principal.FindFirst(TokenUseClaim)?.Value;
            if (!string.Equals(use, RefreshTokenUse, StringComparison.Ordinal))
            {
                return (false, null, false, []);
            }

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return (false, null, false, []);
            }

            var mustRotate = string.Equals(
                principal.FindFirst(MustRotateClaim)?.Value,
                "true",
                StringComparison.OrdinalIgnoreCase);
            var roles = principal.FindAll(ClaimTypes.Role)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return (true, subject, mustRotate, roles);
        }
        catch (SecurityTokenException)
        {
            return (false, null, false, []);
        }
    }

    private List<Claim> BuildBaseClaims(
        string subject,
        string tokenUse,
        bool mustRotateCredentials,
        IEnumerable<string>? roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(TokenUseClaim, tokenUse),
            new(ProviderClaim, _manifest.Slug),
            new(MustRotateClaim, mustRotateCredentials ? "true" : "false"),
        };

        if (roles is not null)
        {
            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        return claims;
    }

    private string CreateToken(IEnumerable<Claim> claims, DateTime notBefore, DateTime expires)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: _credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private void LoadOrCreateKey()
    {
        var path = ResolveKeyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var pem = File.ReadAllText(path);
            _rsa.ImportFromPem(pem);
            return;
        }

        var privatePem = _rsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(path, privatePem);
    }

    private string ResolveKeyPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.SigningKeyPath))
        {
            return _options.SigningKeyPath;
        }

        var tls = Environment.GetEnvironmentVariable("MODULE_TLS_DATA_PATH")
            ?? Environment.GetEnvironmentVariable("BARDIE_GRPC_TLS_DATA_PATH")
            ?? "data/mtls";
        var slug = string.IsNullOrWhiteSpace(_manifest.Slug) ? "auth-module" : _manifest.Slug;
        return Path.Combine(tls, $"{slug}-jwt-rsa.pem");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rsa.Dispose();
    }
}
