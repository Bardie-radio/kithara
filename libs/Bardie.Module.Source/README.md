# Bardie.Module.Source

Shared helpers for **source adapter modules** (Magpie, Starling, Catbird, …).

**Package id:** `Bardie.Module.Source` · **Version:** `0.1.0` · **TFM:** `net10.0`

Depends on [`Bardie.Contracts`](../Bardie.Contracts/README.md) + [`Bardie.Module.Channel`](../Bardie.Module.Channel/README.md). Does **not** depend on `Bardie.Module.Hosting`.

## Consume

```csharp
builder.Services.AddSourceModuleDefaults(builder.Configuration);
// Prefer source.searchFields in module.manifest.json (optional SourceModule:SearchFields fallback)
// SourceModule:MaxParallelJobs (default 4; ≤0 = unlimited)
```

| Type | Role |
|------|------|
| `SourceModuleBase` | `Health`; pause/prefetch gates via `WellKnownSourceCapabilities`; default Stop / Pause / Resume / TrackStatus via registry |
| `WellKnownSourceCapabilities` | Shared `search` / `play` / `pause` / `prefetch` strings (modules + Source Harness) |
| `ModuleManifestSourceBag` | Parse opaque `source.searchFields` from the manifest |
| `SourceSearchFieldsRegisterRequestCustomizer` | Attach `Source.search_fields` on Register (manifest → options → `title`) |
| `ITrackJobRegistry` / `TrackJobRegistry` | Job lifecycle, parallel limit, Stop / Pause / Resume |
| `IFifoAudioSink` / `FifoAudioSink` | PCM → session FIFO (optional pause predicate) |
| `ModuleBlobKeys` | `tunes/<slug>/…` key helper |
| `ModuleTuneCache` | Exists → Get / download → Put + EnsureTune |
| `SourceModuleRpc` / `TrackStatusStreaming` | RpcException map, broken-pipe, OTel tags, status poll |
| `IModuleBlobStorageClient` / `IModuleLibraryClient` | Dial Kithara BlobStorage + Library over participant mTLS |

FIFO / protocol smoke (`sine` proof track): separate package [`Bardie.Module.Source.Debug`](../Bardie.Module.Source.Debug/README.md) — reference from **Debug / test builds only**.

YoutubeExplode, libav transcoder, URI-only play, and module-specific Search/StartTrack stay in the module.

Pack: `dotnet pack libs/Bardie.Module.Source/Bardie.Module.Source.csproj -c Release`
