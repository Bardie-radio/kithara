# ADR 008: OpenTelemetry Observability

**Status:** Accepted

## Context

Bardie is modular — when something breaks, you need the full path across Kithara, Plume, and every module, not three disconnected log files. The reference SRE stack is Grafana-forward (Tempo, Loki, Prometheus), but the contract stays backend-agnostic.

## Decision

**Everything is under coverage.** Every component exports OTLP traces, metrics, and logs:

- Kithara (API, Neck, Stream Server, auth harness)
- Plume, Beak, Cauda (clients)
- Source modules (Magpie, Starling, Catbird, …)
- Auth adapter modules (Bes, Argus, Hecate, …)

W3C `traceparent` propagated across HTTP and gRPC. Service names:

| Component | `service.name` |
|-----------|----------------|
| Kithara | `bardie.kithara` |
| Plume / Beak / Cauda | `bardie.plume`, `bardie.beak`, `bardie.cauda` |
| Sources | `bardie.source.<slug>` (e.g. `bardie.source.magpie`) |
| Auth | `bardie.auth.<slug>` (e.g. `bardie.auth.bes`) |

## Consequences

- OTel is part of the **module contract**, not optional.
- Auth and client modules appear in the same trace graphs as Kithara.
- Span attributes: `struna.id`, `struna.slug`, `source.instance.id`, `auth.adapter.id`.
- Never log tokens or passwords.

## Repos needing follow-up

| Service name | Follow up in |
|--------------|----------------|
| `bardie.plume` | **plume** |
| `bardie.beak` / `bardie.cauda` | **beak**, **cauda** |
| `bardie.source.*` | **magpie**, **starling**, **catbird** |
| `bardie.auth.*` | **bes**, **argus**, **hecate** |
| Collector wiring | Org Compose / [05-deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md) |

## Alternatives considered

- **Logging only** — rejected; insufficient for modular debugging.
- **Kithara-only tracing** — rejected; blind to modules and Plume.

**Related:** [operations/observability.md](../operations/observability.md)

**Read next:** [009-struna-access-and-routing.md](009-struna-access-and-routing.md)
