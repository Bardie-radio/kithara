using System.Collections.Concurrent;

namespace Bardie.Harness.Source.Catalog;

public interface ISourceModuleCatalog
{
    void Upsert(SourceModuleRegistration registration);
    bool TryGet(string slug, out SourceModuleRegistration? registration);
    IReadOnlyCollection<SourceModuleRegistration> List();
    bool Remove(string slug);
    void TouchHeartbeat(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt);
    void RemoveExpired(DateTimeOffset utcNow);
}

public sealed class SourceModuleCatalog : ISourceModuleCatalog
{
    private readonly ConcurrentDictionary<string, SourceModuleRegistration> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(SourceModuleRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _modules[registration.Slug] = registration;
    }

    public bool TryGet(string slug, out SourceModuleRegistration? registration) =>
        _modules.TryGetValue(slug, out registration);

    public IReadOnlyCollection<SourceModuleRegistration> List() => _modules.Values.ToArray();

    public bool Remove(string slug) => _modules.TryRemove(slug, out _);

    public void TouchHeartbeat(string slug, DateTimeOffset lastHeartbeatAt, DateTimeOffset expiresAt)
    {
        _modules.AddOrUpdate(
            slug,
            _ => throw new KeyNotFoundException($"Source module '{slug}' is not registered."),
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
