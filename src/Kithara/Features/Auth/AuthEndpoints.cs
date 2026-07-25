using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Bardie.Auth.V1;
using Bardie.Harness.Auth;
using Bardie.Harness.Auth.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Kithara.Features.Auth;

/// <summary>REST surface for auth discovery / login / refresh / register / claim / bindings / me.</summary>
public static class AuthEndpoints
{
    public const string CredentialsRotationRequired = "credentials_rotation_required";
    public const string InviteClaimLocked = "invite_claim_locked";
    public const string InvalidInvite = "invalid_invite";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/discovery", DiscoveryAsync);
        group.MapPost("/authenticate", AuthenticateAsync);
        group.MapPost("/refresh", RefreshAsync);

        group.MapPost("/register", RegisterAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

        group.MapPost("/claim", ClaimAsync)
            .RequireRateLimiting("invite-claim");

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
        Activity.Current?.SetTag("auth.provider.id", body.ProviderId);
        if (!result.Allowed)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "unauthorized" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (result.UserId is { } uid)
        {
            var user = await orch.Persistence.FindUserByIdAsync(uid, ct).ConfigureAwait(false);
            if (user is { MustCompleteBinding: true })
            {
                return Results.Json(
                    new { error = BindingCompletionGate.MustCompleteBindingRequired },
                    statusCode: StatusCodes.Status403Forbidden);
            }
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
        ClaimInviteJwtService claims,
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

        if (string.Equals(body.ProviderId, ClaimInviteJwtService.ProviderClaimValue, StringComparison.Ordinal))
        {
            var reminted = await claims.TryRefreshAsync(body.RefreshToken, ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        var principal = AuthPrincipal.Get(http);
        if (principal.MustCompleteBinding || BindOnlyGate.IsBindOnlyToken(http.User))
        {
            return Results.Json(
                new { error = BindingCompletionGate.MustCompleteBindingRequired },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!http.User.IsInRole("admin"))
        {
            return Results.Json(
                new { error = "admin_required" },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(body.Username))
        {
            return Results.BadRequest(new { error = "username is required." });
        }

        try
        {
            var (userId, username, otp) = await orch.CreateInviteAsync(
                    body.Username,
                    InviteOtp.Generate,
                    InviteOtp.Hash,
                    ct)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                user_id = userId,
                username,
                registration_password = otp,
            });
        }
        catch (AuthUsernameConflictException)
        {
            return Results.Json(
                new { error = "username_taken" },
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ClaimAsync(
        [FromBody] ClaimRequestBody body,
        HttpContext http,
        ClaimInviteJwtService claims,
        InviteClaimLockout lockout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.RegistrationPassword))
        {
            return Results.BadRequest(new { error = "username and registration_password are required." });
        }

        var username = body.Username.Trim();
        var partition = InviteClaimLockout.PartitionKey(
            http.Connection.RemoteIpAddress?.ToString(),
            username);

        if (lockout.IsLocked(partition, out var lockedUntil))
        {
            return Results.Json(
                new { error = InviteClaimLocked, locked_until = lockedUntil },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var tokens = await claims.TryClaimAsync(username, body.RegistrationPassword, ct)
            .ConfigureAwait(false);
        if (tokens is null)
        {
            lockout.RecordFailure(partition);
            return Results.Json(
                new { error = InvalidInvite },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        lockout.RecordSuccess(partition);
        var (access, refresh, expiresIn) = tokens.Value;
        return Results.Ok(new
        {
            access_token = access,
            refresh_token = refresh,
            token_type = "Bearer",
            expires_in = expiresIn,
            provider_id = ClaimInviteJwtService.ProviderClaimValue,
            must_complete_binding = true,
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
        var isInviteBind = principal.MustCompleteBinding;

        if (isInviteBind && ceremony == BindingCeremony.Update)
        {
            return Results.Json(
                new { error = BindingCompletionGate.MustCompleteBindingRequired },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (isInviteBind)
        {
            ceremony = BindingCeremony.Bind;
        }

        var result = await orch.UpdateUserBindingAsync(
                provider,
                principal.UserId,
                payload,
                ceremony,
                ct,
                isInviteBind: isInviteBind)
            .ConfigureAwait(false);

        if (!result.Ok)
        {
            return Results.Json(
                new { error = result.FailureReason ?? "binding_rejected" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Refresh stashed principal so subsequent filters see cleared flags.
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
            must_complete_binding = refreshed?.MustCompleteBinding ?? false,
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
            username = principal.Username,
            kind = principal.Kind,
            status = principal.Status,
            external_subject = subject,
            provider,
            must_rotate_credentials = principal.MustRotateCredentials,
            must_complete_binding = principal.MustCompleteBinding,
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
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;
    }

    public sealed class ClaimRequestBody
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("registration_password")]
        public string RegistrationPassword { get; set; } = string.Empty;
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
