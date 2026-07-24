using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kithara.Features.Auth;

/// <summary>Mints Kithara-signed JWTs for ephemeral guest users after guest-code exchange.</summary>
public sealed class GuestJwtService
{
    public const string ProviderClaimValue = "kithara.guest";
    public const string GuestStrunaClaim = "bardie_guest_struna";

    private readonly GuestJwtOptions _options;
    private readonly GuestJwtSigningKeyStore _keys;
    private readonly IDbContextFactory<KitharaDbContext> _dbFactory;

    public GuestJwtService(
        IOptions<GuestJwtOptions> options,
        GuestJwtSigningKeyStore keys,
        IDbContextFactory<KitharaDbContext> dbFactory)
    {
        _options = options.Value;
        _keys = keys;
        _dbFactory = dbFactory;
    }

    public async Task<(Guid UserId, string AccessToken, string RefreshToken, long ExpiresIn)?> ExchangeAsync(
        Guid strunaId,
        string guestCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestCode);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var struna = await db.Strunas.FirstOrDefaultAsync(s => s.Id == strunaId, cancellationToken)
            .ConfigureAwait(false);
        if (struna is null
            || struna.ControlAccess != ControlAccess.Protected
            || string.IsNullOrWhiteSpace(struna.GuestCode)
            || !string.Equals(struna.GuestCode, guestCode.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Kind = UserKind.EphemeralGuest,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active",
            GuestStrunaId = strunaId,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var (access, refresh, expiresIn) = MintTokens(user.Id, strunaId);
        return (user.Id, access, refresh, expiresIn);
    }

    /// <summary>
    /// Validates a Kithara-minted guest refresh token and remints access+refresh while the
    /// ephemeral guest user and Struna still exist (GUEST-REF-001).
    /// </summary>
    public async Task<(string AccessToken, string RefreshToken, long ExpiresIn)?> TryRefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        if (!TryValidateRefreshToken(refreshToken, out var userId, out var strunaId))
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null
            || user.Kind != UserKind.EphemeralGuest
            || user.GuestStrunaId != strunaId)
        {
            return null;
        }

        var strunaExists = await db.Strunas.AsNoTracking()
            .AnyAsync(s => s.Id == strunaId, cancellationToken)
            .ConfigureAwait(false);
        if (!strunaExists)
        {
            return null;
        }

        return MintTokens(userId, strunaId);
    }

    private bool TryValidateRefreshToken(string refreshToken, out Guid userId, out Guid strunaId)
    {
        userId = Guid.Empty;
        strunaId = Guid.Empty;

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
            var strunaClaim = principal.FindFirst(GuestStrunaClaim)?.Value;
            if (!Guid.TryParse(subject, out userId) || !Guid.TryParse(strunaClaim, out strunaId))
            {
                return false;
            }

            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
    }

    private (string Access, string Refresh, long ExpiresIn) MintTokens(Guid userId, Guid strunaId)
    {
        var key = _keys.GetSigningKey();
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var accessExpires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
        var refreshExpires = now.AddDays(7);

        var access = CreateToken(
            [
                new Claim("sub", userId.ToString("D")),
                new Claim("bardie_provider", ProviderClaimValue),
                new Claim("token_use", "access"),
                new Claim(GuestStrunaClaim, strunaId.ToString("D")),
            ],
            now,
            accessExpires,
            creds);

        var refresh = CreateToken(
            [
                new Claim("sub", userId.ToString("D")),
                new Claim("bardie_provider", ProviderClaimValue),
                new Claim("token_use", "refresh"),
                new Claim(GuestStrunaClaim, strunaId.ToString("D")),
            ],
            now,
            refreshExpires,
            creds);

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
