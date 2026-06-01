# Logging & Observability: SimLog and AppLog

> Read this before adding a logger to a new class, before changing how a project initializes logging, or before debugging "my log lines don't
> show up." Every YAAT class logs through one of two static facades — `SimLog` (in `Yaat.Sim`, shared) or `AppLog` (in `Yaat.Client.Core`,
> desktop-only). The per-class incantation is mandatory and the threading model behind `SimLog` is non-obvious; this doc is the single source for both.

## Two facades, one sink

There are two static logger facades:

- **`SimLog`** (`src/Yaat.Sim/SimLog.cs`) — used by everything in `Yaat.Sim`, which both `Yaat.Client` and `yaat-server` share. It has no
  filesystem opinion of its own; whoever hosts `Yaat.Sim` (the client, the server, a tool, a test) wires an `ILoggerFactory` into it at startup.
- **`AppLog`** (`src/Yaat.Client.Core/Logging/AppLog.cs`, namespace `Yaat.Client.Logging`) — the desktop client's facade. It owns the file sink
  and, critically, calls `SimLog.Initialize(_factory)` (`AppLog.cs:28`) so that **Sim log lines land in the same `yaat-client.log` file**. There is
  one factory, two front doors.

A class picks its facade by project: code in `Yaat.Sim` uses `SimLog`; code in `Yaat.Client` uses `AppLog`. They are not interchangeable —
`SimLog.CreateLogger` returns a *deferred* logger (see below) while `AppLog.CreateLogger` returns a concrete one, and that difference matters for
static fields.

## The mandatory per-class pattern

Every Yaat.Sim class that logs declares, as a `private static readonly` field:

```csharp
private static readonly ILogger Log = SimLog.CreateLogger("ClassName");
```

This is **not optional** (CLAUDE.md, Error Handling). Real examples: `FlightPhysics.cs:10`, `GroundConflictDetector.cs:60`,
`AircraftProfileDatabase.cs:12`. Two details that trip people up:

- **The category is a string literal, not `typeof(T).Name`.** Most loggers live in `static` classes (`FlightPhysics`, `GroundConflictDetector`)
  where there is no instance type `T` to reflect on, so the category is written out by hand: `SimLog.CreateLogger("FlightPhysics")`. The category
  string is what the test-capture filter and `LayoutInspector`'s `--debug-*` flags match against, so keep it equal to the class name.
- **`SimLog.CreateLogger<T>()` exists** (it returns a logger whose category is `typeof(T).Name`, `SimLog.cs:37`) but the string overload is the
  convention for the static-class case so the category is visible at the call site.

`AppLog` follows the same shape in `Yaat.Client`: `private static readonly ILogger Log = AppLog.CreateLogger<MyWindow>();`
(`ArrivalGeneratorsEditorWindow.axaml.cs:12`) or the string overload (`FlightPlanEditorManager.cs:12`). See the footguns section for why static
`AppLog` fields are safe in the client but would not be in Sim.

## SimLog's deferred-resolution model

The subtle part of `SimLog` is that those `static readonly Log` fields are constructed at **class-load time** — which, for many classes, happens
*before* any `Initialize` call has run (a test references `FlightPhysics`, triggering its static ctor, before the test wires up its factory). A
naive facade would capture whatever factory existed at that moment (none) and log to nothing forever.

`SimLog.CreateLogger` dodges this by returning a `DeferredLogger` (`SimLog.cs:49`) that holds only the **category string**, not a resolved logger.
Every `Log(...)`, `IsEnabled(...)`, and `BeginScope(...)` call re-resolves the current factory at call time via `Resolve()` (`SimLog.cs:51`):

```csharp
var factory = _scopedFactory.Value ?? _staticFactory ?? NullLoggerFactory.Instance;
return factory.CreateLogger(category);
```

So the resolution order is **AsyncLocal scoped → process-wide static → `NullLoggerFactory`**. A static field created at class-load picks up the real
factory as soon as one is installed, because it never cached anything.

## The AsyncLocal-vs-static split

`SimLog` keeps **two** factory slots (`SimLog.cs:16-17`):

```csharp
private static ILoggerFactory? _staticFactory;
private static readonly AsyncLocal<ILoggerFactory?> _scopedFactory = new();
```

The two `Initialize` entry points set them differently:

| Method | Sets `_staticFactory` | Sets `_scopedFactory` (AsyncLocal) | Used by |
|---|---|---|---|
| `Initialize(factory)` (`SimLog.cs:19`) | yes | yes | Production startup (server, client, tools) |
| `InitializeForTest(factory)` (`SimLog.cs:32`) | **no** | yes | Tests only (via `SimLogBuilder`) |

The reason for the split is spelled out in the source comments (`SimLog.cs:8-15`, `25-31`):

- **Tests run in parallel** (xUnit). Each test wires its own `ITestOutputHelper`. If a test set the process-wide static factory, it would leak its
  output helper into unrelated parallel tests — and when that helper is disposed at the test's end, the others get NREs writing to it.
  `InitializeForTest` writes **only** the AsyncLocal slot, which flows with the test's `ExecutionContext` and stays private to that test.
- **The server** dispatches work onto thread-pool workers whose `ExecutionContext` may not carry the AsyncLocal value set during host startup. The
  process-wide static is the unconditional fallback that every thread sees regardless of context flow. That's why production `Initialize` sets
  *both*: AsyncLocal for whoever inherits the startup context, static for everyone else.

## Production init sites

Each host installs a factory exactly once at startup:

- **Server** — `YaatHost.cs:127` (yaat-server): `SimLog.Initialize(app.Services.GetRequiredService<ILoggerFactory>())`. It hands the ASP.NET DI
  `ILoggerFactory` to `SimLog` after `builder.Build()`, so Sim logs flow through the same provider stack as server logs.
- **Desktop client** — `Program.cs:25-26` (yaat): `YaatPaths.Initialize("yaat")` then `AppLog.Initialize("yaat-client.log")`. `AppLog.Initialize`
  builds a `FileLoggerProvider`, wraps it in a `LoggerFactory` at `LogLevel.Debug`, and forwards it to `SimLog.Initialize` (`AppLog.cs:17-29`).
- **LayoutInspector** — `Bootstrap.cs:94`: builds a `LoggerFactory` (default `LogLevel.Warning`, bumped per category by `--debug-fillets` /
  `--debug-exits`, `Bootstrap.cs:81-92`) and calls `SimLog.Initialize`.
- **WASM strips tool** — `tools/Yaat.VStrips.Web/Program.cs:28-33`: inlines a `ConsoleLineLoggerProvider` factory and calls `SimLog.Initialize`
  directly. (`AppLog.InitializeForBrowser` — `AppLog.cs:39` — exists for this purpose but is unused; the WASM client no longer references
  `Yaat.Client.Core`, so the tool builds the provider itself.)
- **TDLS web tool** — `tools/Yaat.VTdls.Web/Program.cs:31`.

`AppLog.Initialize` is called **once** (from `Program.cs`); there is no second production call site.

## The sinks

Two `ILoggerProvider` implementations exist, both in `Yaat.Client.Core` / `Yaat.Client.Strips`:

### `FileLoggerProvider` / `FileLogger` (desktop)

`src/Yaat.Client.Core/Logging/FileLoggerProvider.cs`:

- Opens the log with `FileMode.Create` (`FileLoggerProvider.cs:19`) — **truncates the previous session's log on every launch**. No rolling, no
  retention.
- `FileShare.ReadWrite | FileShare.Delete` so the file is readable (and deletable) while the app holds it open — you can tail it live.
- `StreamWriter { AutoFlush = true }` (`FileLoggerProvider.cs:20`) and a process-wide `lock (WriteLock)` around each write (`FileLoggerProvider.cs:71`) so
  concurrent log calls don't interleave.
- Line format is ``$"{HH:mm:ss.fff} [{level}] {category}: {message}"`` using **local time** (`DateTime.Now`, `FileLoggerProvider.cs:57-69`), with the level
  rendered as the four-char tags `trce`/`dbug`/`info`/`warn`/`fail`/`crit`. An attached `Exception` is written on its own following line.
- `FileLogger.IsEnabled` returns `logLevel != LogLevel.None` — the sink does **not** filter by level. Level/category filtering is the
  `LoggerFactory`'s job (the factory `AppLog` builds is set to `LogLevel.Debug` minimum), not the sink's.

### `ConsoleLineLoggerProvider` (WASM)

`src/Yaat.Client.Strips/Logging/ConsoleLineLoggerProvider.cs` — used by the browser/WASM strips client, where there is no filesystem. It writes one
line per entry to `Console.Out` (or `Console.Error` for `Warning` and above, `ConsoleLineLoggerProvider.cs:49-56`); Mono-WASM forwards those to the browser
DevTools console. Its format is **not** the same as the file sink despite the docstring's claim that it "mirrors" it: it is
`[HH:mm:ss.fff TAG category] message` using **UTC** (`DateTime.UtcNow`) and three-char tags `TRC`/`DBG`/`INF`/`WRN`/`ERR`/`CRT`
(`ConsoleLineLoggerProvider.cs:34-44`). Like the file sink, `IsEnabled` only rejects `LogLevel.None`.

## Where the logs land

Read these files first before speculating about a runtime error:

- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`. The path is `YaatPaths.Combine("yaat-client.log")` (`AppLog.cs:19`), and `YaatPaths.AppDataRoot`
  honors the `YAAT_APPDATA_DIR` env override (`YaatPaths.cs:23-36`) — so a test or sandbox can redirect it without touching your real profile.
- **Server**: `yaat-server.log` at `AppContext.BaseDirectory` (`YaatHost.cs:54`), which in a Debug build resolves to
  `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` relative to the yaat-server repo root. The server logs the resolved path at startup
  (`YaatHost.cs:130`).

## Seeing logs in tests

By default, `SimLog`'s fallback is `NullLoggerFactory` — so in a test that hasn't wired anything, **every Yaat.Sim log line is silently discarded.**
If you expected log output and see none, this is why; do not conclude logging is broken or reach for ad-hoc print statements.

Wire xUnit output through `SimLogBuilder` (`tests/Yaat.Sim.Tests/Helpers/SimLogBuilder.cs`):

```csharp
SimLogBuilder.CreateForTest(output)            // ITestOutputHelper
    .EnableCategory("FlightPhysics", LogLevel.Debug)
    .InitializeSimLog();                        // calls SimLog.InitializeForTest(Build())
```

Behavior to know:

- **Default level is `Warning`+** for categories you don't explicitly enable (`SimLogBuilder.cs:16`). Your class's `Debug`/`Info` lines stay invisible
  until you `EnableCategory(...)` it. Change the floor for unlisted categories with `WithDefaultLevel(...)` (`SimLogBuilder.cs:26`).
- **`EnableCategory` is a case-insensitive substring match** against the logger category (`SimLogBuilder.cs:62`), not an exact match — `"Physics"`
  catches `FlightPhysics`.
- `InitializeSimLog()` routes through `SimLog.InitializeForTest` (`SimLogBuilder.cs:80`), so it sets only the AsyncLocal scoped factory and never the
  process-wide static — this is what keeps parallel tests isolated.
- The builder depends on `MartinCostello.Logging.XUnit` (`builder.AddXUnit(output)`, `SimLogBuilder.cs:53`).

Run with `dotnet test --logger "console;verbosity=detailed" 2>&1 | tee .tmp/test.log` to actually surface the captured output on the console.

Some test classes wire a factory directly instead of using the builder (e.g. `AtFixTriggerDuringPhasesTests.cs:39-40` builds an `AddXUnit` factory at
`LogLevel.Debug` and calls `SimLog.InitializeForTest`). That is fine — the rule is only that tests use **`InitializeForTest`**, never `Initialize`.

## Footguns / Pitfalls

- **Sim log lines vanish in tests by default.** `SimLog` falls back to `NullLoggerFactory`, so without a `SimLogBuilder.InitializeSimLog()` call every
  `Yaat.Sim` log line is dropped. Wire the builder before assuming logging is broken.
- **`SimLogBuilder`'s default is `Warning`+.** `Debug`/`Info` from your class is silent until you `EnableCategory("YourClass", LogLevel.Debug)`
  (substring, case-insensitive). Forgetting this looks identical to "logging is off."
- **Never call `SimLog.Initialize` from a test — use `InitializeForTest`.** `Initialize` sets the *process-wide static* factory, leaking the test's
  `ITestOutputHelper` into unrelated parallel tests; when that helper is disposed at the originating test's end, the others throw. Always go through
  `SimLogBuilder` (which calls `InitializeForTest`).
- **Static-singleton races compound logging confusion.** Tests reading `TestVnasData`-populated singletons can race when run alongside profile-loading
  classes; symptoms can look like flaky log/value mismatches. (A dedicated test-harness reference doc is planned but not yet written; for now see
  CLAUDE.md's "Static singleton races" rule for the `TestVnasData.EnsureInitialized()` fix.)
- **`AppLog.CreateLogger` does NOT defer.** It returns a concrete logger resolved against `_factory` at call time (`AppLog.cs:49-67`); if `_factory`
  is still null it returns a `NullLogger` *permanently*. A `static readonly ILogger Log = AppLog.CreateLogger<T>()` field captured before
  `AppLog.Initialize` runs would log to nothing forever. This is safe in `Yaat.Client` only because those types (Avalonia windows, services) are not
  class-loaded until after `Program.cs` has run `AppLog.Initialize`. Unlike `SimLog`, there is no `DeferredLogger` safety net here.
- **`FileMode.Create` wipes the prior log every launch** (`FileLoggerProvider.cs:19`). There is no rolling or retention — copy `yaat-client.log`
  before relaunching if you need the previous session's output.
- **AsyncLocal resolution depends on `ExecutionContext` flow.** Code that breaks the context (a raw `new Thread`, certain `Task.Run` continuations on
  the server) may fall through to the static factory instead of a scoped one. Usually harmless in production (the static is set), but in a test it can
  mean your scoped `InitializeForTest` factory is bypassed and the line goes to `NullLoggerFactory` instead.
- **The WASM console format is not the file format.** Despite the `ConsoleLineLoggerProvider` docstring, the DevTools-console output uses UTC and
  three-char tags, while the file uses local time and four-char tags. Don't assume timestamps from the two sinks are comparable.
