# gRPC Source Module Contract (v0.1 draft)

Source modules (**Magpie**, **Starling**, **Catbird**, …) host the **work** RPCs below. They **dial Kithara** only to join via [Module Registry](grpc-module-registry.md) (not via `Register` on this service).

**Status:** v0.1 draft — RPC set and dial rules are frozen; field names may still evolve slightly before NuGet publish. Checked-in proto: [`libs/Bardie.Contracts/Protos/source_module.proto`](../../../libs/Bardie.Contracts/Protos/source_module.proto) (package `Bardie.Contracts`).

```protobuf
syntax = "proto3";

package bardie.source.v1;

option csharp_namespace = "Bardie.Source.V1";

service SourceModule {
  rpc Health(HealthRequest) returns (HealthResponse);
  rpc Search(SearchRequest) returns (SearchResponse);
  rpc StartTrack(StartTrackRequest) returns (StartTrackResponse);
  rpc StopTrack(StopTrackRequest) returns (StopTrackResponse);
  rpc StopTracksForStruna(StopTracksForStrunaRequest) returns (StopTracksForStrunaResponse);
  rpc PrefetchTrack(PrefetchTrackRequest) returns (PrefetchTrackResponse);
  rpc PauseTrack(PauseTrackRequest) returns (PauseTrackResponse);
  rpc ResumeTrack(ResumeTrackRequest) returns (ResumeTrackResponse);
  rpc TrackStatus(TrackStatusRequest) returns (stream TrackStatusEvent);
}
```

`PauseTrack` / `ResumeTrack` are part of the **common** contract. Modules that cannot pause (e.g. Starling) omit the `pause` capability at Registry `Register` and return a clear error if called. Magpie **does** advertise and implement pause.

`PrefetchTrack` is likewise capability-gated (`prefetch`). Modules without cache warmup omit it; the host skips dialing when the capability is absent. Magpie advertises `prefetch`.

`StopTracksForStruna` cancels every in-flight job for a Struna. Host uses it for **recovery** only (Struna delete, pause/skip when Neck has no tracked job id) — not on the happy play path. `StartTrack` cancels sibling jobs for the same Struna before registering a new one, so replace/play does not need a host sweep.

## Capabilities (Registry)

Advertised at Module Registry `Register` — not inventing source-type labels:

| Capability | Meaning |
|------------|---------|
| `search` | Implements `Search` |
| `play` | Implements `StartTrack` / `StopTrack` / `StopTracksForStruna` |
| `pause` | Implements `PauseTrack` / `ResumeTrack` without tearing down the job |
| `prefetch` | Implements `PrefetchTrack` (warm blob cache; no FIFO write) |

## Key messages

```protobuf
// Quicksearch ≈ fields with only "title" set to the plain-text query.
message SearchRequest {
  map<string, string> fields = 1;
  int32 limit = 2;  // soft hint; 0 = module default
}

message SearchResult {
  string track_ref = 1;     // module-native ref for StartTrack
  string title = 2;
  string artist = 3;
  string external_id = 4;   // stable id for EnsureTune when known
  map<string, string> metadata = 5;
}

message StartTrackRequest {
  string struna_id = 1;
  string track_ref = 2;         // URL, tune id, YouTube link, stream URI, …
  string audio_endpoint = 3;    // Kithara-owned session path (FIFO / socket)
}

message StartTrackResponse {
  string track_job_id = 1;
}

message StopTracksForStrunaRequest {
  string struna_id = 1;
}

message StopTracksForStrunaResponse {
  bool ok = 1;
  int32 stopped_count = 2;
}

message PrefetchTrackRequest {
  string track_ref = 1;
}

message PrefetchTrackResponse {
  bool ok = 1;
  string external_id = 2;
  bool from_cache = 3;
}

enum TrackState {
  TRACK_STATE_UNSPECIFIED = 0;
  TRACK_STATE_RUNNING = 1;
  TRACK_STATE_PAUSED = 2;
  TRACK_STATE_ENDED = 3;
  TRACK_STATE_ERROR = 4;
  TRACK_STATE_PREPARING = 5;  // resolve/download/transcode; no FIFO PCM yet
}

message TrackStatusEvent {
  string track_job_id = 1;
  TrackState state = 2;
  string title = 3;
  string artist = 4;
  string error_message = 5;  // when state == ERROR
}
```

Invariants (frozen for v0.1):

1. **Kithara dials** the module advertise address for each work RPC (no long-lived command stream).
2. **`audio_endpoint`** is a Kithara-created path on the shared audio volume — modules write PCM; they do not own the listen side.
3. **Capabilities** gate which RPCs the host may call; omit `pause` → reject pause/resume; omit `prefetch` → host skips / module rejects `PrefetchTrack`.

## Audio requirements

- Write **canonical PCM** (`CanonicalPcm` in `Bardie.Module.Source`: s16le / 48 kHz / stereo) to the session endpoint Kithara created
- Do not own the listen side; Neck/FFmpeg reads it for the Struna life
- Endpoint lives on the **shared Compose volume**; path is passed in `StartTrack` — see [ADR 004](../adrs/004-source-instance-socket-audio-plane.md)
- **`PrefetchTrack`**: warm blob cache on enqueue (no FIFO write); `StartTrack` still owns session PCM
- Informal prewarm allowed in addition to `PrefetchTrack`

## Blob + library handshake

On cache miss, Magpie (and similar) **Put** via [BlobStorage](grpc-blob-storage.md), then **EnsureTune** via [Library](grpc-library.md). Drivers and EF stay on Kithara.

## Observability

- Propagate W3C `traceparent` on all RPCs
- Export OTLP with `service.name=bardie.source.<slug>` (e.g. `bardie.source.magpie`)
- Enforce parallel track-job limits internally

**Related:** [grpc-module-registry](grpc-module-registry.md) · [grpc-blob-storage](grpc-blob-storage.md) · [grpc-library](grpc-library.md) · [domains/source-modules.md](../domains/source-modules.md) · [ADR 003](../adrs/003-grpc-control-plane.md) · [ADR 004](../adrs/004-source-instance-socket-audio-plane.md) · [Bardie.Contracts](../../../libs/Bardie.Contracts/README.md)

**Read next:** [grpc-blob-storage.md](grpc-blob-storage.md)
