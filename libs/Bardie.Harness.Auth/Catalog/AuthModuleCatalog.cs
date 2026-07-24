using System.Collections.Concurrent;

namespace Bardie.Harness.Auth.Catalog;

public interface IAuthModuleCatalog
{
    void Upsert(AuthModuleRegistration registration);
    bool TryGet(string slug, out AuthModuleRegistration? registration);
    IReadOnlyCollection<AuthModuleRegistration> List();
    bool Remove(string slug);
    void TouchHeartbeat(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt);
    void RemoveExpired(DateTimeOffset utcNow);
}

public sealed class AuthModuleCatalog : IAuthModuleCatalog
{
    private readonly ConcurrentDictionary<string, AuthModuleRegistration> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(AuthModuleRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _modules[registration.Slug] = registration;
    }

    public bool TryGet(string slug, out AuthModuleRegistration? registration) =>
        _modules.TryGetValue(slug, out registration);

    public IReadOnlyCollection<AuthModuleRegistration> List() => _modules.Values.ToArray();

    public bool Remove(string slug) => _modules.TryRemove(slug, out _);

    public void TouchHeartbeat(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt)
    {
        _modules.AddOrUpdate(
            slug,
            _ => throw new KeyNotFoundException($"Auth module '{slug}' is not registered."),
            (_, existing) => existing with
            {
                LastHeartbeatAt = lastHeartbeatAt,
                ExpiresAt = expiresAt,
            });
    }

    public void RemoveExpired(DateTimeOffset utcNow)
    {
        foreach (var pair in _modules)
        {
            if (pair.Value.ExpiresAt <= utcNow)
            {
                _modules.TryRemove(pair.Key, out _);
            }
        }
    }
}
