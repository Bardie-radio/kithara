using System.Text.Json;
using System.Text.Json.Serialization;
using Bardie.Harness.Source;
using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kithara.Features.Search;

public sealed class SearchCacheOptions
{
    public const string SectionName = "SearchCache";

    /// <summary>TTL for durable/managed principal cache entries. Env: <c>BARDIE_SEARCH_CACHE_TTL</c>.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>One cached search hit exposed to clients as an opaque result id.</summary>
public sealed class CachedSearchHit
{
    [JsonPropertyName("id")]
    public Guid ResultId { get; init; }

    [JsonPropertyName("module")]
    public string ModuleSlug { get; init; } = string.Empty;

    [JsonPropertyName("track_ref")]
    public string TrackRef { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed class SearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SourceModuleHarness _orch;
    private readonly IDbContextFactory<KitharaDbContext> _dbFactory;
    private readonly SearchCacheOptions _options;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        SourceModuleHarness orch,
        IDbContextFactory<KitharaDbContext> dbFactory,
        IOptions<SearchCacheOptions> options,
        ILogger<SearchService> logger)
    {
        _orch = orch;
        _dbFactory = dbFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(bool Ok, IReadOnlyList<CachedSearchHit> Hits, string? FailureReason)> SearchAndCacheAsync(
        Guid principalUserId,
        IReadOnlyDictionary<string, string> fields,
        string? moduleSlug,
        int limit,
        CancellationToken cancellationToken)
    {
        var orchResult = await _orch.SearchAsync(fields, moduleSlug, limit, cancellationToken)
            .ConfigureAwait(false);
        if (!orchResult.Ok)
        {
            return (false, [], orchResult.FailureReason);
        }

        var hits = orchResult.Hits.Select(h => new CachedSearchHit
        {
            ResultId = Guid.NewGuid(),
            ModuleSlug = h.ModuleSlug,
            TrackRef = h.TrackRef,
            Title = h.Title,
            Artist = h.Artist,
            ExternalId = h.ExternalId,
            Metadata = new Dictionary<string, string>(h.Metadata, StringComparer.Ordinal),
        }).ToArray();

        await ReplaceCacheAsync(principalUserId, moduleSlug, fields, hits, cancellationToken)
            .ConfigureAwait(false);

        return (true, hits, null);
    }

    public async Task<CachedSearchHit?> FindCachedHitAsync(
        Guid principalUserId,
        Guid resultId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var entries = await db.SearchResultCacheEntries
            .AsNoTracking()
            .Where(e => e.PrincipalUserId == principalUserId && e.ExpiresAt > now)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in entries)
        {
            var hits = DeserializeHits(entry.ResultsJson);
            var match = hits.FirstOrDefault(h => h.ResultId == resultId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public async Task ClearForPrincipalAsync(Guid principalUserId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.SearchResultCacheEntries
            .Where(e => e.PrincipalUserId == principalUserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        db.SearchResultCacheEntries.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceCacheAsync(
        Guid principalUserId,
        string? moduleSlug,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<CachedSearchHit> hits,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Durable/managed: next search replaces prior cache for this principal.
        var existing = await db.SearchResultCacheEntries
            .Where(e => e.PrincipalUserId == principalUserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            db.SearchResultCacheEntries.RemoveRange(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var ttl = _options.Ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(15) : _options.Ttl;
        db.SearchResultCacheEntries.Add(new SearchResultCacheEntry
        {
            Id = Guid.NewGuid(),
            PrincipalUserId = principalUserId,
            ModuleSlug = moduleSlug?.Trim().ToLowerInvariant() ?? "*",
            QueryKey = BuildQueryKey(fields),
            ResultsJson = JsonSerializer.Serialize(hits, JsonOptions),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to persist search cache for principal {UserId}", principalUserId);
        }
    }

    private static string BuildQueryKey(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            '&',
            fields.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}={p.Value}"));
    }

    private static IReadOnlyList<CachedSearchHit> DeserializeHits(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CachedSearchHit>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public static class SearchServiceCollectionExtensions
{
    public static IServiceCollection AddKitharaSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SearchCacheOptions>(configuration.GetSection(SearchCacheOptions.SectionName));
        services.PostConfigure<SearchCacheOptions>(options =>
        {
            var ttl = configuration["BARDIE_SEARCH_CACHE_TTL"];
            if (!string.IsNullOrWhiteSpace(ttl) && TimeSpan.TryParse(ttl, out var parsed) && parsed > TimeSpan.Zero)
            {
                options.Ttl = parsed;
            }
        });
        services.AddSingleton<SearchService>();
        return services;
    }
}
