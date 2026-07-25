namespace Kithara.Infrastructure.Persistence.Entities;

public enum UserKind
{
    Durable = 0,
    Managed = 1,
    EphemeralGuest = 2,
}

public sealed class User
{
    public Guid Id { get; set; }
    public UserKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "active";
    public string? ManagedByModuleSlug { get; set; }
    public Guid? GuestStrunaId { get; set; }
    public bool MustRotateCredentials { get; set; }

    /// <summary>
    /// Immutable unique Kithara login id for durable users (AUTH-INVITE / AUTH-DISP).
    /// Set once at bootstrap or admin <c>/register</c>; never part of module <c>bind_form</c>.
    /// Optional display name (mutable host profile field) is backlog — not this column.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>Host-hashed one-time registration OTP; null after claim.</summary>
    public string? InvitePasswordHash { get; set; }

    /// <summary>True until the invitee completes first <c>UpdateUserBinding</c> bind.</summary>
    public bool MustCompleteBinding { get; set; }

    /// <summary>
    /// Host-intended roles for the first bind (e.g. <c>admin</c> for bootstrap).
    /// Merged into the binding payload on claim bind; cleared afterward.
    /// </summary>
    public string? InviteRolesJson { get; set; }

    public ICollection<UserAuthBinding> AuthBindings { get; set; } = new List<UserAuthBinding>();
}

public sealed class UserAuthBinding
{
    public Guid UserId { get; set; }
    public string ProviderSlug { get; set; } = string.Empty;
    public string? ExternalSubject { get; set; }
    public string PayloadJson { get; set; } = "{}";

    public User User { get; set; } = null!;
}
