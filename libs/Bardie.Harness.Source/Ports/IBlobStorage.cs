namespace Bardie.Harness.Source.Ports;

/// <summary>
/// Host storage port for library blob put/get. Drivers stay on Kithara; modules dial gRPC BlobStorage.
/// </summary>
public interface IBlobStorage
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="key"/>. Returns bytes written.</summary>
    Task<long> PutAsync(
        string key,
        Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default);

    Task<BlobReadResult?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Open blob payload. Caller must dispose <see cref="Stream"/>.</summary>
public sealed record BlobReadResult(Stream Stream, string? ContentType);
