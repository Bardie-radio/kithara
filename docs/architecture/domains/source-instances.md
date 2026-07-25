# Source sessions and track jobs

```mermaid
sequenceDiagram
  participant API as Kithara_API
  participant Neck
  participant ModA as Source_A
  participant ModB as Source_B
  participant FF as FFmpeg

  API->>Neck: Struna create alive
  Neck->>FF: start once on session FIFO
  API->>Neck: play Magpie entry
  Neck->>ModA: StartTrack fifoPath trackRef
  ModA-->>FF: PCM into FIFO
  Note over Neck,ModA: Queue shift — kill decoder only
  Neck->>ModA: StopTrack
  API->>Neck: play other module entry
  Neck->>ModB: StartTrack same FIFO
  ModB-->>FF: PCM into same FIFO
  API->>Neck: owner Delete
  Note over Neck,ModB: Stop writer before tearing down FIFO
  Neck->>ModB: StopTrack
  Neck->>FF: kill
```

Audio for a Struna uses a **long-lived session** (FFmpeg + Kithara-owned FIFO) and short-lived **track jobs** on source modules. Different modules can take turns on one Struna. On teardown, Neck **stops the track job first**, then kills FFmpeg / closes the FIFO so the source never writes into a gone socket.

## Lifetimes

| Piece | Lifetime | On queue shift |
|-------|----------|----------------|
| FFmpeg / Struna Encoder | Entire Struna alive period | **Never kill** (restarting breaks many ICY players) |
| Session FIFO (Kithara-owned) | Same | **Reuse** |
| Track job (decode) | One queue item | **StopTrack → StartTrack** on possibly another module |

## Silence

While alive with no module writer, Neck’s **silence feeder** writes to the FIFO so FFmpeg never sees a fatal EOF (idle between tracks, pause, empty queue).

## Informal prewarm

Modules may buffer the next track before their turn. No MVP `PrepareTrack` RPC.

## Isolation

Track jobs stay **isolated per Struna** — same track on two Strunas means two jobs ([ADR 005](../adrs/005-isolated-instance-per-stream.md)). No sharing FIFOs across Strunas.

## Canonical PCM

MVP default internal format: **s16le / 48 kHz / stereo**, defined once as `CanonicalPcm` in `Bardie.Module.Source` (FIFO pacing, Neck silence/encode, Magpie transcoder, debug sine). Listener encode (MP3/ICY) is separate — see encode modes on Struna create.

## gRPC surface

See [interfaces/grpc-source-module.md](../interfaces/grpc-source-module.md): `StartTrack`, `StopTrack`, `Search`, `TrackStatus`, `Health`, `Register`.

## Replaces prototype approach

Prototype Neck concatenated playlist files in FFmpeg ([spike notes](../spike/prototype-neck-ffmpeg.md)). Target: modules write PCM into a Neck-owned FIFO while FFmpeg stays up for the Struna life.

**Related:** [ADR 004](../adrs/004-source-instance-socket-audio-plane.md) · [domains/streams.md](streams.md) · [interfaces/streaming-stack.md](../interfaces/streaming-stack.md)

**Read next:** [streams.md](streams.md)
