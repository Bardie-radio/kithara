# Internal Structure

<!-- mermaid-source: docs/architecture/diagrams/internal-structure.mmd -->
```mermaid
flowchart TB
  subgraph inbound [Inbound]
    HTTP[HTTP :8080]
    GRPC[gRPC :5000]
  end
  subgraph kithara [Kithara]
    API[REST API]
    StreamSrv[Stream Server]
    AuthHarness[Auth Harness]
    Registry[Module Registry]
    Neck[Neck Service]
    Silence[Silence feeder]
    Persist[(Persistence)]
  end
  subgraph runtime [Per-Struna runtime]
    FIFO[Session FIFO]
    Encoder[Struna Encoder FFmpeg]
  end
  HTTP --> API
  HTTP --> StreamSrv
  GRPC --> Registry
  API --> AuthHarness
  API --> Neck
  API --> Persist
  AuthHarness --> Registry
  AuthHarness --> Persist
  Neck --> Registry
  Neck --> Silence
  Silence --> FIFO
  Neck --> Encoder
  FIFO --> Encoder
  Encoder --> StreamSrv
  Neck --> Persist
```

How **Kithara** is structured inside one process. Ecosystem layout (Plume, modules, edge) lives in the [org component landscape](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/03-component-landscape.md) and [system context](01-system-context.md).

## Building blocks

| Component | Responsibility |
|-----------|----------------|
| **REST API** | Client-facing control: Struna lifecycle, play/skip/queue, auth discovery/authenticate |
| **Stream Server** | `GET /stream/{slug}` — ICY-over-HTTP listener fan-out |
| **Auth Harness** | Discovery, identity routing, login JWT verify (JWKS), guest-code exchange + ephemeral guest users, **invite bootstrap (AUTH-INVITE)**, refresh proxy to modules, join secrets, listen checks. **Shape as a library with host ports** (user/binding persistence); Bardie-only extras (guests, join secrets, REST BFF) wrap the library — [org 07](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/07-modules-beyond-bardie.md) |
| **Module Registry** | Source + auth + client module register / heartbeat (modules dial Kithara; join secret) |
| **Neck Service** | Alive Struna lifecycle, session FIFOs, silence feeder, `StartTrack`/`StopTrack`, wire encoders to Stream Server. **Source search / download routing** should live in a **source module harness** library Neck/API call into — not buried only in REST handlers |
| **Silence feeder** | Keeps FFmpeg fed when no module writer is attached |
| **Struna Encoder** | Per-alive-Struna FFmpeg; reads session FIFO for Struna life |
| **Persistence** | Users + bindings, Struna metadata, library/Tune refs (SQLite or Postgres) |

## Solution layout

Root buckets: `src/` (host + DummyRegistrar), `libs/` (packable Contracts + Module.* + Harness.*), `tests/`, `docs/`. Prefer **feature-first** folders and Minimal APIs ([aspnet team rules](../../../.cursor/rules/aspnet.mdc) in repo):

```text
src/Kithara/
  Features/
    Streams/      # REST + handlers
    Auth/         # Bardie host wrappers around auth harness (guests, REST BFF)
    Modules/      # registry gRPC (modules dial in)
    Streaming/    # Stream Server, ICY
    Library/      # Tune metadata + storage keys
    Search/       # source harness wrappers + principal-scoped result cache
  Infrastructure/
    Persistence/  # EF, IDbContextFactory — implements harness host ports
    Observability/# OTel registration (service.name=bardie.kithara)
    Neck/         # hosted FFmpeg supervisor + session FIFO + silence
    Storage/      # blob drivers (local MVP) — backing store for source harness storage API
libs/
  Bardie.Contracts/           # packable protos (ModuleRegistry, AuthAdapter, …)
  Bardie.Module.Channel/      # mTLS + manifest + participant/host dial helpers
  Bardie.Module.Hosting/      # ASP.NET participant bootstrap + Bardie Compose env aliases
  Bardie.Module.Auth/         # JWT mint / JWKS Register helpers for auth adapters
  Bardie.Module.Source/       # SourceModule base, FIFO sink, job registry, host dial clients
  Bardie.Module.Source.Debug/ # opt-in sine FIFO/protocol smoke (Debug / test refs only)
  Bardie.Harness.Auth/
  Bardie.Harness.Source/
```

FFmpeg child processes **outlive HTTP requests** — own them with a hosted background supervisor, not a request-scoped service alone.

## Control vs audio

| Plane | Inside Kithara | Outside |
|-------|----------------|---------|
| **Control** | API → Auth / Neck / Registry | gRPC to source & auth modules |
| **Audio** | Silence/FIFO → Encoder → Stream Server | Modules write PCM into FIFO path |

HTTP is for clients/listeners (API + streams). Module control never rides the public HTTP port — see [03-runtime-data-flow](03-runtime-data-flow.md).

## Boundaries (not inside this process)

| Outside | Talks to |
|---------|----------|
| Client modules (Plume, bots, …) | REST API |
| Legacy players | Stream Server |
| Source modules | Registry + FIFO write path |
| External auth adapters | Auth Harness via Registry |
| Edge reverse proxy | HTTP port only (`/api`, `/stream`; OIDC callback on Kithara) |

Container ports, mounts, and edge wiring: [operations/deployment](../operations/deployment.md).

## Related ADRs

- [001 Broadcast sync](../adrs/001-broadcast-sync-model.md)
- [002 Native FFmpeg streaming](../adrs/002-kithara-native-ffmpeg-streaming.md)
- [003 gRPC control plane](../adrs/003-grpc-control-plane.md)
- [004 Session FIFO](../adrs/004-source-instance-socket-audio-plane.md)
- [007 Auth adapters](../adrs/007-auth-adapter-modules.md)

**Read next:** [03-runtime-data-flow.md](03-runtime-data-flow.md)
