using Bardie.Logos.Channel.Hosting;
using Bardie.Harness.Source.Ports;
using Bardie.Storage.V1;
using Grpc.Core;
using Google.Protobuf;

namespace Kithara.Infrastructure.Storage;

/// <summary>gRPC BlobStorage host — modules dial Kithara (:5000, mTLS).</summary>
public sealed class BlobStorageService : BlobStorage.BlobStorageBase
{
    private const int ChunkSize = 64 * 1024;

    private readonly IBlobStorage _storage;

    public BlobStorageService(IBlobStorage storage)
    {
        _storage = storage;
    }

    public override async Task<PutBlobResponse> Put(
        IAsyncStreamReader<PutBlobRequest> requestStream,
        ServerCallContext context)
    {
        var moduleSlug = RequireModuleSlug(context);

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Put stream must start with a header."));
        }

        if (requestStream.Current.PayloadCase != PutBlobRequest.PayloadOneofCase.Header)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First Put message must be header."));
        }

        var header = requestStream.Current.Header;
        var key = string.IsNullOrWhiteSpace(header.Key)
            ? BlobKeyLayout.AssignKey(moduleSlug)
            : header.Key.Trim();

        try
        {
            BlobKeyLayout.EnsureKeyOwnedBy(key, moduleSlug);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "bardie-put-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: ChunkSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
                {
                    if (requestStream.Current.PayloadCase != PutBlobRequest.PayloadOneofCase.Chunk)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Subsequent Put messages must be data chunks."));
                    }

                    var chunk = requestStream.Current.Chunk;
                    if (chunk.Length == 0)
                    {
                        continue;
                    }

                    await file.WriteAsync(chunk.Memory, context.CancellationToken).ConfigureAwait(false);
                }

                await file.FlushAsync(context.CancellationToken).ConfigureAwait(false);
            }

            await using var read = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: ChunkSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

            var contentType = string.IsNullOrWhiteSpace(header.ContentType) ? null : header.ContentType.Trim();
            var size = await _storage
                .PutAsync(key, read, contentType, context.CancellationToken)
                .ConfigureAwait(false);

            return new PutBlobResponse
            {
                Key = key,
                SizeBytes = size,
            };
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    public override async Task Get(
        GetBlobRequest request,
        IServerStreamWriter<GetBlobResponse> responseStream,
        ServerCallContext context)
    {
        var moduleSlug = RequireModuleSlug(context);
        var key = ValidateOwnedKey(request.Key, moduleSlug);

        var opened = await _storage.OpenReadAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Blob '{key}' not found."));
        }

        await using (opened.Stream)
        {
            await responseStream.WriteAsync(new GetBlobResponse
            {
                ContentType = opened.ContentType ?? string.Empty,
                Chunk = ByteString.Empty,
            }).ConfigureAwait(false);

            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await opened.Stream
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), context.CancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await responseStream.WriteAsync(new GetBlobResponse
                {
                    Chunk = ByteString.CopyFrom(buffer, 0, read),
                }).ConfigureAwait(false);
            }
        }
    }

    public override async Task<ExistsBlobResponse> Exists(ExistsBlobRequest request, ServerCallContext context)
    {
        var moduleSlug = RequireModuleSlug(context);
        var key = ValidateOwnedKey(request.Key, moduleSlug);
        var exists = await _storage.ExistsAsync(key, context.CancellationToken).ConfigureAwait(false);
        return new ExistsBlobResponse { Exists = exists };
    }

    public override async Task<DeleteBlobResponse> Delete(DeleteBlobRequest request, ServerCallContext context)
    {
        var moduleSlug = RequireModuleSlug(context);
        var key = ValidateOwnedKey(request.Key, moduleSlug);
        var existed = await _storage.ExistsAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (existed)
        {
            await _storage.DeleteAsync(key, context.CancellationToken).ConfigureAwait(false);
        }

        return new DeleteBlobResponse { Deleted = existed };
    }

    private static string ValidateOwnedKey(string key, string moduleSlug)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blob key is required."));
        }

        try
        {
            BlobKeyLayout.EnsureKeyOwnedBy(key, moduleSlug);
            return key.Trim();
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

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
