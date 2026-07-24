# Bardie.Module.Channel

Shared **mTLS / gRPC channel** helpers for Bardie module hosts **and** module participants (Bes, Magpie, …).

**Package id:** `Bardie.Module.Channel` · **Version:** `0.1.0` · **TFM:** `net10.0`

Depends on [`Bardie.Contracts`](../Bardie.Contracts/README.md) for `RegisterRequest` / `ModuleRegistry` stubs.

## What it owns

| Capability | Notes |
|------------|--------|
| CA + server cert load/generate + persist | Host: `BARDIE_GRPC_TLS_DATA_PATH` / `TlsDataPath` |
| Issue module client certs | Host bootstrap mode **`auto`** |
| Validate inbound client cert → slug | Heartbeat + privileged RPCs |
| Outbound `GrpcChannel` helpers | Host harnesses dial modules |
| Kestrel bind helper | Host HTTPS `:5000` + allow client certificates |
| Bootstrap interceptor | Register may omit client cert; other RPCs require it |
| **`ModuleManifest`** | Static identity loader + `BuildRegisterRequest` |
| **Participant APIs** | Persist Register PEMs, mTLS Heartbeat dial, work-port Kestrel, `ModuleRegistrationHostedService` |

## Consume

| Context | How |
|---------|-----|
| Multi-root / sibling `kithara/libs` | `ProjectReference` to this project (see Bes `Directory.Build.props`) |
| Standalone CI / published consumers | `PackageReference` to `Bardie.Module.Channel` `0.1.0` |

Pack: `dotnet pack libs/Bardie.Module.Channel/Bardie.Module.Channel.csproj -c Release` (with Contracts).

## Host DI

```csharp
services.AddModuleChannel(options =>
{
    options.UseMtls = true; // default
    options.BootstrapMode = ModuleChannelBootstrapMode.Auto; // or Preshared
    options.TlsDataPath = "/data/grpc-tls";
});
```

`AddAuthModuleHarness()` / `AddSourceModuleHarness()` call this with mTLS on by default.

### mTLS exemptions (bootstrap RPCs)

The bootstrap interceptor runs on **every** gRPC call shape. By default **Module Registry `Register`** may omit a client certificate — that RPC *is* the mesh join handshake.

**Options bind caveat:** assigning `AllowWithoutClientCertificate` from JSON or `Configure` **replaces** the whole list. Additive changes should use `AllowMethodWithoutClientCertificate(...)`. `IncludeRegisterWithoutClientCertificate` (default `true`) re-adds `ModuleRegistryMethodPaths.Register` in a `PostConfigure` so a partial JSON list cannot accidentally drop mesh join.

```csharp
services.AddModuleChannel(options =>
{
    // Additive (keeps Register):
    options.AllowMethodWithoutClientCertificate("myhost.v1", "Bootstrap", "Join");

    // Or replace, still keeping Register via PostConfigure unless opted out:
    options.SetAllowWithoutClientCertificate(
        GrpcMethodPath.Format("myhost.v1", "Bootstrap", "Join"));

    // Rare: require client cert even on Register
    // options.IncludeRegisterWithoutClientCertificate = false;
});
```

Paths for Module Registry RPCs come from the generated protobuf descriptor (`ModuleRegistryMethodPaths`), not hand-copied package strings. Proto `package bardie.modules.v1` is the **wire API version** — bumping to `v2` is a breaking contract change (clients must regenerate); it is not dead naming.

Well-known paths: `ModuleRegistryMethodPaths.Register`, `ModuleRegistryMethodPaths.Heartbeat` (Heartbeat still requires mTLS).

appsettings (optional — include Register yourself if you set this key, or rely on `IncludeRegisterWithoutClientCertificate`):

```json
{
  "ModuleChannel": {
    "AllowWithoutClientCertificate": [
      "/bardie.modules.v1.ModuleRegistry/Register"
    ]
  }
}
```

## Participant DI (modules)

```csharp
services.AddModuleParticipant(builder.Configuration, contentRoot: builder.Environment.ContentRootPath);
// optional kind-specific Register oneof (JWKS, search fields, …):
services.AddSingleton<IModuleRegisterRequestCustomizer, MyRegisterCustomizer>();
```

Kestrel work port:

```csharp
builder.WebHost.ConfigureKestrel(k =>
    k.ConfigureBardieModuleParticipantListeners(httpPort: 8080, workGrpcPort: 5001));
```

Static identity in ModuleChannel is **generic** (`slug` / `kind` / `capabilities` / OTel name). Kind bags may ship as opaque JSON (`source.searchFields`, `auth.formFields`, …) in the same file; Module.Source / Module.Auth helpers parse them, and Register oneofs that need runtime material (JWKS) stay in customizers.

## Bootstrap modes (host)

| Mode | Env | Wire behaviour |
|------|-----|----------------|
| **`auto`** (default) | `BARDIE_MODULE_MTLS_BOOTSTRAP=auto` | After join-secret OK, Register may return client cert + **private key** PEMs. **Private mesh / Compose only.** |
| **`preshared`** | `BARDIE_MODULE_MTLS_BOOTSTRAP=preshared` | Operator pre-places CA + per-slug client cert/key under `BARDIE_MODULE_MTLS_PRESHARED_DIR` **before** start. Register **never** returns private keys. Use whenever gRPC may cross a public/untrusted network. |

Steady-state after Register is always mTLS. Mode only changes how certs land on disk.

Private key handling: PEM key files under `TlsDataPath` are owner-only (`0600` on Unix). In-memory keys use `EphemeralKeySet`. Host→module dials open a **short-lived** cert from PEM and dispose it with the channel — do not cache extra private-key handles.

Offline provision helper: `IModuleCertificateIssuer.ProvisionPresharedClientCertificate(slug)` writes files under the preshared directory (admin tooling / DummyRegistrar-style setup).

## Related

- Participant Program bootstrap + Bardie Compose env: [`Bardie.Module.Hosting`](../Bardie.Module.Hosting/README.md)
- JWT-minting auth adapters: [`Bardie.Module.Auth`](../Bardie.Module.Auth/README.md)
- Architecture: [module-channel.md](../../docs/architecture/operations/module-channel.md)
- Registry contract: [grpc-module-registry.md](../../docs/architecture/interfaces/grpc-module-registry.md)
- Config knobs: [configuration.md](../../docs/architecture/operations/configuration.md)
- Mesh trust audit: [security-audit-module-mesh.md](../../docs/architecture/operations/security-audit-module-mesh.md)
