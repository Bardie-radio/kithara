# Glossary

Dual naming: **codename** (musical instruments theme) + **plain English**. When docs say Struna, they mean a stream — and vice versa once you've met the glossary.

| Codename | Plain English | Description |
|----------|---------------|-------------|
| **Kithara** | Core backend | Bardie's main service: API, Neck, Stream Server, module registries |
| **Struna** | Stream | A named broadcast channel; user-chosen `slug` for URLs |
| **Neck** / **Neck Service** | Stream lifecycle service | Starts/stops Strunas, owns session FIFOs, runs FFmpeg encoders, silence feeder |
| **Struna Encoder** | Per-stream FFmpeg process | Reads session FIFO for Struna life; managed by Neck |
| **Session FIFO** | Per-Struna PCM pipe | Kithara-owned named FIFO; modules write; FFmpeg reads |
| **Track job** | Decode / playback job | Ephemeral source-module work writing PCM for one queue item |
| **Stream Server** | ICY HTTP output | Serves `GET /stream/{slug}` to listeners |
| **ICY** / **ICY-over-HTTP** | Shoutcast-style metadata stream | Continuous HTTP audio with in-band `StreamTitle` / `icy-metaint` metadata |
| **Tune** | Library item | Shared library unit: queue + history + optional blob cache; module + external id; not stream-owned |
| **QueueEntry** | Queue item | Struna queue slot pointing at a **Tune id** |
| **Plume** | Web UI client module | Optional **user-aware** client: `/`, `/control/{slug}` (remote desk), `/player/{slug}` (listen surface) |
| **Beak** | Discord bot client | Future **static** client: play Strunas in Discord voice; guild-scoped managed users |
| **Cauda** | Telegram bot client | Future **user-aware** client: remote Struna control from Telegram chats |
| **Client module** | User-facing integration | Deployable surface (Plume, Beak, Cauda, …) that calls Kithara REST |
| **User-aware client** | Login UI module | Client whose end users authenticate with JWT from an auth module (Plume, Cauda) |
| **Static client** | Bot / keyed UI module | Client with no human Bardie login; **Registers** with a join secret, then day-to-day `/api` as **module-managed users** (Beak). Module admin → planned mTLS RPCs |
| **Module-managed user** | Managed user | Alias — see **Managed user** above |
| **Magpie** | YouTube / ytdl source | MVP: search + play via ytdl; cache-first Tune library; writes PCM to session FIFO |
| **Starling** | External / local stream source | Future: re-broadcast direct audio input; sparse Tune (URI, no blob) for history/queue |
| **Catbird** | Local file source | Future: play uploaded / local audio files |
| **Source module** | Audio provider | External container (Magpie, Starling, Catbird, …) registered with Kithara |
| **Module Registry** | Module directory | Inside Kithara: source, auth, and client module registration |
| **Bes** | Login + password auth | MVP auth adapter: username/password proof over gRPC |
| **Argus** | OIDC auth adapter | v0.2: IdP redirect/code exchange, IdP tokens / OIDC session pieces |
| **Hecate** | Passkeys auth adapter | Future: WebAuthn / passkey ceremonies |
| **Auth adapter / provider** | Auth provider | Separate container (Bes, Argus, Hecate, …); issues/forwards JWT + refresh; Kithara verifies + user DB |
| **Auth harness** | Auth router | Discovery merge, opaque Authenticate/Refresh/UpdateUserBinding routing, login JWT verify (JWKS), guest-code exchange + ephemeral guest user/JWT mint, **invite OTP bootstrap + claim JWT**, join secrets |
| **Source harness** | Source router | Catalog lookup, capability-gated dials (`Search` / `StartTrack` / …), host port for blob storage |
| **UserAuthBinding** | Provider binding | `(user, provider_slug)` row + payload in Kithara DB |
| **Durable user** | Full account | Normal login user with auth-module binding(s); survives Struna teardown |
| **Managed user** | Module-owned account | Persistent `User` owned by a **static** client (e.g. one per Discord guild); long-lived, not Struna-scoped |
| **Ephemeral guest user** | Struna guest account | Kithara-created `User` for one guest-code joiner; destroyed when that Struna dies; no auth-module binding |
| **Listen token** | Playback secret | Query credential for **protected** playback (Kithara-owned) |
| **Guest code** | Control bootstrap | Short Kithara-owned code **per Struna**; **exchange only** → new ephemeral guest user + Kithara-minted JWTs (rate-limited) |
| **Guest JWT** | Ephemeral session token | Kithara-signed Bearer (+ refresh) for an ephemeral guest user; Struna-scoped control |
| **Join secret** | Module credential | Long-lived secret in Kithara/Compose config — **Register bootstrap** for source/auth/client modules. Not a standing `/api` admin key; static managed-user admin is planned as mTLS client→host RPCs. |
| **Search result cache** | Playable search refs | Global search results retained per principal so they can play/queue by ref; guests cleared on Struna teardown; durable/managed until next search or configurable timeout |
| **Blob storage** | Library object store | Kithara-owned pluggable backend for Tune bytes (local volume, S3-compatible, WebDAV later) |
| **Storage key** | Opaque blob id | Durable pointer on a Tune; drivers resolve to file/object — not a host path |
| **DbProvider** | Persistence backend | Config switch: `sqlite` or `postgres` |

Module and provider registration slugs are the lowercase codename (`magpie`, `bes`, `argus`, …). Image/Compose names and **GitHub repos** use the same slug (`Bardie-radio/kithara`, `Bardie-radio/plume`, … — **no** `bardie-` repo prefix). OTel `service.name` uses `bardie.kithara`, `bardie.plume`, `bardie.beak`, `bardie.cauda`, `bardie.source.<slug>`, `bardie.auth.<slug>`.

## Prototype vs target

| Term | Prototype (`Neck.cs` spike) | Target |
|------|----------------------------|--------|
| Neck | Playlist → FFmpeg concat | Session FIFO → FFmpeg → Stream Server |
| Struna | Metadata only, no source binding | Slug, access modes, alive-on-create, queue |
| Audio job | N/A | Track job per queue item; multi-source capable |
| Tune | `PlaylistId` + `Playlists` conflict | Library unit for queue/history/optional cache |

See [spike/prototype-neck-ffmpeg.md](spike/prototype-neck-ffmpeg.md).

**Related:** [overview/README](overview/README.md) · [ADRs](adrs/README.md)

**Read next:** [overview/01-system-context.md](overview/01-system-context.md)
