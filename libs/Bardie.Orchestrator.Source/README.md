# Bardie.Orchestrator.Source

Source module **orchestrator** library for Bardie hosts (Kithara today; external hosts later).

**Package id:** `Bardie.Orchestrator.Source` · **Version:** `0.1.0` · **TFM:** `net10.0`

Depends on [`Bardie.Contracts`](../Bardie.Contracts/README.md) + [`Bardie.Module.Channel`](../Bardie.Module.Channel/README.md) + [`Bardie.Module.Source`](../Bardie.Module.Source/README.md) (shared capability vocabulary).

## What it owns

- Source module catalog
- Host port `IBlobStorage` for shared library blob access
- Per-call dials: `SearchAsync`, `StartTrackAsync`, `StopTrackAsync`, `PauseTrackAsync`, `ResumeTrackAsync`, `PrefetchTrackAsync`, `TrackStatusAsync`
- Capability gates via `Bardie.Module.Source.WellKnownSourceCapabilities`

## Consume

```csharp
services.AddSourceModuleOrchestrator(registerModuleChannel: false);
// when Auth orch already called AddModuleChannel — avoid double-register
// host must register IBlobStorage
```

Pack: `dotnet pack libs/Bardie.Orchestrator.Source/Bardie.Orchestrator.Source.csproj -c Release`
