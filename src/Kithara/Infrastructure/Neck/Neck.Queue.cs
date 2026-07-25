using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Infrastructure.Neck;

public sealed partial class Neck
{
    public async Task<IReadOnlyList<QueueEntry>> ListQueueAsync(
        Guid strunaId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.QueueEntries.AsNoTracking()
            .Include(e => e.Tune)
            .Where(e => e.StrunaId == strunaId)
            .OrderBy(e => e.Position)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(QueueEntry? Entry, string? Error)> EnqueueTuneAsync(
        Guid strunaId,
        Guid tuneId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Strunas.AnyAsync(s => s.Id == strunaId, cancellationToken).ConfigureAwait(false))
        {
            return (null, "struna_not_found");
        }

        if (!await db.Tunes.AnyAsync(t => t.Id == tuneId, cancellationToken).ConfigureAwait(false))
        {
            return (null, "tune_not_found");
        }

        var maxPos = await db.QueueEntries
            .Where(e => e.StrunaId == strunaId)
            .Select(e => (int?)e.Position)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? -1;

        var entry = new QueueEntry
        {
            Id = Guid.NewGuid(),
            StrunaId = strunaId,
            TuneId = tuneId,
            Position = maxPos + 1,
        };
        db.QueueEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.Entry(entry).Reference(e => e.Tune).LoadAsync(cancellationToken).ConfigureAwait(false);

        // Warm Magpie blob cache as soon as the tune is queued (not only at StartTrack).
        var tune = entry.Tune;
        var prefetchRef = string.IsNullOrWhiteSpace(tune.ExternalId)
            ? tune.Id.ToString("D")
            : tune.ExternalId;
        var moduleSlug = tune.ModuleSlug;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await _orch.PrefetchTrackAsync(moduleSlug, prefetchRef, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        _logger.LogDebug(
                            "Prefetch for queued tune {TuneId} on {Module}: {Reason}",
                            tune.Id,
                            moduleSlug,
                            result.FailureReason);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Prefetch failed for queued tune {TuneId}", tune.Id);
                }
            },
            CancellationToken.None);

        return (entry, null);
    }

    public async Task<bool> RemoveQueueEntryAsync(
        Guid strunaId,
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.QueueEntries
            .FirstOrDefaultAsync(e => e.Id == entryId && e.StrunaId == strunaId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return false;
        }

        db.QueueEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<PlayTrackOutcome> AdvanceQueueHeadCoreAsync(
        Guid strunaId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var next = await db.QueueEntries
            .Include(e => e.Tune)
            .Where(e => e.StrunaId == strunaId)
            .OrderBy(e => e.Position)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (next is null)
        {
            // Magpie Ended means session FIFO write finished, not that listeners have drained
            // MP3 yet — keep the last now-playing snapshot until skip-to-empty / play / delete.
            return new PlayTrackOutcome(true, null, null, null);
        }

        db.QueueEntries.Remove(next);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var trackRef = string.IsNullOrWhiteSpace(next.Tune.ExternalId)
            ? next.Tune.Id.ToString("D")
            : next.Tune.ExternalId;
        var outcome = await PlayTrackCoreAsync(strunaId, next.Tune.ModuleSlug, trackRef, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Ok
            && (!string.IsNullOrWhiteSpace(next.Tune.Title)
                || !string.IsNullOrWhiteSpace(next.Tune.Artist)
                || !string.IsNullOrWhiteSpace(next.Tune.ArtworkUrl)))
        {
            if (_nowPlaying.TryGetValue(strunaId, out var snap))
            {
                SetNowPlaying(
                    strunaId,
                    snap with
                    {
                        Title = next.Tune.Title,
                        Artist = next.Tune.Artist,
                        ArtworkUrl = next.Tune.ArtworkUrl,
                    });
            }
        }

        return outcome;
    }
}
