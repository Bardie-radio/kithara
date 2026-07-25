using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Features.Library;

public sealed record EnsureTuneCommand(
    string ModuleSlug,
    string ExternalId,
    string? Title,
    string? Artist,
    double? DurationSeconds,
    string? ArtworkUrl,
    string? StorageKey,
    string? ContentType,
    long? SizeBytes);

public sealed record EnsureTuneResult(Guid TuneId, bool Created);

/// <summary>EF upsert for shared-library Tunes (module_slug + external_id).</summary>
public sealed class TuneLibrary
{
    private readonly IDbContextFactory<KitharaDbContext> _dbContextFactory;

    public TuneLibrary(IDbContextFactory<KitharaDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<EnsureTuneResult> EnsureTuneAsync(
        EnsureTuneCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ModuleSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExternalId);

        var moduleSlug = command.ModuleSlug.Trim().ToLowerInvariant();
        var externalId = command.ExternalId.Trim();

        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await db.Tunes
            .FirstOrDefaultAsync(
                t => t.ModuleSlug == moduleSlug && t.ExternalId == externalId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var created = new Tune
            {
                Id = Guid.NewGuid(),
                ModuleSlug = moduleSlug,
                ExternalId = externalId,
                Title = NullIfWhiteSpace(command.Title),
                Artist = NullIfWhiteSpace(command.Artist),
                DurationSeconds = command.DurationSeconds is > 0 ? command.DurationSeconds : null,
                ArtworkUrl = NullIfWhiteSpace(command.ArtworkUrl),
                StorageKey = NullIfWhiteSpace(command.StorageKey),
                ContentType = NullIfWhiteSpace(command.ContentType),
                SizeBytes = command.SizeBytes is > 0 ? command.SizeBytes : null,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Tunes.Add(created);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new EnsureTuneResult(created.Id, Created: true);
        }

        existing.Title = Coalesce(command.Title, existing.Title);
        existing.Artist = Coalesce(command.Artist, existing.Artist);
        if (command.DurationSeconds is > 0)
        {
            existing.DurationSeconds = command.DurationSeconds;
        }

        existing.ArtworkUrl = Coalesce(command.ArtworkUrl, existing.ArtworkUrl);
        existing.StorageKey = Coalesce(command.StorageKey, existing.StorageKey);
        existing.ContentType = Coalesce(command.ContentType, existing.ContentType);
        if (command.SizeBytes is > 0)
        {
            existing.SizeBytes = command.SizeBytes;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EnsureTuneResult(existing.Id, Created: false);
    }

    /// <summary>Lookup by module + external id (or raw track ref) for now-playing artwork enrichment.</summary>
    public async Task<Tune?> FindByModuleRefAsync(
        string moduleSlug,
        string trackRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleSlug) || string.IsNullOrWhiteSpace(trackRef))
        {
            return null;
        }

        var slug = moduleSlug.Trim().ToLowerInvariant();
        var externalId = trackRef.Trim();

        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await db.Tunes.AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.ModuleSlug == slug && t.ExternalId == externalId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Coalesce(string? incoming, string? current)
    {
        var trimmed = NullIfWhiteSpace(incoming);
        return trimmed ?? current;
    }
}
