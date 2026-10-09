# Writing a GuideCapture scene

How to build a screenshot scene in `tools/Yaat.GuideCapture` that renders the same PNG on every run and is cheap to iterate on. The tool's file map is in [`architecture.md`](./architecture.md#yaatguidecapture--cli-tool-toolsyaatguidecapture); the `guide-capture` skill points here.

## What a run does

`dotnet run --project tools/Yaat.GuideCapture -- --scene <name> [--out <dir>] [--scale <n>]`, from the repo root, through `tools/gate.ps1` (`-Slot heavy`). It boots yaat-server in-process (`HAS_YAAT_SERVER`, the sibling checkout), opens the scene's window headless with real Skia pixels, and writes `<out>/<scene.Name>.png`.

- `--out` defaults to `docs/user-guide/img/`. A shot for the release feature showcase uses `--out docs/releases/img` and a scene named `whats-new-<topic>`, so it never overwrites a user-guide image.
- Output is deterministic: the clock is pinned to `FixedTimeProvider.CaptureInstant` (2026-01-15 18:30Z) for the server, the terminal and every wall-clock view, and the scenario seed is pinned (`Program.cs`, `CaptureRngSeed`). A scene that reads `DateTime.Now`, `Random.Shared` or real time breaks this.
- Each run pays the server boot, the room, the scenario load and whatever sim time the scene advances. A scenario-based scene that advances 480 s costs about 1.5 to 2.5 minutes before its own step runs, so every retry pays it again.

## The scene contract

`Capture/Scene.cs`: `Name`, `CreateWindow`, then `BeforeWindowAsync` (process-wide state the window reads), `AfterShowAsync` (wait for connection, load, open things), `GetCaptureTarget` (capture a child window), `ExtraWindows` (closed for you), and `AfterCapture`, which puts back every preference or view setting the scene changed so no later scene inherits it. Register the scene in `Capture/SceneCatalog.cs`.

`Scenes/ScenarioSceneBase.cs` connects, creates a room, loads an ATCTrainer example scenario from `docs/atctrainer-scenario-examples/` (default `S1-OAK-1 Clearances Intro`, 18 aircraft parked at OAK), closes the load report, switches the tab, then calls `OnSceneReadyAsync`. `StandaloneWindowSceneBase` is for dialogs that need no room; `TimelineSceneBase` for playback.

## Placing traffic: spawn it, do not simulate it

Put exactly the aircraft the shot needs where it needs them with `ADD` through `SceneActions.SendCommandAsync`, then advance only the seconds the state needs (`RoomTicks.AdvancePausedAsync`). This is the same recipe the client driver uses ([`client-driver-mcp.md`](./client-driver-mcp.md), "Run yaat-server from source…"). The `ADD` forms are in `COMMANDS.md`, "Add Aircraft (ADD)":

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
- **The active-runways prompt** opens over the views on a scenario load in an RPO room when the server asks for it (`MainViewModel.ActiveRunways.cs`). Answer or close it before a shot that needs the views.
- Menu-bar menus, submenus and button flyouts: `SceneActions.OpenMenuAsync`, `OpenSubmenuAsync`, `OpenButtonFlyoutAsync`. A control's `ContextMenu` in a host window: `Scenes/TerminalRewindMenuScene.cs`.

## Checking the result

Open every PNG you write with an image viewer (an agent reads it with its file-read tool) and check it shows what the scene claims: the menu open, labels legible, nothing clipped, no prompt over the view. A capture that exits 0 but shows the wrong state is a failed scene.
