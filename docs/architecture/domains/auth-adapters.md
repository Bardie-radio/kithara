# Auth Adapters

Auth modules plug into Kithara’s **Auth Harness** over one shared gRPC contract. Modules are **decoupled** from each other — deploying Bes does not configure Argus, and vice versa. There is no built-in auth inside Kithara.

**Split of responsibility:**

| Layer | Owns |
|-------|------|
| **Auth module** (Bes, Argus, Hecate, …) | **`login_form` → Authenticate** (issue/forward JWTs + refresh); **`bind_form` → UpdateUserBinding** (bind + update ceremonies) |
| **Kithara** | Sole `User` rows (`/register`, invite bootstrap); **verify** login JWTs via module JWKS; **mint claim JWTs** after invite OTP; **mint/verify ephemeral guest JWTs**; listen/guest secrets; **join secrets**; merge discovery; route opaque bags; **MustRotate** + **must_complete_binding** control gates |

Everything user-facing for **login** uses the **same JWT protocol** from auth modules. Argus typically **passes through** OIDC tokens; Bes/Hecate **forge** their own JWTs. Kithara does not mint login access tokens — it mints JWTs only for **ephemeral guest users** after guest-code exchange.


```mermaid
flowchart TB
  Client -->|discovery / authenticate / refresh| Kithara
  Kithara --> DB[(User + UserAuthBinding)]
  Kithara -->|"GetProviders / Authenticate / Refresh / UpdateUserBinding"| AuthBox
  subgraph AuthBox [Auth adapters — shared gRPC]
    Bes[Bes]
    Argus[Argus]
    Hecate[Hecate]
  end
  Argus -->|OIDC tokens and refresh| IdP[OIDC IdP]
```

## Providers

| Provider | Shape | Role |
|----------|-------|------|
| **Bes** (MVP) | Container `bes` | Login+password; mints JWT; discovery `login_form` + `bind_form` — deep dive: [Bes docs](https://github.com/Bardie-radio/bes/tree/main/docs/architecture) |
| **Argus** (v0.2) | Container `argus` | OIDC; forwards IdP JWT — [planned](https://github.com/Bardie-radio/argus/blob/main/docs/architecture/01-planned-role.md) |
| **Hecate** (future) | Container `hecate` | Passkeys — [planned](https://github.com/Bardie-radio/hecate/blob/main/docs/architecture/01-planned-role.md) |

## Client UI and public edge

**User-facing surfaces** are Kithara (REST / callbacks) and UI client modules (Plume, Beak, Cauda, …). Auth adapters stay on the **internal** network.

Clients render login / binding UI from discovery by switching on `ProviderDescriptor.ui` / `bind_form` — **not** on provider/module name:

- `login_form` — client renders fields; POST → Authenticate (MVP Bes)
- `bind_form` — module-owned binding data (initial bind **and** later update; ceremony on the RPC). Clients re-authenticate with `login_form` / redirect first, then POST **only** the bind bag → host → `UpdateUserBinding` (do not merge login credentials into the binding payload)
- `redirect` — browser goes to `authorize_url`; returns to a **Kithara** callback
- future ceremony case for passkeys — still mode-based, not `if hecate`

Adapters do **not** expose a public HTTP login surface.

### Can auth stay fully behind Kithara?

**Intent: yes** for the planned modules — BFF-style. Summary:

- **Bes** — credentials POST to Kithara → gRPC `Authenticate` → Bes mints JWT ([Bes contracts](https://github.com/Bardie-radio/bes/blob/main/docs/architecture/02-contracts.md)).
- **Argus** — IdP redirect → Kithara callback → Argus forwards IdP JWTs ([planned](https://github.com/Bardie-radio/argus/blob/main/docs/architecture/01-planned-role.md)).
- **Hecate** — WebAuthn via Kithara ↔ Hecate; Hecate mints JWT ([planned](https://github.com/Bardie-radio/hecate/blob/main/docs/architecture/01-planned-role.md)).

The only other public party in OIDC is the **IdP** itself. Adapters do **not** expose a public HTTP login surface.

## User core + binding store

```text
User
  id, username (unique, immutable login id), created_at, status, …
       ← Kithara-owned only; username set at invite/bootstrap, used for login
       ← display_name (mutable host profile) reserved / backlog — not bind_form

UserAuthBinding
  user_id + provider_slug       ← composite key (bes, argus, hecate, …)
  external_subject              ← pinned to User.Username for durable invite users
  payload (JSON)                ← module-owned binding material (e.g. password hash)
```

| Provider | Typical `payload` examples |
|----------|----------------------------|
| Bes | password hash, reset metadata — **not** username |
| Argus | `sub`, claims snapshot, IdP refresh handle if needed |
| Hecate | credential ids / attestation metadata |

**Username is immutable.** Host invents it on empty-DB bootstrap (`admin`) or admin `POST /api/auth/register`. Clients log in with that id (`login_form` username = `User.Username`). Modules must not expose a rename path via `bind_form`.

First successful login can JIT-provision a `User` + binding when the module asks Kithara to store the user.

User **kinds** (durable / managed / ephemeral guest) — [glossary](../glossary.md). Ephemeral guests have no `UserAuthBinding`.

### First admin / empty DB (AUTH-INVITE)

When the user DB is empty, Kithara creates **DEFAULT_ADMIN** with a **host-owned registration OTP** and logs it once to the **Kithara container log**. The operator claims via `POST /api/auth/claim`, then completes first bind with `POST /api/auth/bindings/{provider}` ceremony **`bind`** (Bes `bind_form`). **`MustRotateCredentials` stays false** for invite completion — control gating uses **`must_complete_binding`** on the claim JWT until bind succeeds; the invite OTP is cleared on bind.

Additional admins: authenticated operator **`POST /api/auth/register`** (**username only**) → **`registration_password` once** → same claim → bind path. No module seed RPC; Bes advertises **`updateBinding`** only. Details: [grpc-auth-adapter](../interfaces/grpc-auth-adapter.md), [auth](../interfaces/auth.md).

## Account linking

Users may **explicitly** link/merge bindings from different providers (prove both sides). No auto-link by email.

**Provider priority tier-list** (env/config at container start; admin API optional later) orders provider slugs when mapped org roles/claims disagree. Struna ACLs are unaffected — they stay in Kithara.

## Join secrets vs user JWTs

**Join secrets** bootstrap module `Register` (Heartbeat is mTLS). They are not user session credentials and must not be used as standing `/api` admin keys. **Static** client modules (e.g. Beak) will administer **module-managed users** over planned **mTLS client→host** RPCs; day-to-day API calls use **per-user credentials** — see [clients](clients.md).

**Related:** [interfaces/auth.md](../interfaces/auth.md) · [interfaces/grpc-auth-adapter.md](../interfaces/grpc-auth-adapter.md) · [ADR 007](../adrs/007-auth-adapter-modules.md)

**Read next:** [library-and-tunes.md](library-and-tunes.md)
