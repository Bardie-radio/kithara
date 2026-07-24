using Kithara.Infrastructure.Neck;
using Xunit;

namespace Kithara.Tests;

public class EncodedAudioFanoutTests
{
    [Fact]
    public async Task Publish_reaches_subscriber_then_Complete_ends_stream()
    {
        var fanout = new EncodedAudioFanout();
        var payload = new byte[] { 1, 2, 3, 4 };

        await using var subscription = fanout.SubscribeAsync().GetAsyncEnumerator();
        var move = subscription.MoveNextAsync().AsTask();

        fanout.Publish(payload);
        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(payload, subscription.Current.ToArray());

        fanout.Complete();
        Assert.False(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Publish_after_subscriber_joins_late_still_delivers()
    {
        var fanout = new EncodedAudioFanout();
        fanout.Publish(new byte[] { 9 });

        await using var subscription = fanout.SubscribeAsync().GetAsyncEnumerator();
        var move = subscription.MoveNextAsync().AsTask();
        fanout.Publish(new byte[] { 1, 2, 3 });
        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 1, 2, 3 }, subscription.Current.ToArray());
        fanout.Complete();
    }
}
