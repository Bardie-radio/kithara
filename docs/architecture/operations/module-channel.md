# ModuleChannel (mTLS + participant library)

Kithara (and future external hosts) embed **`Bardie.Module.Channel`** for module gRPC channel security. Modules (Bes, Magpie, …) embed the same package for **manifest identity**, Register/Heartbeat, and work-port TLS. Mesh join RPCs stay host-owned; **crypto, bootstrap policy, and static module identity live in the library** so embedders do not reinvent Kestrel/GrpcChannel wiring.

**Library home:** [`libs/Bardie.Module.Channel`](../../../libs/Bardie.Module.Channel/README.md) · contracts: [`Bardie.Contracts`](../../../libs/Bardie.Contracts/README.md) · participant bootstrap: [`Bardie.Module.Hosting`](../../../libs/Bardie.Module.Hosting/README.md) · auth adapter kit: [`Bardie.Module.Auth`](../../../libs/Bardie.Module.Auth/README.md)

## Why it exists

Modules dial the host to `Register`, then speak mTLS for Heartbeat and work RPCs. Auth/source harness libraries reuse the same outbound dial helpers. Shipping mTLS as a packable library keeps Bardie Compose and outside hosts on one trust story ([org 07](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/07-modules-beyond-bardie.md)).

## Pack + consume (no proto copies)

| Context | How modules reference libs |
|---------|----------------------------|
| Multi-root workspace / Local Compose sibling layout | If `../kithara/libs` exists → **`ProjectReference`** (Bes `Directory.Build.props`) |
| Standalone CI / published consumers | **`PackageReference`** to versioned `Bardie.Contracts` + `Bardie.Module.Channel` (`0.1.0`); participants also take `Bardie.Module.Hosting` (+ `Bardie.Module.Auth` when minting JWTs) |

Do **not** git-submodule Kithara, copy `.proto`/`.cs` into module repos, or path-include protos from another repo in a module csproj.

## Participant hosting vs Channel

| Package | Owns |
|---------|------|
| **`Bardie.Module.Channel`** | Manifest, Register/Heartbeat, certs, work-port Kestrel TLS, generic `MODULE_*` / `JOIN_SECRET` / `GRPC_ADVERTISE_ADDRESS` |
| **`Bardie.Module.Hosting`** | ASP.NET Program bootstrap (`AddBardieModuleHosting`), `/healthz`, OTel from manifest, **Bardie Compose aliases** (`KITHARA_*` / `BARDIE_*`) |
| **`Bardie.Module.Auth`** | Optional JWT mint / JWKS Register customizer / thin `AuthAdapterModuleBase` for adapters that mint login JWTs |

Channel stays alias-agnostic so non-Bardie hosts can embed it without Compose name knowledge.

## Module manifest (static identity)

Each module ships one **`module.manifest.json`**. ModuleChannel loads **generic** identity only — slug, kind, capabilities, display name, OTel name. It does **not** type Bardie auth/source/client bags; those stay as opaque `Extensions` (or runtime-only customizer output such as JWKS).

```json
{
  "slug": "bes",
  "kind": "auth",
  "displayName": "Bes",
  "otelServiceName": "bardie.auth.bes",
  "capabilities": ["updateBinding"],
  "auth": {
    "loginFormFields": [
      { "name": "username", "label": "Username", "inputType": "text", "required": true },
      { "name": "password", "label": "Password", "inputType": "password", "required": true }
    ],
    "bindFormFields": [
      { "name": "username", "label": "Username", "inputType": "text", "required": false },
      { "name": "password", "label": "Password", "inputType": "password", "required": true }
    ]
  }
}
```

| Field | Who owns | Notes |
|-------|----------|--------|
| `slug`, `kind`, `capabilities` | Manifest (ModuleChannel) | Defaults for core `RegisterRequest` fields |
| `otelServiceName`, `displayName` | Manifest (ModuleChannel) | OTel / ops |
| `source.searchFields` | Manifest → `Bardie.Module.Source` customizer | Advertise on Register `details.source` |
| `auth.loginFormFields` / `auth.bindFormFields` | Manifest → module / `Bardie.Module.Auth` helper | `GetProviders` login + bind schemas (not Register). Legacy `auth.formFields` = login only |
| Kind-specific runtime `oneof` (JWKS, permission ceiling) | **Module / host customizer** | e.g. JWKS from key material — not a static file |
| Extra JSON keys | Opaque `Extensions` | Preserved for module-local parsing; ModuleChannel ignores them |
| Join secret | **Env only** | Never in the manifest file |
| `grpc_advertise_address` | **Env / Compose** | Deployment-specific (`GRPC_ADVERTISE_ADDRESS`) |
| `MODULE_SLUG_OVERRIDE` | **Env** | Overrides manifest slug when community slugs collide |

Loader: `ModuleManifestLoader` + `BuildRegisterRequest(joinSecret, advertiseAddress, customizers?)`.

## Bardie capabilities vocabulary (host convention)

Capabilities are **open strings** on the wire. ModuleChannel never interprets them. The tables below are **Bardie conventions** shared by source modules and the host (Kithara’s Auth Harness gates via `Bardie.Harness.Auth.WellKnownAuthCapabilities`; source vocabulary lives in `Bardie.Module.Source.WellKnownSourceCapabilities`, used by `SourceModuleBase` and the Source Harness) — documented here so module authors see the vocabulary next to Register.

| Put in `capabilities[]` | Keep elsewhere |
|-------------------------|----------------|
| Optional RPCs / behaviours that some modules of the same kind omit | `kind` (`source` / `auth` / `client`) |
| Host routing gates (“may I call UpdateUserBinding / PauseTrack / Search fan-out?”) | Register `details.source.searchFields` — from manifest `source.searchFields` via module customizer |
| | Register `details.auth` JWKS — runtime customizer |
| | Register `details.client.authMode` + `permissionCeiling` — module customizer |

### MVP (advertise what you implement)

| Kind | Capability | Meaning | Who |
|------|------------|---------|-----|
| **source** | `search` | Implements `Search`; eligible for `/api/search` fan-out | Magpie yes; Starling typically no |
| **source** | `play` | Implements `StartTrack` / `StopTrack` (PCM to session FIFO) | Magpie, Starling, Catbird |
| **source** | `pause` | Implements `PauseTrack` / `ResumeTrack` without tearing down the job | Magpie yes; **Starling omits** |
| **source** | `prefetch` | Implements `PrefetchTrack` (warm blob cache; no FIFO write) | Magpie yes; Starling/Catbird typically omit |
| **auth** | `updateBinding` | Host may expose `UpdateUserBinding` + discovery `bind_form` (invite bind, self-service account update / module-signaled forced rotate) | **Bes yes**; IdP-only modules typically **no** |

### Auth — reserved (document now; advertise only when implemented)

| Capability | Why useful |
|------------|------------|
| `selfRegister` | Open signup via `bind_form` → `UpdateUserBinding` ceremony `bind` without operator seed (also surfaces `bind_form` on discovery) |
| `passwordReset` | Host/UI can expose reset; module owns ceremony via `bind_form` later |

**Not a module capability:** account linking stays **Kithara’s story** (explicit multi-provider link in the user DB / harness). Auth adapters only prove identity for their provider — they do not advertise `accountLink`.

### Do not put in `capabilities[]`

- `authenticate` / `refresh` / `getProviders` / `health` — core auth contract every well-known auth module speaks
- `login_form` / `bind_form` / `redirect` — discovery surfaces on `GetProviders` (host strips `bind_form` unless `updateBinding` / `selfRegister`)
- `updateUserBinding` — RPC name; advertise **`updateBinding`** instead
- Permission strings (`create_struna`, …) — **`client.permissionCeiling`** on Register (customizer)
- Source type labels (`youtube`, `live`, `files`) — do not invent these as capabilities
- `PrepareTrack` — out of MVP until the RPC exists
- `accountLink` — Kithara-owned linking, not an adapter Register flag

Auth-focused prose also lives in [grpc-auth-adapter.md](../interfaces/grpc-auth-adapter.md).

## Bootstrap modes

| Mode | Safe on | Register response |
|------|---------|-------------------|
| **`auto`** | Private Docker/Compose overlay, trusted LAN | May include client cert + **private key** PEMs after join-secret check |
| **`preshared`** | Public / untrusted paths | **No private keys on the wire.** Pre-place CA + module client material via a secure offline channel before process start |

**Do not use `auto` across the public internet.** Prefer `preshared` whenever module gRPC leaves a private overlay.

Env knobs: [configuration.md](configuration.md) (`BARDIE_MODULE_MTLS_BOOTSTRAP`, `BARDIE_GRPC_TLS_DATA_PATH`, `BARDIE_MODULE_MTLS_PRESHARED_DIR`).

## Host vs library vs participant

| Library (host) | Host (Kithara) | Library (participant) |
|----------------|----------------|-------------------------|
| Cert issue/validate, Kestrel helper, interceptor, outbound dial factory (`Bardie.Module.Channel`) | Module Registry **service**, `BARDIE_JOIN_SECRETS`, port binding, catalog projection | Channel: manifest, Register→PEM, Heartbeat, work-port TLS. Hosting: Program bootstrap + Compose env. Auth kit: JWT/JWKS when minting |

## Related

- [grpc-module-registry.md](../interfaces/grpc-module-registry.md) · [grpc-auth-adapter.md](../interfaces/grpc-auth-adapter.md) · [security-audit](../mvp/security-audit.md) · [configuration.md](configuration.md) · [Module.Hosting README](../../../libs/Bardie.Module.Hosting/README.md) · [Module.Auth README](../../../libs/Bardie.Module.Auth/README.md)

**Read next:** [grpc-module-registry.md](../interfaces/grpc-module-registry.md)
