using Bardie.Harness.Auth.Ports;
using Kithara.Features.Streams;
using Kithara.Infrastructure.Persistence.Entities;
using Xunit;

namespace Kithara.Tests;

public class StrunaAccessTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StrunaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Theory]
    [InlineData(PlaybackAccess.Public, true)]
    [InlineData(PlaybackAccess.Hidden, true)]
    [InlineData(PlaybackAccess.Protected, false)]
    [InlineData(PlaybackAccess.Private, false)]
    public void IsOpenPlayback_matches_public_and_hidden(PlaybackAccess access, bool expected) =>
        Assert.Equal(expected, StrunaAccess.IsOpenPlayback(access));

    [Fact]
    public void CanListen_public_and_hidden_are_true_for_any_principal()
    {
        Assert.True(StrunaAccess.CanListen(MakeStruna(PlaybackAccess.Public), Principal(OtherId)));
        Assert.True(StrunaAccess.CanListen(MakeStruna(PlaybackAccess.Hidden), Principal(OtherId)));
    }

    [Fact]
    public void AppearsOnListenList_hidden_omits_unrelated_users()
    {
        var struna = MakeStruna(PlaybackAccess.Hidden);
        Assert.False(StrunaAccess.AppearsOnListenList(struna, Principal(OtherId)));
        Assert.True(StrunaAccess.AppearsOnListenList(struna, Principal(OwnerId)));
        Assert.True(StrunaAccess.AppearsOnListenList(MakeStruna(PlaybackAccess.Public), Principal(OtherId)));
    }

    [Fact]
    public void AppearsOnListenList_hidden_includes_control_grantee_and_guest()
    {
        var struna = MakeStruna(PlaybackAccess.Hidden, ControlAccess.Protected);
        struna.ControlGrants.Add(new StrunaControlGrant { StrunaId = StrunaId, UserId = OtherId });

        Assert.True(StrunaAccess.AppearsOnListenList(struna, Principal(OtherId)));

        var guest = new AuthUserRecord(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            nameof(UserKind.EphemeralGuest),
            "Active",
            MustRotateCredentials: false,
            GuestStrunaId: StrunaId);
        Assert.True(StrunaAccess.AppearsOnListenList(struna, guest));
    }

    private static Struna MakeStruna(
        PlaybackAccess playback,
        ControlAccess control = ControlAccess.Private) =>
        new()
        {
            Id = StrunaId,
            Slug = "party",
            Title = "Party",
            PlaybackAccess = playback,
            ControlAccess = control,
            OwnerUserId = OwnerId,
        };

    private static AuthUserRecord Principal(Guid userId) =>
        new(userId, nameof(UserKind.Durable), "Active", MustRotateCredentials: false);
}
