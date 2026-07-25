using System.Collections.Concurrent;

namespace Kithara.Features.Auth;

/// <summary>
/// AUTH-INVITE claim lockout — same class as GUEST-XCHG-002 (N failures → cooldown per IP+username).
/// </summary>
public sealed class InviteClaimLockout
{
    public const int MaxConsecutiveFailures = 5;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsLocked(string partitionKey, out DateTimeOffset? lockedUntilUtc)
    {
        lockedUntilUtc = null;
        if (string.IsNullOrWhiteSpace(partitionKey))
        {
            return false;
        }

        if (!_entries.TryGetValue(partitionKey, out var entry))
        {
            return false;
        }

        var until = Volatile.Read(ref entry.LockedUntilUtcTicks);
        if (until <= 0)
        {
            return false;
        }

        var lockedUntil = new DateTimeOffset(until, TimeSpan.Zero);
        if (lockedUntil > DateTimeOffset.UtcNow)
        {
            lockedUntilUtc = lockedUntil;
            return true;
        }

        Interlocked.Exchange(ref entry.LockedUntilUtcTicks, 0);
        Interlocked.Exchange(ref entry.ConsecutiveFailures, 0);
        return false;
    }

    public void RecordFailure(string partitionKey)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
        {
            return;
        }

        var entry = _entries.GetOrAdd(partitionKey, static _ => new Entry());
        var failures = Interlocked.Increment(ref entry.ConsecutiveFailures);
        if (failures < MaxConsecutiveFailures)
        {
            return;
        }

        var until = DateTimeOffset.UtcNow.Add(Cooldown).UtcTicks;
        Interlocked.Exchange(ref entry.LockedUntilUtcTicks, until);
    }

    public void RecordSuccess(string partitionKey)
    {
        if (string.IsNullOrWhiteSpace(partitionKey))
        {
            return;
        }

        if (_entries.TryGetValue(partitionKey, out var entry))
        {
            Interlocked.Exchange(ref entry.ConsecutiveFailures, 0);
            Interlocked.Exchange(ref entry.LockedUntilUtcTicks, 0);
        }
    }

    public static string PartitionKey(string? clientIp, string username) =>
        $"{clientIp ?? "unknown"}:{username.Trim().ToLowerInvariant()}";

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public long LockedUntilUtcTicks;
    }
}
