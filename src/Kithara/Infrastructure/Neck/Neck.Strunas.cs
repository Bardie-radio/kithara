using System.Diagnostics;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Neck;

public sealed partial class Neck
{
    public async Task<IReadOnlyList<Struna>> ListStrunasAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Strunas.AsNoTracking()
            .Include(s => s.ControlGrants)
            .OrderBy(s => s.Slug)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Struna?> GetStrunaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Strunas.AsNoTracking()
            .Include(s => s.ControlGrants)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Struna?> GetStrunaBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSlug(slug);
        if (normalized is null)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Strunas.AsNoTracking()
            .Include(s => s.ControlGrants)
            .FirstOrDefaultAsync(s => s.Slug == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an encode-alive Struna: persist row + session FIFO + silence + FFmpeg.
    /// </summary>
    public async Task<CreateStrunaOutcome> CreateStrunaAsync(
        Guid ownerUserId,
        string slug,
        string? title,
        PlaybackAccess playback,
        ControlAccess control,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSlug(slug);
        if (normalized is null)
        {
            return new CreateStrunaOutcome(null, CreateStrunaError.InvalidSlug);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Strunas.AnyAsync(s => s.Slug == normalized, cancellationToken).ConfigureAwait(false))
        {
            return new CreateStrunaOutcome(null, CreateStrunaError.SlugConflict);
        }

        var struna = new Struna
        {
            Id = Guid.NewGuid(),
            Slug = normalized,
            Title = string.IsNullOrWhiteSpace(title) ? normalized : title.Trim(),
            PlaybackAccess = playback,
            ControlAccess = control,
            OwnerUserId = ownerUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            GuestCode = CreateSecret(6),
            ListenToken = playback == PlaybackAccess.Protected ? CreateSecret(24) : null,
        };

        db.Strunas.Add(struna);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return new CreateStrunaOutcome(null, CreateStrunaError.SlugConflict);
        }

        using var activity = NeckActivity.Source.StartActivity("neck.struna.create");
        activity?.SetTag("struna.id", struna.Id.ToString("D"));
        activity?.SetTag("struna.slug", struna.Slug);
        activity?.SetTag("playback.access", struna.PlaybackAccess.ToString().ToLowerInvariant());
        activity?.SetTag("control.access", struna.ControlAccess.ToString().ToLowerInvariant());

        var fifo = await EnsureStrunaFifoAsync(struna.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            await _encoder.StartAsync(struna.Id, struna.Slug, fifo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start encoder for Struna {Id}; rolling back", struna.Id);
            await RemoveStrunaFifoAsync(struna.Id, cancellationToken).ConfigureAwait(false);
            db.Strunas.Remove(struna);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        return new CreateStrunaOutcome(struna, null);
    }

    /// <summary>
    /// After host restart: DB Strunas are still "alive" but encoders/FIFOs are not.
    /// Recreate session FIFO + silence + FFmpeg for each row so <c>/stream</c> and play work again.
    /// </summary>
    public async Task RehydrateAliveStrunasAsync(CancellationToken cancellationToken = default)
    {
        var strunas = await ListStrunasAsync(cancellationToken).ConfigureAwait(false);
        if (strunas.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Rehydrating encode-alive for {Count} Struna(s)", strunas.Count);
        foreach (var struna in strunas)
        {
            try
            {
                await EnsureEncodeAliveAsync(struna.Id, struna.Slug, recreateFifo: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to rehydrate encode session for Struna {Id} ({Slug})",
                    struna.Id,
                    struna.Slug);
            }
        }
    }

    /// <summary>
    /// Ensures silence + FFmpeg are running for an alive Struna (idempotent when already started).
    /// </summary>
    public async Task EnsureEncodeAliveAsync(
        Guid strunaId,
        string slug,
        bool recreateFifo = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        if (_encoder.TryGetSession(strunaId, out _))
        {
            return;
        }

        if (recreateFifo)
        {
            // Prior process left a FIFO node with no peers — recreate so RDWR attach is clean.
            await RemoveStrunaFifoAsync(strunaId, cancellationToken).ConfigureAwait(false);
        }

        var fifo = await EnsureStrunaFifoAsync(strunaId, cancellationToken).ConfigureAwait(false);
        await _encoder.StartAsync(strunaId, slug, fifo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down a Struna: StopTrack → silence/FFmpeg stop → remove FIFO → destroy guests → free slug.
    /// Returns destroyed guest user ids so Search can clear their result <b>cache</b> (not search history).
    /// </summary>
    public async Task<DeleteStrunaOutcome> DeleteStrunaAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var struna = await db.Strunas.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (struna is null)
        {
            return new DeleteStrunaOutcome(false, []);
        }

        using var activity = NeckActivity.Source.StartActivity("neck.struna.delete");
        activity?.SetTag("struna.id", id.ToString("D"));
        activity?.SetTag("struna.slug", struna.Slug);

        await StopCurrentTrackAsync(id, cancellationToken).ConfigureAwait(false);
        await StopOrphanWritersAsync(id, preferredModule: null, cancellationToken).ConfigureAwait(false);
        ClearNowPlaying(id);
        await _encoder.StopAsync(id, cancellationToken).ConfigureAwait(false);
        await RemoveStrunaFifoAsync(id, cancellationToken).ConfigureAwait(false);

        var guests = await db.Users
            .Where(u => u.GuestStrunaId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var guestIdList = guests.Select(u => u.Id).ToArray();
        if (guests.Count > 0)
        {
            db.Users.RemoveRange(guests);
        }

        db.Strunas.Remove(struna);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DeleteStrunaOutcome(true, guestIdList);
    }

    private static string? NormalizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var trimmed = slug.Trim().ToLowerInvariant();
        if (trimmed.Length is < 1 or > 64)
        {
            return null;
        }

        foreach (var ch in trimmed)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            {
                continue;
            }

            return null;
        }

        return trimmed;
    }
}
