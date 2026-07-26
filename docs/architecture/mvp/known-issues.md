# Known issues (MVP)

Living index of **non-security** product / design footguns discovered after Phases 4–6. Security findings stay in [security-audit.md](security-audit.md). Soft residuals already listed there (e.g. `AUTH-ROT-002`) are not duplicated here unless they need a design note.

**Plume-local** scaffold / BFF / UI footguns (`PLUME-*`) live in [Plume known-issues](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/known-issues.md) — do not duplicate them here.

**Owner default:** Phase 8 polish unless noted.

IDs use `SURFACE-TOPIC-NNN` (see [security-audit ID scheme](security-audit.md#id-scheme)). Former `KI-*` aliases: `KI-01` → `NECK-PCM-001`, `KI-02` → `NECK-SWP-001`. OTel gaps use `META-OTEL-*`.

| ID | Sev | Summary | Tracking |
|----|-----|---------|----------|
| [NECK-PCM-001](#neck-pcm-001--canonical-pcm-format-constants-are-duplicated) | P2 | **Fixed** — shared `CanonicalPcm` | [kithara#25](https://github.com/Bardie-radio/kithara/issues/25) |
| [NECK-SWP-001](#neck-swp-001--orphan-writer-sweep-runs-on-every-play-including-new-strunas) | P2 | **Fixed** — no orphan sweep on play; recovery-only | [kithara#26](https://github.com/Bardie-radio/kithara/issues/26) |
| [META-OPS-002](#meta-ops-002--final-images-bloated-ubuntu--full-ffmpeg) | P2 | **Fixed** — Alpine finals + bare `ffmpeg-libav*` (no CLI metapackage) | [kithara#33](https://github.com/Bardie-radio/kithara/issues/33) |
| [META-OTEL-001](#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint) | P1 | **Fixed** — opt-in via `compose.otel*.yml` (no default endpoint) | [kithara#34](https://github.com/Bardie-radio/kithara/issues/34) |
| [META-OTEL-002](#meta-otel-002--taskrun-drops-activity-context-magpie--neck) | P1 | **Fixed** — ActivityLink across Magpie/Neck `Task.Run` | [kithara#36](https://github.com/Bardie-radio/kithara/issues/36) |
| [META-OTEL-003](#meta-otel-003--span-attrs--stage-coverage-lag-adr-008) | P2 | **Fixed** (traces/attrs/stages; OTLP logs still optional/deferred) | [kithara#35](https://github.com/Bardie-radio/kithara/issues/35) |
| [STREAM-ACL-001](#stream-acl-001--protected-canlisten-ignores-listen-token-holders) | P3 | `CanListen` / listen list for **protected** are owner+grant only; token holders are stream-only today | backlog |
| [DEPLOY-PLUME-001](#deploy-plume-001--ghcr-plume-missing-vite-wwwrootdist) | Blocker | Published Plume image lacks Vite assets until republish | [pre-publish-audit](pre-publish-audit.md) |
| [META-LOG-001](#meta-log-001--production-log-noise--auth-invite-banner) | P3 | **Fixed** (source) — quiet EF/framework logs; AUTH-INVITE banner | publish required |

---

## DEPLOY-PLUME-001 — GHCR Plume missing Vite `wwwroot/dist`

**Severity:** Blocker for clean `docker compose up` from published images  
**Component:** Plume Dockerfile / publish / static files  
**Status:** **Open** until GHCR image includes `wwwroot/dist`

**Tracking:** [pre-publish-audit](pre-publish-audit.md) · local `deploy-test` builds Plume from source as a workaround

Source already runs `npm run build` before `dotnet publish` and serves via `UseStaticFiles`. Older GHCR tags omit dist → empty Content-Type + `nosniff` breaks the UI. Republish Plume after the Dockerfile/csproj fix is on the default branch.

---

## META-LOG-001 — Production log noise + AUTH-INVITE banner

**Severity:** P3 (ops ergonomics)  
**Component:** Kithara + Plume + Magpie + Bes logging  
**Status:** **Fixed** in source (26 Jul 2026) — **publish required**

EF Core SQL at Information and broad `Microsoft`/`System` noise buried the registration OTP. Production defaults now raise EF/framework categories to Warning; Kithara logs a multi-line AUTH-INVITE WARNING banner; modules print a short startup banner. Does not log join secrets.

---

## NECK-PCM-001 — Canonical PCM format constants are duplicated

**Severity:** P2 (footgun; not a live bug while the MVP profile stays frozen)  
**Component:** Neck + `Bardie.Module.Source` + Magpie transcoder  
**Owner:** Phase 8 / encode-profile polish  
**Status:** **Fixed**

**Tracking:** [kithara#25](https://github.com/Bardie-radio/kithara/issues/25)

MVP locks the session audio plane to **s16le / 48 kHz / stereo** ([grpc-source-module](../interfaces/grpc-source-module.md), [source-instances](../domains/source-instances.md)). That profile is defined once as `CanonicalPcm` in `Bardie.Module.Source` and consumed by:

| Location | Role |
|----------|------|
| `Bardie.Module.Source` `FifoAudioSink` ([kithara-logos-source](https://github.com/Bardie-radio/kithara-logos-source)) | Realtime write pacing |
| `src/Kithara/Infrastructure/Neck/SilenceFeeder.cs` | Zero-PCM feed (+ encoder via `CanonicalPcm`) |
| `Bardie.Module.Source.Debug` `SinePcmProof` ([kithara-logos-source](https://github.com/Bardie-radio/kithara-logos-source)) | Dev sine stream |
| Magpie `Infrastructure/Media/FfmpegPcmTranscoder.cs` | Decode → FIFO |

Chunk / buffer sizes (e.g. `FifoAudioSink.BufferSize`) remain local I/O knobs.

**Failure mode (historical):** changing 48 kHz / stereo in only one site desyncs Magpie writers from Neck’s silence feeder and FFmpeg reader → garbled audio, wrong pacing, `Ended` vs audible end drift.

---

## NECK-SWP-001 — Orphan writer sweep runs on every play (including new Strunas)

**Severity:** P2 (works; dirty hot path / symptom treatment)  
**Component:** Neck `PlayTrackCoreAsync` → `StopOrphanWritersAsync` → Magpie `StopTracksForStruna`  
**Owner:** Phase 8 / Neck polish  
**Status:** **Fixed**  
**Related:** [security-audit NECK-JOB-001](security-audit.md) (TrackStatus disconnect continuity)

**Tracking:** [kithara#26](https://github.com/Bardie-radio/kithara/issues/26)

**Shipped:** Happy play path = `StopCurrentTrack` + Magpie sibling cancel in `Create` only — **no** `StopTracksForStruna` on first play or module-switch play. Struna-scoped cancel remains for recovery (delete, pause when Neck has no job id, skip when Neck has no job id). TrackStatus disconnect reconnect (NECK-JOB-001) keeps orphans exceptional.

**Failure mode (historical):** every first play paid a useless gRPC; mistimed sweeps could race `StartTrack`.

---

## META-OPS-002 — Final images bloated (Ubuntu + full FFmpeg)

**Severity:** P2 (ops / deploy cost; not a runtime bug)  
**Component:** Kithara + Magpie Dockerfiles (FFmpeg.AutoGen); Plume + Bes (Alpine only)  
**Owner:** Phase 8 / ops polish  
**Status:** **Fixed**

**Tracking:** [kithara#33](https://github.com/Bardie-radio/kithara/issues/33) · [magpie#6](https://github.com/Bardie-radio/magpie/issues/6) · [plume#13](https://github.com/Bardie-radio/plume/issues/13) · [bes#10](https://github.com/Bardie-radio/bes/issues/10)

Finals previously used Ubuntu `aspnet:10.0` plus apt `ffmpeg` / `curl`.

**Remediation (shipped):**

1. All four MVP finals → `mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.22` (**not** floating `10.0-alpine`). Alpine 3.23+ ships ffmpeg **8** (`libavutil.so.60`); AutoGen **6.1.0.1** needs 6.1 sonames (`.58` / `.60`). **Build** on Debian `sdk:10.0` (Grpc.Tools `protoc` is glibc-only) and `dotnet publish -r linux-musl-x64 --self-contained false`.
2. Kithara/Magpie: Alpine **split** packages only — `ffmpeg-libavcodec` / `libavformat` / `libavutil` / `libswresample` (3.22 → 6.1.x → `libavcodec.so.60`). **No** `ffmpeg` CLI metapackage.
3. `BARDIE_FFMPEG_ROOT` / `MAGPIE_FFMPEG_ROOT` → `/usr/lib`; Local Compose healthchecks use busybox `wget`.
4. Plume entrypoint uses `su-exec` (Alpine) instead of `setpriv`.

Out of scope: PublishTrimmed / Native AOT; in-app OTLP log export.

---

## META-OTEL-001 — Local Compose omits `OTEL_EXPORTER_OTLP_ENDPOINT`

**Severity:** P1 (blocks Phase 8 continuous-play verification)  
**Component:** Local Compose sketches + all MVP app containers  
**Owner:** Phase 8 / observability verify  
**Status:** **Fixed**

**Tracking:** [kithara#34](https://github.com/Bardie-radio/kithara/issues/34)

Local sketches leave OTel **off** by default (SDK no-ops when `OTEL_EXPORTER_OTLP_ENDPOINT` is unset). To export, set the endpoint and add the matching opt-in overlay (no default URL):

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:4317
docker compose -f local/compose.plume.yml -f local/compose.otel.yml up --build
# phase3 → compose.otel.phase3.yml; phase2 → compose.otel.phase2.yml
```

If you do not set the env / omit the overlay, you are not using OTel.

**Related:** [META-OTEL-002](#meta-otel-002--taskrun-drops-activity-context-magpie--neck) · [ADR 008](../adrs/008-otel-observability.md) · [observability](../operations/observability.md)

---

## META-OTEL-002 — `Task.Run` drops Activity context (Magpie + Neck)

**Severity:** P1 (Phase 8 continuous play exit cannot hold)  
**Component:** Magpie `TrackPlaybackService`; Neck FFmpeg / silence / TrackStatus watchers  
**Owner:** Phase 8 / observability  
**Status:** **Fixed**

**Tracking:** [kithara#36](https://github.com/Bardie-radio/kithara/issues/36)

Background work captures `ActivityContext` before `Task.Run` and starts **linked** roots (`NeckActivity.StartLinked`, `MagpieTrackActivity` + `AddSource`). Long-lived encode / silence / track-job / TrackStatus spans no longer nest under the short RPC/HTTP span.

**Related:** [META-OTEL-001](#meta-otel-001--local-compose-omits-otel_exporter_otlp_endpoint) · [META-OTEL-003](#meta-otel-003--span-attrs--stage-coverage-lag-adr-008)

---

## META-OTEL-003 — Span attrs / stage coverage lag ADR 008

**Severity:** P2 (polish after export + context fix)  
**Component:** Magpie playback; Kithara Stream Server; Module.Hosting OTel; harness dials  
**Owner:** Phase 8 / observability polish  
**Status:** **Fixed** (traces only — OTLP app logs remain optional)

**Tracking:** [kithara#35](https://github.com/Bardie-radio/kithara/issues/35)

Shipped vs ADR 008 / [observability](../operations/observability.md):

- Magpie stage spans: `magpie.track.resolve` / `cache` / `transcode` / `fifo`
- Attrs aligned: `source.track_job.id` (was `track.job_id`); `playback.access` / `control.access` on create + listen; `auth.provider.id` on authenticate
- `GET /stream/{slug}` sets `struna.id` / `struna.slug` / access tags on the ASP.NET span

**Still deferred (not Phase 8):** in-app OTLP **log** export — Bardie operators import Docker logs into Loki.

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

**Related:** [pre-publish-audit.md](pre-publish-audit.md) · [security-audit.md](security-audit.md) · [implementation-plan.md](implementation-plan.md) · [grpc-source-module.md](../interfaces/grpc-source-module.md) · [observability.md](../operations/observability.md)

**Read next:** [pre-publish-audit.md](pre-publish-audit.md) · [security-audit.md](security-audit.md)
