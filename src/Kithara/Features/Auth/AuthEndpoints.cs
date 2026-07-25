using System.Security.Claims;
using System.Text.Json.Serialization;
using Bardie.Auth.V1;
using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Ports;
using Microsoft.AspNetCore.Mvc;

namespace Kithara.Features.Auth;

/// <summary>REST surface for auth discovery / login / refresh / register / bindings / me.</summary>
public static class AuthEndpoints
{
    public const string CredentialsRotationRequired = "credentials_rotation_required";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/discovery", DiscoveryAsync);
        group.MapPost("/authenticate", AuthenticateAsync);
        group.MapPost("/refresh", RefreshAsync);

        group.MapPost("/register", RegisterAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

        group.MapPost("/bindings/{provider}", UpdateBindingAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

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
                login_form = p.LoginFormFields.Select(MapField),
                // Legacy alias for login fields (Plume / older clients).
                form_fields = p.LoginFormFields.Select(MapField),
                bind_form = p.BindFormFields.Count == 0
                    ? null
                    : p.BindFormFields.Select(MapField),
                authorize_url = p.AuthorizeUrl,
            }),
        });
    }

    private static object MapField(Bardie.Harness.Auth.Models.FormFieldDescriptor f) => new
    {
        name = f.Name,
        label = f.Label,
        input_type = f.InputType,
        required = f.Required,
    };

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

    private static async Task<IResult> RegisterAsync(
        [FromBody] RegisterRequestBody body,
        HttpContext http,
        AuthModuleHarness orch,
        IAuthPersistence persistence,
        CancellationToken ct)
    {
        if (!http.User.IsInRole("admin"))
        {
            return Results.Json(
                new { error = "admin_required" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(body.ProviderId))
        {
            return Results.BadRequest(new { error = "provider_id is required." });
        }

        var payload = body.Payload ?? new Dictionary<string, string>();
        var userId = await persistence.CreateDurableUserAsync(
                mustRotateCredentials: false,
                ct)
            .ConfigureAwait(false);

        var result = await orch.UpdateUserBindingAsync(
                body.ProviderId,
                userId,
                payload,
                BindingCeremony.Bind,
                ct)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "binding_rejected" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(new
        {
            user_id = result.UserId,
            external_subject = result.ExternalSubject,
            must_rotate_credentials = result.MustRotateCredentials,
        });
    }

    private static async Task<IResult> UpdateBindingAsync(
        string provider,
        [FromBody] BindingUpdateRequestBody body,
        HttpContext http,
        AuthModuleHarness orch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Results.BadRequest(new { error = "provider is required." });
        }

        var principal = AuthPrincipal.Get(http);
        var payload = body.Payload ?? new Dictionary<string, string>();
        var ceremony = ParseCeremony(body.Ceremony);

        var result = await orch.UpdateUserBindingAsync(
                provider,
                principal.UserId,
                payload,
                ceremony,
                ct)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "binding_rejected" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Refresh stashed principal so subsequent filters see cleared must_rotate.
        var refreshed = await orch.Persistence.FindUserByIdAsync(principal.UserId, ct).ConfigureAwait(false);
        if (refreshed is not null)
        {
            AuthPrincipal.Set(http, refreshed);
        }

        return Results.Ok(new
        {
            user_id = result.UserId,
            external_subject = result.ExternalSubject,
            must_rotate_credentials = result.MustRotateCredentials,
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

    private static BindingCeremony ParseCeremony(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BindingCeremony.Unspecified;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "bind" => BindingCeremony.Bind,
            "update" => BindingCeremony.Update,
            _ => BindingCeremony.Unspecified,
        };
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

    public sealed class RegisterRequestBody
    {
        [JsonPropertyName("provider_id")]
        public string ProviderId { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public Dictionary<string, string>? Payload { get; set; }
    }

    public sealed class BindingUpdateRequestBody
    {
        [JsonPropertyName("payload")]
        public Dictionary<string, string>? Payload { get; set; }

        /// <summary>Optional: <c>bind</c> | <c>update</c>. Host auto-picks when omitted.</summary>
        [JsonPropertyName("ceremony")]
        public string? Ceremony { get; set; }
    }
}
