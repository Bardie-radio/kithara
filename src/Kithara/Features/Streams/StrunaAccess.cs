using Bardie.Harness.Auth.Ports;
using Kithara.Infrastructure.Persistence.Entities;

namespace Kithara.Features.Streams;

/// <summary>
/// Struna listen / control ACL helpers.
/// Owner + grant checks; managed ceiling + grant CRUD on Phase 6.
/// </summary>
public static class StrunaAccess
{
    /// <summary>
    /// True when playback needs no listen token and no Bearer on <c>/stream/{slug}</c>
    /// (URL is enough — includes <see cref="PlaybackAccess.Hidden"/>).
    /// </summary>
    public static bool IsOpenPlayback(PlaybackAccess access) =>
        access is PlaybackAccess.Public or PlaybackAccess.Hidden;

    /// <summary>
    /// True when the principal may DJ this Struna (play / queue / skip / pause).
    /// Tear-down (<c>DELETE</c>) is owner-only.
    /// </summary>
    public static bool CanControl(Struna struna, AuthUserRecord principal)
    {
        if (struna.OwnerUserId == principal.UserId)
        {
            return true;
        }

        if (struna.ControlGrants.Any(g => g.UserId == principal.UserId))
        {
            return true;
        }

        if (string.Equals(principal.Kind, nameof(UserKind.EphemeralGuest), StringComparison.OrdinalIgnoreCase)
            && principal.GuestStrunaId == struna.Id
            && struna.ControlAccess == ControlAccess.Protected)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the principal may listen / use GUID discover routes.
    /// Hidden is listenable like public (URL is enough); listing is gated separately.
    /// Actual <c>/stream/{slug}</c> gates (listen token) are Stream Server.
    /// </summary>
    public static bool CanListen(Struna struna, AuthUserRecord principal) =>
        struna.PlaybackAccess switch
        {
            PlaybackAccess.Public => true,
            PlaybackAccess.Hidden => true,
            PlaybackAccess.Protected =>
                // STREAM-ACL-001 — listen-token holders are stream-gated only today (not REST CanListen).
                struna.OwnerUserId == principal.UserId
                || struna.ControlGrants.Any(g => g.UserId == principal.UserId),
            PlaybackAccess.Private =>
                struna.OwnerUserId == principal.UserId
                || struna.ControlGrants.Any(g => g.UserId == principal.UserId),
            _ => false,
        };

    /// <summary>
    /// <c>GET /api/streams/listen</c> visibility. Hidden is omitted from the public browse list —
    /// only owner, grants, and guests who already control that Struna see it there.
    /// </summary>
    public static bool AppearsOnListenList(Struna struna, AuthUserRecord principal) =>
        struna.PlaybackAccess == PlaybackAccess.Hidden
            ? CanControl(struna, principal)
            : CanListen(struna, principal);
}
