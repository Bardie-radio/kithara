# ADR 009: Struna Access and Routing

**Status:** Accepted (amended: protected control = guest-code exchange → ephemeral guest user + Kithara-minted JWTs; `hidden` playback = public stream gate, list-omitted)

## Context

Streams need human-readable URLs, separate listen vs control permissions, and legacy player compatibility without OIDC. Short guest codes must not be sent on every control API call.

## Decision

**URI map (one domain):**

| Path | Service |
|------|---------|
| `/` | Plume (auth required) |
| `/control/{slug}` | Plume remote control desk |
| `/player/{slug}` | Plume listen / player surface |
| `/api/*` | Kithara REST |
| `/stream/{slug}` | Kithara Stream Server |

`/player` used to mean the control UI; Plume MVP flips that — control is `/control/{slug}`, listen UI is `/player/{slug}`. No `/listen` path.

**Slug:** user-chosen; unique among **alive** Strunas; freed on **DELETE** (or silent cleanup).

**Playback access** (independent): `public` | `hidden` (same `/stream` gate as public; omitted from listen lists except owner/control/guest) | `protected` (listen token) | `private` (full auth).

**Control access** (independent): `private` (auth) | `protected` (guest code → **ephemeral guest user** + Kithara-minted JWTs). **No public control.**

**Protected playback MVP:** query param `/stream/{slug}?token=...`. Listen token is a **Kithara-owned** Struna secret (no Bearer exchange — legacy players). Basic Auth and path token documented for v0.2 evaluation.

**Protected control:** one short **guest code** per Struna (Kithara-owned). Each `POST /api/streams/{id}/guest/exchange` creates a **new ephemeral guest user** and returns Kithara-signed JWT (+ refresh) for that user. Guests are destroyed when the Struna is deleted. Rate-limit exchange; **rotating the code blocks new joins only** (existing guests keep working). See [struna-access](../domains/struna-access.md).

## Consequences

- Paste-friendly URLs for VLC/VRChat on public/protected streams.
- Private playback incompatible with most legacy players (by design).
- Party DJ without durable accounts: share a code; each joiner gets an ephemeral user (ACL + search-cache ownership) until the Struna dies.

## Repos needing follow-up

| Decision | Follow up in |
|----------|----------------|
| URI map + Plume routes | **plume**, org edge/Compose ([05-deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md)) |
| Listen token / guest exchange UX | **plume** (Kithara owns secrets + ephemeral guest users/JWTs) |

## Alternatives considered

- **GUID-only URLs** — rejected; poor UX for external players.
- **Public control plane** — rejected; anonymous queue/skip omitted.
- **Single access level** — rejected; listen and control needs differ.
- **Guest code on every request** — rejected; exchange for ephemeral guest session instead ([ADR 007](007-auth-adapter-modules.md)).
- **Guest = capability JWT without a User row** — superseded; ephemeral guest **users** required for refresh, ACL, and search-cache ownership.

**Related:** [domains/struna-access.md](../domains/struna-access.md) · [interfaces/uri-routing.md](../interfaces/uri-routing.md) · [interfaces/auth.md](../interfaces/auth.md)

**Read next:** [../mvp/v0.1-scope.md](../mvp/v0.1-scope.md)
