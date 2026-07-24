using System.Security.Claims;
using System.Text.Json.Serialization;
using Bardie.Harness.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Kithara.Features.Auth;

/// <summary>REST surface for auth discovery / login / refresh / me.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/discovery", DiscoveryAsync);
        group.MapPost("/authenticate", AuthenticateAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapGet("/me", MeAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

        return endpoints;
    }

    private static async Task<IResult> DiscoveryAsync(AuthModuleHarness orch, CancellationToken ct)
    {
        var providers = await orch.GetProvidersAsync(ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            providers = providers.Select(p => new
            {
                id = p.Id,
                display_name = p.DisplayName,
                module = p.ModuleSlug,
                ui_mode = p.UiMode,
                form_fields = p.FormFields.Select(f => new
                {
                    name = f.Name,
                    label = f.Label,
                    input_type = f.InputType,
                    required = f.Required,
                }),
                authorize_url = p.AuthorizeUrl,
            }),
        });
    }

    private static async Task<IResult> AuthenticateAsync(
        [FromBody] AuthenticateRequestBody body,
        AuthModuleHarness orch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ProviderId))
        {
            return Results.BadRequest(new { error = "provider_id is required." });
        }

        var payload = body.Payload ?? new Dictionary<string, string>();
        var result = await orch.AuthenticateAsync(body.ProviderId, payload, ct).ConfigureAwait(false);
        if (!result.Allowed)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "unauthorized" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            access_token = result.AccessToken,
            refresh_token = result.RefreshToken,
            token_type = result.TokenType,
            expires_in = result.ExpiresIn,
            must_rotate_credentials = result.MustRotateCredentials,
            user_id = result.UserId,
            external_subject = result.ExternalSubject,
        });
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshRequestBody body,
        AuthModuleHarness orch,
        GuestJwtService guests,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ProviderId) || string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            return Results.BadRequest(new { error = "provider_id and refresh_token are required." });
        }

        // GUEST-REF-001: host-minted guest refresh — do not dial auth modules.
        if (string.Equals(body.ProviderId, GuestJwtService.ProviderClaimValue, StringComparison.Ordinal))
        {
            var reminted = await guests.TryRefreshAsync(body.RefreshToken, ct).ConfigureAwait(false);
            if (reminted is null)
            {
                return Results.Json(
                    new { error = "unauthorized" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var (access, refresh, expiresIn) = reminted.Value;
            return Results.Ok(new
            {
                access_token = access,
                refresh_token = refresh,
                token_type = "Bearer",
                expires_in = expiresIn,
            });
        }

        var result = await orch.RefreshAsync(body.ProviderId, body.RefreshToken, ct).ConfigureAwait(false);
        if (!result.Allowed)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "unauthorized" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            access_token = result.AccessToken,
            refresh_token = result.RefreshToken,
            token_type = result.TokenType,
            expires_in = result.ExpiresIn,
        });
    }

    private static IResult MeAsync(HttpContext http, ClaimsPrincipal user)
    {
        var principal = AuthPrincipal.Get(http);
        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var provider = user.FindFirstValue("bardie_provider");

        return Results.Ok(new
        {
            user_id = principal.UserId,
            kind = principal.Kind,
            status = principal.Status,
            external_subject = subject,
            provider,
            must_rotate_credentials = principal.MustRotateCredentials,
        });
    }

    public sealed class AuthenticateRequestBody
    {
        [JsonPropertyName("provider_id")]
        public string ProviderId { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public Dictionary<string, string>? Payload { get; set; }
    }

    public sealed class RefreshRequestBody
    {
        [JsonPropertyName("provider_id")]
        public string ProviderId { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
    }
}
