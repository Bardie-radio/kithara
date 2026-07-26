# Pre-publish audit (MVP GHCR)

**Date:** 2026-07-26  
**Scope:** End-to-end deploy workflow, external deps, security re-check, logging — before the next published GHCR release of the MVP quartet (`kithara`, `plume`, `magpie`, `bes`).  
**Mode:** Operators pull `profile/deploy/` Compose + HTTP-only bundled nginx; TLS terminates at an external edge (e.g. Traefik).

This page is the release gate checklist. Living security IDs stay in [security-audit.md](security-audit.md); non-security footguns in [known-issues.md](known-issues.md).

---

## Verdict

**Do not treat** `IMAGE_TAG=latest` **as a clean publish until Plume is rebuilt and pushed** with the Vite `wwwroot/dist` pipeline (Dockerfile `npm run build` + `IncludeViteDist`). Other MVP images look Alpine/ffmpeg-correct for META-OPS-002; Compose + nginx path map is ready. Logging quieting + AUTH-INVITE banner landed in-tree this pass — **require republish** of all four apps to pick them up.


| Gate                                    | Status                                                                                           |
| --------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Reference Compose + nginx               | Ready ([org](https://github.com/Bardie-radio/.github/tree/main/profile/deploy) `profile/deploy`) |
| Alpine 3.22 + FFmpeg.AutoGen sonames    | Ready in Dockerfiles (code)                                                                      |
| Plume static assets in GHCR             | **Blocker** — fix in source; **publish required**                                                |
| Join secrets / Postgres / PublicBaseUrl | Ready (operator must edit `.env`)                                                                |
| AUTH-INVITE first boot                  | Ready (OTP in Kithara logs)                                                                      |
| Security P0 product remediations        | Closed (residuals ops/backlog)                                                                   |
| Logging Production defaults             | Fixed in source this pass — **publish required**                                                 |


---



## 1) Full deployment workflow



### Publish path


| Repo                           | Workflow                        | Trigger                  | Tag source                                                 | Notes                                                                                                    |
| ------------------------------ | ------------------------------- | ------------------------ | ---------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| kithara / plume / magpie / bes | `.github/workflows/publish.yml` | `workflow_dispatch` only | `<Version>` in `Directory.Build.props` → SemVer + `latest` | Manual; no auto-publish on merge                                                                         |
| Same                           | `version-check.yml`             | CI                       | Docs alignment                                             | See org [version-check](https://github.com/Bardie-radio/.github/blob/main/profile/docs/version-check.md) |


All four currently declare `<Version>0.1.0</Version>`. Publishing overwrites `0.1.0` and `latest` — coordinate lockstep tags if operators pin SemVer.

### Compose / edge


| Artifact                             | Role                                                          | Evidence                                                            |
| ------------------------------------ | ------------------------------------------------------------- | ------------------------------------------------------------------- |
| `.github/profile/deploy/compose.yml` | Bundled nginx `:80` + Postgres + quartet                      | GHCR images; gRPC not published                                     |
| `compose.external-edge.yml`          | Apps only                                                     | Operator supplies Traefik/nginx                                     |
| `edge/nginx.conf`                    | `/api` `/stream` → kithara; `/` `/control` `/player` → plume  | `X-Forwarded-*` set; HTTP listen only                               |
| `.env.example`                       | `IMAGE_TAG`, `PUBLIC_BASE_URL`, `JOIN_SECRET_*`, `POSTGRES_*` | Demo placeholders — rotate                                          |
| `local/deploy-test/`                 | Local mirror                                                  | **Plume** `build:` **from** `../../plume` until GHCR Vite fix lands |




### Plume Vite publish pipeline (blocker)


| Step                                                        | In source?               | On GHCR `latest`?      |
| ----------------------------------------------------------- | ------------------------ | ---------------------- |
| Dockerfile: Node + `npm ci && npm run build` before publish | Yes (`plume/Dockerfile`) | **No** until republish |
| `IncludeViteDist` / `NpmBuildSkipped`                       | Yes (`Plume.csproj`)     | Needs publish          |
| `UseStaticFiles` + no app-level `UseHsts`                   | Yes (`Program.cs`)       | Needs publish          |


Symptom without dist: empty/`application/octet-stream` responses + `X-Content-Type-Options: nosniff` → browser blocks CSS/JS.

### Healthchecks / first boot

- Compose healthchecks: busybox `wget` → Kithara `/health/live`, modules `/healthz` (Alpine — no curl).
- AUTH-INVITE: empty durable DB → `InviteBootstrapHostedService` logs OTP once (WARNING banner after this pass). Claim on Plume `/claim` → Bes bind.
- Volumes: `bardie-kithara-data` (mTLS CA + blobs), shared `bardie-audio`, Plume `dp-keys`.



### Deploy findings


| ID                   | Sev         | Summary                                                                               | Owner           | Status                                       |
| -------------------- | ----------- | ------------------------------------------------------------------------------------- | --------------- | -------------------------------------------- |
| **DEPLOY-PLUME-001** | **Blocker** | Published Plume image missing Vite `wwwroot/dist`                                     | plume (publish) | **Open** until GHCR rebuild                  |
| **DEPLOY-TAG-001**   | Med         | All repos at `0.1.0`; republish overwrites same SemVer                                | ops / all four  | Ops note                                     |
| **DEPLOY-DRIFT-001** | Info        | `local/deploy-test` builds Plume from source; org deploy pulls GHCR                   | local           | Intentional until DEPLOY-PLUME-001 closed    |
| **DEPLOY-ENV-001**   | Med         | Org compose requires `POSTGRES_PASSWORD` / join secrets; local-test has demo defaults | ops             | By design — do not copy demo secrets to prod |


---



## 2) External integrations and dependencies


| Dependency                                     | Pin / surface                          | Risk / footgun                                                                                                    |
| ---------------------------------------------- | -------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| **NuGet** `Bardie.Logos.`* / `Bardie.Module.*` | `0.1.0` on nuget.org                   | Supply-chain: publish Docker builds restore public packages; keep Logos/module SDKs in lockstep with images       |
| **GHCR** `ghcr.io/bardie-radio/<codename>`     | `workflow_dispatch`                    | Pull needs package read; `latest` moves                                                                           |
| **Postgres** `postgres:16-alpine`              | Compose                                | Password via `.env`; Kithara `DbProvider=postgres`                                                                |
| **FFmpeg.AutoGen** `6.1.0.1`                   | Kithara + Magpie                       | **Must** stay on Alpine **3.22** libav sonames (`.58`/`.60`). Floating `10.0-alpine` → 3.23+ ffmpeg 8 breaks load |
| **YoutubeExplode** `6.6.0`                     | Magpie                                 | YouTube HTML/API drift — runtime failure mode, not image-missing                                                  |
| **OTLP**                                       | Optional `OTEL_EXPORTER_OTLP_ENDPOINT` | Unset = no export (META-OTEL-001 Fixed)                                                                           |
| **Browser → edge → Plume/Kithara**             | nginx path map                         | `PUBLIC_BASE_URL` must match browser origin for `<audio>` / CSP `media-src`                                       |
| **Bes**                                        | Join + work gRPC                       | No public HTTP auth surface; JWTs via Kithara REST/BFF                                                            |




### Integration findings


| ID                 | Sev           | Summary                                                                                | Owner             | Status                                  |
| ------------------ | ------------- | -------------------------------------------------------------------------------------- | ----------------- | --------------------------------------- |
| **DEP-FFMPEG-001** | High if unpin | Alpine base must remain `aspnet:10.0-alpine3.22`                                       | kithara, magpie   | Mitigated in Dockerfiles (META-OPS-002) |
| **DEP-YT-001**     | Med           | Magpie YouTube extract can break independently of GHCR                                 | magpie            | Residual / ops                          |
| **DEP-NUGET-001**  | Low           | Central package versions `0.1.0` — confirm Logos packages published before image build | logos + consumers | Ops checklist                           |


---



## 3) Security re-check

Re-validated against current code (Jul 2026). Detail tables remain in [security-audit.md](security-audit.md).

### Existing findings (status)


| ID                                                | Prior           | Re-check              | Evidence                                                           |
| ------------------------------------------------- | --------------- | --------------------- | ------------------------------------------------------------------ |
| GUEST-REF-001 … AUTH-CLAIM-001 (P0/P1 closed set) | Fixed           | **Still Fixed**       | Harness / gates / tests present                                    |
| GUEST-XCHG-001                                    | Partial → Fixed | **Fixed**             | Rate limit + GUEST-XCHG-002 lockout                                |
| GUEST-XCHG-004                                    | —               | **Fixed** (this pass) | Guest-code compare → `ListenTokenComparer.FixedTimeEquals`         |
| GUEST-XCHG-003                                    | Open            | **Still open**        | Dual id/slug partitions                                            |
| MESH-REG-001…004                                  | Open (ops)      | **Still open**        | Auto join-secret takeover residual; Compose keeps `:5000` internal |
| PLUME-SEC-001/002                                 | Fixed           | **Still Fixed**       | CSP + antiforgery middleware                                       |
| PLUME-SESS-001                                    | Deferred        | **Still deferred**    | In-memory session store                                            |
| STREAM-TOK-001                                    | Fixed           | **Still Fixed**       | `ListenTokenComparer.FixedTimeEquals`                              |
| META-OPS-002 / META-OTEL-*                        | Fixed           | **Still Fixed**       | Dockerfiles + overlays                                             |
| META-DOC-001 / META-OPS-001                       | Deferred        | **Still deferred**    | Out of this publish gate                                           |




### HSTS / forwarded headers / gRPC / demos


| Check                       | Result                                                                                                                                                                                            |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Plume `UseHsts`             | **Removed** from app pipeline (comment: TLS edge owns HSTS)                                                                                                                                       |
| Kithara/modules HSTS        | Not enabled on HTTP containers                                                                                                                                                                    |
| `UseForwardedHeaders`       | **Fixed** — Kithara + Plume honor `X-Forwarded-For` / `X-Forwarded-Proto` (**AUTH-FWD-001** / **PLUME-FWD-001**); bundled nginx preserves upstream proto                                          |
| gRPC `:5000` on public edge | **Not published** in reference nginx                                                                                                                                                              |
| Demo secrets in templates   | `.env.example` placeholders OK; **Kithara** `appsettings.json` **no longer embeds demo** `BARDIE_JOIN_SECRETS` — Development-only demos; Production requires Compose/env (**META-CFG-001 Fixed**) |




### New / confirmed findings this audit


| ID                   | Sev     | Summary                                                                                                                                                                                         | Owner    | Status                                                                                                              |
| -------------------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------- | ------------------------------------------------------------------------------------------------------------------- |
| **DEPLOY-PLUME-001** | Blocker | GHCR Plume without Vite dist (MIME/nosniff empty assets)                                                                                                                                        | plume    | Open until publish                                                                                                  |
| **AUTH-FWD-001**     | P2      | Guest/invite rate limits + lockout use `Connection.RemoteIpAddress` without forwarded-headers → all clients share the edge container IP behind nginx                                            | kithara  | **Fixed** — `UseForwardedHeaders` (publish required)                                                                |
| **PLUME-FWD-001**    | P2      | Session `Secure = Request.IsHttps` without forwarded proto; behind Traefik→HTTP nginx, app always sees HTTP (works for HTTP demo; Secure cookies wrong if operators expect HTTPS-aware cookies) | plume    | **Fixed** — `UseForwardedHeaders` + nginx proto pass-through (publish required)                                     |
| **META-CFG-001**     | P3      | Published Kithara image embeds demo join secrets in `appsettings.json`; safe only if Compose/env always overrides                                                                               | kithara  | **Fixed** — removed from base `appsettings.json`; Development-only demos; Compose `.env` still supplies deploy-test |
| **META-DEPLOY-001**  | P2      | Local `compose.plume.yml` / phase sketches publish `:5000` + demo join secrets                                                                                                                  | local    | **Open** (dev-only)                                                                                                 |
| **META-LOG-001**     | P3      | EF SQL + framework noise drowned AUTH-INVITE OTP                                                                                                                                                | all four | **Fixed** this pass (source)                                                                                        |
| **GUEST-XCHG-004**   | P3      | Guest-code ordinal compare                                                                                                                                                                      | kithara  | **Fixed** this pass                                                                                                 |


---



## 4) Logging



### Before

- Default `Information` + only `Microsoft.AspNetCore: Warning` → **EF Core SQL** (`Microsoft.EntityFrameworkCore.Database.Command`) and other `Microsoft.`* / `System.Net.Http.HttpClient` categories stayed noisy.
- AUTH-INVITE OTP was a single-line `LogWarning` — easy to miss in compose interleaved logs.
- Module start lines were one-liners.



### Implemented this pass (source; needs publish)


| Change                                                   | Repos / files                               |
| -------------------------------------------------------- | ------------------------------------------- |
| Quieter Logging defaults + `appsettings.Production.json` | kithara, plume, magpie, bes                 |
| Dev: EF SQL allowed at Information; `DetailedErrors`     | kithara `appsettings.Development.json`      |
| AUTH-INVITE multi-line WARNING banner                    | `InviteBootstrapHostedService.cs`           |
| Startup banners                                          | Kithara / Plume / Magpie / Bes `Program.cs` |
| Plume Dockerfile copies `appsettings.Production.json`    | `plume/Dockerfile`                          |


**Not done (intentional):** nginx access_log left default (browser traffic only; healthchecks hit containers directly). Error/Warn app logs kept.

---



## Publish blockers checklist

Use before declaring the GHCR release good:

- [ ] **plume:** merge Vite/Dockerfile fixes if not on default branch; run **Publish image** workflow; confirm image contains `wwwroot/dist` (or smoke `http://…/` CSS Content-Type)
- [ ] **kithara / magpie / bes / plume:** publish builds that include logging quieting + banners (this audit’s code changes)
- [ ] Confirm all four tags match intended `IMAGE_TAG` (SemVer and/or `latest`)
- [ ] From `profile/deploy`: `cp .env.example .env`, rotate **all** secrets, set `PUBLIC_BASE_URL`
- [ ] `docker compose pull && docker compose up -d` (no local Plume build)
- [ ] `docker compose logs kithara` → AUTH-INVITE banner with Registration OTP (empty volume)
- [ ] Claim → bind → create Struna → `/player/{slug}` + `/stream/{slug}`
- [ ] Confirm `:5000` not reachable from public interface (org deploy — not local `compose.plume.yml`)
- [ ] Confirm GHCR packages are **Public** (or pulls fail for anonymous operators) — one-time per package
- [ ] Prefer one SemVer `IMAGE_TAG` for all four images (avoid mixed `:latest` SHAs)
- [ ] Optional: set `OTEL_EXPORTER_OTLP_ENDPOINT` if verifying traces
- [x] Optional (plume): CI asserts `wwwroot/dist` in the built image (prevent DEPLOY-PLUME-001 recurrence)
- [x] Switch `local/deploy-test` Plume back to GHCR image when DEPLOY-PLUME-001 closed (follow-up)

**Deferred (not publish blockers):** META-OPS-001 sine smoke, META-DOC-001 doc sweep, OTel continuous-play E2E, MESH-REG-* product pinning.

---



## Module-local notes


| Module                                                                                                            | Note                                                                                  |
| ----------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| [Plume ideas](https://github.com/Bardie-radio/plume/blob/main/docs/architecture/ideas.md)                         | Vite dist must be in published image; local deploy-test builds from source until then |
| [Magpie ideas](https://github.com/Bardie-radio/magpie/blob/main/docs/architecture/ideas.md)                       | Alpine 3.22 + libav pin; YouTube extract drift is runtime                             |
| [Bes ideas](https://github.com/Bardie-radio/bes/blob/main/docs/architecture/ideas.md)                             | Alpine final; no public auth HTTP                                                     |
| Org [05-deployment](https://github.com/Bardie-radio/.github/blob/main/profile/docs/architecture/05-deployment.md) | Operator narrative; link here for audit detail — do not duplicate Kithara internals   |


---



## Related

- [security-audit.md](security-audit.md) — finding IDs + trust model  
- [known-issues.md](known-issues.md) — NECK / META-OPS / META-OTEL  
- [implementation-plan.md](implementation-plan.md) — Phase 8 ownership  
- Org deploy: [profile/deploy](https://github.com/Bardie-radio/.github/tree/main/profile/deploy)

**Read next:** [security-audit.md](security-audit.md) · publish checklist above