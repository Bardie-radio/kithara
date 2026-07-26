# gRPC Auth Adapter Contract (v0.1 draft)

Auth adapters (**Bes**, **Argus**, **Hecate**, …) speak **one** work contract. Join is via [Module Registry](grpc-module-registry.md) (module dials Kithara) — `Register` is **not** on this service.

**Status:** v0.1 draft — RPC set and dial rules are frozen; packages published on nuget.org as `Bardie.Logos.Contracts`. Checked-in proto: [`auth_adapter.proto`](https://github.com/Bardie-radio/logos/blob/main/src/Bardie.Logos.Contracts/Protos/auth_adapter.proto) in `Bardie.Logos.Contracts` (wire package `bardie.auth.v1`).

**Unified token protocol for login: JWT.** Modules authenticate/verify and **return access + refresh JWTs** (mint their own, or forward a provider’s — Argus forwards OIDC tokens). Kithara stores users/bindings and verifies those JWTs via module JWKS.

Kithara does **not** mint auth-module login JWTs. It **does** mint JWTs for **ephemeral guest users** after guest-code exchange — see [auth](auth.md). Kithara also mints **claim-scoped** JWTs after invite OTP verification (**AUTH-INVITE**) — provider `kithara.claim`; see [auth](auth.md).

**Authenticate is login-only.** Credential/binding create and update go through **`UpdateUserBinding`** only. Modules must not mutate bindings inside `Authenticate`. Empty-DB bootstrap and admin provision are **host-owned invite OTP** — there is no `SeedAdminBinding` RPC and no `seedAdmin` capability.

```protobuf
service AuthAdapter {
  rpc Health(HealthRequest) returns (HealthResponse);
  rpc GetProviders(GetProvidersRequest) returns (GetProvidersResponse);
  rpc Authenticate(AuthenticateRequest) returns (AuthenticateResponse);
  rpc Refresh(RefreshRequest) returns (RefreshResponse);
  rpc UpdateUserBinding(UpdateUserBindingRequest) returns (UpdateUserBindingResponse);
}
```

Per-request `ValidateToken` against the module is **not** the hot path — Kithara verifies login JWTs locally using JWKS from Registry registration (or IdP JWKS URL Argus supplies).

## Capabilities (Registry)

Capabilities are **optional feature flags within kind `auth`**. They gate host RPCs; they are not the module type and not core verbs every auth adapter must speak (`Health` / `GetProviders` / `Authenticate` / `Refresh`). Full mesh vocabulary (source + auth, what belongs in `capabilities[]` vs elsewhere) lives in [module-channel.md](../operations/module-channel.md).

| Capability | Status | Meaning |
|------------|--------|---------|
| `updateBinding` | **MVP** | Host may expose `UpdateUserBinding` + discovery `bind_form` (invite bind, self-service update / module-signaled forced rotate) |
| `selfRegister` | Reserved | Open signup: host exposes `bind_form` → `UpdateUserBinding` ceremony `bind` without operator invite — advertise only when implemented |
| `passwordReset` | Reserved | Host/UI can expose reset; module owns ceremony via `bind_form` / dedicated flow later — advertise only when implemented |

**Not a module capability:** account linking stays **Kithara’s story** (explicit multi-provider link in the user DB / harness). Auth adapters only prove identity for their provider — they do not advertise `accountLink`.

**Bes** advertises **`updateBinding`** for MVP (`module.manifest.json`). **Argus** typically advertises neither — IdP users are discovered/linked, not locally password-edited on Kithara.

### `UpdateUserBinding`

One binding bag for **initial bind** and **later update** (module-signaled forced rotate + voluntary self-change). Ceremony distinguishes:

| Ceremony | When |
|----------|------|
| `bind` | First binding for this user↔provider (admin `/register` invite completion, open `selfRegister` later) |
| `update` | Replace existing binding (forced rotate, account settings) |

```protobuf
message UpdateUserBindingRequest {
  string provider_id = 1;
  string user_id = 2;
  bytes existing_binding_payload = 3;  // empty on first bind
  map<string, string> payload = 4;     // bind_form bag
  BindingCeremony ceremony = 5;        // bind | update
}

message UpdateUserBindingResponse {
  bool ok = 1;
  bytes binding_payload = 2;
  string external_subject = 3;
  bool must_rotate_credentials = 4;
  repeated string roles = 5;
  map<string, string> entities = 6;
  string error = 7;  // client-safe reason when ok=false
}
```

UI is gated by **`updateBinding`** (or reserved `selfRegister`) on Register **and** `bind_form` on discovery. There is **no** separate `update_form` / password-reset form entity.

**Security:** `UpdateUserBinding` is a privileged RPC. Only Kithara may invoke it — after Module Registry handshake, **mTLS** (client cert issued at Register) identifies Kithara→module calls. Modules must reject callers without a valid Kithara-issued cert.

## Discovery UI (no module-name branching)

Clients render login and binding UI from discovery by switching on `ProviderDescriptor.ui` / `bind_form` **only**. `id` is an opaque handle echoed back as `provider_id` — never `if (id == "bes")`.

| Surface | Client behaviour |
|---------|------------------|
| `ui.login_form` | Render fields; POST → `Authenticate` |
| `ui.redirect` | Navigate to `authorize_url`; return to **Kithara** callback |
| `bind_form` (optional) | Binding-data editor; clients re-auth via `login_form` / redirect **separately**, then POST bind bag only → `UpdateUserBinding` |

Kithara merges descriptors and forwards them on `GET /api/auth/discovery`. It does not interpret field lists or authorize URLs beyond routing.

## Key messages

```protobuf
message ProviderDescriptor {
  string id = 1;            // routing only — UI must not branch on this
  string display_name = 2;
  oneof ui {
    FormSchemaUi login_form = 10;
    RedirectUi redirect = 11;
  }
  FormSchemaUi bind_form = 20;  // optional; same bag for bind + update ceremonies
}

message AuthenticateRequest {
  string provider_id = 1;
  map<string, string> payload = 2;  // login_form bag only — no credential mutation
  bytes existing_binding_payload = 3;
}

message AuthenticateResponse {
  bool allowed = 1;
  string external_subject = 2;
  repeated string roles = 3;
  map<string, string> entities = 4;
  bytes binding_payload = 5;
  bool ensure_user = 6;
  string access_token = 7;
  string refresh_token = 8;
  string token_type = 9;
  int64 expires_in = 10;
  bool must_rotate_credentials = 11; // advisory on token; host denies control until cleared via UpdateUserBinding (module-signaled rotate only — not invite bind)
}
```

Invariants (frozen for v0.1):

1. **JWT in, JWT out** for auth-module login credentials.
2. **Module owns issue + refresh** for those JWTs; Kithara verifies and authorizes.
3. **Kithara owns** user DB rows; modules return binding payloads for Kithara to store.
4. **Kithara passes** `existing_binding_payload` so DB-less adapters (Bes) can verify password proofs without a local store.
5. **Capabilities** gate whether the host exposes `UpdateUserBinding` / `bind_form` (and later reserved caps).
6. **Binding mutation** is never on `Authenticate` — only `UpdateUserBinding`.
7. **Bootstrap / admin provision** uses host invite OTP + claim JWT — not module seed RPCs.

## How modules use the same RPCs

| Module | Discovery | Authenticate / tokens | Binding | Capabilities |
|--------|-----------|------------------------|---------|--------------|
| **Bes** | `login_form` + `bind_form` | Verifies password; **mints** JWT (+ refresh) | Opaque bind bag; step-up = Authenticate | `updateBinding` |
| **Argus** | `redirect` | Completes OIDC; **forwards** IdP JWTs | Optional / JIT | None (typical) |
| **Hecate** | future ceremony `ui` case | Completes WebAuthn; **mints** JWT (+ refresh) | TBD | TBD |

JWT-minting adapters (Bes, typically Hecate) may embed packable **`Bardie.Module.Auth`** for mint/refresh/JWKS Register attach and a thin `AuthAdapterModuleBase` (`Health`, provider-id checks, default `UpdateUserBinding` → Unimplemented). Password/OIDC/passkey ceremony stays in the module. Participant Program bootstrap + Bardie Compose env aliases live in **`Bardie.Module.Hosting`**. Mesh mTLS stays in **`Bardie.Module.Channel`**.

**Related:** [grpc-module-registry](grpc-module-registry.md) · [domains/auth-adapters.md](../domains/auth-adapters.md) · [interfaces/auth.md](auth.md) · [ADR 007](../adrs/007-auth-adapter-modules.md) · [Bardie.Logos.Contracts](https://github.com/Bardie-radio/logos/tree/main/src/Bardie.Logos.Contracts) · [Bardie.Module.Auth](https://github.com/Bardie-radio/kithara-logos-auth)

**Read next:** [uri-routing.md](uri-routing.md)
