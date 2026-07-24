using System.Security.Claims;
using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Ports;

namespace Kithara.Features.Auth;

/// <summary>
/// Resolves JWT → <see cref="AuthUserRecord"/> (login binding or ephemeral guest).
/// Stashed on <see cref="HttpContext.Items"/> by <see cref="RequirePrincipalFilter"/>.
/// </summary>
public static class AuthPrincipal
{
    private const string ItemsKey = "bardie.auth.principal";

    public static AuthUserRecord Get(HttpContext http) =>
        (AuthUserRecord)http.Items[ItemsKey]!;

    public static bool TryGet(HttpContext http, out AuthUserRecord? principal)
    {
        if (http.Items.TryGetValue(ItemsKey, out var raw) && raw is AuthUserRecord record)
        {
            principal = record;
            return true;
        }

        principal = null;
        return false;
    }

    public static void Set(HttpContext http, AuthUserRecord principal) =>
        http.Items[ItemsKey] = principal;

    /// <summary>
    /// Login users via provider binding; guests via <c>sub</c> = user id + <c>bardie_provider</c> guest claim.
    /// </summary>
    public static async Task<AuthUserRecord?> ResolveAsync(
        ClaimsPrincipal user,
        IAuthPersistence persistence,
        AuthModuleHarness authHarness,
        CancellationToken ct)
    {
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var provider = user.FindFirstValue("bardie_provider");
        if (string.Equals(provider, GuestJwtService.ProviderClaimValue, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(subject, out var guestId))
        {
            return await persistence.FindUserByIdAsync(guestId, ct).ConfigureAwait(false);
        }

        provider ??= authHarness.ListRegisteredModules().FirstOrDefault()?.Slug;
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return await persistence.FindUserByBindingSubjectAsync(provider, subject, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Ensures an authenticated Bardie user row exists for the Bearer JWT.
/// Runs after <c>RequireAuthorization</c>; short-circuits 401 when the subject has no user.
/// </summary>
public sealed class RequirePrincipalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (AuthPrincipal.TryGet(http, out _))
        {
            return await next(context).ConfigureAwait(false);
        }

        var persistence = http.RequestServices.GetRequiredService<IAuthPersistence>();
        var authHarness = http.RequestServices.GetRequiredService<AuthModuleHarness>();
        var principal = await AuthPrincipal.ResolveAsync(
                http.User,
                persistence,
                authHarness,
                http.RequestAborted)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return Results.Unauthorized();
        }

        AuthPrincipal.Set(http, principal);
        return await next(context).ConfigureAwait(false);
    }
}
