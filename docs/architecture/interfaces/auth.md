# Auth API and Permissions

Clients authenticate through **Kithara**, not by calling auth adapters on the public edge. Plume is optional — any client can use the same REST flow. Adapters stay on the internal gRPC plane.

**Token model:**

| Class | Who mints | Who verifies | Use |
|-------|-----------|--------------|-----|
| **Login JWT** (+ refresh) | Auth module (issue or forward) | Kithara via module JWKS | Durable users (Bes/Argus/…) |
| **Ephemeral guest JWT** (+ refresh) | **Kithara** | Kithara via its signing key | Guest-code joiners on a protected-control Struna |
| **Managed-user credentials** | Static client admin (future mTLS RPCs mint/reset) | Kithara | Beak-style tenancy users |

Kithara does **not** mint auth-module **login** JWTs. It **does** mint JWTs for **ephemeral guest users** after guest-code exchange. **Join secrets** bootstrap module `Register` (then Heartbeat is mTLS) — not end-user credentials and **not** standing static-module admin on `/api`.

```mermaid
sequenceDiagram
  participant Client
  participant Kithara
  participant Adapter as Auth_adapter

  Client->>Kithara: GET /api/auth/discovery
  Kithara->>Adapter: GetProviders
  Adapter-->>Kithara: providers
  Kithara-->>Client: merged discovery
  Client->>Kithara: POST /api/auth/authenticate or callback
  Kithara->>Adapter: Authenticate opaque payload
  Adapter-->>Kithara: allowed + roles + access_jwt + refresh
  Note over Kithara: ensure User/binding if asked
  Kithara-->>Client: access JWT + refresh (from module)
  Client->>Kithara: API call Bearer login JWT
  Note over Kithara: Verify JWT via module JWKS
  Client->>Kithara: POST /api/auth/refresh
  Kithara->>Adapter: Refresh
  Adapter-->>Kithara: new access JWT + refresh
```

## Discovery

`GET /api/auth/discovery` — Auth Harness merges `GetProviders()` from registered adapters. There is no built-in provider.

MVP: one provider from **Bes** with `ui.login_form` (typed fields) plus optional `bind_form` for register / rotate / self-update. Client (e.g. Plume) renders from the field lists — adapters do **not** host login HTML. Clients switch on the `ui` oneof case and `bind_form` presence only; they must not branch on provider `id`.

Redirect-style providers (Argus) set `ui.redirect.authorize_url`. The browser returns to **Kithara**, not to Plume or the adapter. Kithara forwards the opaque callback payload to that adapter’s `Authenticate`. Path: `/api/auth/callback` under `/api/*` (no separate public `/auth` prefix for MVP).

## Authenticate, refresh, binding, and API access

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/auth/authenticate` | `login_form` bag → module `Authenticate` → if allowed, **module-issued (or forwarded) JWT** + refresh |
| POST | `/api/auth/refresh` | Opaque refresh → **module** `Refresh` **or** host guest remint (see below) |
| GET/POST | `/api/auth/callback` | Browser return for redirect flows; same path as authenticate — **not** OIDC-named |
| POST | `/api/auth/register` | Admin: create durable `User` (**username only**); response returns **`registration_password` once** (invite OTP) |
| POST | `/api/auth/claim` | **Public.** Body: `username` + `registration_password` → Kithara-minted **claim-scoped JWT** (`bardie_provider=kithara.claim`, `must_complete_binding`) |
| POST | `/api/auth/bindings/{provider}` | Binding create/update → `UpdateUserBinding` (`bind` if none, else `update`) + `bind_form` bag — invite completion uses ceremony `bind`; includes module-signaled forced rotate |

Kithara does not mint login JWTs and does not interpret provider-specific crypto beyond verifying signatures with the module’s registered JWKS. It routes the bag, persists binding data when asked, and enforces Struna ACLs using claims/roles from the verified JWT (plus DB).

- **Refresh (login):** entirely on the auth-module side.
- **Refresh (ephemeral guest — Phase 6 / GUEST-REF-001):** host path on the same `POST /api/auth/refresh`. Detect Kithara guest (e.g. `bardie_provider=kithara.guest`), validate + remint until Struna teardown / capped lifetime — do **not** dial an auth adapter.
- Revoke / logout: module- and IdP-dependent for login users; guests die with the Struna. Rotating the guest code **does not** kill existing guests — it only blocks new exchanges.
- **`must_complete_binding`:** invitees (claim JWT, `bardie_bind_only`, **no role claims**) may call **`GET /api/auth/me`** and **`POST /api/auth/bindings/{provider}`** only — all other authenticated REST returns `403` + `must_complete_binding` (**AUTH-CLAIM-001**). Invite roles live on the user row until bind. Ceremony `bind` clears invite state; claim **access and refresh** both stop resolving after `CompleteInvite`. Invite OTP cleared on the user row.
- **`must_rotate_credentials`:** **module-signaled forced rotate only** — not invite completion. Host returns `403` + `credentials_rotation_required` on play/queue/skip/grants/create while the flag is set; binding update remains allowed (AUTH-ROT-002).
- **`POST /api/auth/register`:** requires a fully bound admin — claim / pending-bind principals are rejected even if the JWT carries `admin`.

## Bootstrap admin and invites (AUTH-INVITE)

When the user DB is empty, Kithara invents **DEFAULT_ADMIN** with a **host-owned registration OTP** (logged once to the **Kithara container log** — same trust model as guest codes, not a public HTTP field). Operator flow: read OTP from logs → `POST /api/auth/claim` → claim JWT (**bindings + `/me` only** until bind) → `POST /api/auth/bindings/{provider}` ceremony **`bind`** with the module’s `bind_form` bag (password-only for Bes; host injects immutable `User.Username`) → claim tokens die → normal login JWT path. Admin **`POST /api/auth/register`** creates additional users (**username only**, immutable thereafter) and returns **`registration_password` once** per user for the same claim → bind path — **not** callable from a claim session.

No `SeedAdminBinding` RPC and no `seedAdmin` capability — bootstrap and provision are entirely host-side invite OTP. See [grpc-auth-adapter](grpc-auth-adapter.md) and [domains/auth-adapters](../domains/auth-adapters.md).

## Guest control (protected Struna)

Short **guest codes** are Kithara-owned bootstrap secrets — **exchange only**, never sent on every control call.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/streams/{id}/guest/exchange` | Body: guest code → create **ephemeral guest user** + Kithara-signed JWT (+ refresh) |

Each exchange = **one new ephemeral guest user** for that joiner, scoped to the Struna. Destroyed when the Struna is deleted. Details: [struna-access](../domains/struna-access.md).

## Secrets ownership

| Secret | Owner | Purpose |
|--------|-------|---------|
| Login access JWT / refresh | **Auth module** (issue or forward) | Durable API clients; Kithara verifies via module JWKS |
| Ephemeral guest JWT / refresh | **Kithara** (mint) | Guest joiners after code exchange. Signing key: env if set, else auto-generated + persisted |
| Guest code | **Kithara** (on Struna) | Bootstrap only — exchange for ephemeral guest session |
| Listen token | **Kithara** (on Struna) | Protected playback `/stream/{slug}?token=` (no exchange) |
| **Join secret** | **Kithara** config | Module identity — `Register` bootstrap only (Heartbeat = mTLS). Static managed-user **admin** → future mTLS client→host RPCs, not this secret on HTTP |

## User kinds (one DB)

Thin `User` rows live in Kithara’s database. Kind matters for lifetime and token minting:

| Kind | Lifetime | Tokens | Binding |
|------|----------|--------|---------|
| **Durable user** | Until deleted | Auth-module JWT | `UserAuthBinding` |
| **Managed user** | Until module revokes | Per-user credentials (static client) | `managed_by_module` + tenancy ref |
| **Ephemeral guest user** | Until Struna delete | Kithara-minted JWT | None (Struna-scoped) |

See [glossary](../glossary.md). Auth modules have no separate user DB.

## Client modules: user-aware vs static

Every client module **Registers over gRPC** like any other module ([grpc-module-registry](grpc-module-registry.md)). Then:

| Mode | Meaning | Credential on `/api` |
|------|---------|----------------------|
| **user-aware** | End users log in | Bearer **login JWT** from an auth module |
| **static** | Owns many **managed users** | **Per-user credentials** for day-to-day `/api`; module admin via future **mTLS** RPCs |

See [clients](../domains/clients.md).

## Permission / ACL (MVP)

| Principal | Create Struna | Control a Struna | Search + use own result refs |
|-----------|---------------|------------------|------------------------------|
| **Durable user** (registered via auth module) | Yes | Owner, or **grant** from owner (private); protected-control guests use guest path | Yes |
| **Managed user** (static UI) | Up to module’s **advertised ceiling** (typical: create + manage own Strunas) | Same, within ceiling; create-time or runtime entity scope ≤ ceiling; unset → default to advertised set | Yes |
| **Ephemeral guest user** | **No** | **Only** the Struna whose guest code they exchanged | Yes (cleared on Struna teardown) |

**Ownership:** stored on the Struna model (`OwnerUserId` or equivalent) at create time = creator.

**Private control:** owner **plus** explicit grants to other durable/managed users. **Phase 6:** owner-only CRUD under `/api/streams/{id}/grants` (persist `StrunaControlGrant`). Ephemeral guests are not on that list — they use the protected guest-code path instead.

**Static module ceiling (Phase 6 enforce):** declared at Module Registry handshake and stored as `permission_ceiling` for managed users. Create-struna and grant mutations for managed principals must stay ≤ ceiling (deny above). When the static UI creates a managed user it may narrow scope; it **cannot** raise rights above the advertised ceiling. If it sets nothing, Kithara applies the advertised defaults. User-aware clients are unconstrained by ceiling.

## Permission matrix (summary)

| Action | Who |
|--------|-----|
| Create Struna | Any durable user; managed users if ceiling allows |
| Control (private) | Owner + grants |
| Control (protected) | Ephemeral guests for **that** Struna only; also owner/grants as durable principals |
| Listen (private) | Per Struna listen ACL / auth |
| Guest code exchange | Valid code + rate limit (no prior login) |
| Use search result refs | **Same principal** that ran the search |
| Link auth providers | Durable authenticated user |

**Org roles** may arrive in login JWT claims and/or from the module’s authenticate result stored on the binding. **Provider priority tier-list** arbitrates when multiple bindings disagree. **Struna ACLs** always live in Kithara.

## Join secrets

Long-lived secrets in Kithara config for **every** module (source, auth, client). They authenticate **`Register` only** (bootstrap before mTLS). **Heartbeat** and later work/admin RPCs use the module client cert. Ordinary Struna/API work for static modules uses each managed user’s own credentials — never the join secret on `/api`.

**Related:** [domains/auth-adapters.md](../domains/auth-adapters.md) · [grpc-auth-adapter.md](grpc-auth-adapter.md) · [struna-access](../domains/struna-access.md) · [ADR 007](../adrs/007-auth-adapter-modules.md)

**Read next:** [rest-api.md](rest-api.md)
