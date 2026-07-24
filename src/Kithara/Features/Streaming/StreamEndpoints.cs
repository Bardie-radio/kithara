using System.Text;
using Kithara.Features.Auth;
using Kithara.Features.Streams;
using Kithara.Infrastructure.Neck;
using Kithara.Infrastructure.Persistence.Entities;
using Bardie.Orchestrator.Auth;
using Bardie.Orchestrator.Auth.Ports;

namespace Kithara.Features.Streaming;

/// <summary>
/// ICY-over-HTTP Stream Server: <c>GET /stream/{slug}</c> with fan-out from Neck FFmpeg output.
/// </summary>
public static class StreamEndpoints
{
    public const int IcyMetaInt = 8192;

    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stream/{slug}", StreamAsync)
            .AllowAnonymous()
            .WithName("StreamIcy");

        return endpoints;
    }

    private static async Task StreamAsync(
        string slug,
        HttpContext http,
        Neck neck,
        StrunaEncoderSupervisor encoder,
        IAuthPersistence persistence,
        AuthModuleOrchestrator authOrch,
        CancellationToken ct)
    {
        var struna = await neck.GetStrunaBySlugAsync(slug, ct).ConfigureAwait(false);
        if (struna is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = "not_found" }, ct).ConfigureAwait(false);
            return;
        }

        if (!await AuthorizePlaybackAsync(http, struna, persistence, authOrch, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!encoder.TryGetSession(struna.Id, out var session) || session is null)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsJsonAsync(new { error = "encoder_not_ready" }, ct)
                .ConfigureAwait(false);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "audio/mpeg";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["icy-name"] = struna.Title;
        http.Response.Headers["icy-genre"] = "Bardie";
        http.Response.Headers["icy-metaint"] = IcyMetaInt.ToString();
        http.Response.Headers.Append("Accept-Ranges", "none");

        // Disable response buffering so listeners hear live audio.
        var feature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        await using var icy = new IcyMetadataStream(
            http.Response.Body,
            () => neck.GetStreamTitle(struna.Id),
            IcyMetaInt);

        try
        {
            await foreach (var chunk in session.Fanout.SubscribeAsync(ct).ConfigureAwait(false))
            {
                await icy.WriteAudioAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Listener disconnected.
        }
    }

    private static async Task<bool> AuthorizePlaybackAsync(
        HttpContext http,
        Struna struna,
        IAuthPersistence persistence,
        AuthModuleOrchestrator authOrch,
        CancellationToken ct)
    {
        switch (struna.PlaybackAccess)
        {
            case PlaybackAccess.Public:
                return true;

            case PlaybackAccess.Protected:
            {
                var token = http.Request.Query["token"].FirstOrDefault();
                if (string.IsNullOrEmpty(struna.ListenToken)
                    || !string.Equals(token, struna.ListenToken, StringComparison.Ordinal))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { error = "listen_token_required" }, ct)
                        .ConfigureAwait(false);
                    return false;
                }

                return true;
            }

            case PlaybackAccess.Private:
            {
                if (http.User.Identity?.IsAuthenticated != true)
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { error = "authentication_required" }, ct)
                        .ConfigureAwait(false);
                    return false;
                }

                var principal = await AuthPrincipal.ResolveAsync(http.User, persistence, authOrch, ct)
                    .ConfigureAwait(false);
                if (principal is null || !StrunaAccess.CanListen(struna, principal.UserId))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await http.Response.WriteAsJsonAsync(new { error = "forbidden" }, ct)
                        .ConfigureAwait(false);
                    return false;
                }

                return true;
            }

            default:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return false;
        }
    }
}

/// <summary>Injects SHOUTcast-style ICY metadata every N audio bytes (<c>icy-metaint</c>).</summary>
internal sealed class IcyMetadataStream : IAsyncDisposable
{
    private readonly Stream _output;
    private readonly Func<string> _titleFactory;
    private readonly int _metaInt;
    private int _bytesUntilMeta;

    public IcyMetadataStream(Stream output, Func<string> titleFactory, int metaInt)
    {
        _output = output;
        _titleFactory = titleFactory;
        _metaInt = metaInt;
        _bytesUntilMeta = metaInt;
    }

    public async Task WriteAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
    {
        var remaining = audio;
        while (!remaining.IsEmpty)
        {
            if (_bytesUntilMeta == 0)
            {
                await WriteMetadataBlockAsync(cancellationToken).ConfigureAwait(false);
                _bytesUntilMeta = _metaInt;
            }

            var take = Math.Min(remaining.Length, _bytesUntilMeta);
            await _output.WriteAsync(remaining[..take], cancellationToken).ConfigureAwait(false);
            remaining = remaining[take..];
            _bytesUntilMeta -= take;
        }
    }

    private async Task WriteMetadataBlockAsync(CancellationToken cancellationToken)
    {
        var title = SanitizeTitle(_titleFactory() ?? string.Empty);
        var payload = string.IsNullOrEmpty(title)
            ? string.Empty
            : $"StreamTitle='{title}';";

        var bytes = Encoding.UTF8.GetBytes(payload);
        var lengthUnits = (bytes.Length + 15) / 16;
        if (lengthUnits > 255)
        {
            lengthUnits = 255;
            bytes = bytes.AsSpan(0, 255 * 16).ToArray();
        }

        var block = new byte[1 + (lengthUnits * 16)];
        block[0] = (byte)lengthUnits;
        if (bytes.Length > 0)
        {
            bytes.CopyTo(block.AsSpan(1));
        }

        await _output.WriteAsync(block, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SanitizeTitle(string title) =>
        title
            .Replace('\'', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
