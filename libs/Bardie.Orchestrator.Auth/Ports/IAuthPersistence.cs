namespace Bardie.Orchestrator.Auth.Ports;

/// <summary>
/// Host persistence port for auth-orchestrator user/binding storage.
/// </summary>
public interface IAuthPersistence
{
    Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default);

    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Looks up a binding by provider slug + external subject (e.g. username).</summary>
    Task<AuthBindingRecord?> FindBindingBySubjectAsync(
        string providerSlug,
        string externalSubject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a durable user + binding. Returns the user id.
    /// </summary>
    Task<Guid> EnsureUserWithBindingAsync(
        EnsureUserBindingRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a durable/managed user id for a verified login subject.</summary>
    Task<AuthUserRecord?> FindUserByBindingSubjectAsync(
        string providerSlug,
        string externalSubject,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves any user row by id (login subjects and ephemeral guests).</summary>
    Task<AuthUserRecord?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record AuthBindingRecord(
    Guid UserId,
    string ProviderSlug,
    string ExternalSubject,
    string PayloadJson,
    bool MustRotateCredentials);

public sealed record AuthUserRecord(
    Guid UserId,
    string Kind,
    string Status,
    bool MustRotateCredentials,
    Guid? GuestStrunaId = null,
    string? ManagedByModuleSlug = null);

public sealed record EnsureUserBindingRequest(
    string ProviderSlug,
    string ExternalSubject,
    string PayloadJson,
    bool MustRotateCredentials,
    IReadOnlyList<string>? Roles = null);
