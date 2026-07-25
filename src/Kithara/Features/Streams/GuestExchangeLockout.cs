using System.Collections.Concurrent;

namespace Kithara.Features.Streams;

/// <summary>
/// GUEST-XCHG-002 — after N consecutive failures per IP+Struna, lock exchange for a cooldown.
/// Complements the fixed-window rate limit (GUEST-XCHG-001); does not replace it.
/// </summary>
public sealed class GuestExchangeLockout
{
    public const int MaxConsecutiveFailures = 5;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>True when the partition is currently locked out.</summary>
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

        // Cooldown elapsed — clear lock (failures reset on next success/failure bookkeeping).
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

    /// <summary>Builds the lockout partition key (IP + Struna id).</summary>
    public static string PartitionKey(string? clientIp, Guid strunaId) =>
        $"{clientIp ?? "unknown"}:{strunaId:D}";

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public long LockedUntilUtcTicks;
    }
}
