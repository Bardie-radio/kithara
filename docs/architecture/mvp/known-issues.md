# Known issues (MVP)

Living index of **non-security** product / design footguns discovered after Phases 4–6. Security findings stay in [security-audit.md](security-audit.md). Soft residuals already listed there (e.g. `NECK-JOB-001`, `AUTH-ROT-002`) are not duplicated here unless they need a design note.

**Plume-local** scaffold / BFF / UI footguns (`PLUME-*`) live in [Plume known-issues](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/mvp/known-issues.md) — do not duplicate them here.

**Owner default:** Phase 8 polish unless noted.

IDs use `SURFACE-TOPIC-NNN` (see [security-audit ID scheme](security-audit.md#id-scheme)). Former `KI-*` aliases: `KI-01` → `NECK-PCM-001`, `KI-02` → `NECK-SWP-001`.

| ID | Sev | Summary | Tracking |
|----|-----|---------|----------|
| [NECK-PCM-001](#neck-pcm-001--canonical-pcm-format-constants-are-duplicated) | P2 | Session PCM rate/channels hard-coded in several places | [kithara#25](https://github.com/Bardie-radio/kithara/issues/25) |
| [NECK-SWP-001](#neck-swp-001--orphan-writer-sweep-runs-on-every-play-including-new-strunas) | P2 | `StopOrphanWriters` / `StopTracksForStruna` on the hot play path | [kithara#26](https://github.com/Bardie-radio/kithara/issues/26) |

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

**Related:** [security-audit.md](security-audit.md) · [implementation-plan.md](implementation-plan.md) · [grpc-source-module.md](../interfaces/grpc-source-module.md)

**Read next:** [security-audit.md](security-audit.md)
