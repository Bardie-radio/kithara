using System.Text;
using Bardie.Harness.Source.Ports;
using Microsoft.Extensions.Options;

namespace Kithara.Infrastructure.Storage;

/// <summary>
/// Local filesystem blob driver. Keys map to <c>{BARDIE_STORAGE_PATH}/tunes/&lt;source_slug&gt;/…</c>.
/// </summary>
public sealed class LocalBlobStorage : IBlobStorage
{
    private readonly string _root;
    private readonly ILogger<LocalBlobStorage> _logger;

    public LocalBlobStorage(IOptions<BlobStorageOptions> options, ILogger<LocalBlobStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.Value.Path) ? "data/blobs" : options.Value.Path);
        Directory.CreateDirectory(_root);
    }

    public async Task<long> PutAsync(
        string key,
        Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlobKeyLayout.EnsureValidKey(key);
        ArgumentNullException.ThrowIfNull(content);

        var path = BlobKeyLayout.ResolvePath(_root, key);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
            WriteContentTypeSidecar(path, contentType);

            var size = new FileInfo(path).Length;
            _logger.LogDebug("Put blob {Key} ({Size} bytes) under {Root}", key, size, _root);
            return size;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task<BlobReadResult?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlobKeyLayout.EnsureValidKey(key);

        var path = BlobKeyLayout.ResolvePath(_root, key);
        if (!File.Exists(path))
        {
            return Task.FromResult<BlobReadResult?>(null);
        }

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult<BlobReadResult?>(new BlobReadResult(stream, ReadContentTypeSidecar(path)));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlobKeyLayout.EnsureValidKey(key);
        var path = BlobKeyLayout.ResolvePath(_root, key);
        return Task.FromResult(File.Exists(path));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BlobKeyLayout.EnsureValidKey(key);
        var path = BlobKeyLayout.ResolvePath(_root, key);
        TryDelete(path);
        TryDelete(ContentTypeSidecarPath(path));
        return Task.CompletedTask;
    }

    private static string ContentTypeSidecarPath(string blobPath) => blobPath + ".ctype";

    private static void WriteContentTypeSidecar(string blobPath, string? contentType)
    {
        var sidecar = ContentTypeSidecarPath(blobPath);
        if (string.IsNullOrWhiteSpace(contentType))
        {
            TryDelete(sidecar);
            return;
        }

        File.WriteAllText(sidecar, contentType.Trim(), Encoding.UTF8);
    }

    private static string? ReadContentTypeSidecar(string blobPath)
    {
        var sidecar = ContentTypeSidecarPath(blobPath);
        if (!File.Exists(sidecar))
        {
            return null;
        }

        var text = File.ReadAllText(sidecar, Encoding.UTF8).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void TryDelete(string path)
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
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }
}
