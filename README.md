# Kithara

> The core backend of Bardie — managing synchronized audio streams, playback control, and module orchestration.

Kithara is the main instrument of the Bardie ecosystem. It exposes a REST API for clients, coordinates source and auth modules over gRPC, encodes live audio with FFmpeg, and serves synchronized streams to listeners over HTTP.

🚧 **Heavily Work in Progress.** Solution builds on `net10.0` (`Kithara.sln`); runtime needs `Microsoft.AspNetCore.App` 10.

---

## ✨ Features

- 🎧 **Live broadcast** — one encoder per Struna (stream); everyone hears the same moment
- 🧩 **Modular ecosystem** — client, source, and auth adapters plug into a shared core
- 🔐 **Flexible access** — independent playback and control permissions per stream
- 📡 **Native streaming** — FFmpeg → ICY-over-HTTP at `/stream/{slug}`
- 📊 **Observable** — OpenTelemetry across Kithara and connected modules

---

## 🏗️ Architecture

📖 **[Architecture documentation](docs/architecture/README.md)** — design decisions, domain model, API contracts, and ADRs.

Kithara sits at the center of Bardie. **Client modules**, **source modules**, and **auth adapters** all connect to it; Kithara owns stream lifecycle, auth harnesses, and audio output.

### 🎼 Core responsibilities

- **Struna management** — create, configure, and tear down synchronized streams
- **Playback control** — play, skip, stop, and queue tunes via source instances
- **Neck service** — stream lifecycle: module coordination, FFmpeg encoding, listener fan-out
- **Auth harness** — delegates login and permissions to auth adapter modules

### 🔌 Modular components

```text
├── Client modules       → User-facing control and discovery surfaces
│   ├── Plume            → Web UI (MVP); / and /player/{slug}
│   ├── Beak             → Discord voice + stream control (future)
│   └── Cauda            → Telegram remote Struna control (future)
├── Source modules       → Audio providers (gRPC + FIFO)
│   ├── Magpie           → YouTube / ytdl search and play (MVP)
│   ├── Starling         → External / local stream input (future)
│   └── Catbird          → Local / uploaded files (future)
├── Auth adapters        → Login proof + protocol auth (gRPC; no built-in)
│   ├── Bes              → Login + password (MVP)
│   ├── Argus            → OIDC — Zitadel, Google, … (v0.2)
│   └── Hecate           → Passkeys (future)
└── Legacy players       → Listen-only: VLC, VRChat via /stream/{slug}
```

More client modules may follow — Bardie does not assume a single UI; it assumes the channels your community already uses.

### 🌐 URI map

| Path | Handler |
|------|---------|
| `/api` | Kithara REST API |
| `/stream/{slug}` | Live ICY-over-HTTP audio (Kithara) |
| `/player/{slug}` | Stream control surface (**Plume** — not served by Kithara) |
| `/` | Plume main UI |

Full routing detail: [uri-routing](docs/architecture/interfaces/uri-routing.md).

Ecosystem overview: [org architecture hub](https://github.com/Bardie-radio/.github/tree/main/profile/docs/architecture)

---

## 📖 Documentation

| Section | Contents |
|---------|----------|
| [Overview](docs/architecture/overview/) | System context, internal structure, data flow |
| [Domains](docs/architecture/domains/) | Streams, source instances, auth, [clients](docs/architecture/domains/clients.md) |
| [Interfaces](docs/architecture/interfaces/) | REST API, gRPC contracts, streaming |
| [ADRs](docs/architecture/adrs/) | Architecture decision records |
| [MVP v0.1](docs/architecture/mvp/v0.1-scope.md) | Scope and milestones |
| [Spike](docs/architecture/spike/prototype-neck-ffmpeg.md) | Prototype assessment |

---

## 🚀 Self-hosting

Kithara is designed to run as part of a self-hosted Bardie stack — typically alongside Plume (or another client module), source modules, and an auth adapter behind a reverse proxy.

- Whole stack: [org deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md)
- This container: [operations/deployment](docs/architecture/operations/deployment.md)
- MVP layout: [mvp/v0.1-scope](docs/architecture/mvp/v0.1-scope.md)
