using Kithara.Infrastructure.Neck;
using Xunit;

namespace Kithara.Tests;

public class NowPlayingInfoTests
{
    [Fact]
    public void StreamTitle_prefers_artist_and_title_never_track_ref()
    {
        Assert.Equal(
            "Rick - Never",
            new NowPlayingInfo("magpie", "dQw4w9wgXcQ", "job", "Never", "Rick", false).StreamTitle);
        Assert.Equal(
            "Never",
            new NowPlayingInfo("magpie", "dQw4w9wgXcQ", "job", "Never", null, false).StreamTitle);
        Assert.Equal(
            string.Empty,
            new NowPlayingInfo("magpie", "dQw4w9wgXcQ", "job", null, null, false).StreamTitle);
    }
}
