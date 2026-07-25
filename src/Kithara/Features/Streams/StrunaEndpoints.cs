using System.Text.Json.Serialization;
using Bardie.Harness.Auth.Ports;
using Bardie.Harness.Source;
using Kithara.Features.Auth;
using Kithara.Features.Library;
using Kithara.Features.Search;
using Kithara.Infrastructure.Neck;
using Kithara.Infrastructure.Persistence;
using Kithara.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Kithara.Features.Streams;

/// <summary>
/// REST surface for Strunas (<c>/api/streams</c> — wire path stays English).
/// Lifecycle / FIFO / track jobs / queue: <see cref="Neck"/>.
/// Resource ACL for <c>{id}</c> routes: <see cref="StrunaControlFilter"/> / <see cref="StrunaDiscoverFilter"/>.
/// </summary>
public static class StrunaEndpoints
{
    public static IEndpointRouteBuilder MapStrunaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var root = endpoints.MapGroup("/api/streams");

        // Bootstrap is unauthenticated (guest code only); GUEST-XCHG-001 rate-limits per IP + Struna.
        root.MapPost("/{id:guid}/guest/exchange", GuestExchangeAsync)
            .RequireRateLimiting("guest-exchange");
        root.MapPost("/by-slug/{slug}/guest/exchange", GuestExchangeBySlugAsync)
            .RequireRateLimiting("guest-exchange");

        // Open playback (public | hidden): no Bearer — shared player URL is enough.
        root.MapGet("/by-slug/{slug}", GetOpenBySlugAsync);
        root.MapGet("/by-slug/{slug}/now-playing", NowPlayingBySlugAsync);

        var group = root.MapGroup(string.Empty)
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

        // Named lists before {id} so they are not captured as GUIDs.
        group.MapGet("/listen", ListListenAsync);
        group.MapGet("/control", ListControlAsync);
        group.MapPost("/", CreateAsync);

        group.MapGet("/{id:guid}", GetAsync)
            .AddEndpointFilter<StrunaDiscoverFilter>();
        group.MapGet("/{id:guid}/now-playing", NowPlayingAsync)
            .AddEndpointFilter<StrunaDiscoverFilter>();

        var dj = group.MapGroup("/{id:guid}")
            .AddEndpointFilter<StrunaControlFilter>();
        dj.MapDelete("/", DeleteAsync);
        dj.MapPost("/play", PlayAsync);
        dj.MapPost("/quickplay", QuickPlayAsync);
        dj.MapPost("/pause", PauseAsync);
        dj.MapPost("/skip", SkipAsync);
        dj.MapGet("/queue", ListQueueAsync);
        dj.MapPost("/queue", EnqueueAsync);
        dj.MapPost("/quickqueue", QuickQueueAsync);
        dj.MapDelete("/queue/{entryId:guid}", RemoveQueueEntryAsync);

        // Owner-only grant CRUD (stricter than CanControl — grantees cannot grant others).
        var grants = group.MapGroup("/{id:guid}/grants")
            .AddEndpointFilter<StrunaOwnerFilter>();
        grants.MapGet("/", ListGrantsAsync);
        grants.MapPost("/", AddGrantAsync);
        grants.MapDelete("/{userId:guid}", RemoveGrantAsync);

        return endpoints;
    }

    private static Task<IResult> ListListenAsync(HttpContext http, Neck neck, CancellationToken ct) =>
        ListFilteredAsync(http, neck, ct, StrunaAccess.AppearsOnListenList);

    private static Task<IResult> ListControlAsync(HttpContext http, Neck neck, CancellationToken ct) =>
        ListFilteredAsync(http, neck, ct, StrunaAccess.CanControl);

    private static async Task<IResult> ListFilteredAsync(
        HttpContext http,
        Neck neck,
        CancellationToken ct,
        Func<Struna, AuthUserRecord, bool> predicate)
    {
        var principal = AuthPrincipal.Get(http);
        var list = await neck.ListStrunasAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { strunas = list.Where(s => predicate(s, principal)).Select(MapStruna) });
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateStrunaBody body,
        HttpContext http,
        Neck neck,
        ManagedPermissionGate permissions,
        CancellationToken ct)
    {
        var principal = AuthPrincipal.Get(http);

        // Ephemeral guests cannot create Strunas.
        if (string.Equals(principal.Kind, nameof(UserKind.EphemeralGuest), StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }

        var ceilingDeny = permissions.DenyReason(principal, ManagedPermissions.CreateStruna);
        if (ceilingDeny is not null)
        {
            return Results.Json(new { error = ceilingDeny }, statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = await neck.CreateStrunaAsync(
                principal.UserId,
                body.Slug ?? string.Empty,
                body.Title,
                ParsePlayback(body.PlaybackAccess),
                ParseControl(body.ControlAccess),
                ct)
            .ConfigureAwait(false);

        return outcome.Error switch
        {
            CreateStrunaError.InvalidSlug => Results.BadRequest(new
            {
                error = "slug is required (lowercase alphanumeric + hyphen).",
            }),
            CreateStrunaError.SlugConflict => Results.Conflict(new { error = "slug conflict." }),
            _ => Results.Json(MapStrunaCreated(outcome.Struna!), statusCode: StatusCodes.Status201Created),
        };
    }

    private static IResult GetAsync(HttpContext http) =>
        Results.Ok(MapStruna(StrunaRequest.Entity(http)));

    private static async Task<IResult> DeleteAsync(
        Guid id,
        Neck neck,
        SearchService search,
        CancellationToken ct)
    {
        var outcome = await neck.DeleteStrunaAsync(id, ct).ConfigureAwait(false);
        if (!outcome.Deleted)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        // Search result cache (play refs), not search history.
        foreach (var guestId in outcome.GuestUserIds)
        {
            await search.ClearForPrincipalAsync(guestId, ct).ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> PlayAsync(
        Guid id,
        [FromBody] PlayBody? body,
        HttpContext http,
        Neck neck,
        SearchService search,
        IDbContextFactory<KitharaDbContext> dbFactory,
        CancellationToken ct)
    {
        var resolved = await ResolvePlayTargetAsync(
                StrunaRequest.Principal(http).UserId,
                ParseGuid(body?.SearchResultId ?? body?.ResultId),
                body?.Module,
                body?.TrackRef,
                ParseGuid(body?.TuneId),
                search,
                dbFactory,
                ct)
            .ConfigureAwait(false);
        if (resolved.ErrorResult is not null)
        {
            return resolved.ErrorResult;
        }

        return MapPlayResult(
            await neck.PlayTrackAsync(id, resolved.ModuleSlug, resolved.TrackRef, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> QuickPlayAsync(
        Guid id,
        [FromBody] QuickPlayBody body,
        HttpContext http,
        Neck neck,
        SearchService search,
        SourceModuleHarness orch,
        CancellationToken ct)
    {
        var query = body.Q ?? body.Query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new { error = "q is required." });
        }

        var hit = await SearchFirstAsync(
                StrunaRequest.Principal(http).UserId, query, body.Module, search, orch, ct)
            .ConfigureAwait(false);
        if (hit is null)
        {
            return Results.NotFound(new { error = "no_results" });
        }

        return MapPlayResult(
            await neck.PlayTrackAsync(id, hit.ModuleSlug, hit.TrackRef, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> PauseAsync(Guid id, Neck neck, CancellationToken ct) =>
        MapPlayResult(await neck.PauseTrackAsync(id, ct).ConfigureAwait(false));

    private static async Task<IResult> SkipAsync(Guid id, Neck neck, CancellationToken ct) =>
        MapPlayResult(await neck.SkipAsync(id, ct).ConfigureAwait(false));

    private static async Task<IResult> NowPlayingAsync(
        Guid id,
        Neck neck,
        TuneLibrary tunes,
        CancellationToken ct) =>
        await BuildNowPlayingResultAsync(id, neck, tunes, ct).ConfigureAwait(false);

    private static async Task<IResult> GetOpenBySlugAsync(string slug, Neck neck, CancellationToken ct)
    {
        var struna = await neck.GetStrunaBySlugAsync(slug, ct).ConfigureAwait(false);
        if (struna is null || !StrunaAccess.IsOpenPlayback(struna.PlaybackAccess))
        {
            return Results.NotFound(new { error = "not_found" });
        }

        return Results.Ok(MapStruna(struna));
    }

    private static async Task<IResult> NowPlayingBySlugAsync(
        string slug,
        Neck neck,
        TuneLibrary tunes,
        CancellationToken ct)
    {
        var struna = await neck.GetStrunaBySlugAsync(slug, ct).ConfigureAwait(false);
        if (struna is null || !StrunaAccess.IsOpenPlayback(struna.PlaybackAccess))
        {
            return Results.NotFound(new { error = "not_found" });
        }

        return await BuildNowPlayingResultAsync(struna.Id, neck, tunes, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> BuildNowPlayingResultAsync(
        Guid id,
        Neck neck,
        TuneLibrary tunes,
        CancellationToken ct)
    {
        var now = neck.GetNowPlaying(id);
        if (now is null)
        {
            return Results.Ok(new { playing = false });
        }

        var artworkUrl = now.ArtworkUrl;
        var title = now.Title;
        var artist = now.Artist;
        if (string.IsNullOrWhiteSpace(artworkUrl)
            || string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(artist))
        {
            var tune = await tunes
                .FindByModuleRefAsync(now.ModuleSlug, now.TrackRef, ct)
                .ConfigureAwait(false);
            if (tune is not null)
            {
                artworkUrl = string.IsNullOrWhiteSpace(artworkUrl) ? tune.ArtworkUrl : artworkUrl;
                title = string.IsNullOrWhiteSpace(title) ? tune.Title : title;
                artist = string.IsNullOrWhiteSpace(artist) ? tune.Artist : artist;
            }
        }

        var streamTitle = BuildStreamTitle(artist, title);
        if (string.IsNullOrWhiteSpace(streamTitle))
        {
            streamTitle = now.StreamTitle;
        }

        return Results.Ok(new
        {
            playing = true,
            paused = now.Paused,
            module = now.ModuleSlug,
            track_ref = now.TrackRef,
            track_job_id = now.TrackJobId,
            title,
            artist,
            stream_title = streamTitle,
            artwork_url = artworkUrl,
        });
    }

    private static string BuildStreamTitle(string? artist, string? title)
    {
        var hasArtist = !string.IsNullOrWhiteSpace(artist);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        if (hasArtist && hasTitle)
        {
            return $"{artist!.Trim()} - {title!.Trim()}";
        }

        if (hasTitle)
        {
            return title!.Trim();
        }

        return hasArtist ? artist!.Trim() : string.Empty;
    }

    private static async Task<IResult> ListQueueAsync(Guid id, Neck neck, CancellationToken ct)
    {
        var queue = await neck.ListQueueAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            entries = queue.Select(e => new
            {
                id = e.Id,
                position = e.Position,
                tune_id = e.TuneId,
                module = e.Tune.ModuleSlug,
                title = e.Tune.Title,
                artist = e.Tune.Artist,
            }),
        });
    }

    private static async Task<IResult> EnqueueAsync(
        Guid id,
        [FromBody] PlayBody body,
        HttpContext http,
        Neck neck,
        SearchService search,
        TuneLibrary tunes,
        IDbContextFactory<KitharaDbContext> dbFactory,
        CancellationToken ct)
    {
        var resolved = await ResolvePlayTargetAsync(
                StrunaRequest.Principal(http).UserId,
                ParseGuid(body.SearchResultId ?? body.ResultId),
                body.Module,
                body.TrackRef,
                ParseGuid(body.TuneId),
                search,
                dbFactory,
                ct)
            .ConfigureAwait(false);
        if (resolved.ErrorResult is not null)
        {
            return resolved.ErrorResult;
        }

        if (string.IsNullOrWhiteSpace(resolved.ModuleSlug) || string.IsNullOrWhiteSpace(resolved.TrackRef))
        {
            return Results.BadRequest(new { error = "queue requires a track intent." });
        }

        var tuneId = ParseGuid(body.TuneId);
        if (tuneId is null)
        {
            var ensured = await tunes.EnsureTuneAsync(
                    new EnsureTuneCommand(
                        resolved.ModuleSlug,
                        resolved.TrackRef,
                        Title: null,
                        Artist: null,
                        DurationSeconds: null,
                        ArtworkUrl: null,
                        StorageKey: null,
                        ContentType: null,
                        SizeBytes: null),
                    ct)
                .ConfigureAwait(false);
            tuneId = ensured.TuneId;
        }

        return await EnqueueTuneResultAsync(neck, id, tuneId.Value, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> QuickQueueAsync(
        Guid id,
        [FromBody] QuickPlayBody body,
        HttpContext http,
        Neck neck,
        SearchService search,
        TuneLibrary tunes,
        SourceModuleHarness orch,
        CancellationToken ct)
    {
        var query = body.Q ?? body.Query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new { error = "q is required." });
        }

        var hit = await SearchFirstAsync(
                StrunaRequest.Principal(http).UserId, query, body.Module, search, orch, ct)
            .ConfigureAwait(false);
        if (hit is null)
        {
            return Results.NotFound(new { error = "no_results" });
        }

        var ensured = await tunes.EnsureTuneAsync(
                new EnsureTuneCommand(
                    hit.ModuleSlug,
                    string.IsNullOrWhiteSpace(hit.ExternalId) ? hit.TrackRef : hit.ExternalId,
                    hit.Title,
                    hit.Artist,
                    null,
                    ArtworkFromHit(hit),
                    null,
                    null,
                    null),
                ct)
            .ConfigureAwait(false);

        return await EnqueueTuneResultAsync(neck, id, ensured.TuneId, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> RemoveQueueEntryAsync(
        Guid id,
        Guid entryId,
        Neck neck,
        CancellationToken ct)
    {
        var ok = await neck.RemoveQueueEntryAsync(id, entryId, ct).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
    }

    private static async Task<IResult> GuestExchangeAsync(
        Guid id,
        [FromBody] GuestExchangeBody body,
        Neck neck,
        GuestJwtService guests,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.GuestCode ?? body.Code))
        {
            return Results.BadRequest(new { error = "guest_code is required." });
        }

        var struna = await neck.GetStrunaAsync(id, ct).ConfigureAwait(false);
        if (struna is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        return await CompleteGuestExchangeAsync(id, body.GuestCode ?? body.Code!, guests, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Slug-based exchange for client UX (share <c>/control/{slug}</c>; code stays out of the URL).
    /// </summary>
    private static async Task<IResult> GuestExchangeBySlugAsync(
        string slug,
        [FromBody] GuestExchangeBody body,
        Neck neck,
        GuestJwtService guests,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.GuestCode ?? body.Code))
        {
            return Results.BadRequest(new { error = "guest_code is required." });
        }

        var struna = await neck.GetStrunaBySlugAsync(slug, ct).ConfigureAwait(false);
        if (struna is null)
        {
            return Results.NotFound(new { error = "not_found" });
        }

        return await CompleteGuestExchangeAsync(struna.Id, body.GuestCode ?? body.Code!, guests, ct)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> CompleteGuestExchangeAsync(
        Guid strunaId,
        string guestCode,
        GuestJwtService guests,
        CancellationToken ct)
    {
        var exchanged = await guests.ExchangeAsync(strunaId, guestCode, ct).ConfigureAwait(false);
        if (exchanged is null)
        {
            return Results.Json(new { error = "invalid_guest_code" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (userId, access, refresh, expiresIn) = exchanged.Value;
        return Results.Ok(new
        {
            access_token = access,
            refresh_token = refresh,
            token_type = "Bearer",
            expires_in = expiresIn,
            user_id = userId,
        });
    }

    private static async Task<IResult> ListGrantsAsync(Guid id, Neck neck, CancellationToken ct)
    {
        var grants = await neck.ListGrantsAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            grants = grants.Select(g => new { user_id = g.UserId }),
        });
    }

    private static async Task<IResult> AddGrantAsync(
        Guid id,
        [FromBody] GrantBody body,
        HttpContext http,
        Neck neck,
        ManagedPermissionGate permissions,
        CancellationToken ct)
    {
        var principal = AuthPrincipal.Get(http);
        var ceilingDeny = permissions.DenyReason(principal, ManagedPermissions.ManageGrants);
        if (ceilingDeny is not null)
        {
            return Results.Json(new { error = ceilingDeny }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!Guid.TryParse(body.UserId, out var userId))
        {
            return Results.BadRequest(new { error = "user_id is required." });
        }

        var (grant, error) = await neck.AddGrantAsync(id, userId, ct).ConfigureAwait(false);
        return error switch
        {
            "not_found" => Results.NotFound(new { error = "not_found" }),
            "user_not_found" => Results.NotFound(new { error = "user_not_found" }),
            "owner_already_controls" => Results.BadRequest(new { error = "owner_already_controls" }),
            "guest_not_grantable" => Results.BadRequest(new { error = "guest_not_grantable" }),
            _ => Results.Ok(new { user_id = grant!.UserId }),
        };
    }

    private static async Task<IResult> RemoveGrantAsync(
        Guid id,
        Guid userId,
        HttpContext http,
        Neck neck,
        ManagedPermissionGate permissions,
        CancellationToken ct)
    {
        var principal = AuthPrincipal.Get(http);
        var ceilingDeny = permissions.DenyReason(principal, ManagedPermissions.ManageGrants);
        if (ceilingDeny is not null)
        {
            return Results.Json(new { error = ceilingDeny }, statusCode: StatusCodes.Status403Forbidden);
        }

        var ok = await neck.RemoveGrantAsync(id, userId, ct).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound(new { error = "not_found" });
    }

    private static async Task<IResult> EnqueueTuneResultAsync(
        Neck neck,
        Guid strunaId,
        Guid tuneId,
        CancellationToken ct)
    {
        var (entry, error) = await neck.EnqueueTuneAsync(strunaId, tuneId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return Results.NotFound(new { error = error ?? "not_found" });
        }

        return Results.Json(
            new { id = entry.Id, position = entry.Position, tune_id = entry.TuneId },
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<CachedSearchHit?> SearchFirstAsync(
        Guid principalUserId,
        string query,
        string? module,
        SearchService search,
        SourceModuleHarness orch,
        CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = query.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(module))
        {
            var (ok, hits, _) = await search.SearchAndCacheAsync(
                    principalUserId, fields, module, limit: 1, ct)
                .ConfigureAwait(false);
            return ok && hits.Count > 0 ? hits[0] : null;
        }

        foreach (var source in orch.OrderPlayableSources())
        {
            var (ok, hits, _) = await search.SearchAndCacheAsync(
                    principalUserId, fields, source.Slug, limit: 1, ct)
                .ConfigureAwait(false);
            if (ok && hits.Count > 0)
            {
                return hits[0];
            }
        }

        return null;
    }

    private static async Task<(string? ModuleSlug, string? TrackRef, IResult? ErrorResult)> ResolvePlayTargetAsync(
        Guid principalUserId,
        Guid? searchResultId,
        string? module,
        string? trackRef,
        Guid? tuneId,
        SearchService search,
        IDbContextFactory<KitharaDbContext> dbFactory,
        CancellationToken ct)
    {
        if (searchResultId is Guid resultId)
        {
            var hit = await search.FindCachedHitAsync(principalUserId, resultId, ct).ConfigureAwait(false);
            if (hit is null)
            {
                return (null, null, Results.NotFound(new { error = "search_result_not_found" }));
            }

            return (hit.ModuleSlug, hit.TrackRef, null);
        }

        if (!string.IsNullOrWhiteSpace(module) && !string.IsNullOrWhiteSpace(trackRef))
        {
            return (module.Trim(), trackRef.Trim(), null);
        }

        if (tuneId is Guid tid)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var tune = await db.Tunes.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tid, ct)
                .ConfigureAwait(false);
            if (tune is null)
            {
                return (null, null, Results.NotFound(new { error = "tune_not_found" }));
            }

            var refValue = string.IsNullOrWhiteSpace(tune.ExternalId)
                ? tune.Id.ToString("D")
                : tune.ExternalId;
            return (tune.ModuleSlug, refValue, null);
        }

        return (null, null, null);
    }

    private static IResult MapPlayResult(PlayTrackOutcome outcome)
    {
        if (outcome.Ok)
        {
            return Results.Ok(new { track_job_id = outcome.TrackJobId });
        }

        return outcome.Error switch
        {
            PlayTrackError.StrunaNotFound => Results.NotFound(new { error = "not_found" }),
            PlayTrackError.NothingToResume => Results.BadRequest(new
            {
                error = outcome.Detail ?? "play body required (no active track to unpause).",
            }),
            _ => Results.Json(
                new { error = outcome.Detail ?? "start_track_failed" },
                statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static object MapStruna(Struna s) => new
    {
        id = s.Id,
        slug = s.Slug,
        title = s.Title,
        playback_access = s.PlaybackAccess.ToString().ToLowerInvariant(),
        control_access = s.ControlAccess.ToString().ToLowerInvariant(),
        owner_user_id = s.OwnerUserId,
        created_at = s.CreatedAt,
    };

    private static object MapStrunaCreated(Struna s) => new
    {
        id = s.Id,
        slug = s.Slug,
        title = s.Title,
        playback_access = s.PlaybackAccess.ToString().ToLowerInvariant(),
        control_access = s.ControlAccess.ToString().ToLowerInvariant(),
        owner_user_id = s.OwnerUserId,
        created_at = s.CreatedAt,
        guest_code = s.GuestCode,
        listen_token = s.ListenToken,
    };

    private static PlaybackAccess ParsePlayback(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "protected" => PlaybackAccess.Protected,
            "private" => PlaybackAccess.Private,
            "hidden" => PlaybackAccess.Hidden,
            _ => PlaybackAccess.Public,
        };

    private static ControlAccess ParseControl(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "protected" => ControlAccess.Protected,
            _ => ControlAccess.Private,
        };

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var id) ? id : null;

    private static string? ArtworkFromHit(CachedSearchHit hit)
    {
        if (hit.Metadata.TryGetValue("artwork_url", out var url)
            && !string.IsNullOrWhiteSpace(url))
        {
            return url.Trim();
        }

        return null;
    }

    public sealed class CreateStrunaBody
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("playback_access")]
        public string? PlaybackAccess { get; set; }

        [JsonPropertyName("control_access")]
        public string? ControlAccess { get; set; }
    }

    public sealed class PlayBody
    {
        [JsonPropertyName("search_result_id")]
        public string? SearchResultId { get; set; }

        /// <summary>Alias for <see cref="SearchResultId"/>.</summary>
        [JsonPropertyName("result_id")]
        public string? ResultId { get; set; }

        [JsonPropertyName("module")]
        public string? Module { get; set; }

        [JsonPropertyName("track_ref")]
        public string? TrackRef { get; set; }

        [JsonPropertyName("tune_id")]
        public string? TuneId { get; set; }
    }

    public sealed class QuickPlayBody
    {
        [JsonPropertyName("q")]
        public string? Q { get; set; }

        /// <summary>Alias for <see cref="Q"/>.</summary>
        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("module")]
        public string? Module { get; set; }
    }

    public sealed class GuestExchangeBody
    {
        [JsonPropertyName("guest_code")]
        public string? GuestCode { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }

    public sealed class GrantBody
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }
}
