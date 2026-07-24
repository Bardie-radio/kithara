namespace Bardie.Module.Channel.Participant;

/// <summary>
/// Options for a module process that dials the registry host (Register / Heartbeat) and hosts a work gRPC port.
/// </summary>
public sealed class ModuleParticipantOptions
{
    public const string SectionName = "ModuleParticipant";

    /// <summary>
    /// Where this module dials Register / Heartbeat.
    /// Env: <c>MODULE_HOST_GRPC_ADDRESS</c> · config: <c>ModuleParticipant:HostGrpcAddress</c>.
    /// </summary>
    public string HostGrpcAddress { get; set; } = "https://localhost:5000";

    /// <summary>Env: <c>JOIN_SECRET</c> — never stored in the manifest.</summary>
    public string JoinSecret { get; set; } = string.Empty;

    /// <summary>Env: <c>GRPC_ADVERTISE_ADDRESS</c> — where the host dials this module for work RPCs.</summary>
    public string GrpcAdvertiseAddress { get; set; } = string.Empty;

    /// <summary>Path to <c>module.manifest.json</c>. Env: <c>MODULE_MANIFEST_PATH</c>.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Directory for CA / client / work-server PEMs. Env: <c>MODULE_TLS_DATA_PATH</c>.</summary>
    public string TlsDataPath { get; set; } = "data/mtls";

    /// <summary>TCP port for the module work gRPC listener (HTTPS + mesh CA trust). Env: <c>MODULE_WORK_GRPC_PORT</c>.</summary>
    public int WorkGrpcPort { get; set; } = 5001;

    /// <summary>When false, skip automatic Register + Heartbeat hosted service.</summary>
    public bool EnableRegistration { get; set; } = true;

    /// <summary>DNS SANs embedded in the module work-port server certificate.</summary>
    /// <remarks>
    /// Put the module <c>slug</c> first — host dials pin CN/SAN to the registered slug (MESH-CHN-001).
    /// Example Magpie: <c>["magpie", "localhost"]</c>.
    /// </remarks>
    public string[] ServerDnsNames { get; set; } = ["localhost"];

    /// <summary>
    /// Host client identity (CN/SAN) that may dial this module's work-port (MESH-CHN-001).
    /// Must match the host mesh cert — e.g. Bardie modules set <c>kithara</c>.
    /// Empty rejects all work-port clients.
    /// Env: <c>MODULE_EXPECTED_HOST_IDENTITY</c>.
    /// </summary>
    public string ExpectedHostClientIdentity { get; set; } = string.Empty;
}
