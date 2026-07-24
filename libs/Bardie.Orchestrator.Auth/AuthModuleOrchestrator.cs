using System.Collections.Concurrent;
using System.Text;
using Bardie.Orchestrator.Auth.Catalog;
using Bardie.Orchestrator.Auth.Models;
using Bardie.Orchestrator.Auth.Ports;
using Bardie.Auth.V1;
using Bardie.Module.Channel.Certificates;
using Bardie.Module.Channel.Channel;
using Bardie.Module.Channel.Participant;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bardie.Orchestrator.Auth;

/// <summary>
/// Auth module orchestrator: merges discovery, routes Authenticate/Refresh/SeedAdmin, persists users via host port.
/// </summary>
public sealed class AuthModuleOrchestrator
{
    private readonly IAuthModuleCatalog _catalog;
    private readonly IAuthPersistence _persistence;
    private readonly IModuleGrpcChannelFactory _channelFactory;
    private readonly IModuleCertificateStore _certificateStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthModuleOrchestrator> _logger;
    /// <summary>AUTH-ORCH-001: discovery <c>provider_id</c> → module slug (filled by <see cref="GetProvidersAsync"/>).</summary>
    private readonly ConcurrentDictionary<string, string> _providerToModule =
        new(StringComparer.OrdinalIgnoreCase);

    public AuthModuleOrchestrator(
        IAuthModuleCatalog catalog,
        IAuthPersistence persistence,
        IModuleGrpcChannelFactory channelFactory,
        IModuleCertificateStore certificateStore,
        IConfiguration configuration,
        ILogger<AuthModuleOrchestrator> logger)
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
                    var mapped = MapProvider(provider, module.Slug);
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

    public async Task<SeedAdminResult?> TrySeedAdminAsync(CancellationToken cancellationToken = default)
    {
        if (await _persistence.HasAnyUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var module = _catalog.List()
            .FirstOrDefault(m => m.Capabilities.Any(c =>
                string.Equals(c, WellKnownAuthCapabilities.SeedAdmin, StringComparison.OrdinalIgnoreCase)));

        if (module is null)
        {
            _logger.LogDebug("No auth module with {Capability} capability registered yet.", WellKnownAuthCapabilities.SeedAdmin);
            return null;
        }

        using var channel = CreateModuleChannel(module.GrpcAdvertiseAddress, module.Slug);
        var client = new AuthAdapter.AuthAdapterClient(channel);

        SeedAdminResponse response;
        try
        {
            response = await client.SeedAdminAsync(
                    new SeedAdminRequest { CorrelationId = Guid.NewGuid().ToString("N") },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "SeedAdmin RPC failed for module {Slug}", module.Slug);
            return null;
        }

        if (!response.Created || !response.EnsureUser || string.IsNullOrWhiteSpace(response.ExternalSubject))
        {
            _logger.LogWarning(
                "SeedAdmin from {Slug} did not create a user (created={Created}, ensure={Ensure}).",
                module.Slug,
                response.Created,
                response.EnsureUser);
            return new SeedAdminResult(false, response.WelcomeLogText, null, response.ExternalSubject);
        }

        // Re-check emptiness — another instance may have raced.
        if (await _persistence.HasAnyUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Users already exist after SeedAdmin race; skipping persist.");
            return null;
        }

        var payloadJson = response.BindingPayload.IsEmpty
            ? "{}"
            : Encoding.UTF8.GetString(response.BindingPayload.Span);
        var userId = await _persistence.EnsureUserWithBindingAsync(
                new EnsureUserBindingRequest(
                    module.Slug,
                    response.ExternalSubject,
                    payloadJson,
                    response.MustRotateCredentials,
                    response.Roles.ToArray()),
                cancellationToken)
            .ConfigureAwait(false);

        return new SeedAdminResult(true, response.WelcomeLogText, userId, response.ExternalSubject);
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

    private static MergedProviderDescriptor MapProvider(ProviderDescriptor provider, string moduleSlug)
    {
        var uiMode = provider.UiCase switch
        {
            ProviderDescriptor.UiOneofCase.FormSchema => "form_schema",
            ProviderDescriptor.UiOneofCase.Redirect => "redirect",
            _ => "unknown",
        };

        IReadOnlyList<FormFieldDescriptor> fields = [];
        string? authorizeUrl = null;
        if (provider.FormSchema is not null)
        {
            fields = provider.FormSchema.Fields
                .Select(f => new FormFieldDescriptor(f.Name, f.Label, f.InputType, f.Required))
                .ToArray();
        }
        else if (provider.Redirect is not null)
        {
            authorizeUrl = provider.Redirect.AuthorizeUrl;
        }

        return new MergedProviderDescriptor(
            provider.Id,
            provider.DisplayName,
            moduleSlug,
            uiMode,
            fields,
            authorizeUrl);
    }
}
