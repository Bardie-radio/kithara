using Kithara.Features.Streams;
using Xunit;

namespace Kithara.Tests;

public class GuestExchangeLockoutTests
{
    [Fact]
    public void Locks_after_max_consecutive_failures_and_clears_on_success()
    {
        var lockout = new GuestExchangeLockout();
        var key = GuestExchangeLockout.PartitionKey("127.0.0.1", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        for (var i = 0; i < GuestExchangeLockout.MaxConsecutiveFailures - 1; i++)
        {
            lockout.RecordFailure(key);
            Assert.False(lockout.IsLocked(key, out _));
        }

        lockout.RecordFailure(key);
        Assert.True(lockout.IsLocked(key, out var until));
        Assert.NotNull(until);
        Assert.True(until > DateTimeOffset.UtcNow);

        lockout.RecordSuccess(key);
        Assert.False(lockout.IsLocked(key, out _));
    }

    [Fact]
    public void Success_resets_failure_streak_before_lock()
    {
        var lockout = new GuestExchangeLockout();
        var key = GuestExchangeLockout.PartitionKey("10.0.0.1", Guid.NewGuid());

        for (var i = 0; i < GuestExchangeLockout.MaxConsecutiveFailures - 1; i++)
        {
            lockout.RecordFailure(key);
        }

        lockout.RecordSuccess(key);

        for (var i = 0; i < GuestExchangeLockout.MaxConsecutiveFailures - 1; i++)
        {
            lockout.RecordFailure(key);
            Assert.False(lockout.IsLocked(key, out _));
        }
    }
}
