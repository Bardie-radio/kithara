using Kithara.Features.Streaming;
using Xunit;

namespace Kithara.Tests;

public class ListenTokenComparerTests
{
    [Fact]
    public void FixedTimeEquals_matching_tokens_returns_true()
    {
        Assert.True(ListenTokenComparer.FixedTimeEquals("secret-token", "secret-token"));
    }

    [Fact]
    public void FixedTimeEquals_mismatch_or_empty_returns_false()
    {
        Assert.False(ListenTokenComparer.FixedTimeEquals("secret-token", "other-token"));
        Assert.False(ListenTokenComparer.FixedTimeEquals("short", "longer-token"));
        Assert.False(ListenTokenComparer.FixedTimeEquals(null, "secret"));
        Assert.False(ListenTokenComparer.FixedTimeEquals("secret", null));
        Assert.False(ListenTokenComparer.FixedTimeEquals("", "secret"));
        Assert.False(ListenTokenComparer.FixedTimeEquals("secret", ""));
        Assert.False(ListenTokenComparer.FixedTimeEquals(null, null));
    }
}
