using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Ports;
using Kithara.Features.Auth;
using Kithara.Infrastructure.Neck;
using Kithara.Infrastructure.Persistence.Entities;

namespace Kithara.Features.Streams;

/// <summary>Per-request Struna entity after a discover/control gate.</summary>
internal static class StrunaRequest
{
    private const string StrunaKey = "bardie.struna.entity";

    public static AuthUserRecord Principal(HttpContext http) => AuthPrincipal.Get(http);

    public static Struna Entity(HttpContext http) =>
        (Struna)http.Items[StrunaKey]!;

    public static void SetEntity(HttpContext http, Struna struna) =>
        http.Items[StrunaKey] = struna;
}

/// <summary>Gate: principal may <see cref="StrunaAccess.CanControl"/> this route's <c>id</c>.</summary>
internal sealed class StrunaControlFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var denied = await StrunaResourceGate.AuthorizeAsync(
                context.HttpContext,
                requireControl: true,
                requireOwner: false)
            .ConfigureAwait(false);
        return denied ?? await next(context).ConfigureAwait(false);
    }
}

/// <summary>Gate: principal is the Struna owner (grant CRUD).</summary>
internal sealed class StrunaOwnerFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var denied = await StrunaResourceGate.AuthorizeAsync(
                context.HttpContext,
                requireControl: true,
                requireOwner: true)
            .ConfigureAwait(false);
        return denied ?? await next(context).ConfigureAwait(false);
    }
}

/// <summary>Gate: principal may listen <b>or</b> control this route's <c>id</c>.</summary>
internal sealed class StrunaDiscoverFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var denied = await StrunaResourceGate.AuthorizeAsync(
                context.HttpContext,
                requireControl: false,
                requireOwner: false)
            .ConfigureAwait(false);
        return denied ?? await next(context).ConfigureAwait(false);
    }
}

internal static class StrunaResourceGate
{
    public static async Task<IResult?> AuthorizeAsync(
        HttpContext http,
        bool requireControl,
        bool requireOwner)
    {
        if (!http.Request.RouteValues.TryGetValue("id", out var raw)
            || !Guid.TryParse(Convert.ToString(raw), out var strunaId))
        {
            return Results.NotFound(new { error = "not_found" });
        }

        // Prefer principal already resolved by RequirePrincipalFilter.
        AuthUserRecord principal;
        if (AuthPrincipal.TryGet(http, out var cached) && cached is not null)
        {
            principal = cached;
        }
        else
        {
            var persistence = http.RequestServices.GetRequiredService<IAuthPersistence>();
            var authHarness = http.RequestServices.GetRequiredService<AuthModuleHarness>();
            var resolved = await AuthPrincipal.ResolveAsync(
                    http.User,
                    persistence,
                    authHarness,
                    http.RequestAborted)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                return Results.Unauthorized();
            }

            AuthPrincipal.Set(http, resolved);
            principal = resolved;
        }

        var neck = http.RequestServices.GetRequiredService<Neck>();
        var struna = await neck.GetStrunaAsync(strunaId, http.RequestAborted).ConfigureAwait(false);
        if (struna is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        if (requireOwner)
        {
            if (struna.OwnerUserId != principal.UserId)
            {
                return Results.Forbid();
            }

            var rotateDeny = CredentialsRotationGate.DenyIfRequired(principal);
            if (rotateDeny is not null)
            {
                return rotateDeny;
            }

            var bindDeny = BindingCompletionGate.DenyIfRequired(principal);
            if (bindDeny is not null)
            {
                return bindDeny;
            }
        }
        else
        {
            var allowed = requireControl
                ? StrunaAccess.CanControl(struna, principal)
                : StrunaAccess.CanListen(struna, principal) || StrunaAccess.CanControl(struna, principal);

            if (!allowed)
            {
                return Results.Forbid();
            }

            if (requireControl)
            {
                var rotateDeny = CredentialsRotationGate.DenyIfRequired(principal);
                if (rotateDeny is not null)
                {
                    return rotateDeny;
                }

                var bindDeny = BindingCompletionGate.DenyIfRequired(principal);
                if (bindDeny is not null)
                {
                    return bindDeny;
                }
            }
        }

        StrunaRequest.SetEntity(http, struna);
        return null;
    }
}
