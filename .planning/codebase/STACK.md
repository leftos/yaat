# Technology Stack

**Analysis Date:** 2026-09-01

## Languages

**Primary:**
- C# (nullable, implicit usings enabled) — .NET 10, used across every `src/`, `tests/`, and `tools/` project in both `yaat` and `yaat-server`

**Secondary:**
- JavaScript (ESM) — Discord bot Cloudflare Worker, `X:/dev/yaat/tools/discord-bot/`
- Python — repo tooling under `tools/` and `docs/` (e.g. `tools/bug_bundle.py`, `tools/build-mva-data.py`, `tools/refresh-navdata.py`); linted/formatted with ruff per `pyproject.toml` (150-char lines, py313, complexity ≤8)
- PowerShell — `tools/test-all.ps1` cross-repo build/test driver

## Runtime

**Environment:**
- .NET 10 SDK (all `.csproj` files target `net10.0`; confirmed in `src/Yaat.Client/Yaat.Client.csproj`, `src/Yaat.Sim/Yaat.Sim.csproj`, and the yaat-server `Yaat.Server.csproj`)
- Solution format: `.slnx` (`yaat.slnx`) — newer XML-slim solution format, not `.sln`
- `global.json` (`X:/dev/yaat/global.json`) pins the test runner to Microsoft.Testing.Platform (`"test": {"runner": "Microsoft.Testing.Platform"}`), which changes `dotnet test` CLI syntax (options after `--`, e.g. `--filter-method`) — no VSTest-style `--filter`/`--logger` forms

**Package Manager:**
- NuGet (per-project `<PackageReference>`, no central `Directory.Packages.props` — confirmed empty/absent in repo root)
- `Directory.Build.props` sets the shared `<Version>0.12.24-beta</Version>` for all projects
- pnpm for the Discord bot Node workspace (`tools/discord-bot/package.json`; `pnpm.onlyBuiltDependencies` lists `esbuild`, `workerd`)

## Frameworks

**Core (Client):**
- Avalonia UI 12.1.0 — cross-platform desktop UI (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.Controls.ColorPicker`, `Avalonia.AvaloniaEdit`) — see `src/Yaat.Client/Yaat.Client.csproj`
- SkiaSharp — comes in transitively via `Avalonia.Skia`, not a direct package reference (per `CLAUDE.md`); used for radar/ground canvas rendering
- CommunityToolkit.Mvvm 8.4.0 — `[ObservableProperty]`/`[RelayCommand]` MVVM source generators, used in every client project
- Avalonia.Browser (WASM) — `tools/Yaat.VStrips.Web/`, `tools/Yaat.VTdls.Web/` browser front-ends; require the `wasm-tools` workload (`dotnet workload restore`)

**Core (Server):**
- ASP.NET Core (`Microsoft.NET.Sdk.Web`) — `src/Yaat.Server/Yaat.Server.csproj` (yaat-server repo)
- SignalR (server: `Microsoft.AspNetCore.SignalR`-hosted hubs; client: `Microsoft.AspNetCore.SignalR.Client` 10.0.3) — real-time comms between client and server, `/hubs/training`
- MessagePack 3.1.7 (server) — binary wire format used for CRC protocol DTOs

**Testing:**
- xunit.v3 3.2.2 with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.8.1 — `tests/Yaat.Sim.Tests/Yaat.Sim.Tests.csproj` and all other test projects
- `MartinCostello.Logging.XUnit.v3` 0.7.1 — routes `ILogger` output into xUnit test output
- Test runner invoked via Microsoft.Testing.Platform CLI (see `global.json`), not classic VSTest
- Node/Vitest 4.1.10 — Discord bot unit tests (`tools/discord-bot/package.json`)

**Build/Dev:**
- CSharpier — C# code formatter, run via `dotnet csharpier format .` in pre-commit (`prek.toml`)
- `dotnet format` (style + analyzers) — also pre-commit gated
- `prek` — Rust pre-commit hook runner (`prek.toml`), replaces Python `pre-commit`
- Google.Protobuf 3.29.5 + Grpc.Tools 2.71.0 — compiles `src/Yaat.Sim/Proto/nav_data.proto` (`GrpcServices="None"`, protobuf used purely for its serialization format, not gRPC transport)
- Velopack 1.2.0 — installer packaging and auto-update, used by both `Yaat.Client` and `Yaat.Client.Core`
- Wrangler 4.114.0 — Cloudflare Worker dev/deploy CLI for the Discord bot

## Key Dependencies

**Critical (Client — `src/Yaat.Client/Yaat.Client.csproj`):**
- LM-Kit.NET 2026.7.4 — sole inference engine for both LLM (`LocalLlmService`) and Whisper STT (`WhisperSttEngine`); Community Edition, requires attribution on the product page. `Yaat.Client` is the sole owner of this dependency per `CLAUDE.md` — no other `src/` project may reference it. Replaced LLamaSharp + Whisper.net in 2026-04.
- `org.k2fsa.sherpa.onnx` 1.12.40 — ONNX-based speech runtime component
- NAudio 2.2.1 — managed DSP for radio FX (playback itself uses PortAudioSharp2 for cross-platform support)
- PortAudioSharp2 1.0.6 — cross-platform audio playback (pilot voice, PTT ding)
- SharpHook 7.0.0 — cross-platform global keyboard hook for push-to-talk (passthrough mode, never suppresses keys)
- Microsoft.Diagnostics.Runtime (ClrMD) 4.0.732401 — used by `UiThreadWatchdog` to snapshot-attach and dump managed call stacks when the UI thread hard-freezes (GitHub #347); Windows-only at runtime
- SQLitePCLRaw.lib.e_sqlite3 3.50.3 — pinned native SQLite binary (patched for GHSA-2m69-gcr7-jv3q); pulled in transitively via LM-Kit.NET → Microsoft.Data.Sqlite 10.0.10 → SQLitePCLRaw.bundle_e_sqlite3 2.1.11
- DialogHost.Avalonia 0.12.3 — pinned to override a 0.12.1-nightly prerelease pulled in by MessageBox.Avalonia
- MessageBox.Avalonia 3.3.1.1, MetadataExtractor 2.9.2, SharpCompress 0.48.0

**Infrastructure (Sim — `src/Yaat.Sim/Yaat.Sim.csproj`, shared by every client and yaat-server):**
- Geo 1.2.0 — geodesy/geo-calculation primitives
- Microsoft.Extensions.Logging.Abstractions 10.0.3 — `SimLog` logging abstraction (falls back to `NullLoggerFactory` in tests unless explicitly wired)

**Server-only (`src/Yaat.Server/Yaat.Server.csproj`, yaat-server repo):**
- MessagePack 3.1.7 — CRC wire protocol serialization
- Microsoft.AspNetCore.Authentication.JwtBearer 10.0.9 — YAAT session-token auth
- SolaceSystems.Solclient.Messaging 10.30.0 — Solace SMF client for FAA SCDS SWIM feed (the only client API the broker accepts besides JMS)
- System.Security.Cryptography.ProtectedData 10.0.9 (client-side, `Yaat.Client.Core`) — DPAPI encryption for cached VATSIM session tokens at rest (Windows; no-op elsewhere)

## Configuration

**Environment:**
- yaat-server: `appsettings.json` / `appsettings.Development.json` / `appsettings.Local.json` (`src/Yaat.Server/`) provide the base config schema (`Yaat:AdminPassword`, `Yaat:Vatsim:ClientId/ClientSecret/CallbackUrl`, `Yaat:Vatusa:ApiKey`, `Yaat:Auth:JwtSigningKey`/`RequireVatsimAuth`, `LiveTraffic:Enabled`) — all secret values ship blank in the committed file
- Docker deployment env vars (double-underscore config binding, `Yaat__Section__Key`) are set in `docker-compose.yml`: `ADMIN_PASSWORD`, `VATSIM_CLIENT_ID`/`VATSIM_CLIENT_SECRET`, `VATUSA_API_KEY`, `JWT_SIGNING_KEY`, `REQUIRE_VATSIM_AUTH`, `LIVE_TRAFFIC_ENABLED`, `SWIM_USERNAME`/`SWIM_STDDS_PASSWORD`/`SWIM_STDDS_QUEUE`/`SWIM_SFDPS_PASSWORD`/`SWIM_SFDPS_QUEUE`, `SWIM_RAW_LOG_MAX_MB`/`SWIM_RAW_LOG_MAX_HOURS`
- Client per-user data routes through `YaatPaths.AppDataRoot`/`YaatPaths.Combine(...)` (in `Yaat.Sim`), never raw `Environment.GetFolderPath`, so it resolves consistently under `%LOCALAPPDATA%/yaat/`
- Discord bot secrets live in Cloudflare Worker vars/secrets (`tools/discord-bot/wrangler.toml`) — `DISCORD_BOT_TOKEN`, `DISCORD_ALLOWED_USER_ID`, `GITHUB_REPO`, `VALIDATION_REPO`, plus KV namespace `THREAD_ISSUES`
- `.env` file presence noted in `docs/discord-integration.md` (holds `DISCORD_BOT_TOKEN` for manual thread triage) — existence only, contents never read per this document's security policy

**Build:**
- `Directory.Build.props` — shared `<Version>`
- `yaat.slnx` — solution file listing all `src/`, `tests/`, `tools/` projects
- `docker-compose.yml` / `docker-compose.image.yml` — local/production container orchestration for yaat-server
- `src/Yaat.Server/Dockerfile` (yaat-server repo) — multi-stage build; `mcr.microsoft.com/dotnet/sdk:10.0` base, installs `wasm-tools` workload + `python-is-python3` (needed by `emcc`'s native-compile phase) to publish the two WASM front-ends before publishing the server; the server pulls yaat source via `extern/yaat/` (git submodule-style vendored checkout)
- `pyproject.toml` (repo root) — ruff config for Python tooling scripts

## Platform Requirements

**Development:**
- .NET 10 SDK
- `dotnet workload restore` once per clone (installs `wasm-tools`, required because `yaat.slnx` includes the Avalonia Browser/WASM projects)
- Close `Yaat.Client` before builds to avoid DLL lock warnings (Windows)
- yaat-server for full client functionality (SignalR hub connection); client's default auto-connect URL is `http://localhost:5000`, but `dotnet run --project src/Yaat.Server` (from `launchSettings.json`) binds `http://localhost:5130` in Development — point the client at 5130 when running from source

**Production:**
- yaat-server: Docker container built from `src/Yaat.Server/Dockerfile`, deployed to a DigitalOcean droplet (see `docs/plans/` / memory `yaat1_droplet_access.md` — private ops detail, root@ IP not restated here) pulling CI-built images from GHCR (`ghcr.io/leftos/yaat-server:latest`)
- yaat.Client: Velopack-packaged installer with auto-update (`docs/installer-release.md`); macOS build is separately notarized/signed (`release-macos.yml`, `docs/macos-code-signing.md`)
- Discord bot: Cloudflare Workers (serverless), deployed via `wrangler deploy`

---

*Stack analysis: 2026-09-01*
