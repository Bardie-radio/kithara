namespace Kithara.Features.Modules;

/// <summary>
/// Bardie host conventions for <c>RegisterRequest.kind</c>.
/// The mesh contract treats kind as an open string; only this host maps these values to harness catalogs.
/// ModuleChannel does not know about these constants.
/// </summary>
public static class WellKnownModuleKinds
{
    public const string Source = "source";
    public const string Auth = "auth";
    public const string Client = "client";

    public static string Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? string.Empty : kind.Trim().ToLowerInvariant();

    public static bool IsWellKnown(string normalizedKind) =>
        normalizedKind is Source or Auth or Client;
}
