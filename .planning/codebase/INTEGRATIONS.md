# External Integrations

**Analysis Date:** 2026-09-01

## APIs & External Services

**VATSIM Identity/Auth:**
- VATSIM Connect (OAuth2 + PKCE) — `https://auth.vatsim.net/oauth/authorize`, `https://auth.vatsim.net/oauth/token`, `https://auth.vatsim.net/api/user`, `https://auth.vatsim.net/api/fsd-jwt` (found via grep in yaat-server `src/Yaat.Server/Auth/VatsimAuthService.cs`)
  - Server-side implementation: `src/Yaat.Server/Auth/VatsimAuthService.cs`, `src/Yaat.Server/Auth/AuthStateStore.cs`, `src/Yaat.Server/Auth/YaatTokenService.cs` (yaat-server repo)
  - Config keys: `Yaat:Vatsim:ClientId` / `ClientSecret` / `CallbackUrl` (`src/Yaat.Server/appsettings.json`); `CallbackUrl` is derived from `YAAT_DOMAIN` in `docker-compose.yml` and must match the registered OAuth redirect URI
  - Public/PKCE client model — client secret can be left blank per `docker-compose.yml` comments
  - See `docs/vatsim-auth.md` for full flow (the server is the identity authority — confirmed against `YaatOptions.cs`/`VatsimAuthService.cs`)

**VATUSA:**
- `https://api.vatusa.net` — mentor lookup (`isMentor` resolves without an API key; `Yaat:Vatusa:ApiKey` optional per `docker-compose.yml` comment)
  - `appsettings.json` logging category `System.Net.Http.HttpClient.vatusa` confirms a named `HttpClient` for this integration
  - Config: `Yaat:Vatusa:Enabled`, `Yaat:Vatusa:ApiKey`

**vNAS (VATSIM NAS) Data APIs:**
- Config API: `https://configuration.vnas.vatsim.net/` — facility hierarchy, positions, coordination channels (examples in `docs/vnas-artcc-config-examples/`)
- Data API: `https://data-api.vnas.vatsim.net/api/artccs/{id}`, `https://data-api.vnas.vatsim.net/api/training` — nav data, scenarios, ARTCC data
  - Client: `src/Yaat.Sim/Data/Vnas/VnasDataService.cs`, `src/Yaat.Sim/Data/Vnas/VnasConfig.cs`, `src/Yaat.Sim/Data/Vnas/NavDataPathResolver.cs`
  - Airport ground map: `https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map` — fetched by `src/Yaat.Sim/Data/Airport/AirportLayoutDownloader.cs`, cached at `%LOCALAPPDATA%/yaat/cache/airports/`
  - Video maps: `https://data-api.vnas.vatsim.net/Files/VideoMaps`
  - Endpoint sends no freshness/cache headers — see memory `vnas_endpoint_cache_freshness.md` (client-side caching strategy is self-imposed, not server-driven)

**FAA SWIM (System Wide Information Management):**
- FAA SCDS delivers SWIM data over Solace SMF (Solace Message Format) — "the only client API the broker accepts besides JMS" per `src/Yaat.Server/Yaat.Server.csproj` comment
- Client library: `SolaceSystems.Solclient.Messaging` 10.30.0
- Two SWIM products configured independently: STDDS and SFDPS (`Swim__Stdds__*`, `Swim__Sfdps__*` env vars in `docker-compose.yml`) — each needs a broker username/password/queue name issued via the SWIFT portal; blank = product off
- TLS trust: DigiCert Global Root G2 PEM bundled at `src/Yaat.Server/Data/Certs/*.pem` (yaat-server repo)
- Raw message logging: `Swim__RawLog__Directory` (`/data/swim-raw`), capped by `Swim__RawLog__MaxTotalMegabytes` / `MaxAgeHours`
- Feature-gated off by default: `LiveTraffic__Enabled` (`false` unless explicitly opted in per deployment; stays off on the production YAAT1 droplet until the feed ships per compose comment)
- Feeds `src/Yaat.Sim/LiveTraffic/` (shadow aircraft: `AircraftLiveTraffic`, `LiveTrafficKinematics`) — see `docs/live-traffic.md`

## Data Storage

**Databases:**
- None found. No `DbContext`, EF Core, SQL Server, or Postgres references detected in yaat-server (`grep` for these patterns returned no matches in `src/Yaat.Server`).
- The transitively-pulled SQLite (`SQLitePCLRaw.lib.e_sqlite3`, via LM-Kit.NET on the client) is used internally by LM-Kit.NET's own storage, not as an application database — confirmed by the pin comment in `src/Yaat.Client/Yaat.Client.csproj`.

**File Storage:**
- Local filesystem only, no cloud object storage detected.
- Server-side persistent volumes (Docker): `/data/session-checkpoints` (`Yaat__SessionCheckpointPath`), `/data/facility-data` (`Yaat__FacilityDataPath` — controller-drawn ASDE-X/SAAB SAID geometry), `/data/logs` (`Yaat__LogPath`), `/data/swim-raw` (raw SWIM message log)
- Session persistence: `src/Yaat.Server/Simulation/Persistence/SessionPersistenceService.cs`, `SessionRestoreHostedService.cs` (yaat-server repo) — see `docs/session-persistence.md`
- Client cache: `%LOCALAPPDATA%/yaat/cache/airports/` (downloaded ground layouts), `%LOCALAPPDATA%/yaat/backends/cuda13/` (on-demand CUDA backend for LM-Kit, downloaded via `CudaBackendInstaller`)
- Client logs: `%LOCALAPPDATA%/yaat/yaat-client.log`; server logs: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (dev) or `/data/logs` (Docker)

**Caching:**
- None (no Redis/Memcached found). In-process caching only (e.g., vNAS data cached to local disk per above).

## Authentication & Identity

**Auth Provider:**
- VATSIM Connect (OAuth2) is the sole identity provider — the server is the identity authority (`docs/vatsim-auth.md`)
- Session tokens: YAAT-issued JWT (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.9), signed with `Yaat:Auth:JwtSigningKey` — `src/Yaat.Server/Auth/YaatTokenService.cs`
- `Yaat:Auth:RequireVatsimAuth` gate (default `true`) — can be disabled per deployment
- Client-side token cache encrypted at rest via DPAPI (`System.Security.Cryptography.ProtectedData`, Windows; no-op fallback on other platforms) — `src/Yaat.Client.Core/`

## Monitoring & Observability

**Error Tracking:**
- None (no Sentry/Bugsnag/Application Insights detected). Bug reports are captured via the in-app bug-bundle export mechanism (`docs/snapshots-and-replay.md`, `tools/bug_bundle.py`), not a third-party error tracker.

**Logs:**
- `Microsoft.Extensions.Logging.Abstractions`-based `SimLog` (Yaat.Sim, shared) and `ILogger` (server); client uses `AppLog` — see `docs/logging.md`
- Rolling log files on disk (client and server), no centralized log aggregation service found

## CI/CD & Deployment

**Hosting:**
- yaat-server: Docker container on a self-hosted DigitalOcean droplet ("YAAT1"), image pulled from GHCR (`ghcr.io/leftos/yaat-server:latest`) — deploy details in memory `droplet_deploys_pull_ci_image.md` (private ops)
- Discord bot: Cloudflare Workers (`tools/discord-bot/`, deployed via `wrangler deploy`)
- Client: distributed as a Velopack-installed desktop app with auto-update, not server-hosted

**CI Pipeline:**
- GitHub Actions (`X:/dev/yaat/.github/workflows/`): `ci.yml` (build/test), `release.yml` / `release-macos.yml` (Velopack packaging + notarized macOS build), `nightly-review.yml` + `nightly-review-alert.yml` (automated overnight code review bot), `nightly-taxi-grid.yml` (heavy per-spot taxi coverage sweep), `discord-docs.yml` / `discord-release.yml` (Discord announcements), `yaat-crc-config.yml`, `claude.yml` / `claude-code-review.yml` (Claude Code GitHub Actions integration, `anthropics/claude-code-action`)
- Runners: `ubuntu-latest`, `windows-latest`, `macos-latest`, `macos-15-intel` (matrix build for cross-platform release)
- yaat-server repo has its own `discord-scenario-validation.yml` (Sunday cron, validates all ARTCC scenarios)

## Environment Configuration

**Required env vars (server, Docker deployment — `docker-compose.yml`):**
- `ADMIN_PASSWORD`, `VATSIM_CLIENT_ID`, `VATSIM_CLIENT_SECRET`, `YAAT_DOMAIN`, `VATUSA_API_KEY`, `JWT_SIGNING_KEY`, `REQUIRE_VATSIM_AUTH`, `LIVE_TRAFFIC_ENABLED`, `SWIM_USERNAME`, `SWIM_STDDS_PASSWORD`, `SWIM_STDDS_QUEUE`, `SWIM_SFDPS_PASSWORD`, `SWIM_SFDPS_QUEUE`, `SWIM_RAW_LOG_MAX_MB`, `SWIM_RAW_LOG_MAX_HOURS`

**Secrets location:**
- Docker deployment: `.env` file (not read by this analysis; existence-only) feeding `docker-compose.yml` variable substitution
- GitHub Actions: repository secrets, e.g. `LMKIT_LICENSE_KEY` (baked into `Yaat.Client` at publish time via `-p:LmKitLicenseKey=...`, per `src/Yaat.Client/Yaat.Client.csproj` comment), `DISCORD_BOT_TOKEN`, `DISCORD_CI_WEBHOOK_URL`
- Discord bot: Cloudflare Worker secrets (`wrangler secret put ...`), not committed

## Webhooks & Callbacks

**Incoming:**
- `/github` on the Discord bot Worker — receives GitHub `issues` + `issue_comment` webhook events, syncs status to linked Discord threads (`docs/discord-integration.md`)
- VATSIM OAuth callback: `https://${YAAT_DOMAIN}/auth/vatsim/callback` (yaat-server)

**Outgoing:**
- GitHub API calls from the Discord bot (issue creation/comment sync) via a GitHub App installation
- Discord webhook: `DISCORD_CI_WEBHOOK_URL` posts Nightly Review CI outcomes to a Discord channel
- Discord bot dispatches `discord-scenario-validation.yml` (yaat-server) via GitHub Actions `workflow_dispatch`

---

*Integration audit: 2026-09-01*
