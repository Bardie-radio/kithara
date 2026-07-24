using System.Text.Json.Serialization;
using Bardie.Harness.Auth;
using Kithara.Features.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Kithara.Features.Search;

/// <summary>REST surface for global search (<c>/api/search</c>).</summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/search")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePrincipalFilter>();

        group.MapGet("/quick", QuickSearchAsync);
        group.MapPost("/", SearchAsync);

        return endpoints;
    }

    private static async Task<IResult> QuickSearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? query,
        [FromQuery] string? module,
        [FromQuery] int? limit,
        HttpContext http,
        SearchService search,
        CancellationToken ct)
    {
        var text = q ?? query;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.BadRequest(new { error = "q (or query) is required." });
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = text.Trim(),
        };

        var (ok, hits, failure) = await search.SearchAndCacheAsync(
                AuthPrincipal.Get(http).UserId,
                fields,
                module,
                limit ?? 0,
                ct)
            .ConfigureAwait(false);

        if (!ok)
        {
            return Results.Json(
                new { error = failure ?? "search_failed" },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new { results = hits.Select(MapHit) });
    }

    private static async Task<IResult> SearchAsync(
        [FromBody] SearchRequestBody body,
        HttpContext http,
        SearchService search,
        CancellationToken ct)
    {
        var fields = body.Fields ?? new Dictionary<string, string>();
        if (fields.Count == 0 && !string.IsNullOrWhiteSpace(body.Title))
        {
            fields = new Dictionary<string, string> { ["title"] = body.Title };
        }

        if (fields.Count == 0)
        {
            return Results.BadRequest(new { error = "fields (or title) is required." });
        }

        var (ok, hits, failure) = await search.SearchAndCacheAsync(
                AuthPrincipal.Get(http).UserId,
                fields,
                body.Module,
                body.Limit ?? 0,
                ct)
            .ConfigureAwait(false);

        if (!ok)
        {
            return Results.Json(
                new { error = failure ?? "search_failed" },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new { results = hits.Select(MapHit) });
    }

    private static object MapHit(CachedSearchHit hit) => new
    {
        id = hit.ResultId,
        module = hit.ModuleSlug,
        track_ref = hit.TrackRef,
        title = hit.Title,
        artist = hit.Artist,
        external_id = hit.ExternalId,
        metadata = hit.Metadata,
    };

    public sealed class SearchRequestBody
    {
        [JsonPropertyName("module")]
        public string? Module { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("fields")]
        public Dictionary<string, string>? Fields { get; set; }

        [JsonPropertyName("limit")]
        public int? Limit { get; set; }
    }
}
