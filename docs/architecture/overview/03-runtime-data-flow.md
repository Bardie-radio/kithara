# Runtime Data Flow

How control, auth, audio, and telemetry move at runtime.

<!-- mermaid-source: docs/architecture/diagrams/runtime-data-flow.mmd -->
```mermaid
flowchart TB
  IdP["OIDC / other IdP"]
  Media["External media / live audio"]
  DB[(Database)]
  OTel[OTel collector]
  Players[Legacy players]

  subgraph modules [Bardie modules]
    UI[UI client]
    Auth[Auth module]
    Src[Source module]
  end

  subgraph kithara [Kithara]
    API[REST API]
    AuthHarness[Auth Harness]
    Registry[Module Registry]
    Neck[Neck]
    Silence[Silence feeder]
    FIFO[Session FIFO]
    Encoder[Struna Encoder]
    StreamSrv[Stream Server]
  end

  UI -->|REST /api| API
  Players -->|ICY /stream| StreamSrv
  UI -.->|optional listen| StreamSrv

  API --> AuthHarness
  API --> Neck
  API --> DB
  AuthHarness --> DB
  Neck --> DB
  AuthHarness --> Registry
  Neck --> Registry
  Neck --> Silence
  Silence --> FIFO
  Neck --> Encoder
  FIFO --> Encoder
  Encoder -->|encoded pipe| StreamSrv

  AuthHarness <-->|gRPC Authenticate Refresh| Auth
  Auth -.->|optional adapter hop| IdP
  UI -.->|browser redirect| IdP
  IdP -.->|OIDC callback| API

  Neck <-->|gRPC StartTrack Search| Src
  Src -->|PCM write| FIFO
  Src -.->|optional fetch or live input| Media

  API -.->|OTLP| OTel
  Auth -.->|OTLP| OTel
  Src -.->|OTLP| OTel
  UI -.->|OTLP| OTel
```

## Planes

| Plane | Path | Data |
|-------|------|------|
| **Control** | UI → REST API → Neck / Auth Harness → Module Registry → module gRPC | Play, queue, discovery, authenticate, refresh |
| **Persistence** | API / Auth Harness / Neck → Database | Users, bindings, Struna metadata, library refs |
| **Audio** | Source → session FIFO → Encoder → **Stream Server** → listeners | Canonical PCM in; ICY / encoded audio out |
| **Telemetry** | Every box → OTel collector | Traces, metrics, logs (OTLP) |

## Module shapes (same sockets, different guts)

Modules share one registration / gRPC surface with Kithara. What they do *outside* that socket varies:

| Shape | Auth example | Source example | External hop? |
|-------|--------------|----------------|---------------|
| **Self-contained** | Bes mints JWT in-module | Catbird reads local / uploaded files | No |
| **Fetch + process** | — | Magpie pulls via ytdl, decodes, writes PCM | Yes — media hosts |
| **Adapter / pass-through** | Argus forwards IdP JWTs and refreshes through the IdP | Starling re-broadcasts a continuous external input | Yes — IdP or live audio |

Dashed edges on the diagram are those optional external hops. Solid edges are the always-on Bardie paths (REST, gRPC, FIFO, ICY, OTLP).

UI clients stay on REST for control; they never call auth or source modules directly. Auth adapters stay behind Kithara (BFF). The IdP is the other public party only for redirect-style login.

**Related:** [02-internal-structure](02-internal-structure.md) · [ADR 003](../adrs/003-grpc-control-plane.md) · [ADR 004](../adrs/004-source-instance-socket-audio-plane.md) · [ADR 007](../adrs/007-auth-adapter-modules.md)

**Read next:** [../domains/source-instances.md](../domains/source-instances.md)
