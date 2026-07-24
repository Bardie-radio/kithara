using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Kithara.Features.Auth;

public sealed class GuestJwtOptions
{
    public const string SectionName = "GuestJwt";

    public string Issuer { get; set; } = "bardie.kithara.guest";
    public string Audience { get; set; } = "bardie.kithara";
    public int AccessTokenMinutes { get; set; } = 15;
    public string? SigningKey { get; set; }
    public string? SigningKeyPath { get; set; }
}

/// <summary>
/// Loads or auto-generates the ephemeral-guest JWT signing key.
/// </summary>
public sealed class GuestJwtSigningKeyStore
{
    private readonly GuestJwtOptions _options;
    private readonly ILogger<GuestJwtSigningKeyStore> _logger;
    private readonly object _gate = new();
    private SymmetricSecurityKey? _key;

    public GuestJwtSigningKeyStore(
        Microsoft.Extensions.Options.IOptions<GuestJwtOptions> options,
        ILogger<GuestJwtSigningKeyStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public SymmetricSecurityKey GetSigningKey()
    {
        if (_key is not null)
        {
            return _key;
        }

        lock (_gate)
        {
            if (_key is not null)
            {
                return _key;
            }

            var material = ResolveKeyMaterial();
            _key = new SymmetricSecurityKey(material) { KeyId = "guest" };
            return _key;
        }
    }

    private byte[] ResolveKeyMaterial()
    {
        if (!string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            return Encoding.UTF8.GetBytes(_options.SigningKey);
        }

        var path = _options.SigningKeyPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dataRoot = Environment.GetEnvironmentVariable("BARDIE_GRPC_TLS_DATA_PATH")
                ?? Path.Combine("data", "mtls");
            path = Path.Combine(Path.GetDirectoryName(dataRoot) ?? "data", "guest-jwt.key");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            _logger.LogInformation("Loaded guest JWT signing key from {Path}", path);
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(bytes));
        _logger.LogInformation("Generated guest JWT signing key at {Path}", path);
        return bytes;
    }
}

public static class AuthAuthenticationServiceCollectionExtensions
{
    public const string LoginBearerScheme = JwtBearerDefaults.AuthenticationScheme;

    public static IServiceCollection AddKitharaAuthAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GuestJwtOptions>(configuration.GetSection(GuestJwtOptions.SectionName));
        services.PostConfigure<GuestJwtOptions>(options =>
        {
            var key = configuration["BARDIE_GUEST_JWT_SIGNING_KEY"];
            if (!string.IsNullOrWhiteSpace(key))
            {
                options.SigningKey = key;
            }

            var ttl = configuration["BARDIE_GUEST_JWT_ACCESS_TTL"];
            if (TimeSpan.TryParse(ttl, out var parsed) && parsed > TimeSpan.Zero)
            {
                options.AccessTokenMinutes = (int)Math.Ceiling(parsed.TotalMinutes);
            }
            else if (int.TryParse(ttl, out var minutes) && minutes > 0)
            {
                options.AccessTokenMinutes = minutes;
            }
        });

        services.AddSingleton<GuestJwtSigningKeyStore>();
        services.AddSingleton<GuestJwtService>();
        services.AddSingleton<AuthModuleJwksKeyProvider>();
        services.AddHostedService<AuthModuleJwksRefreshHostedService>();
        services.AddMemoryCache();
        services.AddHttpClient(nameof(AuthModuleJwksKeyProvider));

        services.AddAuthentication(LoginBearerScheme)
            .AddJwtBearer(LoginBearerScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = true,
                    ValidAudience = "bardie.kithara",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role,
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Reject refresh tokens on API calls.
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var use = context.Principal?.FindFirst("token_use")?.Value;
                        if (string.Equals(use, "refresh", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Fail("Refresh tokens are not valid for API access.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddOptions<JwtBearerOptions>(LoginBearerScheme)
            .Configure<AuthModuleJwksKeyProvider, GuestJwtSigningKeyStore>((options, keyProvider, guestKeys) =>
            {
                // AUTH-JWKS-001: resolver reads the pre-warmed snapshot only — never GetResult().
                options.TokenValidationParameters.IssuerSigningKeyResolver =
                    (_, _, _, _) => keyProvider.GetCachedSigningKeys().Append(guestKeys.GetSigningKey());
            });

        services.AddAuthorization();
        return services;
    }
}
