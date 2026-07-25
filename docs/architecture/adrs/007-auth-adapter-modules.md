# ADR 007: Auth Adapter Modules

**Status:** Accepted (amended: modules issue/forward login JWTs + own refresh; Kithara verifies via JWKS + owns user DB and join secrets; Kithara mints JWTs for **ephemeral guest users** and **claim-scoped invite JWTs** (`kithara.claim`); binding create/update via `UpdateUserBinding` / `bind_form`; **AUTH-INVITE** replaces `seedAdmin` / `SeedAdminBinding` for bootstrap and admin provision; **MustRotateCredentials** gates control only for module-signaled forced rotate — not invite completion).

## Context

Auth must be modular (login+password, OIDC, passkeys, custom) without forcing a self-hosted IdP for every deploy. Operators want **one Bardie user database** (Kithara’s). OIDC already issues JWTs; forging a second “Bardie-only” **login** session token in core fights that model. Modules must stay decoupled. Public edge should stay Kithara + UI clients where protocolally possible. Short guest codes must not ride every control request. Empty DB needs a safe bootstrap. Guest joiners need ACLs and search-cache ownership like other principals.

## Decision

- **Auth Harness** lives **inside Kithara** (discovery merge, route opaque auth/refresh payloads, **login JWT verification** via registered JWKS, **join secrets**, listen/guest secrets, **guest-code exchange**, **invite OTP bootstrap / admin provision**). Kithara does **not** mint auth-module **login** JWTs (except **claim JWTs** after invite OTP for bind completion).
- **Ephemeral guest users:** On protected-control guest-code exchange, Kithara creates a **new ephemeral guest user** per joiner, mints access (+ refresh) JWTs for that user, and destroys those users when the Struna is deleted. Rotating the guest code **blocks new joins only**. Not an auth-module account; still a `User` row for ACL / search cache. See [struna-access](../domains/struna-access.md).
- **No built-in auth provider.** Every login method is a separate auth-adapter container on gRPC.
- **Named adapters:** **Bes** (password, MVP), **Argus** (OIDC, v0.2), **Hecate** (passkeys, future). Modules are independent — no cross-module “modes.”
- **Unified adapter contract:** `GetProviders` + `Authenticate` + `Refresh` + `UpdateUserBinding`. No protocol-specific RPCs. Opaque payloads for forms, callbacks, ceremonies. Authenticate is login-only — binding mutation uses `UpdateUserBinding` / `bind_form`. **No `SeedAdminBinding`.**
- **Capabilities** at Module Registry `Register` (e.g. `updateBinding`). Bes advertises binding update; Argus typically does not.
- **AUTH-INVITE:** When the user DB is empty, Kithara invents DEFAULT_ADMIN with a **host registration OTP** (log once). **`POST /api/auth/claim`** verifies OTP and mints a claim JWT (`must_complete_binding`). First bind via **`POST /api/auth/bindings/{provider}`** ceremony `bind` clears invite state; **`MustRotateCredentials` stays false** for invite completion. Admin **`POST /api/auth/register`** is username-only and returns **`registration_password` once** per user for the same path.
- **Discovery schemas:** `login_form` → Authenticate; optional `bind_form` → UpdateUserBinding (ceremony bind | update).
- **Unified credential protocol for login: JWT.** Modules return `access_token` (JWT) + `refresh_token`. **Argus** forwards OIDC tokens; **Bes** / **Hecate** mint their own. Kithara verifies login JWTs locally (module/IdP JWKS).
- **User core + `UserAuthBinding`** live only in Kithara. Kinds: durable, managed, ephemeral guest — see [glossary](../glossary.md).
- **Client modules** register via the same Module Registry gRPC as source/auth ([grpc-module-registry](../interfaces/grpc-module-registry.md)), declaring **user-aware** or **static**.
- **Join secrets** in Kithara config authenticate all modules.
- **Public edge:** Kithara + UI clients. Adapters stay internal (BFF).
- **Listen tokens** and **guest codes** are Struna secrets owned by Kithara.
- **Explicit account linking**; **provider priority tier-list** when bindings disagree.

## Consequences

- One Bardie user DB; login token issuance stays with auth modules / IdPs.
- Guests are first-class principals for ACL and search-result ownership without becoming durable accounts.
- Empty deploy can bootstrap via **host invite OTP** without a hardcoded Kithara password env or module seed RPC.
- Plume never hardcodes password fields; stack works without Plume.
- MVP Compose includes `bes` alongside Kithara.

## Alternatives considered

- **Kithara mints Bardie identity JWT after module allow (for login)** — rejected; duplicates OIDC issuing.
- **Guest = bare capability JWT, not a User** — superseded; ephemeral guest **users** needed for refresh, ACL, and search-cache ownership.
- **Guest code on every control request** — rejected; exchange for session JWTs instead.
- **Guest exchange via auth modules (Bes/Argus)** — rejected; guests are Kithara-local principals.
- **Built-in local password inside Kithara** — rejected; every auth method is a module (Bes).
- **Per-request ValidateToken to the module** — rejected as the hot path; prefer local JWKS verify.
- **Protocol-specific RPCs** (e.g. `ExchangeOidcCode`) — rejected; breaks a unified module contract.
- **Adapter-owned user database** — rejected; second Bardie DB.
- **Adapter-hosted login HTTP** — rejected; browsers must not call modules (ADR 003).
- **Separate bot tokens vs join secrets** — rejected; one **join secret** class.
- **First admin via Kithara env password** — rejected; prefer host invite OTP (AUTH-INVITE).
- **Module `SeedAdminBinding` / `seedAdmin` capability** — superseded by AUTH-INVITE (Jul 2026 amendment).

**Related:** [domains/auth-adapters.md](../domains/auth-adapters.md) · [interfaces/auth.md](../interfaces/auth.md) · [interfaces/grpc-auth-adapter.md](../interfaces/grpc-auth-adapter.md)

**Read next:** [008-otel-observability.md](008-otel-observability.md)
