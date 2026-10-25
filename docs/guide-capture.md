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

Put exactly the aircraft the shot needs where it needs them with `ADD` through `SceneActions.SpawnAsync(vm, "ADD …", timeout)`, which returns the new aircraft (`SendCommandAsync` sends any other command and returns its terminal reply). Then advance only what the state needs: `RoomTicks.AdvanceUntilAsync(vm, ctx, callsign, done, new RoomTicks.Stage(what, step, max))` runs to a state, and `RoomTicks.AdvancePausedAsync` runs a fixed number of seconds. This is the same recipe the client driver uses (yaat-server `docs/client-driver-mcp.md`, "Run yaat-server from source…"). The `ADD` forms are in `COMMANDS.md`, "Add Aircraft (ADD)":

| Need | Command |
|---|---|
| Parked at a stand or spot | `ADD V S P @H1`, `ADD I L J @F8 CRJ7 *SKW` (type and carrier named) |
| On final, n miles out | `ADD IFR L J 28R 8` |
| Lined up / departing | `ADD VFR S P 28R` / `ADD IFR S P 28R NIMI6.OAK.SAU` |
| Airborne at a bearing and distance from the airport | `ADD IFR H J -270 15 10000` |
| At a fix or FRD | `ADD IFR L J @SUNOL 8000` |

`ADD` needs an active scenario. Loading a full example scenario and advancing hundreds of seconds until traffic happens to be in the picture is slow and fragile: the radar example's picture after 480 s holds only two drawn aircraft. No empty-scenario fixture or base class is committed yet (YAAT-541 adds one); until then, spawn into the default scenario and choose a part of the airport its traffic does not crowd.

## Staging an aircraft for a menu entry

A menu entry appears only in the state that offers it, so a scene's staging is decided from the code before any capture runs, never found by running captures. Work it out in this order, and put the result in the scene (and in any brief) as exact commands with the check that proves each state:

1. **Where the entry lives.** The quick-command strip is the situation's list in `src/Yaat.Client.Core/ContextMenus/QuickCommandDefaults.cs`, filtered by `QuickCommandResolver.Visibility` (`QuickCommandResolver.cs`, rules in `AircraftCommandApplicability.cs`). All Commands (`MenuCatalog.cs`) ignores visibility. An entry in no situation's list (Join airway, `MenuCatalog.cs:193`) opens only through `OpenAllCommandsEntryAsync`.
2. **Which situation the aircraft must be in.** `SituationClassifier.cs` decides it from the aircraft's phase, rules and flight plan. Print `aircraft.Situation` before opening the menu and throw unless it is the one the entry needs: a wrong state then fails in seconds with the reason, not after a capture.
3. **What the spawn files.** The airborne, fix and parking `ADD` forms file no departure, destination or route (`AircraftGenerator.cs:137-142,400,454`); only the lined-up, departure, on-final and STAR forms do. A command that needs a destination (`JFAC`, `CAPP`) needs an `FP` first: `FP <type> <alt> <dep> <route…> <dest>`, as `FP B739 040 KSJC DCT KOAK`.
4. **What must not be sent.** A command that changes the state the entry depends on hides it: an approach or descent clearance hides Cleared approach (`QuickCommandResolver.cs:45-47`), so never send `CAPP` to stage that shot.
5. **Where it sits on the scope.** At the radar's default range 40 the scope spans about ±20 NM across and only about ±7 NM up and down, about 40 px per NM, with OAK near the centre. An aircraft outside that box is off screen and its menu clamps into a corner. The menu opens to the right of and below the target, so a target left of centre keeps its symbol and data block clear of it. An `@fix` or bearing spawn heads straight at the airport.

Run captures one at a time (two GuideCapture builds collide on file locks), each once: a failed check or a wrong picture means the staging is wrong, which is fixed at step 1–5, not by trying placements.

### Recipes

Each was run and its PNG checked. Use them as written; a new state gets its recipe here once proven.

| Shot | Commands | State to assert |
|---|---|---|
| IFR arrival offered Cleared approach (approach flyout, runway 30 default) | `ADD IFR L J -090 15 4000`, `FP B739 040 KSJC DCT KOAK`, `JFAC ILS30` | `Approach` (intercept-course phase, `SituationClassifier.cs:206`; `JFAC` is lateral-only, so Cleared approach stays) |
| Enroute IFR jet with a route ahead (Hold at fix on the later fix) | `ADD IFR H J @OAK270012 350`, `DCT SUNOL ALTAM`, wait until navigating to SUNOL; type ALTAM | `IfrEnroute`; the Inbound course box defaults to the SUNOL→ALTAM leg only for a fix past the next one (`HoldFlyout.cs:199-206`); above 14,000 ft legs open on 1½ min |
| IFR aircraft with a filed airway, joining others at different fixes (Join airway, through All Commands) | `ADD IFR S+ T @OAK270010 080`, `FP DH8C 080 KOAK OAK V6 SAC KSMF`, `FH 360`, wait until the heading is within 5° of 360 | `IfrEnroute`; the filed route gives the ◆ row (`MenuCatalog.cs:1759`). Pointed straight at OAK, every airway through OAK is joined at OAK, hence the turn |
| Jet taxiing on a route past several bars (Hold short of… ladder, ground view) | `ADD VFR S+ J @KAI5 C56X`, `TAXI C B W RWY 30`, wait until ground speed is above 5 kt; `GroundViewZoom.Apply` ×3 then `PlaceInGroundView` at 0.12/0.15 | `Taxiing` (Hold short of… is on the Taxiing and HoldingShort strips, `QuickCommandDefaults.cs:41,59`) |
| Helicopter parked (Air taxi flyout, ground view) | `ADD VFR S H @KAI7 R44`; same zoom and placement | `AtParking` (`QuickCommandDefaults.cs:20`). OAK's map has no helipads, so Pads · parking lists parking only |
| A vStrips rack auto-sorted | `FlightStripsScene`'s staging, then `vm.SendCommandForViewCoreAsync("", VStripsCanonicalBuilder.BuildRackSort(bay.FacilityId, bay.Name, 0, on: true), "")`; a strip command cannot go through `SendCommandAsync` with no aircraft selected (`CommandScopes.SendsWithoutSelection`) | the reply's `Success`; the rack's callsigns A–Z |
| VFR aircraft with nearby airborne traffic (Report traffic in sight) | `ADD VFR S P @OAK270010 045 P28A`, then traffic: `ADD VFR S P @OAK286011 055 SR22`, `ADD VFR S P @OAK265016 035 C172`, `ADD VFR S T @OAK250012 065 C208`, `ADD IFR L J @OAK300005 080 B738`, `ADD VFR S P @OAK240008 025 C152` | `VfrFlightFollowing` (a VFR `ADD` files no plan, `SituationClassifier.cs:287-301`) |

Approach rows read "short" when the runway is under 1.15 × the type's landing distance (`RunwayLandability.cs:17,33-47`): OAK 28L/28R are short for a B739, runway 30 is not, so a jet's approach shot uses 30.

## Opening menus and popups

- **Do not simulate a pointer right-click on `MainWindow`.** Avalonia's `LightDismissOverlayLayer` takes every press even with no popup open, and the class is internal, so a scene cannot move it aside.
- **Call the view's own builder** and open the result at the target's screen point: `RadarView.BuildAircraftRightClickMenu` (`Views/Radar/RadarView.ContextMenus.cs`), `GroundView.BuildAircraftRightClickMenu` and `GroundView.BuildNodeContextMenu` (`Views/Ground/GroundView.axaml.cs`). These are `internal`; GuideCapture sees them.
- **The "Session setup" popup** opens over the views on a scenario load in an RPO room when the server asks for the active runways (`MainViewModel.ActiveRunways.cs`, `MainViewModel.SessionSetup.cs`). `ScenarioSceneBase` answers it for every scene that loads a scenario, right after the load report closes and before `OnSceneReadyAsync`, with `SceneActions.AnswerActiveRunwaysPromptAsync` (confirm the popup with the server's pre-filled runways, else cancel; nothing when no prompt is open), so no shot shows it and no menu loses the pointer input to it.
- **A flyout on a menu's strip icon** opens with `QuickCommandStrip.OpenSubmenu` on the icon after `SceneActions.PointAt`, never `SceneActions.Click`: a pointer click on a strip icon closes the headless menu first, and `Click` is only for a control that opens its own popup. It is placed beside the menu, clear of its rows; `Scenes/WhatsNewExitsAheadScene.cs` shows the result and stages the shot around it.
- `SceneActions.OpenStripFlyoutAsync(window, menu, entryId, …)` does that for the strip icon of a `MenuIds` entry and returns the flyout's `MenuFlyoutPresenter` once its rows are laid out; a flyout that refills while open (Push back to…) is then waited on for its settled rows, as `Scenes/WhatsNewPushBackScene.cs` does.
- `SceneActions.PlaceInGroundView(window, vm, position, x, y)` moves the zoomed ground view so a point sits at fractions of its width and height, near the upper left to leave a menu and its flyouts room to the right.
- **A radar range/bearing shot needs two aircraft** to latch the line to; `Scenes/WhatsNewRblLabelScene.cs` picks the pair.
- Menu-bar menus, submenus and button flyouts: `SceneActions.OpenMenuAsync`, `OpenSubmenuAsync`, `OpenButtonFlyoutAsync`. A control's `ContextMenu` in a host window: `Scenes/TerminalRewindMenuScene.cs`.
- An aircraft whose strip has no icon for an entry opens it through All Commands: `SceneActions.OpenAllCommandsEntryAsync(window, menu, label, timeout)` opens All Commands and each submenu down to the entry headed `label` (through `OpenSubmenuAsync`) and returns that entry, as `Scenes/WhatsNewTaxiToRunwayTabsScene.cs` does.

## Checking the result

Open every PNG you write with an image viewer (an agent reads it with its file-read tool) and check it shows what the scene claims: the menu open, labels legible, nothing clipped, no prompt over the view. A capture that exits 0 but shows the wrong state is a failed scene.
