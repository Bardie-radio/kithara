using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Bardie.Harness.Auth.Ports;
using Microsoft.IdentityModel.Tokens;

namespace Kithara.Features.Auth;

/// <summary>
/// Mints Kithara-signed claim-scoped JWTs after invite OTP verification (AUTH-INVITE).
/// Reuses the guest signing key store; distinct <c>bardie_provider</c> value.
/// </summary>
public sealed class ClaimInviteJwtService
{
    public const string ProviderClaimValue = "kithara.claim";
    public const string BindOnlyClaim = "bardie_bind_only";

    private readonly GuestJwtOptions _options;
    private readonly GuestJwtSigningKeyStore _keys;
    private readonly IAuthPersistence _persistence;

    public ClaimInviteJwtService(
        Microsoft.Extensions.Options.IOptions<GuestJwtOptions> options,
        GuestJwtSigningKeyStore keys,
        IAuthPersistence persistence)
    {
        _options = options.Value;
        _keys = keys;
        _persistence = persistence;
    }

    public async Task<(string AccessToken, string RefreshToken, long ExpiresIn)?> TryClaimAsync(
        string username,
        string registrationPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationPassword);

        var user = await _persistence.FindUserByUsernameAsync(username.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (user is null
            || !user.MustCompleteBinding
            || string.IsNullOrWhiteSpace(user.InvitePasswordHash)
            || !InviteOtp.Verify(user.InvitePasswordHash, registrationPassword))
        {
            return null;
        }

        // Invite roles stay on the user row until bind — claim JWTs must not carry admin/user roles.
        return MintTokens(user.UserId, user.Username ?? username.Trim());
    }

    public async Task<(string AccessToken, string RefreshToken, long ExpiresIn)?> TryRefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRefreshToken(refreshToken, out var userId))
        {
            return null;
        }

        var user = await _persistence.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.MustCompleteBinding)
        {
            return null;
        }

        return MintTokens(user.UserId, user.Username ?? userId.ToString("D"));
    }

    private bool TryValidateRefreshToken(string refreshToken, out Guid userId)
    {
        userId = Guid.Empty;
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _keys.GetSigningKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(refreshToken, parameters, out _);
            var use = principal.FindFirst("token_use")?.Value;
            if (!string.Equals(use, "refresh", StringComparison.Ordinal))
            {
                return false;
            }

            var provider = principal.FindFirst("bardie_provider")?.Value;
            if (!string.Equals(provider, ProviderClaimValue, StringComparison.Ordinal))
            {
                return false;
            }

            var subject = principal.FindFirst("sub")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(subject, out userId);
        }
        catch (SecurityTokenException)
        {
            return false;
        }
    }

    private (string Access, string Refresh, long ExpiresIn) MintTokens(Guid userId, string username)
    {
        var key = _keys.GetSigningKey();
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var accessExpires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
        var refreshExpires = now.AddHours(4);

        var accessClaims = new Claim[]
        {
            new("sub", userId.ToString("D")),
            new("bardie_provider", ProviderClaimValue),
            new("token_use", "access"),
            new(BindOnlyClaim, "true"),
            new("username", username),
        };

        var refreshClaims = new Claim[]
        {
            new("sub", userId.ToString("D")),
            new("bardie_provider", ProviderClaimValue),
            new("token_use", "refresh"),
            new(BindOnlyClaim, "true"),
        };

        var access = CreateToken(accessClaims, now, accessExpires, creds);
        var refresh = CreateToken(refreshClaims, now, refreshExpires, creds);
        return (access, refresh, (long)(accessExpires - now).TotalSeconds);
    }

    private string CreateToken(
        IEnumerable<Claim> claims,
        DateTime notBefore,
        DateTime expires,
        SigningCredentials creds)
    {
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
