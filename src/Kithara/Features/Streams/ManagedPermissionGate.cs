using Bardie.Orchestrator.Auth.Ports;
using Kithara.Features.Modules;
using Kithara.Infrastructure.Persistence.Entities;

namespace Kithara.Features.Streams;

/// <summary>Permission strings advertised on static-client Register <c>permission_ceiling</c>.</summary>
public static class ManagedPermissions
{
    public const string CreateStruna = "create_struna";
    public const string ManageGrants = "manage_grants";
}

/// <summary>
/// Enforces static-client permission ceilings for managed users (Phase 6).
/// Durable / user-aware principals are unconstrained.
/// </summary>
public sealed class ManagedPermissionGate
{
    private readonly InMemoryModuleRegistry _registry;

    public ManagedPermissionGate(InMemoryModuleRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Returns <c>null</c> when allowed; otherwise an error code for the REST body.
    /// </summary>
    public string? DenyReason(AuthUserRecord principal, string permission)
    {
        if (!string.Equals(principal.Kind, nameof(UserKind.Managed), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var moduleSlug = principal.ManagedByModuleSlug;
        if (string.IsNullOrWhiteSpace(moduleSlug))
        {
            // Managed row without a managing module — cannot evaluate ceiling; deny.
            return "permission_ceiling_denied";
        }

        if (!_registry.TryGet(moduleSlug, out var module) || module is null)
        {
            return "permission_ceiling_denied";
        }

        if (!string.Equals(module.ClientAuthMode, "static", StringComparison.OrdinalIgnoreCase))
        {
            // User-aware clients are unconstrained.
            return null;
        }

        if (module.PermissionCeiling.Any(p =>
                string.Equals(p, permission, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return "permission_ceiling_denied";
    }
}
