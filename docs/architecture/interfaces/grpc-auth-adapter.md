# gRPC Auth Adapter Contract (v0.1 draft)

Auth adapters (**Bes**, **Argus**, **Hecate**, …) speak **one** work contract. Join is via [Module Registry](grpc-module-registry.md) (module dials Kithara) — `Register` is **not** on this service.

**Status:** v0.1 draft — RPC set and dial rules are frozen; field names may still evolve slightly before NuGet publish. Checked-in proto: [`libs/Bardie.Contracts/Protos/auth_adapter.proto`](../../../libs/Bardie.Contracts/Protos/auth_adapter.proto) (package `Bardie.Contracts`).

**Unified token protocol for login: JWT.** Modules authenticate/verify and **return access + refresh JWTs** (mint their own, or forward a provider’s — Argus forwards OIDC tokens). Kithara stores users/bindings and verifies those JWTs via module JWKS.

Kithara does **not** mint auth-module login JWTs. It **does** mint JWTs for **ephemeral guest users** after guest-code exchange — see [auth](auth.md).

There is **no** protocol-specific RPC. Redirect callbacks, form posts, and ceremony payloads all arrive as an opaque bag on `Authenticate`.

```protobuf
syntax = "proto3";

package bardie.auth.v1;

option csharp_namespace = "Bardie.Auth.V1";

service AuthAdapter {
  rpc Health(HealthRequest) returns (HealthResponse);
  rpc GetProviders(GetProvidersRequest) returns (GetProvidersResponse);
  rpc Authenticate(AuthenticateRequest) returns (AuthenticateResponse);
  rpc Refresh(RefreshRequest) returns (RefreshResponse);
  rpc SeedAdmin(SeedAdminRequest) returns (SeedAdminResponse);
  // User + binding persistence goes through Kithara — adapters do not open a DB.
}
```

Per-request `ValidateToken` against the module is **not** the hot path — Kithara verifies login JWTs locally using JWKS from Registry registration (or IdP JWKS URL Argus supplies).

## Capabilities (Registry)

Capabilities are **optional feature flags within kind `auth`**. They gate host RPCs; they are not the module type and not core verbs every auth adapter must speak (`Health` / `GetProviders` / `Authenticate` / `Refresh`). Full mesh vocabulary (source + auth, what belongs in `capabilities[]` vs elsewhere) lives in [module-channel.md](../operations/module-channel.md).

| Capability | Status | Meaning |
|------------|--------|---------|
| `seedAdmin` | **MVP** | Host may call `SeedAdmin` when the user DB is empty |
| `selfRegister` | Reserved | Open signup via `Authenticate` (e.g. Bes “register” form) without operator seed — advertise only when implemented |
| `passwordReset` | Reserved | Host/UI can expose reset; module owns ceremony in the opaque `Authenticate` bag — advertise only when implemented |

**Not a module capability:** account linking stays **Kithara’s story** (explicit multi-provider link in the user DB / harness). Auth adapters only prove identity for their provider — they do not advertise `accountLink`.

**Bes** advertises `seedAdmin` only for MVP (`module.manifest.json`). **Argus** typically does **not** — IdP users are discovered/linked, not locally invented.

### `SeedAdmin`

Called by Kithara when the user DB is empty (or operator forces re-seed). Module creates admin credentials (random secret), asks Kithara to persist user + binding (`ensure_user` / binding payload), and returns a **welcome fragment** (credentials text). Kithara writes that fragment to **its container log** — never to a public HTTP surface.

```protobuf
message SeedAdminRequest {
  string correlation_id = 1;  // optional operator hint — not secrets
}

message SeedAdminResponse {
  bool created = 1;
  string welcome_log_text = 2;  // includes one-time credentials; Kithara logs only
  string external_subject = 3;
  bytes binding_payload = 4;
  bool ensure_user = 5;
  bool must_rotate_credentials = 6;
  repeated string roles = 7;
  map<string, string> entities = 8;
}
```

Seeded admins **must** change credentials on first successful login (`must_rotate_credentials` on the user). The same flag can later force rotation for any user.

**Security:** `SeedAdmin` is a privileged RPC. Only Kithara may invoke it — after Module Registry handshake, **mTLS** (client cert issued at Register) identifies Kithara→module calls. Modules must reject callers without a valid Kithara-issued cert. Same class of protection applies to other risky RPCs.

## Discovery UI (no module-name branching)

Clients render login from discovery by switching on `ProviderDescriptor.ui` **only**. `id` is an opaque handle echoed back as `provider_id` on `Authenticate` / `Refresh` — never `if (id == "bes")`.

| `ui` case | Client behaviour |
|-----------|------------------|
| `form_schema` | Render `fields`; POST payload keys = `FormField.name` |
| `redirect` | Navigate to `authorize_url`; return to **Kithara** callback |

Kithara merges descriptors and forwards them on `GET /api/auth/discovery`. It does not interpret field lists or authorize URLs beyond routing.

## Key messages

```protobuf
message ProviderDescriptor {
  string id = 1;            // routing only — UI must not branch on this
  string display_name = 2;
  oneof ui {
    FormSchemaUi form_schema = 10;
    RedirectUi redirect = 11;
  }
}

message FormSchemaUi {
  repeated FormField fields = 1;
}

message FormField {
  string name = 1;       // key in Authenticate.payload
  string label = 2;
  string input_type = 3; // text | password | email | …
  bool required = 4;
}

message RedirectUi {
  string authorize_url = 1;
}

message AuthenticateRequest {
  string provider_id = 1;
  map<string, string> payload = 2;  // credentials, callback params, ceremony data, …
  bytes existing_binding_payload = 3;  // Kithara-loaded binding (empty = none)
}

message AuthenticateResponse {
  bool allowed = 1;
  string external_subject = 2;
  repeated string roles = 3;
  map<string, string> entities = 4;
  bytes binding_payload = 5;
  bool ensure_user = 6;
  string access_token = 7;   // JWT — minted by module or forwarded (e.g. OIDC)
  string refresh_token = 8;  // opaque to Kithara; module owns refresh semantics
  string token_type = 9;     // "Bearer"
  int64 expires_in = 10;
  bool must_rotate_credentials = 11; // honor / set when user must change creds
}

message RefreshRequest {
  string provider_id = 1;
  string refresh_token = 2;
}

message RefreshResponse {
  bool allowed = 1;
  string access_token = 2;
  string refresh_token = 3;
  int64 expires_in = 4;
  string token_type = 5;
}
```

Invariants (frozen for v0.1):

1. **JWT in, JWT out** for auth-module login credentials.
2. **Module owns issue + refresh** for those JWTs; Kithara verifies and authorizes.
3. **Kithara owns** user DB rows when the module asks to store them (`ensure_user` + `binding_payload`).
4. **Kithara passes** `existing_binding_payload` so DB-less adapters (Bes) can verify password proofs without a local store.
5. **Capabilities** decide whether Kithara may call `SeedAdmin` (and later reserved caps).

## How modules use the same RPCs

| Module | `GetProviders` `ui` | `Authenticate` / tokens | `seedAdmin` |
|--------|---------------------|-------------------------|-------------|
| **Bes** | `form_schema` (username/password fields) | Verifies password; **mints** JWT (+ refresh) | Yes |
| **Argus** | `redirect` (`authorize_url`) | Completes OIDC; **forwards** IdP JWTs | No (typical) |
| **Hecate** | future ceremony `ui` case | Completes WebAuthn; **mints** JWT (+ refresh) | TBD |

JWT-minting adapters (Bes, typically Hecate) may embed packable **`Bardie.Module.Auth`** for mint/refresh/JWKS Register attach and a thin `AuthAdapterModuleBase` (`Health`, provider-id checks, default `SeedAdmin` → Unimplemented). Password/OIDC/passkey ceremony stays in the module. Participant Program bootstrap + Bardie Compose env aliases live in **`Bardie.Module.Hosting`**. Mesh mTLS stays in **`Bardie.Module.Channel`**.

**Related:** [grpc-module-registry](grpc-module-registry.md) · [domains/auth-adapters.md](../domains/auth-adapters.md) · [interfaces/auth.md](auth.md) · [ADR 007](../adrs/007-auth-adapter-modules.md) · [Bardie.Contracts](../../../libs/Bardie.Contracts/README.md) · [Bardie.Module.Auth](../../../libs/Bardie.Module.Auth/README.md)

**Read next:** [uri-routing.md](uri-routing.md)
