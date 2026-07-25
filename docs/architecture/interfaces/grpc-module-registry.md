# gRPC Module Registry (v0.1 draft)

Every Bardie module — **source**, **auth**, and **client** — joins the same way: the module **dials Kithara** and calls `Register` (plus heartbeats). There is no special case for Plume vs Magpie vs Bes on the join path.

**Status:** v0.1 draft — RPC set and dial rules are frozen; field names may still evolve slightly before NuGet publish. Checked-in proto: [`libs/Bardie.Contracts/Protos/module_registry.proto`](../../../libs/Bardie.Contracts/Protos/module_registry.proto) (package `Bardie.Contracts`).

Kithara hosts this service on internal gRPC (`:5000`). Modules default `KITHARA_GRPC_ADDRESS` to Compose DNS (e.g. `kithara:5000`) so local stacks need little wiring.

```protobuf
syntax = "proto3";

package bardie.modules.v1;

option csharp_namespace = "Bardie.Modules.V1";

service ModuleRegistry {
  rpc Register(RegisterRequest) returns (RegisterResponse);
  rpc Heartbeat(HeartbeatRequest) returns (HeartbeatResponse);
}

message RegisterRequest {
  string slug = 1;                    // lowercase codename; operator may override via env
  string join_secret = 2;             // bootstrap trust (before mTLS cert exists)
  string kind = 3;                    // open string — not a closed enum
  repeated string capabilities = 4;   // e.g. search, play, pause, prefetch, seedAdmin
  string grpc_advertise_address = 5;  // where the host dials this module for work RPCs
  oneof details {
    SourceRegisterDetails source = 10;   // optional; used when kind is well-known "source"
    AuthRegisterDetails auth = 11;
    ClientRegisterDetails client = 12;
  }
}

message SourceRegisterDetails {
  repeated SearchFieldDescriptor search_fields = 1;
}

message SearchFieldDescriptor {
  string name = 1;     // title (mandatory for searchable modules), artist, owner, …
  bool required = 2;
}

message AuthRegisterDetails {
  string jwks_uri = 1;    // URL host fetches for login-JWT verify
  string jwks_json = 2;   // optional inline JWKS snapshot at Register
}

message ClientRegisterDetails {
  string auth_mode = 1;                    // "user-aware" | "static"
  repeated string permission_ceiling = 2;  // static modules only; max rights for managed users
}

message RegisterResponse {
  // Populated in bootstrap mode `auto` (private mesh). Empty in `preshared`.
  string client_certificate_pem = 1;
  string client_private_key_pem = 2;
  string ca_certificate_pem = 3;
  // Non-secret metadata (safe in both modes)
  string ca_thumbprint = 4;
  int64 certificate_expires_unix = 5;
}

// Identity after Register is the mTLS client certificate — no join_secret here.
message HeartbeatRequest {
  string slug = 1;
}

message HeartbeatResponse {
  bool ok = 1;
  int64 next_heartbeat_after_seconds = 2;
}
```

## Kind is open; Bardie well-known values are host convention

`kind` is a **string**, not a protobuf enum. **ModuleChannel** never interprets it — only slug + certs matter for mTLS.

| Kind value | Who defines it | Kithara Phase 1 behaviour |
|------------|----------------|---------------------------|
| `source` | Bardie well-known (`WellKnownModuleKinds`) | Upsert source harness catalog; use `details.source` when present |
| `auth` | Bardie well-known | Upsert auth harness catalog; use `details.auth` when present |
| `client` | Bardie well-known | Registry only |
| any other non-empty string | Host / external project | **Register + Heartbeat still succeed**; no harness catalog projection |

Other hosts can reuse the same join RPC + ModuleChannel and map their own kind strings (or ignore `oneof details`). Bardie-shaped `oneof` branches stay optional typed bags for well-known kinds — they are not a closed taxonomy of the mesh.

## Dial rules

| Direction | When |
|-----------|------|
| **Module → Kithara** | `Register`, `Heartbeat`, storage put/get |
| **Kithara → module** | Each work RPC (`Search`, `StartTrack`, `Authenticate`, `UpdateUserBinding`, `SeedAdminBinding`, …) as a **fresh dial** to `grpc_advertise_address` |

Per-call dials keep operations atomic: one RPC = one span, one auth decision, easier timeouts and least-privilege checks. No long-lived command stream from module to Kithara for work.

## Channel security

1. First contact: `Register` with **join secret**.
2. Cert material depends on ModuleChannel bootstrap mode (`BARDIE_MODULE_MTLS_BOOTSTRAP`):
   - **`auto`** (default for private Compose/LAN): host **issues** a client cert and returns PEM fields on `RegisterResponse`. **Not for public networks** — private keys travel on the wire.
   - **`preshared`**: operator pre-places CA + module client cert/key offline; response PEM key fields stay **empty**; clients must not require them.
3. After pairing, the **whole gRPC surface** (both directions) uses **mTLS**. `Heartbeat` renews liveness (and may later rotate certs); it does **not** carry the join secret.

The join secret is only the bootstrap; it is not a standing impersonation key for work RPCs once mTLS is up. **Caveat:** anyone who holds the join secret can still call `Register` again whenever the registry accepts that slug (restart, TTL gap, cold start) and, in **auto**, receive a new client key — see [security-audit](../mvp/security-audit.md) (`MESH-REG-001`).

## Rules

| Rule | Why |
|------|-----|
| **All modules Register over gRPC** | One join surface; UI modules are not “REST-only citizens” |
| **Join secret required on Register** | Bootstrap identity for every kind |
| **`kind` is an open string** | Mesh join stays reusable; product taxonomies live in the host |
| **Well-known kinds may carry typed `oneof details`** | JWKS / search schema / client auth mode without parallel RPCs |
| **Unknown kinds are registry-only (Phase 1)** | Still mTLS-paired; no Auth/Source catalog upsert |
| Capabilities advertised at Register | Host routes only what the module claims (open strings; Bardie maps well-known values in the host — see [module-channel](../operations/module-channel.md)) |
| **Static clients advertise a permission ceiling** | Managed users cannot be granted rights above what the module declared at handshake |
| **Heartbeat is mTLS-only** | No join secret on the steady-state path |
| **REST `/api` is for end users** | Client modules call REST to turn SPI into UX — not to join the mesh |

Work RPCs live on **per-kind contracts** the module hosts at `grpc_advertise_address` (Source/Auth work protos are separate — not part of this freeze).

## Client modules

Same `Register` as everyone else: join secret + `kind=client` + optional `ClientRegisterDetails` (`user-aware` \| `static` + permission ceiling when static). Day-to-day Struna control still uses REST `/api` — see [clients](../domains/clients.md).

**Locked:** all clients use Registry `Register` + `Heartbeat` only for mesh membership. There are **no** day-to-day client work RPCs. **Static** clients will get **client→host managed-user admin** RPCs (mTLS slug identity) when Beak needs them — **not** join-secret admin on `/api`. User-aware clients (Plume) never need that surface.

## Observability

Each work RPC is its own client call from Kithara → module: propagate W3C `traceparent`, record module slug + RPC name. Module OTel names stay `bardie.source.*` / `bardie.auth.*` / `bardie.plume` (etc.).

**Related:** [grpc-source-module](grpc-source-module.md) · [grpc-auth-adapter](grpc-auth-adapter.md) · [clients](../domains/clients.md) · [module-channel](../operations/module-channel.md) · [ADR 003](../adrs/003-grpc-control-plane.md) · [security-audit](../mvp/security-audit.md)

**Read next:** [grpc-source-module.md](grpc-source-module.md)
