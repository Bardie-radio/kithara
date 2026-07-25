using System.Collections.Concurrent;
using System.Text;
using Bardie.Harness.Auth.Catalog;
using Bardie.Harness.Auth.Models;
using Bardie.Harness.Auth.Ports;
using Bardie.Auth.V1;
using Bardie.Module.Channel.Certificates;
using Bardie.Module.Channel.Channel;
using Bardie.Module.Channel.Participant;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bardie.Harness.Auth;

/// <summary>
/// Auth module harness: merges discovery, routes Authenticate/Refresh/UpdateUserBinding,
/// persists users via host port. Host-owned invite bootstrap (AUTH-INVITE) — no SeedAdminBinding.
/// </summary>
public sealed class AuthModuleHarness
{
    /// <summary>Host-chosen DEFAULT_ADMIN username for empty-DB invite bootstrap.</summary>
    public const string DefaultAdminUsername = "admin";

    private static readonly string[] AdminInviteRoles = ["admin"];
    private static readonly string[] UserInviteRoles = ["user"];

    private readonly IAuthModuleCatalog _catalog;
    private readonly IAuthPersistence _persistence;
    private readonly IModuleGrpcChannelFactory _channelFactory;
    private readonly IModuleCertificateStore _certificateStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthModuleHarness> _logger;
    /// <summary>AUTH-ORCH-001: discovery <c>provider_id</c> → module slug (filled by <see cref="GetProvidersAsync"/>).</summary>
    private readonly ConcurrentDictionary<string, string> _providerToModule =
        new(StringComparer.OrdinalIgnoreCase);

    public AuthModuleHarness(
        IAuthModuleCatalog catalog,
        IAuthPersistence persistence,
        IModuleGrpcChannelFactory channelFactory,
        IModuleCertificateStore certificateStore,
        IConfiguration configuration,
        ILogger<AuthModuleHarness> logger)
    {
        _catalog = catalog;
        _persistence = persistence;
        _channelFactory = channelFactory;
        _certificateStore = certificateStore;
        _configuration = configuration;
        _logger = logger;
    }

    public IAuthModuleCatalog Catalog => _catalog;

    public IAuthPersistence Persistence => _persistence;

    public IModuleGrpcChannelFactory ChannelFactory => _channelFactory;

    /// <summary>Raw catalog registrations (slug, JWKS, capabilities).</summary>
    public IReadOnlyCollection<AuthModuleRegistration> ListRegisteredModules() => _catalog.List();

    /// <summary>
    /// Empty-DB bootstrap: invent DEFAULT_ADMIN with host OTP invite (no module RPC).
    /// </summary>
    public async Task<InviteBootstrapResult?> TryBootstrapInviteAsync(
        Func<string> otpFactory,
        Func<string, string> otpHasher,
        CancellationToken cancellationToken = default)
    {
        if (await _persistence.HasAnyDurableUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var otp = otpFactory();
        var hash = otpHasher(otp);
        var userId = await _persistence.CreateInvitedUserAsync(
                new CreateInvitedUserRequest(
                    DefaultAdminUsername,
                    hash,
                    AdminInviteRoles,
                    MustRotateCredentials: false),
                cancellationToken)
            .ConfigureAwait(false);

        return new InviteBootstrapResult(true, userId, DefaultAdminUsername, otp);
    }

    /// <summary>
    /// Admin provision: username-only invite. Returns plaintext OTP once.
    /// </summary>
    public async Task<(Guid UserId, string Username, string RegistrationPassword)> CreateInviteAsync(
        string username,
        Func<string> otpFactory,
        Func<string, string> otpHasher,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var otp = otpFactory();
        var hash = otpHasher(otp);
        var normalized = username.Trim();
        var userId = await _persistence.CreateInvitedUserAsync(
                new CreateInvitedUserRequest(
                    normalized,
                    hash,
                    UserInviteRoles,
                    MustRotateCredentials: false),
                cancellationToken)
            .ConfigureAwait(false);

        return (userId, normalized, otp);
    }

    public async Task<IReadOnlyList<MergedProviderDescriptor>> GetProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var modules = OrderModules(_catalog.List());
        var merged = new List<MergedProviderDescriptor>();

        foreach (var module in modules)
        {
            try
            {
                using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
                var client = new AuthAdapter.AuthAdapterClient(channel);
                var response = await client.GetProvidersAsync(
                        new GetProvidersRequest(),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                foreach (var provider in response.Providers)
                {
                    var mapped = MapProvider(provider, module);
                    // AUTH-ORCH-001: keep provider_id → module map warm for Authenticate/Refresh routing.
                    if (!string.IsNullOrWhiteSpace(mapped.Id))
                    {
                        _providerToModule[mapped.Id] = module.Slug;
                    }

                    merged.Add(mapped);
                }
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "GetProviders failed for auth module {Slug} at {Address}",
                    module.Slug,
                    module.GrpcAdvertiseAddress);
            }
        }

        return merged;
    }

    public async Task<AuthenticateResult> AuthenticateAsync(
        string providerId,
        IReadOnlyDictionary<string, string> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var module = await ResolveModuleForProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (module is null)
        {
            return new AuthenticateResult(
                Allowed: false,
                AccessToken: null,
                RefreshToken: null,
                TokenType: "Bearer",
                ExpiresIn: 0,
                ExternalSubject: null,
                UserId: null,
                MustRotateCredentials: false,
                FailureReason: $"Unknown provider '{providerId}'.");
        }

        var subjectHint = ResolveSubjectHint(payload);
        byte[] existingBinding = [];
        if (!string.IsNullOrWhiteSpace(subjectHint))
        {
            var binding = await _persistence.FindBindingBySubjectAsync(
                    module.Slug,
                    subjectHint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (binding is not null)
            {
                existingBinding = Encoding.UTF8.GetBytes(binding.PayloadJson);
            }
        }

        using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
        var client = new AuthAdapter.AuthAdapterClient(channel);
        var request = new AuthenticateRequest
        {
            ProviderId = providerId,
            ExistingBindingPayload = ByteString.CopyFrom(existingBinding),
        };
        foreach (var (key, value) in payload)
        {
            request.Payload[key] = value;
        }

        AuthenticateResponse response;
        try
        {
            response = await client.AuthenticateAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Authenticate RPC failed for provider {Provider}", providerId);
            return new AuthenticateResult(
                Allowed: false,
                AccessToken: null,
                RefreshToken: null,
                TokenType: "Bearer",
                ExpiresIn: 0,
                ExternalSubject: null,
                UserId: null,
                MustRotateCredentials: false,
                FailureReason: ex.Status.Detail);
        }

        if (!response.Allowed)
        {
            return new AuthenticateResult(
                Allowed: false,
                AccessToken: null,
                RefreshToken: null,
                TokenType: response.TokenType is { Length: > 0 } ? response.TokenType : "Bearer",
                ExpiresIn: 0,
                ExternalSubject: null,
                UserId: null,
                MustRotateCredentials: false,
                FailureReason: "Credentials rejected.");
        }

        Guid? userId = null;
        var mustRotate = response.MustRotateCredentials;
        if (response.EnsureUser && !string.IsNullOrWhiteSpace(response.ExternalSubject))
        {
            var payloadJson = response.BindingPayload.IsEmpty
                ? "{}"
                : Encoding.UTF8.GetString(response.BindingPayload.Span);
            userId = await _persistence.EnsureUserWithBindingAsync(
                    new EnsureUserBindingRequest(
                        module.Slug,
                        response.ExternalSubject,
                        payloadJson,
                        response.MustRotateCredentials,
                        response.Roles.ToArray()),
                    cancellationToken)
                .ConfigureAwait(false);

            var user = await _persistence.FindUserByBindingSubjectAsync(
                    module.Slug,
                    response.ExternalSubject,
                    cancellationToken)
                .ConfigureAwait(false);
            mustRotate = user?.MustRotateCredentials ?? mustRotate;
        }
        else if (!string.IsNullOrWhiteSpace(response.ExternalSubject))
        {
            var user = await _persistence.FindUserByBindingSubjectAsync(
                    module.Slug,
                    response.ExternalSubject,
                    cancellationToken)
                .ConfigureAwait(false);
            userId = user?.UserId;
            mustRotate = user?.MustRotateCredentials ?? mustRotate;
        }

        return new AuthenticateResult(
            Allowed: true,
            AccessToken: response.AccessToken,
            RefreshToken: response.RefreshToken,
            TokenType: string.IsNullOrWhiteSpace(response.TokenType) ? "Bearer" : response.TokenType,
            ExpiresIn: response.ExpiresIn,
            ExternalSubject: response.ExternalSubject,
            UserId: userId,
            MustRotateCredentials: mustRotate,
            FailureReason: null);
    }

    public async Task<RefreshResult> RefreshAsync(
        string providerId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var module = await ResolveModuleForProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (module is null)
        {
            return new RefreshResult(false, null, null, "Bearer", 0, $"Unknown provider '{providerId}'.");
        }

        using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
        var client = new AuthAdapter.AuthAdapterClient(channel);

        try
        {
            var response = await client.RefreshAsync(
                    new RefreshRequest
                    {
                        ProviderId = providerId,
                        RefreshToken = refreshToken,
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.Allowed)
            {
                return new RefreshResult(false, null, null, "Bearer", 0, "Refresh rejected.");
            }

            return new RefreshResult(
                true,
                response.AccessToken,
                response.RefreshToken,
                string.IsNullOrWhiteSpace(response.TokenType) ? "Bearer" : response.TokenType,
                response.ExpiresIn,
                null);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Refresh RPC failed for provider {Provider}", providerId);
            return new RefreshResult(false, null, null, "Bearer", 0, ex.Status.Detail);
        }
    }

    /// <summary>
    /// Creates a durable user (or uses <paramref name="userId"/>) and calls module
    /// <c>UpdateUserBinding</c> with ceremony bind/update.
    /// When <paramref name="isInviteBind"/> is true (claim principal completing first bind),
    /// host roles override module roles and <c>MustRotateCredentials</c> stays false.
    /// </summary>
    public async Task<UpdateUserBindingResult> UpdateUserBindingAsync(
        string providerId,
        Guid userId,
        IReadOnlyDictionary<string, string> payload,
        BindingCeremony ceremony,
        CancellationToken cancellationToken = default,
        bool isInviteBind = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var module = await ResolveModuleForProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (module is null)
        {
            return new UpdateUserBindingResult(
                false,
                null,
                null,
                false,
                $"Unknown provider '{providerId}'.");
        }

        var existing = await _persistence.FindBindingByUserAsync(userId, module.Slug, cancellationToken)
            .ConfigureAwait(false);
        byte[] existingBytes = existing is null
            ? []
            : Encoding.UTF8.GetBytes(existing.PayloadJson);

        // Auto-pick ceremony when caller leaves unspecified.
        if (ceremony == BindingCeremony.Unspecified)
        {
            ceremony = existing is null ? BindingCeremony.Bind : BindingCeremony.Update;
        }

        if (!ModuleAllowsBindingCeremony(module, ceremony))
        {
            return new UpdateUserBindingResult(
                false,
                userId,
                null,
                false,
                $"Provider '{providerId}' does not advertise binding updates.");
        }

        // AUTH-INVITE: User.Username is the immutable host login id (set at invite/bootstrap).
        // Never let bind_form invent a parallel mutable ExternalSubject.
        var hostUser = await _persistence.FindUserByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        var payloadForModule = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hostUser?.Username))
        {
            if (ceremony == BindingCeremony.Bind)
            {
                payloadForModule["username"] = hostUser.Username;
            }
            else
            {
                // Update is binding-secret only — username is not in the bag and must not rename subject.
                payloadForModule.Remove("username");
            }
        }

        using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
        var client = new AuthAdapter.AuthAdapterClient(channel);
        var request = new UpdateUserBindingRequest
        {
            ProviderId = providerId,
            UserId = userId.ToString("D"),
            ExistingBindingPayload = ByteString.CopyFrom(existingBytes),
            Ceremony = ceremony,
        };
        foreach (var (key, value) in payloadForModule)
        {
            request.Payload[key] = value;
        }

        UpdateUserBindingResponse response;
        try
        {
            response = await client.UpdateUserBindingAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "UpdateUserBinding RPC failed for provider {Provider}", providerId);
            return new UpdateUserBindingResult(false, userId, null, false, ex.Status.Detail);
        }

        if (!response.Ok)
        {
            var reason = string.IsNullOrWhiteSpace(response.Error)
                ? "Binding update rejected."
                : response.Error.Trim();
            return new UpdateUserBindingResult(
                false,
                userId,
                null,
                false,
                reason);
        }

        var externalSubject = string.IsNullOrWhiteSpace(response.ExternalSubject)
            ? existing?.ExternalSubject
            : response.ExternalSubject;
        if (!string.IsNullOrWhiteSpace(hostUser?.Username))
        {
            externalSubject = hostUser.Username;
        }

        if (string.IsNullOrWhiteSpace(externalSubject))
        {
            return new UpdateUserBindingResult(
                false,
                userId,
                null,
                false,
                "Binding update rejected: module returned no subject.");
        }

        IReadOnlyList<string>? roles = response.Roles.ToArray();
        var mustRotate = response.MustRotateCredentials;
        if (isInviteBind || hostUser is { MustCompleteBinding: true })
        {
            // First claim bind: user-chosen secret — never set MustRotate; prefer host invite roles.
            mustRotate = false;
            if (hostUser?.InviteRoles is { Count: > 0 })
            {
                roles = hostUser.InviteRoles;
            }
        }

        var payloadJson = response.BindingPayload.IsEmpty
            ? "{}"
            : Encoding.UTF8.GetString(response.BindingPayload.Span);
        try
        {
            await _persistence.EnsureUserWithBindingAsync(
                    new EnsureUserBindingRequest(
                        module.Slug,
                        externalSubject,
                        payloadJson,
                        mustRotate,
                        roles,
                        userId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthBindingConflictException ex)
        {
            _logger.LogWarning(ex, "UpdateUserBinding subject conflict for provider {Provider}", providerId);
            return new UpdateUserBindingResult(false, userId, null, false, ex.Message);
        }

        if (isInviteBind || hostUser is { MustCompleteBinding: true })
        {
            await _persistence.CompleteInviteAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        return new UpdateUserBindingResult(
            true,
            userId,
            externalSubject,
            mustRotate,
            null);
    }

    /// <summary>
    /// AUTH-ORCH-001: route via discovery <c>provider_id → module</c> map; still pass <c>provider_id</c> on the wire.
    /// Falls back to slug match, then single-module only when unambiguous.
    /// </summary>
    private async Task<AuthModuleRegistration?> ResolveModuleForProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveModuleForProvider(providerId);
        if (resolved is not null)
        {
            return resolved;
        }

        // Warm discovery map once (provider_id may differ from module slug).
        await GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        return ResolveModuleForProvider(providerId);
    }

    private AuthModuleRegistration? ResolveModuleForProvider(string providerId)
    {
        if (_providerToModule.TryGetValue(providerId, out var mappedSlug)
            && _catalog.TryGet(mappedSlug, out var mapped)
            && mapped is not null)
        {
            return mapped;
        }

        var modules = _catalog.List();
        var exact = modules.FirstOrDefault(m =>
            string.Equals(m.Slug, providerId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // Single-module MVP: allow provider id matching the only registered auth module.
        return modules.Count == 1 ? modules.First() : null;
    }

    private IReadOnlyList<AuthModuleRegistration> OrderModules(
        IReadOnlyCollection<AuthModuleRegistration> modules)
    {
        var priority = _configuration["BARDIE_AUTH_PROVIDER_PRIORITY"];
        if (string.IsNullOrWhiteSpace(priority))
        {
            return modules.OrderBy(m => m.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var order = priority.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return modules
            .OrderBy(m =>
            {
                var index = Array.FindIndex(
                    order,
                    s => string.Equals(s, m.Slug, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Grpc.Net.Client.GrpcChannel CreateModuleChannel(string advertiseAddress, string moduleSlug)
    {
        var address = ModuleParticipantServiceCollectionExtensions.NormalizeGrpcAddress(advertiseAddress);
        if (!_certificateStore.IsLoaded)
        {
            throw new InvalidOperationException("Host TLS material is not loaded.");
        }

        // Short-lived PEM copy for this dial; channel disposes it (never Export Kestrel's ServerCertificate).
        // MESH-CHN-001: pin work-port server cert CN/SAN to the registered module slug.
        return _channelFactory.CreateChannel(
            address,
            _certificateStore.OpenOutboundClientIdentity(),
            trustRemoteServerCertificate: false,
            ownsClientCertificate: true,
            expectedServerIdentity: moduleSlug);
    }

    private static string? ResolveSubjectHint(IReadOnlyDictionary<string, string> payload)
    {
        foreach (var key in new[] { "username", "user", "email", "login" })
        {
            if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool ModuleAllowsBindingCeremony(
        AuthModuleRegistration module,
        BindingCeremony ceremony)
    {
        var caps = module.Capabilities;
        return ceremony switch
        {
            BindingCeremony.Update =>
                WellKnownAuthCapabilities.HasCapability(caps, WellKnownAuthCapabilities.UpdateBinding),
            BindingCeremony.Bind =>
                WellKnownAuthCapabilities.HasCapability(caps, WellKnownAuthCapabilities.UpdateBinding)
                || WellKnownAuthCapabilities.HasCapability(caps, WellKnownAuthCapabilities.SelfRegister),
            _ => false,
        };
    }

    private static MergedProviderDescriptor MapProvider(
        ProviderDescriptor provider,
        AuthModuleRegistration module)
    {
        var uiMode = provider.UiCase switch
        {
            ProviderDescriptor.UiOneofCase.LoginForm => "login_form",
            ProviderDescriptor.UiOneofCase.Redirect => "redirect",
            _ => "unknown",
        };

        IReadOnlyList<FormFieldDescriptor> loginFields = [];
        IReadOnlyList<FormFieldDescriptor> bindFields = [];
        string? authorizeUrl = null;
        if (provider.LoginForm is not null)
        {
            loginFields = provider.LoginForm.Fields
                .Select(f => new FormFieldDescriptor(f.Name, f.Label, f.InputType, f.Required))
                .ToArray();
        }
        else if (provider.Redirect is not null)
        {
            authorizeUrl = provider.Redirect.AuthorizeUrl;
        }

        // Host only surfaces bind_form when the module advertises updateBinding (or selfRegister).
        var exposeBindForm =
            WellKnownAuthCapabilities.HasCapability(module.Capabilities, WellKnownAuthCapabilities.UpdateBinding)
            || WellKnownAuthCapabilities.HasCapability(module.Capabilities, WellKnownAuthCapabilities.SelfRegister);
        if (exposeBindForm && provider.BindForm is not null)
        {
            bindFields = provider.BindForm.Fields
                .Select(f => new FormFieldDescriptor(f.Name, f.Label, f.InputType, f.Required))
                .ToArray();
        }

        return new MergedProviderDescriptor(
            provider.Id,
            provider.DisplayName,
            module.Slug,
            uiMode,
            loginFields,
            bindFields,
            authorizeUrl);
    }
}
