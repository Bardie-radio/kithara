# Configuration

Env and Compose knobs for the **Kithara container** — database, collectors, modules, and auth.

## Kithara

| Variable | Description |
|----------|-------------|
| `POSTGRES_HOST` | When set, use Postgres — Jellyfin-style discrete knobs. **Required in Production** (Compose / GHCR image) |
| `POSTGRES_PORT` | Default `5432` |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | Database name / role / password (`PASSWORD` required with `HOST`) |
| `DbProvider` | `sqlite` (**Development only**) or `postgres` — Production rejects sqlite |
| `DbConnectionString` | Full EF string for advanced overrides when `POSTGRES_HOST` is unset (Development sqlite, or Production `DbProvider=postgres`) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional. External collector URL (e.g. Alloy). **Omit** when unused (SDK no-ops). Org deploy: uncomment in `profile/deploy/compose.yml` |
| `BARDIE_JOIN_SECRETS` | Map of module slug → secret (source, auth, and client modules — register + static admin). **Required via env/Compose in Production** — not embedded in published `appsettings.json` (demo values only in Development). Treat as root credentials for mesh bootstrap — [security-audit](../mvp/security-audit.md) |
| `BARDIE_MODULE_MTLS_BOOTSTRAP` | `auto` (default, private mesh) \| `preshared` (no private keys on Register) |
| `BARDIE_GRPC_TLS_DATA_PATH` | Host CA + gRPC server cert directory. **Image default** `/data/mtls` — mount a volume on `/data` |
| `BARDIE_MODULE_MTLS_PRESHARED_DIR` | Per-slug client cert dirs when bootstrap is `preshared` |
| `BARDIE_AUTH_PROVIDER_PRIORITY` | Ordered provider slugs for claim/role arbitration |
| `BARDIE_STRUNA_SILENCE_CLEANUP` | Auto-delete after silent duration (planned) |
| `BARDIE_GUEST_JWT_SIGNING_KEY` | Optional. If set, Kithara uses this key to sign ephemeral guest JWTs. If unset, auto-generate on first boot and persist on the data volume |
| `BARDIE_GUEST_JWT_ACCESS_TTL` | Access-token lifetime for ephemeral guests (default ~15m) |
| `BARDIE_GUEST_JWT_REFRESH_*` | Refresh window for ephemeral guests (until Struna teardown / capped lifetime — sketch) |
| `BARDIE_SEARCH_CACHE_TTL` | Timeout for durable/managed search-result cache (guests clear on Struna teardown) |
| `BARDIE_STORAGE_DRIVER` | Blob backend: `local` (MVP) \| `s3` \| later `webdav`. **Required in Compose** (reference default `local`) |
| `BARDIE_STORAGE_PATH` | Local driver root. **Required in Compose** when `local` (reference default `/data/blobs` on the Kithara `/data` volume). Image ENV may still default for bare `dotnet`/image runs |
| `BARDIE_STORAGE_S3_*` | S3-compatible endpoint, bucket, region, credentials (sketch) |
| `BARDIE_STRUNA_FIFO_PATH` | Shared FIFO scratch root (`{root}/strunas/{id}.pcm`). **Image default** `/audio` — Kithara-only env. Magpie has **no** FIFO path knob; it opens the absolute `audio_endpoint` from StartTrack. Mount the **same** volume at that path on Magpie (and any other PCM writer) |
| `BARDIE_FFMPEG_ROOT` | FFmpeg.AutoGen native lib directory. **Image default** `/usr/lib` (Alpine) |
| `BARDIE_FORWARDED_HEADERS` | Optional master switch (`true`) — or set any of the knobs below. **Unset = no proxy** (do not honor `X-Forwarded-*`) |
| `BARDIE_FORWARDED_HEADERS_FORWARD_LIMIT` | Proxy hops to trust. Reference Compose sets **2** (Traefik → nginx → app); single edge: `1`. No image/code default |
| `BARDIE_FORWARDED_HEADERS_CLEAR_KNOWN` | When `true`, clear ASP.NET `KnownProxies` / `KnownIPNetworks` (Docker mesh). Reference Compose sets `true`. Unset/false keeps loopback-only trust |
| `BARDIE_FORWARDED_HEADERS_KNOWN_PROXIES` | Optional comma-separated proxy IPs (applied after clear) |
| `BARDIE_FORWARDED_HEADERS_KNOWN_NETWORKS` | Optional comma-separated CIDRs (e.g. `10.0.0.0/8`) |

**User/login** JWT mint / refresh TTLs belong on the **auth module** (e.g. Bes) — Kithara only verifies those via module JWKS. Optional Kithara knobs later: JWKS cache / clock-skew tolerances.

Library blobs (Magpie cache, Catbird uploads) use the storage driver above on **Kithara only** — modules do not duplicate `BARDIE_STORAGE_*`; they use Kithara as storage interface/discovery. See [storage](../domains/storage.md). Not Redis. Neck FIFOs: `BARDIE_STRUNA_FIFO_PATH` is **Kithara-only**; Magpie mounts the shared volume at the same path and writes wherever Kithara’s `audio_endpoint` points (Compose: one named volume → `/audio` on both).

**Compose tip:** popular stacks (e.g. Jellyfin+Postgres) expose `POSTGRES_HOST` / `USER` / `PASSWORD` / `DB` instead of a monolithic connection string. Kithara follows that when `POSTGRES_HOST` is set; power users can still pass `DbConnectionString` with `POSTGRES_HOST` unset.

## Module discovery

Source and auth modules register via gRPC on startup. Compose sets:

- `KITHARA_GRPC_ADDRESS` (internal DNS to Kithara `:5000`)
- Join secret matching Kithara (`BARDIE_JOIN_SECRETS`)
- Optional `MODULE_SLUG_OVERRIDE` when community slugs collide

## Bes (MVP password auth)

Separate `bes` container. User + `UserAuthBinding` rows stay in Kithara’s DB — Bes has no separate auth DB. JWT mint / refresh lifetime knobs live on Bes (not on Kithara).

## Struna slug uniqueness

Alive Struna slugs must be unique among themselves. They are **not** blocked against edge path segments (`api`, `player`, …): public listen is always `/stream/{slug}`, so collisions with other route trees are impossible by prefix. See [streams](../domains/streams.md).

**Related:** [deployment.md](deployment.md) · [observability.md](observability.md) · [module-channel.md](module-channel.md) · [security-audit](../mvp/security-audit.md) · [auth-adapters](../domains/auth-adapters.md)

**Read next:** [security-audit](../mvp/security-audit.md)
