using System.Security.Claims;
using Bardie.Harness.Auth.Ports;

namespace Kithara.Features.Auth;

/// <summary>
/// AUTH-CLAIM-001: claim / bind-only principals may call bindings + <c>/me</c> only.
/// </summary>
public static class BindOnlyGate
{
    public const string BindOnlyRequired = BindingCompletionGate.MustCompleteBindingRequired;

    public static bool IsBindOnlyToken(ClaimsPrincipal user)
    {
        var bindOnly = user.FindFirstValue(ClaimInviteJwtService.BindOnlyClaim);
        if (string.Equals(bindOnly, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var provider = user.FindFirstValue("bardie_provider");
        return string.Equals(
            provider,
            ClaimInviteJwtService.ProviderClaimValue,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (string.Equals(value, "/api/auth/me", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.StartsWith("/api/auth/bindings/", StringComparison.OrdinalIgnoreCase);
    }

    public static IResult? DenyIfDisallowed(HttpContext http, AuthUserRecord principal)
    {
        var pending = principal.MustCompleteBinding || IsBindOnlyToken(http.User);
        if (!pending)
        {
            return null;
        }

        if (IsAllowedPath(http.Request.Path))
        {
            return null;
        }

        return Results.Json(
            new { error = BindOnlyRequired },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
