using Bardie.Orchestrator.Auth.Ports;
using Kithara.Infrastructure.Persistence.Entities;

namespace Kithara.Features.Streams;

/// <summary>
/// Struna listen / control ACL helpers.
/// Owner + grant checks; managed ceiling + grant CRUD on Phase 6.
/// </summary>
public static class StrunaAccess
{
    /// <summary>
    /// True when the principal may DJ this Struna (play / queue / skip / pause / delete).
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
    /// True when the principal may discover this Struna as listenable via REST.
    /// Actual <c>/stream/{slug}</c> gates (listen token) are Stream Server.
    /// </summary>
    public static bool CanListen(Struna struna, Guid principalUserId) =>
        struna.PlaybackAccess switch
        {
            PlaybackAccess.Public => true,
            PlaybackAccess.Protected =>
                struna.OwnerUserId == principalUserId
                || struna.ControlGrants.Any(g => g.UserId == principalUserId),
            PlaybackAccess.Private =>
                struna.OwnerUserId == principalUserId
                || struna.ControlGrants.Any(g => g.UserId == principalUserId),
            _ => false,
        };
}
