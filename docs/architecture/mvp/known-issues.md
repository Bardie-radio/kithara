# Known issues (MVP)

Living index of **non-security** product / design footguns discovered after Phases 4–6. Security findings stay in [security-audit.md](security-audit.md). Soft residuals already listed there (e.g. `NECK-JOB-001`, `AUTH-ROT-002`) are not duplicated here unless they need a design note.

**Plume-local** scaffold / BFF / UI footguns (`PLUME-*`) live in [Plume known-issues](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/known-issues.md) — do not duplicate them here.

**Owner default:** Phase 8 polish unless noted.

IDs use `SURFACE-TOPIC-NNN` (see [security-audit ID scheme](security-audit.md#id-scheme)). Former `KI-*` aliases: `KI-01` → `NECK-PCM-001`, `KI-02` → `NECK-SWP-001`. OTel gaps use `META-OTEL-*`.

| ID | Sev | Summary | Tracking |
|----|-----|---------|----------|
| [NECK-PCM-001](#neck-pcm-001--canonical-pcm-format-constants-are-duplicated) | P2 | Session PCM rate/channels hard-coded in several places | [kithara#25](https://github.com/Bardie-radio/kithara/issues/25) |
| [NECK-SWP-001](#neck-swp-001--orphan-writer-sweep-runs-on-every-play-including-new-strunas) | P2 | `StopOrphanWriters` / `StopTracksForStruna` on the hot play path | [kithara#26](https://github.com/Bardie-radio/kithara/issues/26) |
| [META-OPS-002](#meta-ops-002--final-images-bloated-ubuntu--full-ffmpeg) | P2 | Final images: Ubuntu aspnet + full apt `ffmpeg`; move to Alpine + bare-minimum libav | [kithara#33](https://github.com/Bardie-radio/kithara/issues/33) |
| [META-OTEL-001](#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint) | P1 | Local Compose never sets OTLP endpoint; Tempo has no `bardie.*` | [kithara#34](https://github.com/Bardie-radio/kithara/issues/34) |
| [META-OTEL-002](#meta-otel-002--taskrun-drops-activity-context-magpie--neck) | P1 | Fire-and-forget `Task.Run` orphans Magpie track work + Neck encode spans | [kithara#36](https://github.com/Bardie-radio/kithara/issues/36) |
| [META-OTEL-003](#meta-otel-003--span-attrs--stage-coverage-lag-adr-008) | P2 | Missing Magpie stages, attrs, listen tags; no OTLP logs | [kithara#35](https://github.com/Bardie-radio/kithara/issues/35) |
| [STREAM-ACL-001](#stream-acl-001--protected-canlisten-ignores-listen-token-holders) | P3 | `CanListen` / listen list for **protected** are owner+grant only; token holders are stream-only today | backlog |

---

## NECK-PCM-001 — Canonical PCM format constants are duplicated

**Severity:** P2 (footgun; not a live bug while the MVP profile stays frozen)  
**Component:** Neck + `Bardie.Module.Source` + Magpie transcoder  
**Owner:** Phase 8 / encode-profile polish

**Tracking:** [kithara#25](https://github.com/Bardie-radio/kithara/issues/25)

MVP locks the session audio plane to **s16le / 48 kHz / stereo** ([grpc-source-module](../interfaces/grpc-source-module.md), [source-instances](../domains/source-instances.md)). That profile is re-stated as private/`Out*` constants in multiple binaries:

| Location | Role |
|----------|------|
| `libs/Bardie.Module.Source/FifoAudioSink.cs` | Realtime write pacing (`SampleRate`, `Channels`) |
| `src/Kithara/Infrastructure/Neck/SilenceFeeder.cs` | Zero-PCM feed + encoder assumes these (`public const`) |
| `libs/Bardie.Module.Source.Debug/SinePcmProof.cs` | Dev sine stream |
| Magpie `Infrastructure/Media/FfmpegPcmTranscoder.cs` | Decode → FIFO (`OutSampleRate`, `OutChannels`) |

Chunk / buffer sizes (e.g. `FifoAudioSink.BufferSize`) are **not** the issue — they are local I/O knobs.

**Failure mode:** changing 48 kHz / stereo in only one site desyncs Magpie writers from Neck’s silence feeder and FFmpeg reader → garbled audio, wrong pacing, `Ended` vs audible end drift.

**Remediation sketch:** one shared `CanonicalPcm` (or equivalent) in `Bardie.Module.Source`, consumed by Fifo pacing, SilenceFeeder, Magpie transcoder, and the debug sine generator. Docs should point at that definition rather than only repeating the numbers.

---

## NECK-SWP-001 — Orphan writer sweep runs on every play (including new Strunas)

**Severity:** P2 (works; dirty hot path / symptom treatment)  
**Component:** Neck `PlayTrackCoreAsync` → `StopOrphanWritersAsync` → Magpie `StopTracksForStruna`  
**Owner:** Phase 8 / Neck polish  
**Related:** [security-audit NECK-JOB-001](security-audit.md) (TrackStatus disconnect can orphan Neck jobs)

**Tracking:** [kithara#26](https://github.com/Bardie-radio/kithara/issues/26)

When Neck has **no tracked job** for a Struna, play still dials `StopTracksForStruna` on the target module before `StartTrack` — including **brand-new Strunas that never had a job**. Module-switch plays similarly sweep the *previous* module.

That bulk cancel exists because Neck can lose `track_job_id` while Magpie still holds a FIFO writer (`StopTrack(job_id)` then cannot run). Magpie `Create` already cancels siblings for the same Struna; the host sweep is recovery for host bookkeeping failure, not the happy path.

**Failure mode (design):** every first play pays a useless gRPC; Struna-scoped stop stays on the normal control path; mistimed sweeps race `StartTrack` (already partially gated because of that).

**Remediation sketch:**

1. Happy path = `StopCurrentTrack` + Magpie sibling cancel in `Create` only — **no** orphan sweep on first play.
2. Keep Struna-scoped cancel for rare recovery (delete / proven desync / skip with unknown orphans), not every `play`.
3. Harden job-map / TrackStatus continuity (NECK-JOB-001) so orphans become exceptional.
4. Demote or drop `StopTracksForStruna` from the common contract once Neck no longer needs it for play.

---

## META-OPS-002 — Final images bloated (Ubuntu + full FFmpeg)

**Severity:** P2 (ops / deploy cost; not a runtime bug)  
**Component:** Kithara + Magpie Dockerfiles (FFmpeg.AutoGen); Plume + Bes (Alpine only)  
**Owner:** Phase 8 / ops polish

**Tracking:** [kithara#33](https://github.com/Bardie-radio/kithara/issues/33) · [magpie#6](https://github.com/Bardie-radio/magpie/issues/6) · [plume#13](https://github.com/Bardie-radio/plume/issues/13) · [bes#10](https://github.com/Bardie-radio/bes/issues/10)

Local Compose finals are far larger than the managed apps need:

| Image | ~size | Dominant cost |
|-------|------:|---------------|
| `bardie-kithara:*` | ~745MB | `aspnet:10.0` + apt `ffmpeg` metapackage |
| `bardie-magpie:*` | ~657MB | same |
| `bardie-plume:*` / `bardie-bes:*` | ~242MB | mostly stock Ubuntu aspnet (+ `curl`) |

Neck and Magpie load **shared libs via FFmpeg.AutoGen** (PCM→MP3 encode; demux/decode/resample). They do not need the FFmpeg CLI or Ubuntu’s video-codec dependency tree.

**Remediation sketch:**

1. Final stage → `aspnet:10.0-alpine` (or equivalent) for all four MVP services.
2. Kithara/Magpie: ship **only** the libav pieces each process uses — not `apk`/`apt` `ffmpeg` metapackages. Keep FFmpeg.AutoGen soname compatibility (today 6.1.x / `libavcodec.so.60`): pin/build 6.1 libs on Alpine, or bump AutoGen with test proof.
3. Update `BARDIE_FFMPEG_ROOT` / `MAGPIE_FFMPEG_ROOT` (Alpine lib layout ≠ `/usr/lib/x86_64-linux-gnu`) and healthcheck tooling (`curl` vs busybox wget).
4. Success bar (local uncompressed): Magpie/Kithara well under ~350MB; Plume/Bes clearly under ~242MB.

Out of scope here: PublishTrimmed / Native AOT.

---

## META-OTEL-001 — Local Compose omits `OTEL_EXPORTER_OTLP_ENDPOINT`

**Severity:** P1 (blocks Phase 8 continuous-play verification)  
**Component:** Local Compose sketches + all MVP app containers  
**Owner:** Phase 8 / observability verify

**Tracking:** [kithara#34](https://github.com/Bardie-radio/kithara/issues/34)

SDK bootstrap is in place (`bardie.kithara`, `bardie.plume`, `bardie.source.magpie`, `bardie.auth.bes`) with OTLP exporters, but **Local** `compose.phase2.yml` / `compose.phase3.yml` / `compose.plume.yml` never set `OTEL_EXPORTER_OTLP_ENDPOINT`. Live Tempo has no `bardie.*` resource names (operator stack shows unrelated services only).

Phase 8 already owns “point every app at the external collector”; this records that **current sketches omit the env**, so nothing leaves the processes in the Local path.

**Remediation sketch:** set the endpoint (and optional `OTEL_RESOURCE_ATTRIBUTES`) on the MVP quartet in Local sketches, or document a clear operator inject path; smoke until Tempo lists all four `service.name` values.

**Related:** [META-OTEL-002](#meta-otel-002--taskrun-drops-activity-context-magpie--neck) still blocks continuous play after export works · [ADR 008](../adrs/008-otel-observability.md) · [observability](../operations/observability.md)

---

## META-OTEL-002 — `Task.Run` drops Activity context (Magpie + Neck)

**Severity:** P1 (Phase 8 continuous play exit cannot hold)  
**Component:** Magpie `TrackPlaybackService`; Neck FFmpeg / silence / TrackStatus watchers  
**Owner:** Phase 8 / observability

**Tracking:** [kithara#36](https://github.com/Bardie-radio/kithara/issues/36)

Sync hops stitch on paper (W3C + AspNetCore / HttpClient / GrpcNetClient): Plume BFF → Kithara HTTP → Bes Authenticate / Magpie `StartTrack` **RPC**. Fire-and-forget background work then **loses `Activity.Current`**:

| Site | Effect |
|------|--------|
| Magpie `Start` → `Task.Run(RunJobAsync)` | RPC span ends; resolve/cache/FFmpeg/FIFO invisible; `TagTrackJob(Activity.Current)` usually no-ops |
| Neck `FfmpegMp3PcmEncoder` `Task.Run` | `neck.encoder.run` often becomes an orphan root |
| `SilenceFeeder` long attach | Often orphan after create HTTP ends |
| `Neck.Playback` TrackStatus watcher | Detached from the play request |

**Remediation sketch:**

1. Capture parent `Activity` / `ActivityContext` before `Task.Run`; start child or linked spans inside the work.
2. Prefer **ActivityLink** for long-lived encode/silence keyed by `struna.id` / track job id — do not force them under the short API span.
3. Magpie: dedicated track-job `ActivitySource` + `AddSource` registration.

**Related:** [META-OTEL-001](#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint) · [META-OTEL-003](#meta-otel-003--span-attrs--stage-coverage-lag-adr-008)

---

## META-OTEL-003 — Span attrs / stage coverage lag ADR 008

**Severity:** P2 (polish after export + context fix)  
**Component:** Magpie playback; Kithara Stream Server; Module.Hosting OTel; harness dials  
**Owner:** Phase 8 / observability polish

**Tracking:** [kithara#35](https://github.com/Bardie-radio/kithara/issues/35)

After META-OTEL-001/002, remaining gaps vs [ADR 008](../adrs/008-otel-observability.md) / [observability](../operations/observability.md):

- Magpie has no resolve/cache/transcode/FIFO **stage** spans
- Missing / drifted attrs: `playback.access`, `control.access`, `auth.provider.id`; code `track.job_id` vs docs `source.track_job.id`
- `GET /stream/{slug}` lacks enriched `struna.*` tags beyond the route template
- ADR asks for OTLP **logs**; SDK wires traces + metrics only
- Resource attrs: only `service.name` + version unless operator sets `OTEL_RESOURCE_ATTRIBUTES`
- Work-RPC “record module slug + RPC name” is auto GrpcNetClient only

**Remediation sketch:** align attribute names; Magpie stage spans; listen enrichment; decide OTLP logs for MVP vs defer.

---

## STREAM-ACL-001 — Protected `CanListen` ignores listen-token holders

**Severity:** P3 (design gap; stream gate is correct)  
**Component:** `StrunaAccess.CanListen` / `AppearsOnListenList` vs Stream Server  
**Owner:** Phase 8 / access-model polish

**Tracking:** backlog (no GitHub issue yet)

Protected playback is **token-based** on `/stream/{slug}?token=…` ([struna-access](../domains/struna-access.md)). REST `CanListen` / listen-list membership today only check **owner + control grant** — same as private for list purposes. Holders of the listen token can play audio but do not appear as “may listen” principals on GUID discover / home lists, and Plume cannot treat “I have the token” as session ACL without a Bearer exchange (intentionally omitted for legacy players).

**Failure mode (design):** token-only listeners are second-class on REST surfaces; any future “listenable to me” UX that keys off `CanListen` under-counts them.

**Remediation sketch (later):** decide whether listen-token capability should ever mint a short-lived listen principal, stay stream-only forever, or get a separate discovery path — do not silently widen `CanListen` to “everyone with the query param” on authenticated REST.

---

**Related:** [security-audit.md](security-audit.md) · [implementation-plan.md](implementation-plan.md) · [grpc-source-module.md](../interfaces/grpc-source-module.md) · [observability.md](../operations/observability.md)

**Read next:** [security-audit.md](security-audit.md)
