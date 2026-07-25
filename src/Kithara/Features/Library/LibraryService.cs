using Bardie.Library.V1;
using Bardie.Logos.Channel.Hosting;
using Grpc.Core;
using Kithara.Infrastructure.Storage;

namespace Kithara.Features.Library;

/// <summary>gRPC Library host — modules dial Kithara (:5000, mTLS) to upsert Tunes.</summary>
public sealed class LibraryService : Bardie.Library.V1.Library.LibraryBase
{
    private readonly TuneLibrary _tunes;

    public LibraryService(TuneLibrary tunes)
    {
        _tunes = tunes;
    }

    public override async Task<EnsureTuneResponse> EnsureTune(
        EnsureTuneRequest request,
        ServerCallContext context)
    {
        var callerSlug = RequireModuleSlug(context);
        var moduleSlug = string.IsNullOrWhiteSpace(request.ModuleSlug)
            ? callerSlug
            : request.ModuleSlug.Trim().ToLowerInvariant();

        if (!string.Equals(moduleSlug, callerSlug, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "module_slug must match the calling module identity."));
        }

        if (string.IsNullOrWhiteSpace(request.ExternalId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "external_id is required."));
        }

        // LIB-TUNE-001: Tune metadata must not claim another module's blob key.
        string? storageKey = null;
        if (!string.IsNullOrWhiteSpace(request.StorageKey))
        {
            storageKey = request.StorageKey.Trim();
            try
            {
                BlobKeyLayout.EnsureKeyOwnedBy(storageKey, moduleSlug);
            }
            catch (ArgumentException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }
        }

        var result = await _tunes.EnsureTuneAsync(
            new EnsureTuneCommand(
                ModuleSlug: moduleSlug,
                ExternalId: request.ExternalId,
                Title: request.Title,
                Artist: request.Artist,
                DurationSeconds: request.DurationSeconds > 0 ? request.DurationSeconds : null,
                ArtworkUrl: request.ArtworkUrl,
                StorageKey: storageKey,
                ContentType: request.ContentType,
                SizeBytes: request.SizeBytes > 0 ? request.SizeBytes : null),
            context.CancellationToken).ConfigureAwait(false);

        return new EnsureTuneResponse
        {
            TuneId = result.TuneId.ToString("D"),
            Created = result.Created,
        };
    }

    private static string RequireModuleSlug(ServerCallContext context)
    {
        if (context.UserState.TryGetValue(ModuleChannelBootstrapInterceptor.ModuleSlugUserStateKey, out var presented)
            && presented is string slug
            && !string.IsNullOrWhiteSpace(slug))
        {
            return slug.Trim().ToLowerInvariant();
        }

        throw new RpcException(new Status(StatusCode.Unauthenticated, "Module identity required."));
    }
}
