namespace Bardie.Harness.Source.Catalog;

public sealed record SearchFieldDescriptor
{
    public required string Name { get; init; }
    public bool Required { get; init; }
}

public sealed record SourceModuleRegistration
{
    public required string Slug { get; init; }
    public required string GrpcAdvertiseAddress { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<SearchFieldDescriptor> SearchFields { get; init; } = [];
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeatAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
