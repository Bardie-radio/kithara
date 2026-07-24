namespace Bardie.Harness.Auth.Catalog;

public sealed record AuthModuleRegistration
{
    public required string Slug { get; init; }
    public required string GrpcAdvertiseAddress { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string? JwksUri { get; init; }
    public string? JwksJson { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
