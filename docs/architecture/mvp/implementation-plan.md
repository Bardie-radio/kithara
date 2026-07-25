# Implementation plan (v0.1)

Ordered build plan to bring Kithara (and the MVP module stack) alive without coupling modules to each other’s guts.

**Scope:** [v0.1-scope.md](v0.1-scope.md) · **Milestone sketch:** [v0.1-milestones.md](v0.1-milestones.md)

This page is the **how and in what order**. Milestones stay the short delivery ladder; here we expand work packages, freeze points, and modularity rules.

## Philosophy: modularity first

Kithara must not care **which** auth, source, or UI module is connected — only that each speaks the **unified contract for its type**. Modules must not depend on each other’s implementation details.


| Rule                                  | Means in practice                                                                                                                                                                                                                                                                            |
| ------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **One contract per module type**      | Source → `SourceModule` gRPC; Auth → `AuthModule` gRPC; Client → `ClientModule` gRPC + REST `/api` for UX; **all** kinds join via Module Registry gRPC                                                                                                                                       |
| **Opaque payloads at the edge**       | Clients never call Bes/Magpie; Kithara routes bags and verifies tokens                                                                                                                                                                                                                       |
| **Identity by slug + join secret**    | Module swap = Compose + secret map, not Kithara code changes                                                                                                                                                                                                                                 |
| **Harnesses as libraries**            | Auth module harness + source module harness are **library-shaped** (host ports for persistence / storage / Bardie extras). Kithara is one host; outside reuse is planned — [org 07](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/07-modules-beyond-bardie.md) |
| **Spike code is not the model**       | Follow docs/ADRs over `Neck.cs` / prototype `Tune`/`Playlist` shapes                                                                                                                                                                                                                         |
| **Freeze the socket before the guts** | Lock proto/REST sketches enough to implement both sides, then fill behaviour                                                                                                                                                                                                                 |
| **OTel from day one**                 | Wire OpenTelemetry in `Program.cs` / module entrypoints in **Phase 1** — not a Phase 8 afterthought. Auto-instrument HTTP/gRPC/EF; custom spans only where middleware is blind (Neck, FIFO, FFmpeg).                                                                                         |


If a feature requires Magpie to know Bes exists (or Plume to know Magpie’s ytdl quirks), the design is wrong — put the knowledge in Kithara’s harnesses or in the shared contract.

```mermaid
flowchart TB
  subgraph freeze [Freeze early]
    Proto[gRPC contracts]
    REST[REST /api sketch]
    Data[Core data model]
  end
  subgraph kithara_core [Kithara vertical slices]
    Skeleton[Skeleton + persistence]
    Registry[Module Registry]
    AuthHarness[Auth Harness]
    Neck[Neck + FIFO + FFmpeg]
    Stream[Stream Server ICY]
  end
  subgraph modules [Parallel module work after freeze]
    Bes[Bes]
    Magpie[Magpie]
    Plume[Plume]
  end
  Proto --> Registry
  Proto --> Bes
  Proto --> Magpie
  REST --> Skeleton
  REST --> Plume
  Data --> Skeleton
  Skeleton --> Registry
  Registry --> AuthHarness
  AuthHarness --> Bes
  Registry --> Neck
  Neck --> Magpie
  Neck --> Stream
  Stream --> Plume
```





## Current baseline (honest)

**Phases 1–6 complete.** Phase 3 closed the source vertical; Phases 4–6 closed encode-alive Neck, ICY Stream Server, and control/auth hardening (Jul 2026). Soft residuals from the Phases 4–6 audit (host rotate gate, guest lockout, host E2E, doc drift) are owned by **Phase 8** — see [security-audit](security-audit.md). Spike Controllers / Playlist / Neck are gone from runtime — see [spike/prototype-neck-ffmpeg](../spike/prototype-neck-ffmpeg.md) for historical FFmpeg notes only.

Next shipping focus: **Phase 7 (Plume)** and **Phase 8 (Compose + verify)**.


| Area    | Today                                                                | Later                                 |
| ------- | -------------------------------------------------------------------- | ------------------------------------- |
| Layout  | Feature folders + packable Module.* / Harness.* / Contracts          | Plume (7); Compose E2E (8)            |
| Models  | ADR 006 EF entities + migrations + grant CRUD                        | —                                     |
| Auth    | Orch + Bes + JWT + guest refresh + AUTH/GUEST-* (soft residuals → 8) | Host rotate gate / lockout polish (8) |
| Audio   | Encode-alive: silence + FFmpeg + Magpie PCM + ICY `/stream`          | Continuity polish / NECK-JOB-001 (8)  |
| Control | Full DJ REST + pause-as-silence + TrackStatus now-playing            | —                                     |
| Modules | Registry + mTLS host↔slug pin; Bes + Magpie live                     | Plume (7)                             |




## Phase map

Phases are **dependency-ordered** for *shipping* outcomes. Phases 4–6 ran in parallel after Phase 3 and are now closed; integrate and verify at Phase 8.


| Phase | Name                          | Outcome                                                                               | Status                |
| ----- | ----------------------------- | ------------------------------------------------------------------------------------- | --------------------- |
| **1** | Kithara skeleton              | Feature layout, DB, Module Registry, join secrets, **OTel bootstrap**                 | Complete              |
| **2** | Auth vertical                 | Harness + Bes + JWT verify + bootstrap user path                                      | Complete              |
| **3** | Source vertical               | Source protocol + Magpie proof (`Search` / `StartTrack` / FIFO write)                 | Complete              |
| **4** | Neck + encode                 | Alive Struna, silence feeder, FFmpeg supervisor + **Channel peer pin (MESH-CHN-001)** | Complete              |
| **5** | Stream Server                 | `GET /stream/{slug}` ICY + listen-token gate                                          | Complete              |
| **6** | Control REST + auth hardening | Remaining control depth + **security P0/P1** + harness routing (AUTH-ORCH-001)        | Complete              |
| **7** | Plume MVP                     | Umbrella: reference UI exists (Plume Phases 1–6)                                      | **Next**              |
| **8** | Compose + verify              | Reference stack, join secrets, OTLP E2E, **QA/OPS/DOC debt**                          | After 7 (or parallel) |


Phase 7 needs Phase 2 + enough of 5–6 (now available). Phase 8 needs MVP apps green enough to compose — OTel export itself is already live from Phase 1 / each module’s first boot.

### Phases 1–3 review → phase ownership


| ID             | Kind          | Summary                                                                               | Phase                                                                   |
| -------------- | ------------- | ------------------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| GUEST-REF-001  | Security P0   | Guest refresh path missing                                                            | **6**                                                                   |
| LIB-TUNE-001   | Security P0   | `EnsureTune` storage_key ownership                                                    | **6**                                                                   |
| AUTH-ROT-001   | Security P0   | `must_rotate_credentials` never enforced                                              | **6**                                                                   |
| AUTH-ROLE-001  | Security P0   | Every Bes mint → `roles=[admin]`                                                      | **6**                                                                   |
| AUTH-JWKS-001  | Security P1   | JWKS sync-over-async                                                                  | **6**                                                                   |
| GUEST-XCHG-001 | Security P1   | Guest exchange rate-limit                                                             | **6**                                                                   |
| MESH-CHN-001   | Security P1   | Channel host↔slug mTLS pin                                                            | **4**                                                                   |
| AUTH-ORCH-001  | Design/debt   | Auth harness: `provider_id`→module from discovery (still pass `provider_id` on wire)  | **6**                                                                   |
| NECK-JOB-002   | Design/debt   | Wire `TrackStatus` / recover jobs; Neck in-memory map                                 | **4**                                                                   |
| META-QA-001    | QA            | Host integration tests; Magpie/Bes module-local tests                                 | **8** (+ land tests with 4–6 PRs)                                       |
| META-OPS-001   | Ops           | Phase3 sine smoke vs Magpie Release image                                             | **8**                                                                   |
| META-OPS-002   | Ops           | Alpine final images + bare-minimum FFmpeg libs (Kithara/Magpie); Alpine for Plume/Bes | **8** ([kithara#33](https://github.com/Bardie-radio/kithara/issues/33)) |
| META-DOC-001   | Docs          | Doc vs code drift (Tune path, Bes ops, Magpie scope, phase status)                    | **8**                                                                   |
| META-OTEL-001  | Ops           | Local Compose omits `OTEL_EXPORTER_OTLP_ENDPOINT`                                     | **8** ([kithara#34](https://github.com/Bardie-radio/kithara/issues/34)) |
| META-OTEL-002  | Ops           | `Task.Run` drops Activity (Magpie track + Neck encode)                                | **8** ([kithara#36](https://github.com/Bardie-radio/kithara/issues/36)) |
| META-OTEL-003  | Ops           | Span attrs / Magpie stages / listen tags lag ADR 008                                  | **8** ([kithara#35](https://github.com/Bardie-radio/kithara/issues/35)) |
| MESH-REG-*     | Mesh residual | Join-secret takeover / auto key-on-wire / ephemeral CA                                | Ops + backlog ([security-audit](security-audit.md))                     |




### OTel in practice (ASP.NET / modules)

You do not hand-wrap every method. Typical pattern:

1. **Bootstrap once** in `Program.cs` (or module main): OpenTelemetry SDK + OTLP exporter + `service.name=bardie.kithara` (etc.).
2. **Auto-instrumentation** for ASP.NET Core HTTP, gRPC, HttpClient, EF Core — middleware/handlers create spans for inbound/outbound calls and propagate W3C `traceparent`.
3. **Custom Activity / spans** only where auto-instrumentation is blind: Neck lifecycle, silence feeder, FFmpeg process, session FIFO attach, track-job state machines.
4. **Attributes** from [observability](../operations/observability.md): `struna.id`, `struna.slug`, `source.module`, … — never tokens/passwords.
5. If `OTEL_EXPORTER_OTLP_ENDPOINT` is unset, export no-ops — Local sketches currently omit it ([META-OTEL-001](known-issues.md#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint)). Background `Task.Run` still orphans Magpie/Neck work even after export ([META-OTEL-002](known-issues.md#meta-otel-002--taskrun-drops-activity-context-magpie--neck)).

Same contract on Bes/Magpie/Plume from their first runnable container ([ADR 008](../adrs/008-otel-observability.md)).

---



## Phase 0 — Contract freeze

**Why first:** Magpie/Bes/Plume cannot safely implement against moving sketches. Modularity dies if each module invents its own register/auth/play shape.

### Work

1. **Own the** `.proto` **files in** `libs/Bardie.Contracts` **and publish a versioned package** (`Bardie.Contracts`) for module authors — single source of truth for:
  - `ModuleRegistry` on Kithara (modules dial in; mTLS cert issued on success)
  - `AuthAdapter` work RPCs (Kithara dials per call) — **done** in Phase 2
  - `SourceModule` + `BlobStorage` + `Library` — **Phase 3 freeze** (current)
2. Promote interface pages from “sketch” to **v0.1 draft** (field names may still evolve; RPC set and dial rules must not). Auth + registry are draft; source/storage/library promote with the Phase 3 freeze.
3. Lock REST path set in [rest-api](../interfaces/rest-api.md) for MVP verbs (auth, streams, play, queue, **global** search, guest exchange).
4. Lock **target EF model** outline: `User` kinds, `UserAuthBinding`, `Struna`, `Tune`, `QueueEntry`, search-result cache — discard prototype `Playlist` as product schema ([ADR 006](../adrs/006-stream-source-tune-data-model.md)).
5. Document shared **audio volume** + session endpoint conventions ([ADR 004](../adrs/004-source-instance-socket-audio-plane.md)).



### Exit criteria

- Magpie and Bes can scaffold servers/clients against checked-in protos.
- Plume can stub against documented REST paths.
- No phase-1+ code invents a second register or auth protocol.



### Cross-repo


| Repo                | Follow-up                                                                 |
| ------------------- | ------------------------------------------------------------------------- |
| **magpie**, **bes** | `PackageReference` / sibling `ProjectReference` to `Bardie.Contracts`     |
| **plume**           | REST client stubs from rest-api                                           |
| **org**             | Join-secret / volume notes in deployment narrative when attach is decided |


---



## Phase 1 — Kithara skeleton

**Status: complete.** Dual listeners, Module Registry, `Bardie.Module.Channel` mTLS (`auto`  `preshared`), harness lib scaffolds, ADR-006 EF, OTel `bardie.kithara`.

**Why:** Everything else hangs off registry, persistence, HTTP/gRPC hosts, and telemetry plumbing.

### Work

1. **Feature-first layout** under `src/Kithara` + packable `libs/` (Harness.Auth/Source, Module.Channel/Hosting/Auth) — see [02-internal-structure](../overview/02-internal-structure.md) and [module-channel](../operations/module-channel.md):

```text
src/Kithara/
  Features/
    Modules/        # Module Registry gRPC (host)
    Auth/ Search/ Streams/ Streaming/ Library/   # Bardie wrappers (filled later)
  Infrastructure/
    Persistence/ Observability/ Storage/ Neck/
libs/
  Bardie.Contracts/
  Bardie.Module.Channel/
  Bardie.Module.Hosting/
  Bardie.Module.Auth/
  Bardie.Harness.Auth/
  Bardie.Harness.Source/
```

1. Config: `DbProvider` / `DbConnectionString`, `BARDIE_JOIN_SECRETS`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `BARDIE_MODULE_MTLS_BOOTSTRAP`, `BARDIE_GRPC_TLS_*` ([configuration](../operations/configuration.md)).
2. **OpenTelemetry bootstrap** in `Program.cs`: OTLP exporter, `service.name=bardie.kithara`, ASP.NET + gRPC + HttpClient + EF auto-instrumentation; W3C propagation on. Safe when collector is absent.
3. EF migrations for core tables (ADR 006 shapes).
4. **Module Registry** service: `Register` authenticated by **join secret**; issues client certs in `auto` mode (or confirms preshared material); **Heartbeat authenticated by mTLS** (not join secret). Track slug, capabilities, advertise address, JWKS (auth), search schema (sources); project AUTH/SOURCE into harness catalogs. Registry RPCs appear as spans once gRPC instrumentation is on.
5. Dual listeners: HTTP `:8080`, gRPC HTTPS `:5000` (internal) via `Bardie.Module.Channel` helpers.
6. Health/readiness endpoints suitable for Compose.



### Exit criteria

- Empty Kithara boots with SQLite.
- A dummy module can register with a join secret and appear in registry state (and harness catalog for AUTH/SOURCE).
- With a collector configured, a health or register request produces a trace for `bardie.kithara`.
- No playlist-centric API.



### Explicitly not yet

- Real Bes/Magpie behaviour, FFmpeg, ICY, Plume.

---



## Phase 2 — Auth vertical (Bes + Harness)

**Status: complete.** Contracts package + Auth Harness + Bes + JWT verify + bootstrap **AUTH-INVITE** (host OTP).

**Why:** Control APIs need a verified identity. Auth stays behind Kithara (BFF).

### Work (Kithara)

1. Auth Harness: merge `GetProviders`, route opaque `Authenticate` / `Refresh`, persist `User` + `UserAuthBinding` when `ensure_user`.
2. JWT Bearer middleware: verify **user** JWTs via registered module JWKS (cache JWKS).
3. REST: `/api/auth/discovery`, `/authenticate`, `/refresh` ([auth](../interfaces/auth.md)).
4. Guest JWT signing: env key if set, else auto-generate + persist; mint path used in Phase 6.
5. Bootstrap when DB empty: host invite OTP for DEFAULT_ADMIN (log once); claim → bind. Admin `/register` username-only + one-time `registration_password`.



### Work (Bes — parallel)

1. Implement `AuthAdapter` against frozen proto.
2. `form_schema` discovery; mint access + refresh JWT; publish JWKS.
3. Binding payload = password hash material for Kithara to store.



### Exit criteria

- `curl`/client: discovery → authenticate → call a protected stub endpoint with Bearer.
- Swapping Bes for a mock adapter requires only registry + secret — no Kithara auth-code fork.



### Cross-repo


| Repo      | Follow-up                                             |
| --------- | ----------------------------------------------------- |
| **bes**   | MVP container + OTel `bardie.auth.bes`                |
| **plume** | Can start discovery-driven login UI against real auth |


---



## Phase 3 — Source vertical (protocol + Magpie proof)

**Status: complete.** Source protocol + Magpie proof (`Search` / `StartTrack` / FIFO write). Residuals owned elsewhere: consume `TrackStatus` (closed in Phase 4), CI E2E (→ Phase 8).

**Why:** Prove multi-container audio control before investing in FFmpeg lifecycle.

### Work (Kithara)

1. Registry dials module advertise address for `Search` / `StartTrack` / `StopTrack` / `TrackStatus` — **done** (`Bardie.Harness.Source` real dials + capability gates).
2. Temporary **FIFO smoke**: create a session FIFO path, call Magpie `StartTrack`, verify PCM bytes appear (even before Stream Server) — REST create/play + Local `scripts/phase3-source-smoke.sh`.
3. Storage interface MVP: local driver + opaque keys under `tunes/<source_slug>/…`; Magpie put/get via `BlobStorage` — **done**.
4. Library write path: Magpie dials `Library.EnsureTune` after Put on cache miss (Kithara owns EF upsert) — **done**.
5. **Phase 6 control REST (landed under Phase 3, no FFmpeg then):** search + principal **search cache**; Struna create/get/delete; `/listen` + `/control` lists; play/quickplay/pause/skip/now-playing; queue/quickqueue; guest exchange. Encode-alive + silence landed in Phase 4.
6. Shared source-module lib `Bardie.Module.Source` — **done**.



### Work (Magpie — parallel)

1. Implement source contract: register, search (+ URL/id fallback), track jobs writing **s16le / 48 kHz / stereo** to `audio_endpoint` — **done** (`src/Magpie`, `Bardie.Module.Source`).
2. Cache-first Tune resolve via storage contract (`tunes/magpie/…` keys) — **done** (YoutubeExplode + FFmpeg.AutoGen; sine track for local proof).
3. Honor `StopTrack` / `PauseTrack` / `ResumeTrack`; advertise `search` | `play` | `pause` — **done**.
4. Local Compose: `local/compose.phase3.yml` + `scripts/phase3-source-smoke.sh` (`SEARCH_QUERY=sine`).



### Exit criteria

- Magpie registers; Kithara can Search and StartTrack; PCM lands on a FIFO Kithara created.
- A second fake source module could register without Magpie code changes.



### Cross-repo


| Repo       | Follow-up                                     |
| ---------- | --------------------------------------------- |
| **magpie** | ytdl + decode + OTel `bardie.source.magpie`   |
| **org**    | Shared volume / storage networking in Compose |


---



## Phase 4 — Neck (alive Struna + FFmpeg)

**Status: complete.** Encode-alive create, silence feeder, in-process MP3 supervisor, `TrackStatus` → now-playing / queue advance, pause-as-silence, `PrefetchTrack` on enqueue, FIFO realtime pacing, Channel peer pin (MESH-CHN-001). Soft continuity residuals → Phase 8 ([security-audit](security-audit.md) NECK-JOB-001).

**Why:** Broadcast sync and ICY continuity require long-lived encoder + silence ([ADR 001](../adrs/001-broadcast-sync-model.md), [ADR 004](../adrs/004-source-instance-socket-audio-plane.md)). Host→module dials intensify here — Channel peer pinning closed with this phase.

### Work

1. Hosted **FFmpeg supervisor** (not request-scoped) + `IDbContextFactory` — discard spike singleton+scoped pattern — **done**.
2. Promote create from **control-alive** (slug + FIFO already) to **encode-alive**: start silence feeder + FFmpeg reading the session FIFO — **done**.
3. `DELETE /api/streams/{id}` → `StopTrack` first, then kill FFmpeg, close FIFO, free slug (guest teardown already clears search cache) — **done**.
4. Pause = silence feeder on; empty `play` = unpause ([playback-control](../domains/playback-control.md)) — **done**.
5. Queue head → `StartTrack` / skip → `StopTrack` + next; **never** restart FFmpeg on queue shift — **done**.
6. **Operator encode profile (locked):** PCM s16le / 48 kHz / stereo → MP3 (~128 kbps, `libmp3lame`). No user-facing `compatibility` / `quality` create field for MVP — **done**.
7. **NECK-JOB-002:** `TrackStatus` drives now-playing / queue advance; silence on until `Running`; `Preparing` through download/transcode; `StopTracksForStruna` + Magpie sibling cancel; per-Struna gate; ICY `StreamTitle` never falls back to raw `track_ref` — **done**. Cross-restart Magpie reattach remains out of scope (orphan jobs die with lost dials).
8. **MESH-CHN-001 ([security-audit](security-audit.md)):** bilateral host↔slug mTLS pin — **done**.



### Exit criteria

- Alive Struna produces continuous encoded audio on FFmpeg’s output pipe with silence between tracks.
- Skip does not drop ICY listeners (verified once Phase 5 exists; pipe continuity checked here).
- Work-port dials reject a non-host mesh client cert; host dials reject a work-port cert that is not the registered module’s.



### Discard from spike

- Playlist concat demuxer approach, Icecast-style output URL, ICY via FFmpeg stdin — see [spike](../spike/prototype-neck-ffmpeg.md).

---



## Phase 5 — Stream Server (ICY)

**Status: complete.** `GET /stream/{slug}` with ICY + fan-out from Neck encode; listen-token / access gates; `StreamTitle` from Neck now-playing. Soft residuals (constant-time token compare) → Phase 8.

**Why:** Listeners are the product surface; API-only is not a radio.

### Work

1. `GET /stream/{slug}` with ICY headers + `icy-metaint` metadata injection ([http-stream-output](../interfaces/http-stream-output.md)) — **done**.
2. Fan-out from FFmpeg pipe to N listeners — **done**.
3. Playback access gates: public / protected query token / private Bearer ([struna-access](../domains/struna-access.md)) — **done**.
4. Push now-playing → `StreamTitle` updates from Neck/track status — **done**.



### Exit criteria

- VLC (or equivalent) plays a public slug URL continuously across a skip.
- Protected stream rejects missing/wrong token.

---



## Phase 6 — Control REST complete + auth hardening

**Status: complete.** Grant CRUD, permission ceiling, pause-as-silence + empty play unpause, now-playing aligned with ICY, AUTH/GUEST/LIB product remediations, AUTH-ORCH-001 harness routing. Soft residuals (host rotate deny, guest failure lockout) → Phase 8 — see [security-audit](security-audit.md).

**Why:** Clients (Plume or raw HTTP) need a trustworthy DJ surface — not just verbs that work.

### Landed under Phase 3 (control verbs)


| Slice                                                                                              | Status   |
| -------------------------------------------------------------------------------------------------- | -------- |
| `GET /api/search/quick` (`q`/`query`), `POST /api/search` + principal **search cache** (≠ history) | **Done** |
| `GET /api/streams/listen`, `GET /api/streams/control`                                              | **Done** |
| `POST/GET/DELETE /api/streams`, `POST …/play` / `quickplay`                                        | **Done** |
| `POST …/pause`, `POST …/skip`, `GET …/now-playing`                                                 | **Done** |
| Queue / quickqueue CRUD                                                                            | **Done** |
| Guest exchange + destroy guests with Struna (+ clear their search cache)                           | **Done** |
| Owner + grant (+ protected-control guest) ACL stubs                                                | **Done** |




### Closed in Phase 6 — control

1. **Grant CRUD** (owner-only) — **done**.
2. **Managed permission ceiling** — **done**.
3. Pause-as-silence + empty `play` unpause — **done**.
4. `GET …/now-playing` aligned with ICY `StreamTitle` — **done**.



### Closed in Phase 6 — security ([security-audit](security-audit.md))


| ID                 | Status                                                                              |
| ------------------ | ----------------------------------------------------------------------------------- |
| **GUEST-REF-001**  | **Done** — host guest refresh                                                       |
| **LIB-TUNE-001**   | **Done** — `EnsureKeyOwnedBy`                                                       |
| **AUTH-ROT-001**   | **Done** — `UpdateUserBinding` / `bind_form`; host control deny (AUTH-ROT-002); seed/register orphan + subject collision fixed (AUTH-SEED-001 / AUTH-BIND-001) |
| **AUTH-ROLE-001**  | **Done** — roles from binding                                                       |
| **AUTH-JWKS-001**  | **Done** — async JWKS snapshot                                                      |
| **AUTH-JWKS-002**  | **Done** — Register awaits JWKS; fail-closed rejects Register                       |
| **GUEST-XCHG-001** | **Partial** — 10/min rate limit; failure lockout **Done** (GUEST-XCHG-002)         |
| **AUTH-DISP-001**  | **Done** — unique `User.Username`; `/me` + invite `must_complete_binding` |




### Closed in Phase 6 — harness routing

1. **AUTH-ORCH-001:** Auth Harness `provider_id → module` map — **done**.



### Exit criteria

- Full DJ loop with Bes JWT and with guest JWT on a protected-control Struna.
- Magpie is selectable only via `module` slug / priority — no Magpie-specific REST.
- Security checklist items for Phase 6 in [security-audit](security-audit.md) are closed (GUEST-REF-001, LIB-TUNE-001, AUTH-ROLE-001, AUTH-JWKS-001, AUTH-ROT-001, AUTH-DISP-001); GUEST-XCHG-002 closed in Phase 8.
- `provider_id` that is not equal to module slug still authenticates against the correct adapter.

---



## Phase 7 — Plume MVP (optional client)

**Why:** Reference user-aware UI; stack must still work without it.

Kithara Phase 7 is the **stack umbrella** — “Plume exists and meets exit criteria.” Delivery order lives in Plume’s own phases:

- [Plume implementation plan](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/implementation-plan.md) (Plume Phases 1–6)
- [Plume UI stack](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/03-ui-stack.md) (`/control/{slug}` desk vs `/player/{slug}` listen surface)

Do **not** renumber Plume work as “Kithara 7.1 / 7.2”. Satisfies this phase when Plume Phases 1–6 exit.

### Work (Plume — summary)

1. Edge routes `/`, `/control/{slug}`, `/player/{slug}`.
2. BFF session + discovery-driven Bes login (no JWT in browser).
3. Control desk (`/control/{slug}`): queue, search, transport; poll now-playing.
4. Listen surface (`/player/{slug}`): prominent now-playing; browser audio **off by default**; optional `/stream/{slug}`.
5. Guest exchange UX for protected control; Register + OTel `bardie.plume`.



### Exit criteria

- Human can create a Struna, search Magpie, play, and hear it in VLC via `/stream/{slug}`.
- Removing Plume from Compose leaves API + stream + modules working.



### Cross-repo


| Repo      | Follow-up                                                                                                                                                                                                                                                                                                                     |
| --------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **plume** | [implementation-plan](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/implementation-plan.md) · [03-ui-stack](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/03-ui-stack.md) · [mvp/v0.1-scope](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/v0.1-scope.md) |
| **org**   | Edge path map `/control/`* + `/player/*` — keep aligned                                                                                                                                                                                                                                                                       |


---



## Phase 8 — Compose bundle + verify telemetry

**Why:** Modularity is proven only when modules attach by config. OTel export already exists from Phase 1 — this phase **wires the collector** and proves cross-service traces. Also closes QA / ops / doc debt from the Phases 1–3 review.

### Work

1. Reference Compose: edge + `plume` + `kithara` + `magpie` + `bes` ([org deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md)).
2. `BARDIE_JOIN_SECRETS` for all modules; audio/storage volumes as decided.
3. Point every app at the **external** OTel collector (`OTEL_EXPORTER_OTLP_ENDPOINT`); confirm `service.name` values per [observability](../operations/observability.md). See [META-OTEL-001](known-issues.md#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint) / [kithara#34](https://github.com/Bardie-radio/kithara/issues/34).
4. Smoke script / checklist: register → login → create → play → listen → skip — **and** a single play trace spanning Plume → Kithara → Magpie. Requires [META-OTEL-002](known-issues.md#meta-otel-002--taskrun-drops-activity-context-magpie--neck) (Activity across `Task.Run`) before the continuous tree can hold; attrs/stages under [META-OTEL-003](known-issues.md#meta-otel-003--span-attrs--stage-coverage-lag-adr-008).
5. **META-QA-001:** Host integration tests (discovery→`/me`, create→play→FIFO readable, guest exchange, **AUTH-INVITE** bootstrap: empty DB invite → claim → bind). Prefer landing tests alongside Phase 4–6 PRs; Phase 8 is the freeze that they must pass. Magpie/Bes module-local unit tests. **auth-invite-bindonly / AUTH-CLAIM-001:** claim allow-list + post-bind access death — **closed** (unit + Plume BFF invite tests).
6. **META-OPS-001:** Align Local phase3 sine smoke with Magpie image config (Debug sine helper vs Release YouTube default) so the documented smoke path is honest.
7. **META-OPS-002:** Shrink finals — Alpine base for the MVP quartet; Kithara/Magpie ship bare-minimum FFmpeg.AutoGen shared libs (not apt/apk `ffmpeg` metapackages). See [known-issues](known-issues.md#meta-ops-002--final-images-bloated-ubuntu--full-ffmpeg) / [kithara#33](https://github.com/Bardie-radio/kithara/issues/33).
8. **META-DOC-001:** Sweep doc drift (library Tune path, Bes operations JWT wording, Magpie Register wording, MVP phase status vs code).



### Exit criteria

- Documented `docker compose up` path for the MVP quartet.
- Collector shows a continuous play path across all four `bardie.*` service names (META-OTEL-001/002 closed or explicitly deferred).
- META-QA-001 / META-OPS-001 / META-OPS-002 / META-DOC-001 / META-OTEL-* closed or explicitly deferred with owners.

---



## Suggested coding order inside Kithara (Phase 1–6)

Use this when slicing PRs:

1. Solution layout + DI + config + **OTel bootstrap** + DB migrations
2. Module Registry (join secret) + gRPC host (spans via auto-instrumentation)
3. Auth Harness + JWT verify + auth REST
4. Library/Tune + local storage driver
5. Source client (dial Magpie) + FIFO smoke (+ custom spans on attach)
6. Neck supervisor + silence + FFmpeg (+ custom spans on lifecycle)
7. Stream Server + listen ACL
8. Remaining stream control REST + guest JWT

Prefer **vertical slices** that end in a demoable behaviour over horizontal “all models then all APIs.” Add custom Activity attributes as each feature lands — do not defer a big “instrumentation pass.”

---



## What “done” means for v0.1

Aligned with [v0.1-scope](v0.1-scope.md):

- [x] Alive Struna with slug; silence until first track; DELETE frees slug  
- [x] Magpie search/play via unified source contract  
- [x] Bes login via unified auth contract; Kithara verifies JWTs  
- [x] ICY `/stream/{slug}` with metadata; protected listen token  
- [x] Guest code → ephemeral guest user + JWT; guests die with Struna  
- [ ] Plume optional; Compose + join secrets; OTel live from Phase 1, verified E2E in Phase 8

Out of scope stays out: Argus/Hecate, Beak/Cauda, Catbird/Starling, Icecast/HLS primary, multi-instance Kithara, `PrepareTrack`.

---



## Decisions locked (from design review)


| Topic                   | Decision                                                                                                                                                                                                                                   |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Register dial**       | Modules **dial Kithara** to join; default `KITHARA_GRPC_ADDRESS` = Compose DNS (`kithara:5000`). Kithara hosts [Module Registry](../interfaces/grpc-module-registry.md).                                                                   |
| **Work RPCs**           | Module advertises address; **Kithara dials the module per operation** (no long-lived command stream) — atomic calls for OTel + access control.                                                                                             |
| **All modules equal**   | Source, auth, **and client** (Plume, …) Register over gRPC. REST `/api` is the end-user surface UI modules use for UX.                                                                                                                     |
| **Audio attach**        | Shared Compose volume; Kithara creates per-Struna session endpoints on demand; modules write PCM ([ADR 004](../adrs/004-source-instance-socket-audio-plane.md)). Prefer Unix sockets in implementation.                                    |
| **Storage**             | Drivers **only on Kithara**; modules dial a **thin** put/get API. No per-module `BARDIE_STORAGE_`*.                                                                                                                                        |
| **Pause**               | Part of common source contract (`PauseTrack` / `ResumeTrack`); Magpie implements; Starling omits `pause` capability.                                                                                                                       |
| **Bootstrap admin**     | **AUTH-INVITE:** host registration OTP (log once); `POST /api/auth/claim` → bind ceremony; admin `/register` username-only + one-time password. No `seedAdmin` / `SeedAdminBinding`. |
| **Multi-source**        | Design for many sources from day one (priority / fan-out) — no Magpie-only shortcuts.                                                                                                                                                      |
| **Search**              | **Global** REST; principal-scoped cache. Guests: clear on Struna teardown. Durable/managed: replace on next search + configurable timeout.                                                                                                 |
| **Guests**              | Guest code **per Struna** → each exchange creates an **ephemeral guest user** + Kithara JWTs (+ refresh); destroyed with Struna. **Rotate code = block new joins only** (existing guests keep working until Struna delete).                |
| **ACL**                 | Any registered durable/managed user may create Strunas; **owner** on Struna model; private control = owner + owner grants; ephemeral guests = **only** control that Struna; managed users ≤ static module’s advertised permission ceiling. |
| **Proto packaging**     | **Published package** (versioned contracts) for module authors / contributors.                                                                                                                                                             |
| **Guest JWT signing**   | If `BARDIE_GUEST_JWT_SIGNING_KEY` (or key file) is set → use it; else **auto-generate** on first boot and **persist** next to data volume. Access TTL default ~15m; refresh until Struna teardown (or capped refresh lifetime).            |
| **Module channel auth** | Target: join secret at Register → Kithara issues module client cert → **mTLS on the whole gRPC surface** afterward.                                                                                                                        |
| **Encode mode UI**      | Dropped from user-facing create for now; operator/FFmpeg profile instead.                                                                                                                                                                  |
| **Tune model**          | Unified library unit for **queue + history + optional blob cache**; sparse Tunes OK (e.g. Starling URI, no bytes). `QueueEntry` → Tune id.                                                                                                 |
| **Naming**              | **durable user** / **managed user** (static UI; long-lived) / **ephemeral guest user** (guest code; Struna-scoped).                                                                                                                        |


Design-review open questions are **closed**. Phase 0 can proceed from the locked table above.

---



## Related

- [v0.1-scope.md](v0.1-scope.md) · [v0.1-milestones.md](v0.1-milestones.md) · [security-audit.md](security-audit.md)
- [glossary](../glossary.md) · [grpc-module-registry](../interfaces/grpc-module-registry.md) · [grpc-source-module](../interfaces/grpc-source-module.md) · [grpc-blob-storage](../interfaces/grpc-blob-storage.md) · [grpc-library](../interfaces/grpc-library.md) · [auth](../interfaces/auth.md)
- Org: [05-deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md)

**Read next:** [security-audit.md](security-audit.md) · Phase 7 Plume · Phase 8 Compose + verify.