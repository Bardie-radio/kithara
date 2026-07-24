using Bardie.Orchestrator.Auth.Catalog;
using Bardie.Module.Channel;
using Bardie.Module.Channel.Certificates;
using Bardie.Modules.V1;
using Bardie.Orchestrator.Source.Catalog;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Kithara.Features.Modules;

/// <summary>
/// Module Registry business logic (join secret, mTLS bootstrap, catalog projection).
/// Separated from the gRPC service so Phase 1 tests can exercise Register without a full host.
/// </summary>
public sealed class ModuleRegistryOperations
{
    private readonly InMemoryModuleRegistry _registry;
    private readonly IAuthModuleCatalog _authCatalog;
    private readonly ISourceModuleCatalog _sourceCatalog;
    private readonly IModuleCertificateStore _certificateStore;
    private readonly IModuleCertificateIssuer _certificateIssuer;
    private readonly ModuleRegistryOptions _registryOptions;
    private readonly ModuleChannelOptions _channelOptions;
    private readonly ILogger<ModuleRegistryOperations> _logger;

    public ModuleRegistryOperations(
        InMemoryModuleRegistry registry,
        IAuthModuleCatalog authCatalog,
        ISourceModuleCatalog sourceCatalog,
        IModuleCertificateStore certificateStore,
        IModuleCertificateIssuer certificateIssuer,
        IOptions<ModuleRegistryOptions> registryOptions,
        IOptions<ModuleChannelOptions> channelOptions,
        ILogger<ModuleRegistryOperations> logger)
    {
        _registry = registry;
        _authCatalog = authCatalog;
        _sourceCatalog = sourceCatalog;
        _certificateStore = certificateStore;
        _certificateIssuer = certificateIssuer;
        _registryOptions = registryOptions.Value;
        _channelOptions = channelOptions.Value;
        _logger = logger;
    }

    /// <param name="presentedCertSlug">
    /// Slug derived from an inbound mTLS client cert, if any (optional on Register).
    /// </param>
    public RegisterResponse Register(RegisterRequest request, string? presentedCertSlug = null)
    {
        var slug = request.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slug is required."));
        }

        var kind = WellKnownModuleKinds.Normalize(request.Kind);
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "kind is required."));
        }

        if (!JoinSecretsConfiguration.Validate(_registryOptions.JoinSecrets, slug, request.JoinSecret ?? string.Empty))
        {
            _logger.LogWarning("Register denied for slug {Slug}: join secret mismatch", slug);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid join secret."));
        }

        if (string.IsNullOrWhiteSpace(request.GrpcAdvertiseAddress))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "grpc_advertise_address is required."));
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_registryOptions.HeartbeatTtl);
        var response = new RegisterResponse
        {
            CaThumbprint = _certificateStore.IsLoaded ? _certificateStore.CaThumbprint : string.Empty,
        };

        switch (_channelOptions.BootstrapMode)
        {
            case ModuleChannelBootstrapMode.Auto:
            {
                var issued = _certificateIssuer.IssueClientCertificate(slug);
                response.ClientCertificatePem = issued.ClientCertificatePem;
                response.ClientPrivateKeyPem = issued.ClientPrivateKeyPem;
                response.CaCertificatePem = issued.CaCertificatePem;
                response.CaThumbprint = issued.CaThumbprint;
                response.CertificateExpiresUnix = issued.ExpiresAt.ToUnixTimeSeconds();
                break;
            }
            case ModuleChannelBootstrapMode.Preshared:
            {
                if (!_certificateStore.TryGetPresharedClientExpiry(slug, out var expires))
                {
                    _logger.LogWarning(
                        "Register denied for slug {Slug}: preshared client cert material missing",
                        slug);
                    throw new RpcException(new Status(
                        StatusCode.FailedPrecondition,
                        "Preshared client certificate material is missing for this slug."));
                }

                if (!string.IsNullOrEmpty(presentedCertSlug)
                    && !string.Equals(presentedCertSlug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(new Status(
                        StatusCode.PermissionDenied,
                        "Presented client certificate slug does not match Register slug."));
                }

                response.ClientCertificatePem = string.Empty;
                response.ClientPrivateKeyPem = string.Empty;
                response.CaCertificatePem = _certificateStore.CaCertificatePem;
                response.CaThumbprint = _certificateStore.CaThumbprint;
                response.CertificateExpiresUnix = expires.ToUnixTimeSeconds();
                break;
            }
            default:
                throw new RpcException(new Status(StatusCode.Internal, "Unknown mTLS bootstrap mode."));
        }

        var capabilities = request.Capabilities.ToArray();
        string? clientAuthMode = null;
        IReadOnlyList<string> permissionCeiling = [];
        if (string.Equals(kind, WellKnownModuleKinds.Client, StringComparison.OrdinalIgnoreCase)
            && request.Client is not null)
        {
            clientAuthMode = string.IsNullOrWhiteSpace(request.Client.AuthMode)
                ? null
                : request.Client.AuthMode.Trim().ToLowerInvariant();
            if (string.Equals(clientAuthMode, "static", StringComparison.Ordinal)
                && request.Client.PermissionCeiling.Count > 0)
            {
                permissionCeiling = request.Client.PermissionCeiling
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        _registry.Upsert(new ModuleRegistrationRecord
        {
            Slug = slug,
            Kind = kind,
            GrpcAdvertiseAddress = request.GrpcAdvertiseAddress,
            Capabilities = capabilities,
            RegisteredAt = now,
            LastHeartbeatAt = now,
            ExpiresAt = expiresAt,
            ClientAuthMode = clientAuthMode,
            PermissionCeiling = permissionCeiling,
        });

        ProjectToOrchestratorCatalogs(request, slug, kind, capabilities, now, expiresAt);

        _logger.LogInformation(
            "Module {Slug} ({Kind}) registered; bootstrap={Bootstrap}; well_known={WellKnown}",
            slug,
            kind,
            _channelOptions.BootstrapMode,
            WellKnownModuleKinds.IsWellKnown(kind));

        return response;
    }

    public HeartbeatResponse Heartbeat(HeartbeatRequest request, string? presentedCertSlug)
    {
        var slug = request.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "slug is required."));
        }

        if (_channelOptions.UseMtls)
        {
            if (string.IsNullOrEmpty(presentedCertSlug)
                || !string.Equals(presentedCertSlug, slug, StringComparison.OrdinalIgnoreCase))
            {
                throw new RpcException(new Status(
                    StatusCode.Unauthenticated,
                    "Heartbeat requires an mTLS client certificate matching the slug."));
            }
        }

        if (!_registry.TryGet(slug, out _))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Module '{slug}' is not registered."));
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_registryOptions.HeartbeatTtl);
        _registry.TouchHeartbeat(slug, now, expiresAt);
        TouchAuthCatalog(slug, now, expiresAt);
        TouchSourceCatalog(slug, now, expiresAt);

        return new HeartbeatResponse
        {
            Ok = true,
            NextHeartbeatAfterSeconds = _registryOptions.NextHeartbeatAfterSeconds,
        };
    }

    private void TouchAuthCatalog(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt)
    {
        if (_authCatalog.TryGet(slug, out _))
        {
            _authCatalog.TouchHeartbeat(slug, lastHeartbeatAt, expiresAt);
        }
    }

    private void TouchSourceCatalog(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt)
    {
        if (_sourceCatalog.TryGet(slug, out _))
        {
            _sourceCatalog.TouchHeartbeat(slug, lastHeartbeatAt, expiresAt);
        }
    }

    private void ProjectToOrchestratorCatalogs(
        RegisterRequest request,
        string slug,
        string kind,
        IReadOnlyList<string> capabilities,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        switch (kind)
        {
            case WellKnownModuleKinds.Auth:
                _authCatalog.Upsert(new AuthModuleRegistration
                {
                    Slug = slug,
                    GrpcAdvertiseAddress = request.GrpcAdvertiseAddress,
                    Capabilities = capabilities,
                    JwksUri = request.Auth?.JwksUri,
                    JwksJson = request.Auth?.JwksJson,
                    RegisteredAt = now,
                    LastHeartbeatAt = now,
                    ExpiresAt = expiresAt,
                });
                break;

            case WellKnownModuleKinds.Source:
                var fields = request.Source?.SearchFields
                    .Select(f => new Bardie.Orchestrator.Source.Catalog.SearchFieldDescriptor
                    {
                        Name = f.Name,
                        Required = f.Required,
                    })
                    .ToArray()
                    ?? [];
                _sourceCatalog.Upsert(new SourceModuleRegistration
                {
                    Slug = slug,
                    GrpcAdvertiseAddress = request.GrpcAdvertiseAddress,
                    Capabilities = capabilities,
                    SearchFields = fields,
                    RegisteredAt = now,
                    LastHeartbeatAt = now,
                    ExpiresAt = expiresAt,
                });
                break;

            case WellKnownModuleKinds.Client:
                // Ceiling lives on ModuleRegistrationRecord (Upsert above); no orch catalog for clients.
                break;

            default:
                _logger.LogInformation(
                    "Module {Slug} registered with host-unknown kind {Kind}; registry only (no orch catalog)",
                    slug,
                    kind);
                break;
        }
    }
}
