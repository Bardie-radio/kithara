# Client modules (Kithara contract)

What Kithara treats as a **client module**, how it attaches to core, and which credentials it may use on `/api`. The catalog of planned clients (Plume, Beak, Cauda, …) lives in the [org client modules](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/06-client-modules.md) page.

```mermaid
flowchart TB
  ClientMod[Client_module]
  Players[Legacy_players]
  ClientMod -->|REST /api| API[Kithara_REST]
  Players -->|ICY /stream| StreamSrv[Stream_Server]
```



## What counts as a client module

A **client module** is a separate deployable that presents Bardie on some channel (web, chat, bot, …) and drives Strunas through Kithara’s **REST API**. It **Registers over gRPC** like every other module (join secret + auth mode), calls `/api` for create/control/search/queue, and may provide player surfaces.

Out of scope for “client module”: legacy players (VLC, direct browser playback, etc) that only hit `GET /stream/{slug}`, no control over system or authorization in most cases

Kithara does **not** serve `/`, `/control/*`, or `/player/*` — those belong to a UI client (typically Plume) at the edge. See [uri-routing](../interfaces/uri-routing.md).

## Auth modes (contract)

When a client module registers, it declares how it authenticates to `/api`:


| Mode           | Meaning                                                                      | Credential on `/api`                                                         |
| -------------- | ---------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| **user-aware** | End users log in; module acts with their identity                            | Bearer **user JWT** from an auth module (via Kithara discovery/authenticate) |
| **static**     | No human Bardie login through this UI; module owns **many** persistent users | **Per-user credentials** for day-to-day `/api` (no join secret on HTTP)      |


Module-level **capability rights** (what the static app may do at all) are declared at registration. Per-user / Struna ACLs still live in Kithara.

### gRPC surface by client mode

| Client | gRPC today | Future (static only) |
|--------|------------|----------------------|
| **All** (Plume, Beak, …) | `Register` + `Heartbeat` (join secret = **Register bootstrap only**; Heartbeat = mTLS) | — |
| **User-aware** | No admin/work RPCs — REST + end-user JWT via BFF | — |
| **Static** | Same as all | **Client→host managed-user admin** RPCs over mTLS (create/list/revoke + credential mint/reset + ceiling attach) |

There is no client work proto yet and Kithara does not dial a client’s advertise address. Do **not** put managed-user admin on `/api` with the join secret — that would be BFF-reachable and reintroduces shared-secret impersonation. Track as Kithara Feature: *Static client managed-user admin over mTLS gRPC (not join-secret REST)*.

### Static modules and module-managed users

Do **not**:


| Shape                                                     | Why not                              |
| --------------------------------------------------------- | ------------------------------------ |
| One user for the whole static module                      | Shared identity across every tenancy |
| One user per short-lived session (e.g. per voice channel) | Too many users; session ≠ tenancy    |
| Many users all acting under the **same** join secret      | Shared-secret impersonation          |


**Chosen shape:** each **tenancy boundary** gets a durable `User` with **distinct** credentials for day-to-day `/api`. Module-root admin (create/list/revoke managed users, mint/reset creds) is **planned** as **mTLS client→host** RPCs after Register — not join-secret REST. Kithara records `managed_by_module` + external tenancy ref. At Register the static module advertises a **permission ceiling** (typical: create Strunas + manage ones it created). Creating a managed user may set a narrower entity scope or adjust it at runtime — never above the ceiling; if unset, Kithara defaults to the advertised set. Concrete tenancy keys (e.g. Discord guild) are module-specific — see [org catalog](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/06-client-modules.md) and each module’s docs.

## Attachment to core

1. **Register** — gRPC Module Registry (same as source/auth): join secret + `kind=CLIENT` + auth mode (`user-aware` | `static`) + static module rights when static — [grpc-module-registry](../interfaces/grpc-module-registry.md)
2. **Auth** — user JWT; or per-managed-user credentials for API work; or ephemeral guest JWT after guest exchange. Static **module admin** → future mTLS admin RPCs (not `/api` + join secret)
3. **Control** — REST playback/queue/search as in [rest-api](../interfaces/rest-api.md)
4. **Listen** — optional; `/stream/{slug}` (or embed). Not required to be a client module

Day-to-day Struna control is **REST only**. Mesh join is **always** Registry RPC. Compose / join-secret wiring: [org deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md) · [operations/deployment](../operations/deployment.md).


## OTel

Client modules export OTLP (`bardie.plume`, `bardie.beak`, `bardie.cauda`, …) into the same graph as Kithara ([ADR 008](../adrs/008-otel-observability.md)).

**Related:** [org client modules](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/06-client-modules.md) · [auth.md](../interfaces/auth.md) · [struna-access.md](struna-access.md) · [uri-routing.md](../interfaces/uri-routing.md)

**Read next:** [../interfaces/rest-api.md](../interfaces/rest-api.md)