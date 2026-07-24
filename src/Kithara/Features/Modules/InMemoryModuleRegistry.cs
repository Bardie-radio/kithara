using System.Collections.Concurrent;

namespace Kithara.Features.Modules;

public sealed class ModuleRegistrationRecord
{
    public required string Slug { get; init; }
    /// <summary>Open kind string (see <see cref="WellKnownModuleKinds"/> for Bardie conventions).</summary>
    public required string Kind { get; init; }
    public required string GrpcAdvertiseAddress { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Client modules only: <c>user-aware</c> | <c>static</c>.</summary>
    public string? ClientAuthMode { get; init; }

    /// <summary>Static client modules: max rights for managed users (e.g. <c>create_struna</c>).</summary>
    public IReadOnlyList<string> PermissionCeiling { get; init; } = [];
}

/// <summary>In-memory Module Registry with heartbeat TTL (Phase 1).</summary>
public sealed class InMemoryModuleRegistry
{
    private readonly ConcurrentDictionary<string, ModuleRegistrationRecord> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(ModuleRegistrationRecord record) => _modules[record.Slug] = record;

    public bool TryGet(string slug, out ModuleRegistrationRecord? record) =>
        _modules.TryGetValue(slug, out record);

    public IReadOnlyCollection<ModuleRegistrationRecord> List() => _modules.Values.ToArray();

    public bool Remove(string slug) => _modules.TryRemove(slug, out _);

    public void TouchHeartbeat(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt)
    {
        if (!_modules.TryGetValue(slug, out var existing))
        {
            throw new KeyNotFoundException($"Module '{slug}' is not registered.");
        }

        _modules[slug] = new ModuleRegistrationRecord
        {
            Slug = existing.Slug,
            Kind = existing.Kind,
            GrpcAdvertiseAddress = existing.GrpcAdvertiseAddress,
            Capabilities = existing.Capabilities,
            RegisteredAt = existing.RegisteredAt,
            LastHeartbeatAt = lastHeartbeatAt,
            ExpiresAt = expiresAt,
            ClientAuthMode = existing.ClientAuthMode,
            PermissionCeiling = existing.PermissionCeiling,
        };
    }

    public IReadOnlyList<ModuleRegistrationRecord> RemoveExpired(DateTimeOffset utcNow)
    {
        var removed = new List<ModuleRegistrationRecord>();
        foreach (var pair in _modules)
        {
            if (pair.Value.ExpiresAt <= utcNow && _modules.TryRemove(pair.Key, out var record))
            {
                removed.Add(record);
            }
        }

        return removed;
    }
}
