# Writing a GuideCapture scene

How to build a screenshot scene in `tools/Yaat.GuideCapture` that renders the same PNG on every run and is cheap to iterate on. The tool's file map is in [`architecture.md`](./architecture.md#yaatguidecapture--cli-tool-toolsyaatguidecapture); the `guide-capture` skill points here.

## What a run does

`dotnet run --project tools/Yaat.GuideCapture -- --scene <name> [--out <dir>] [--scale <n>]`, from the repo root, through `tools/gate.ps1` (`-Slot heavy`). It boots yaat-server in-process (`HAS_YAAT_SERVER`, the sibling checkout), opens the scene's window headless with real Skia pixels, and writes `<out>/<scene.Name>.png`.

- `--out` defaults to the scene's own folder (`Scene.DefaultOutDir`): `docs/user-guide/img/`, or `docs/releases/img/` for a showcase scene, one named `whats-new-<topic>`. Without `--out` a PNG goes under the repo root (`CaptureContext.RepoRoot`, found from `yaat.slnx`) whatever the current directory; an explicit `--out` is relative to the current directory. A showcase scene runs only when named with `--scene`; a plain run skips every `whats-new-*` scene (`SceneCatalog.Select`).
- Re-running a guide scene with no `--out` rewrites its tracked PNG byte-identically, so it is a safe check that a change did not move the picture.
- A fresh worktree's first capture downloads CIFP, which adds to that run's time.
- Output is deterministic: the clock is pinned to `FixedTimeProvider.CaptureInstant` (2026-01-15 18:30Z) for the server, the terminal and every wall-clock view, and the scenario seed is pinned (`Program.cs`, `CaptureRngSeed`). A scene that reads `DateTime.Now`, `Random.Shared` or real time breaks this.
- Each run pays the server boot, the room, the scenario load and whatever sim time the scene advances. A scenario-based scene that advances 480 s costs about 1.5 to 2.5 minutes before its own step runs, so every retry pays it again.

## The scene contract

`Capture/Scene.cs`: `Name`, `CreateWindow`, then `BeforeWindowAsync` (process-wide state the window reads), `AfterShowAsync` (wait for connection, load, open things), `GetCaptureTarget` (capture a child window), `ExtraWindows` (closed for you), and `AfterCapture`, which puts back every preference or view setting the scene changed so no later scene inherits it. Register the scene in `Capture/SceneCatalog.cs`.

`Scenes/ScenarioSceneBase.cs` connects, creates a room, loads an ATCTrainer example scenario from `docs/atctrainer-scenario-examples/` (default `S1-OAK-1 Clearances Intro`, 18 aircraft parked at OAK), closes the load report, switches the tab, then calls `OnSceneReadyAsync`. `StandaloneWindowSceneBase` is for dialogs that need no room; `TimelineSceneBase` for playback.

A window that sets no `Background` captures as pure black, so a scene's window needs one (the Active Runways window sets `#1E1E1E`).

## Placing traffic: spawn it, do not simulate it

Put exactly the aircraft the shot needs where it needs them with `ADD` through `SceneActions.SpawnAsync(vm, "ADD …", timeout)`, which returns the new aircraft (`SendCommandAsync` sends any other command and returns its terminal reply). Then advance only what the state needs: `RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, done, new RoomTicks.Stage(what, step, max))` runs to a state, and `RoomTicks.AdvancePausedAsync` runs a fixed number of seconds. This is the same recipe the client driver uses ([`client-driver-mcp.md`](./client-driver-mcp.md), "Run yaat-server from source…"). The `ADD` forms are in `COMMANDS.md`, "Add Aircraft (ADD)":

| Need | Command |
|---|---|
| Parked at a stand or spot | `ADD V S P @H1`, `ADD I L J @F8 CRJ7 *SKW` (type and carrier named) |
| On final, n miles out | `ADD IFR L J 28R 8` |
| Lined up / departing | `ADD VFR S P 28R` / `ADD IFR S P 28R NIMI6.OAK.SAU` |
| Airborne at a bearing and distance from the airport | `ADD IFR H J -270 15 10000` |
| At a fix or FRD | `ADD IFR L J @SUNOL 8000` |

`ADD` needs an active scenario. Loading a full example scenario and advancing hundreds of seconds until traffic happens to be in the picture is slow and fragile: the radar example's picture after 480 s holds only two drawn aircraft. No empty-scenario fixture or base class is committed yet (YAAT-541 adds one); until then, spawn into the default scenario and choose a part of the airport its traffic does not crowd.

## Opening menus and popups

- **Do not simulate a pointer right-click on `MainWindow`.** Avalonia's `LightDismissOverlayLayer` takes every press even with no popup open, and the class is internal, so a scene cannot move it aside.
- **Call the view's own builder** and open the result at the target's screen point: `RadarView.BuildAircraftRightClickMenu` (`Views/Radar/RadarView.ContextMenus.cs`), `GroundView.BuildAircraftRightClickMenu` and `GroundView.BuildNodeContextMenu` (`Views/Ground/GroundView.axaml.cs`). These are `internal`; GuideCapture sees them.
- **The active-runways prompt** opens over the views on a scenario load in an RPO room when the server asks for it (`MainViewModel.ActiveRunways.cs`). `ScenarioSceneBase` answers it for every scene that loads a scenario, right after the load report closes and before `OnSceneReadyAsync`, with `SceneActions.AnswerActiveRunwaysPromptAsync` (confirm the server's pre-filled runways, else cancel; nothing when no prompt is open), so no shot shows it and no menu loses the pointer input to it.
- **A flyout on a menu's strip icon** opens with `QuickCommandStrip.OpenSubmenu` on the icon after `SceneActions.PointAt`, never `SceneActions.Click`: a pointer click on a strip icon closes the headless menu first, and `Click` is only for a control that opens its own popup. It is placed beside the menu, clear of its rows; `Scenes/WhatsNewExitsAheadScene.cs` shows the result and stages the shot around it.
- `SceneActions.OpenStripFlyoutAsync(window, menu, entryId, …)` does that for the strip icon of a `MenuIds` entry and returns the flyout's `MenuFlyoutPresenter` once its rows are laid out; a flyout that refills while open (Push back to…) is then waited on for its settled rows, as `Scenes/WhatsNewPushBackScene.cs` does.
- `SceneActions.PlaceInGroundView(window, vm, position, x, y)` moves the zoomed ground view so a point sits at fractions of its width and height, near the upper left to leave a menu and its flyouts room to the right.
- **A radar range/bearing shot needs two aircraft** to latch the line to; `Scenes/WhatsNewRblLabelScene.cs` picks the pair.
- Menu-bar menus, submenus and button flyouts: `SceneActions.OpenMenuAsync`, `OpenSubmenuAsync`, `OpenButtonFlyoutAsync`. A control's `ContextMenu` in a host window: `Scenes/TerminalRewindMenuScene.cs`.

## Checking the result

Open every PNG you write with an image viewer (an agent reads it with its file-read tool) and check it shows what the scene claims: the menu open, labels legible, nothing clipped, no prompt over the view. A capture that exits 0 but shows the wrong state is a failed scene.
