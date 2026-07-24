using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Neck;

public sealed partial class Neck
{
    public async Task<IReadOnlyList<StrunaControlGrant>> ListGrantsAsync(
        Guid strunaId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.StrunaControlGrants.AsNoTracking()
            .Where(g => g.StrunaId == strunaId)
            .OrderBy(g => g.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(StrunaControlGrant? Grant, string? Error)> AddGrantAsync(
        Guid strunaId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var struna = await db.Strunas.FirstOrDefaultAsync(s => s.Id == strunaId, cancellationToken)
            .ConfigureAwait(false);
        if (struna is null)
        {
            return (null, "not_found");
        }

        if (struna.OwnerUserId == userId)
        {
            return (null, "owner_already_controls");
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return (null, "user_not_found");
        }

        if (user.Kind == UserKind.EphemeralGuest)
        {
            return (null, "guest_not_grantable");
        }

        var existing = await db.StrunaControlGrants
            .FirstOrDefaultAsync(g => g.StrunaId == strunaId && g.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, null);
        }

        var grant = new StrunaControlGrant { StrunaId = strunaId, UserId = userId };
        db.StrunaControlGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (grant, null);
    }

    public async Task<bool> RemoveGrantAsync(
        Guid strunaId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var grant = await db.StrunaControlGrants
            .FirstOrDefaultAsync(g => g.StrunaId == strunaId && g.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (grant is null)
        {
            return false;
        }

        db.StrunaControlGrants.Remove(grant);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
