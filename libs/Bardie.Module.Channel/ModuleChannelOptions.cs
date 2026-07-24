namespace Bardie.Module.Channel;

/// <summary>
/// Options for Bardie module-mesh mTLS. Defaults favour private Compose/LAN (<see cref="ModuleChannelBootstrapMode.Auto"/>, <see cref="UseMtls"/> = true).
/// </summary>
public sealed class ModuleChannelOptions
{
    public const string SectionName = "ModuleChannel";

    /// <summary>When true (default), server and outbound helpers expect mTLS.</summary>
    public bool UseMtls { get; set; } = true;

    /// <summary>
    /// Full gRPC method paths (<c>/package.Service/Method</c>) that may proceed without a client certificate.
    /// Prefer <see cref="AllowMethodWithoutClientCertificate"/> to <b>add</b> entries.
    /// Binding this list from JSON/<c>Configure</c> <b>replaces</b> the whole collection — use
    /// <see cref="IncludeRegisterWithoutClientCertificate"/> (default true) so mesh <c>Register</c> is not dropped accidentally.
    /// </summary>
    public List<string> AllowWithoutClientCertificate { get; set; } = [];

    /// <summary>
    /// When true (default), <see cref="ModuleRegistryMethodPaths.Register"/> is always kept on the allowlist
    /// after options bind. Set false only if the host intentionally requires a client certificate on Register.
    /// </summary>
    public bool IncludeRegisterWithoutClientCertificate { get; set; } = true;

    /// <summary>Env: <c>BARDIE_MODULE_MTLS_BOOTSTRAP</c> — <c>auto</c> | <c>preshared</c>.</summary>
    public ModuleChannelBootstrapMode BootstrapMode { get; set; } = ModuleChannelBootstrapMode.Auto;

    /// <summary>Env: <c>BARDIE_GRPC_TLS_DATA_PATH</c> — host CA / server cert directory.</summary>
    public string TlsDataPath { get; set; } = "data/mtls";

    /// <summary>Env: <c>BARDIE_MODULE_MTLS_PRESHARED_DIR</c> — per-slug client certs when bootstrap is <see cref="ModuleChannelBootstrapMode.Preshared"/>.</summary>
    public string? PresharedDir { get; set; }

    /// <summary>Lifetime for auto-issued module client certificates.</summary>
    public TimeSpan ClientCertificateLifetime { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// DNS names embedded in the host mesh certificate SAN.
    /// The first entry is also used as the certificate CN (host client identity for work-port dials).
    /// Host apps must set this to their mesh identity (e.g. Kithara: <c>["kithara", "localhost"]</c>).
    /// </summary>
    public string[] ServerDnsNames { get; set; } = ["localhost"];

    /// <summary>Adds a method to <see cref="AllowWithoutClientCertificate"/> (idempotent).</summary>
    public ModuleChannelOptions AllowMethodWithoutClientCertificate(string package, string service, string method)
    {
        return AllowMethodWithoutClientCertificate(GrpcMethodPath.Format(package, service, method));
    }

    /// <summary>Adds a full method path to <see cref="AllowWithoutClientCertificate"/> (idempotent).</summary>
    public ModuleChannelOptions AllowMethodWithoutClientCertificate(string fullMethodPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullMethodPath);
        AllowWithoutClientCertificate ??= [];
        if (!AllowWithoutClientCertificate.Contains(fullMethodPath, StringComparer.Ordinal))
        {
            AllowWithoutClientCertificate.Add(fullMethodPath);
        }

        return this;
    }

    /// <summary>Replaces <see cref="AllowWithoutClientCertificate"/> with the given full method paths.</summary>
    public ModuleChannelOptions SetAllowWithoutClientCertificate(params string[] fullMethodPaths)
    {
        AllowWithoutClientCertificate = [.. fullMethodPaths];
        return this;
    }
}
