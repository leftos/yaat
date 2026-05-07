# AGENTS.md — YAAT

> Full instructions: `CLAUDE.md`. Annotated file tree: `docs/architecture.md`.

## Build & Test

```bash
# Build (warnings=fatal — never pass -q)
dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log

# Test a single project
dotnet test tests/Yaat.Sim.Tests 2>&1 | tee .tmp/test.log

# Cross-repo: build & test both yaat AND yaat-server (requires sibling ../yaat-server)
pwsh tools/test-all.ps1
```

- **Never pass `-q`** to any `dotnet` command — it causes spurious errors.
- **Tee all output** to `.tmp/` files so results can be reviewed without re-running.
- `.tmp/` is gitignored; create it if missing.

## Format Pipeline (run in this exact order)

```bash
dotnet format style           # code style
dotnet format analyzers       # analyzer warnings
dotnet csharpier format .     # CSharpier (150-char line width)
dotnet build -p:TreatWarningsAsErrors=true
```

**Do NOT run bare `dotnet format`** — its whitespace rules conflict with CSharpier.

## Pre-commit

Uses **prek** (Rust), not standard pre-commit. Run manually: `prek run`. Hooks fire automatically on `git commit`. The final hook is `dotnet build -p:TreatWarningsAsErrors=true` — if it fails, the commit is blocked.

## Prerequisites

- .NET 10 SDK
- `dotnet workload install wasm-tools` (required before restore — `Yaat.VStrips.Web` targets `net10.0-browser`)
- `dotnet tool install -g csharpier`
- [yaat-server](https://github.com/leftos/yaat-server) cloned as `../yaat-server`

## Project Boundaries

| Project | What it is |
|---------|------------|
| `src/Yaat.Sim/` | Shared simulation lib (no UI deps). Referenced by both Yaat.Client and yaat-server. |
| `src/Yaat.Client/` | Avalonia desktop app (MVVM, SkiaSharp, SignalR). |
| `src/Yaat.Client.Core/` | Client logic shared by desktop and browser strips clients. |
| `src/Yaat.Client.Strips/` | Flight strips layer — pure Avalonia, no desktop/Win32 deps (WASM-safe). |
| `tools/Yaat.VStrips.Web/` | Browser strips client (WASM). |

## Testing Rules

- **Real data only**: Always use `TestVnasData.EnsureInitialized()` — loads real `NavData.dat` and `FAACIFP18.gz` from `tests/Yaat.Sim.Tests/TestData/`. Never create synthetic nav data stubs.
- **Test locations**: `tests/Yaat.Sim.Tests/` (sim logic), `tests/Yaat.Client.Tests/` (view models).
- **TestProjectSettings**: Explicit `TestVnasData.TestProjectDirectory` required when test assembly isn't in `tests/Yaat.Sim.Tests/`.
- **SimLog default**: Falls back to `NullLoggerFactory` in tests — all Sim log output is silently swallowed. Use `SimLogBuilder.CreateForTest(output).EnableCategory(...).InitializeSimLog()` to enable.
- **Timeouts**: Always run test suites with a 30s timeout — a suite that hasn't finished in 30s is stuck (infinite graph loop).
  ```bash
  run-with-timeout 30 dotnet test tests/Yaat.Sim.Tests 2>&1 | tee .tmp/test.log
  ```

## Key Conventions

- **SignalR callbacks run on background threads** — marshal to UI via `Dispatcher.UIThread.Post()`.
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]` from CommunityToolkit.Mvvm. `_camelCase` → `PascalCase`.
- **No optional parameters** — make them required so the compiler enforces wiring.
- **No swallowing exceptions** — log with `AppLog` (client) or `SimLog.CreateLogger("ClassName")` (Sim).
- **Logs**: Client logs at `%LOCALAPPDATA%/yaat/yaat-client.log`. Read them before speculating about errors.

## Adding Commands

Every new command type must be added to ALL of:
1. `CanonicalCommandType` enum
2. `CommandRegistry.All` definitions
3. `CommandScheme.Default()`

Tests enforce completeness. Missing any one breaks the build.

## Command Cheatsheet

CI checks that `docs/command-cheatsheet.html` is in sync with `docs/command-cheatsheet.json`. Regenerate: `node tools/build-cheatsheet.mjs`. Verify: `node tools/build-cheatsheet.mjs --check`.
