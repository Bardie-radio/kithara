namespace Kithara.Infrastructure.Persistence.Entities;

public enum PlaybackAccess
{
    Public = 0,
    Protected = 1,
    Private = 2,
    /// <summary>Same stream gate as public; omitted from listen lists except owner / control / guest.</summary>
    Hidden = 3,
}

public enum ControlAccess
{
    Private = 0,
    Protected = 1,
}

public sealed class Struna
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public PlaybackAccess PlaybackAccess { get; set; } = PlaybackAccess.Public;
    public ControlAccess ControlAccess { get; set; } = ControlAccess.Private;
    public Guid OwnerUserId { get; set; }
    public string? ListenToken { get; set; }
    public string? GuestCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public ICollection<QueueEntry> QueueEntries { get; set; } = new List<QueueEntry>();
    public ICollection<StrunaControlGrant> ControlGrants { get; set; } = new List<StrunaControlGrant>();
}

public sealed class StrunaControlGrant
{
    public Guid StrunaId { get; set; }
    public Guid UserId { get; set; }

    public Struna Struna { get; set; } = null!;
    public User User { get; set; } = null!;
}
