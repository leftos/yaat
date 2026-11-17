# Radar Display and Rendering

> Read this before touching anything under `src/Yaat.Client/Views/Radar/` or the shared `src/Yaat.Client/Views/Map/`
> base: `RadarCanvas`, `RadarRenderer`, `TargetRenderer`, `VideoMapRenderer`, the datablock/tag layout structs,
> the radar context menus, the EuroScope interactive tag flyouts, or `MapViewport`. The two-thread snapshot split and the
> draw-vs-hit-test geometry duplication are unenforced invariants — break one and you get a cross-thread race or clicks that
> miss text, with no compiler signal.

Its File map below lists the files; the rest of this doc explains the pipeline, the projection contract, the datablock geometry, the pointer-handler ladder, and the menu conventions. The EuroScope tag mode mirrors the upstream EuroScope behavior documented in [euroscope/overview.md](euroscope/overview.md) (and siblings) — those docs are the external design reference; this one is the YAAT implementation.

## File map

All paths are under `src/Yaat.Client/`.

| File | Role |
|---|---|
| `Views/NavRouteShapeLook.cs` | Internal `NavRouteShapeLook.For(kind)` → `(Dashed, Arrow)`: the one place the Radar's "Show nav route" and the Scope's pilot-lens intent path take a `NavRouteShapeKind`'s look from |
| `Views/Map/MapCanvasBase.cs` | Abstract `ICustomDrawOperation` base: pan/zoom input handling, the UI-thread→render-thread snapshot split, the 100 ms force-repaint timer |
| `Views/Map/MapViewport.cs` | Equirectangular lat/lon ↔ screen projection shared by the map views: rotation, zoom |
| `Views/Radar/RadarCanvas.cs` | The radar `Control` (SkiaSharp canvas): styled properties, pointer handlers, hit-testing, Range/Zoom sync and the pan/zoom lock, datablock drag; `MenuCue` (the row's `MapCue`, resolved into the snapshot) and a repaint on a highlight change |
| `Views/Radar/RadarRenderer.cs` | Stateless per-frame orchestrator: clears, then draws maps → rings → fixes → paths → targets → overlays; `DrawMenuCue` hands the menu cue to `MapCueRenderer` |
| `Views/Radar/TargetRenderer.cs` | Aircraft symbols (dashed for live-traffic shadows, half alpha while coasting — [live-traffic.md](live-traffic.md)), leader lines, datablocks, PTLs, history trails, speech bubbles |
| `Views/Radar/RadarDatablockLayout.cs` | Pure layout struct for the STARS full datablock (up to 5 lines): line/field geometry shared by the renderer and click hit-testing, alongside the EuroScope tag layout |
| `Views/Radar/EuroScopeTagLayout.cs` | Pure layout for the EuroScope tag (4 lines + ModeC + NoLndgClnc): `TagFieldId` and per-field rects for hit testing and flyout dispatch |
| `Views/Radar/RadarView.axaml.cs` | The radar view control: wires canvas events; dispatches EuroScope field clicks to flyouts; the DCB (range, map shortcuts, FIX, LOCK) and brightness buttons |
| `Views/Radar/RadarView.ContextMenus.cs` | Partial: the radar's right-click menus, both through `AircraftMenuBuilder`: the aircraft menu with the radar's view section (Display ▸, Draw route), and the map point menu (`BuildMapPointMenu`: the selected aircraft's point header and items, then FRD (left out when the header shows it), markers, Measure and the dimmed MVA row (`MvaRow`), left out where no MVA sector covers the point) |
| `Yaat.Client.Core/ContextMenus/QuickCommandResolver.cs` / `QuickCommandDefaults.cs` / `QuickCommandStrip.cs` | Situation → the quick commands (icon strip, then text entries) at the top of the aircraft menu ([client-context-menus.md](client-context-menus.md)) |
| `Views/Radar/Flyouts/*.cs` | EuroScope interactive-tag pickers (altitude, speed, runway, squawk, scratchpad, handoff) + heading mode |
| `ViewModels/RadarViewModel.cs` | Radar view (its draw-route mode is a `DrawRouteState`); video map loading, toggle items (`VideoMapToggleItem`, with `TdmOnly` from the map's vNAS flag; the Radar ignores it), DCB, persistence |
| `ViewModels/DrawRouteState.cs` | One view's draw-route mode, shared by the Radar and the Scope (each view model owns its own): `IsDrawing`, `Callsign`, the placed waypoints (`DrawnWaypoint`) and each one's `WaypointCondition`, the FRD-or-`lat,lon` naming of a placed point, undo, remove, remove-after and cancel, and the `DCTF …;AT …` a confirm sends; `Changed` fires after every change |
| `ViewModels/RadarViewModel.Measure.cs` | Partial: distance measuring tool on the radar (nautical miles) |
| `ViewModels/RangeBearingViewState.cs` | Observable mirror of the one RangeBearingLineStore behind both map views (shared slot pool, per-view visibility); owns the tool's behaviour |
| `Views/Map/VideoMapRenderer.cs` | Video-map polylines (A/B brightness categories) and labels |
| `Views/Map/MapBlink.cs` | The one on/off phase (half a second each) every blinking map token follows; Clock is pinned by the screenshot harness |
| `Views/Map/MapLineStyle.cs` | Line-width scale for the map canvases (`GetScale`, `Apply`) |
| `Views/Map/AircraftVisibility.cs` | `FilterAircraft`, the one rule for which aircraft the Radar and the Scope draw and hit-test (`IsDrawable` under it) |
| `Views/Map/DatablockText.cs` | The STARS full-block text builders and placement rules the Scope, Ground view and Radar share: owner/scratchpad, conflict and ATPA lines, cwt/type, collapsed lines, student-level marker, `ResolveBlockOffset` |
| `Views/Map/TargetColors.cs` | The target and datablock colours the three views share (`StaleAlpha`, TPA, ATPA cone colours) and `DrawSquawkMismatchLine` |
| `Views/Map/TextStyle.cs` | Paired (SKFont, SKPaint) for measuring + drawing text; keeps draw and hit-test metrics identical |
| `Views/Map/DatablockDeconfliction.cs` | Pure opt-in datablock overlap resolver shared by radar + ground (snap / free-form) |
| `Views/Map/RangeBearingLines.cs` | Distance measuring tool (CRC STARS *T): endpoints, 15-slot store (lines tagged per RblView — radar, ground and Scope each render only their own), label formatting, resolver, readout anchor + viewport clamp (RblLabelPlacement), hit-test |
| `Views/Map/RblReadoutPlacement.cs` | Places each range/bearing readout at the least-covered of eight spots around its anchor and nudges auto-placed datablocks clear when every spot is covered; shared by radar + ground canvases |
| `Views/Map/RangeBearingRenderer.cs` | Draws measurement lines + readouts at the rects RblReadoutPlacement chose; shared by radar + ground renderers |
| `Views/Map/MapCueRenderer.cs` | Draws a menu row's `ResolvedMapCue` (the solid ring, the dashed bearing line with its label, the dashed resolved ring); the Radar's `DrawMenuCue` and the Scope's `ScopeRenderer.Cue.cs` both call it |
| `Views/Map/RightClickGesture.cs` | Right-button click-vs-drag tracker shared by radar + ground: menu on release-without-drag, pan otherwise |
| `Views/Map/RightClickTarget.cs` | One thing a right click hit (aircraft or parking/spot/helipad node) and its picker label |
| `Views/Map/RightClickPicker.cs` | Small menu listing several right-click targets; choosing one raises that target's own right-click event |
| `Views/Map/RightClickTargetOrder.cs` | Static HoveredFirst: puts the target the hover label names first in the picker list (moved if listed, else inserted), the rest in order |
| `Views/Radar/RadarView.Popups.cs` | Partial: the waypoint-condition popup (the menu pickers are Views/MenuPopups.cs) |
| `Views/Radar/RadarViewWindow.axaml.cs` | Pop-out radar window; takes a geometry key + title and hosts a RadarViewModel via SetViewModel (primary: "RadarView"/vm.Radar; extra instance: RadarView#n/its own VM, exposed as RadarVm for the Ctrl+F8 hotkey); window DataContext stays MainViewModel |
| `Views/Radar/Flyouts/FlyoutAppearance.cs` | Shared visual styling for tag flyouts (altitude, speed, runway, scratchpad popups) |
| `Views/Radar/Flyouts/RadarSpeedFlyout.cs` | The EuroScope tag's assigned-speed picker (80..350 kt in tens, Resume Normal Speed, Final Approach Speed), opened by a click on the speed fields; not the quick-command Assign speed flyout (ContextMenus/SpeedFlyout.cs) |
| `ViewModels/ScopeViewModel.cs` | The YAAT Scope window's view-model: its own centre, range, video maps, MVA-tint flag (ShowMvaHints) and datablock template pick (TemplateId, PickTemplateCommand; a template id or Mixed; the toolbar state is in ScopeViewModel.Toolbar.cs: tray open, range wheel step, button echo) and the Viewer (a ScopeViewer rebuilt when the active TCP, position or acting callsign/facility changes); seeds from `MainViewModel.ScopeMaps` (`SeedFromMapSource`) on every facility load, never from the Radar: toggles from the new facility with the maps already on kept on by map id, 40 NM range (`DefaultRangeNm`), the facility's centre and MVA-tint default; opened before a facility loads it waits (`IsWaitingForMapSource`) for the first load; a cleared source empties its maps and keeps centre and range, and a late primary-airport recentre moves a Scope still at its seeded centre and range |
| `ViewModels/ScopeViewModel.Tools.cs` | Partial: the TOOLS tray's Rings tool: ArmedRingTool (set from the band's rings value when the tray opens; cleared by Esc, a second press of the lit button, the tray closing or another tray opening), RingSizeNm (SIZE), the per-callsign rings (`RingsOf`, `SetRings`, `ClearRings`, published as `LocalRings`) and `PlaceRing`, which runs `.scope rings`; never saved or sent, cleared with the aircraft; `ClearTrackState` drops the Scope's whole per-track view state (its `DataBlockState`: dwell locks, block offsets, highlights, z-order; the rings and the PTLs) and runs on scenario unload, restart and any rewind, this client's own included |
| `ViewModels/ScopeViewModel.Ptl.cs` | Partial: the per-track PTLs (`TogglePtl`, `ClearPtl`, published as `LocalPtlCallsigns` to `ScopeCanvas.LocalPtlCallsignsProperty`); what `.scope ptl` and a STARS `MF R` entry run; never saved or sent, cleared with the aircraft and by `ClearTrackState` |
| `ViewModels/ScopeViewModel.Lens.cs` | Partial: the pilot lens — `Lens` (a `ScopeLensSettings` record: Enabled, Path, Queue and CheckIn, `DrawsPath`, `DrawsQueue` and `DrawsCheckIn` when the lens and that switch are on; all off on a fresh scope), `SetLensEnabled` and `SetLensOverlay` (what `.scope lens` calls), the toggled intent-path callsigns (`ToggleLensPath`, `ClearLensPath`, `ClearLensPaths`, published as `LensPathCallsigns` to `ScopeCanvas.LensPathCallsignsProperty`); the switches are saved, the toggled tracks are not, and they drop with the aircraft (`MainViewModel.Aircraft.cs`) and in `ClearTrackState` |
| `ViewModels/ShownRouteRecords.cs` | The records the Radar's route overlay and the Scope's intent path share: `DrawnWaypoint` (a resolved fix with its crossing-restriction token lines; empty name for an arc-densification vertex) and `VectorTail` (a heading arrow from a point) |
| `ViewModels/ScopeViewModel.MinSep.cs` | Partial: the STARS min-sep pair (`MIN`): `MinSep`, `ClearMinSep`, `CompletesMinSep`, `FillTrackFromClick`, `TryRunMinSep`, CRC's error texts, the recompute on either aircraft's change |
| `Views/Scope/MinSepRenderer.cs` | `ScopeMinSep` (the pair and its projection) and its renderer: the predicted-point lines, markers and the boxed `0.00 NM` / `NO XING` readout (`LabelLines`, `FormatNm`) |
| `ViewModels/ScopeViewModel.Menus.cs` | Partial: the Display submenu's rows as they reach the view model: `IsNavRouteShown` and `ToggleNavRoute` (the pilot lens's per-track intent path; turning it on also turns the lens and PATH on when either is off), `HasRing` and `ToggleRing` (the Scope-local J-ring and cone: a checked row removes the ring at any size, an unchecked one places it at the Rings tool's current SIZE) |
| `ViewModels/ScopeViewModel.Measure.cs` | Partial: the Scope's side of the distance measuring tool — MeasureView (`RblView.Scope`), MeasureUnits (nautical miles), the shared `Measure` store, MeasureTrackLookup, `ActivateMeasureSurface`/`IsActiveMeasureSurface`, `ArmMeasure` (Ctrl+M: arms the tool and writes the prompt to the Scope's response and the status line), `TakeMeasurePrompt` and `CancelMeasure` (Esc's measure steps), `PickMeasurePoint`, `PlaceMeasurement` and `CancelMeasureFromPointer` (the pointer's picks, Alt-drags and right-click cancel, with their status shown in the response); the lines are session state in the shared 15-slot pool, never saved, and not part of `ClearTrackState` |
| `ViewModels/ScopeViewModel.Display.cs` | Partial: the Scope's display state — Brightness (a ScopeBrightness, opening at Default; never sent, kept in a pref set) and SetBrightness, which `.scope brite` calls; MapToggles (the Scope's own ScopeMapToggle list, rebuilt from `ScopeMapSource.Maps` at every seed) with SetMapOn/ClearMaps for `.scope map`; IsTdm (top-down mode; saved in a pref set); DbSettings (a ScopeDbSettings record: 4th line, hover dwell, ATPA distance, CHAR size; ATPA and CHAR seeded from the preferences, never written back) with SetDbToggle/SetDbCharSize for `.scope db` |
| `Services/ScopeMapSource.cs` | The main facility's map and display catalogue for the Scope (`MainViewModel.ScopeMaps`): `ScopeMapInfo` records, brightness categories, map group, weather airports, primary airport, centre, fixes, `MvaFacilityId`, `ShowMvaHintsDefault`, `DescribePoint`, `GetFieldElevation`; it fetches the facility's maps itself (`LoadAsync`), `MainViewModel` feeds it beside the primary Radar; raises `Loaded` and `Cleared` |
| `ViewModels/ScopeMapToggle.cs` | One of the Scope's own video-map toggles (MapId, StarsId, ShortName, Name, BrightnessCategory, TdmOnly, Label `{StarsId} {ShortName}`, IsOn); toggling it never touches the Radar's |
| `ViewModels/ScopeViewModel.Tearoffs.cs` | Partial: the Scope's button tear-offs — Tearoffs (ScopeTearoff copies by ScopeButtonKey), IsTornOff, PlaceTearoff and DeleteTearoffs (what `.scope tearoff` calls), the delete-mode marks (ToggleTearoffMark) and ButtonFor |
| `ViewModels/ScopeViewModel.Bands.cs` | Partial: the Scope's zoom bands — Pins, EffectiveBand (the profile's band for the range with the pins over it), PinOthersLevel, FlippedOthersLevel, the pin clear `.scope auto` runs, and the pins' apply from a pref set |
| `ViewModels/ScopeViewModel.Profile.cs` | Partial: follows the room's profile (MainViewModel.ScopeProfile) — ProfileKind, ToolbarButtons (the bar in the profile's order), the views' order and the profile's template (kept when the facility has a saved state); ApplyPrefSetProfile for `.scope pref load` |
| `ViewModels/ScopeViewModel.PrefSets.cs` | Partial: the Scope's pref sets — ActivePrefSet, PrefSetNames, CapturePrefSet, ApplyPrefSet (views opened as saved through RestoreViewPlace, tear-offs replaced, a set applied once the Scope seeds when it loads before the Radar's maps), and the saved state per facility (written on Dispose and when the viewer changes facility, restored when the Scope opens for or moves to a facility) |
| `ViewModels/ScopeViewModel.Views.cs` | Partial: the Scope's views dock state — DockViews (open and docked), FloatingViews (open and torn off), OpenViews, HasDockedViews, IsViewOpen, SetViewOpen, SetViewPlace, ApplyOpenViews — and the ScopeDockViewModel base of a card (name, title, Place, DockCommand, StartReading/StopReading while open) |
| `ViewModels/SsaViewModel.cs` | The SSA card's view-model: refreshes its SsaLines every second while open |
| `ViewModels/ConflictListViewModel.cs` | The LA/CA/MCI card's view-model: ConflictListRows from MainViewModel.ConflictAlerts, refreshed on ConflictAlertsApplied |
| `Views/Scope/` | The YAAT Scope (docs/plans/yaat-scope/README.md): the pure engine plus the primary Scope, shown as the main window's Scope tab or popped out into its own window (View > Windows > Sco_pe, the "PopOutRadarView" key; geometry key "ScopeView") |
| `Views/Scope/ScopeWindow.axaml(.cs)` | The Scope's pop-out window: hosts a `ScopeHost`; `ownsViewModel` is false for the primary Scope, whose view model outlives the window and docks back, true for a window that disposes its own |
| `Views/Scope/ScopeHost.axaml(.cs)` | The Scope as one control (toolbar, `ScopeView`, views dock) over the `ScopeViewModel` in its DataContext; the Scope tab and the Scope window each build their own, never both at once |
| `Views/MainWindow.ScopeHosting.cs` | Partial of `MainWindow`: `ScopeTabHeader`, `ScopePopOutMenuHeader`, the primary Scope's pop-out (`HandleScopePopOut`: open the `ScopeWindow`, or save the state and close it; closing the window docks back and clears `IsScopePoppedOut`) and `DisposePrimaryScopeAtExit` |
| `Views/Scope/ScopeCanvas.Ladder.cs` | Partial of `ScopeCanvas`: `ScopePress`, `ScopeRelease` and `ScopeMove`, and the press ladder (`RunPressLadder`), release ladder (`RunReleaseLadder`) and move hooks (`RunMoveHooks`) whose rungs are partial methods a feature implements |
| `Views/Scope/ScopeCanvas.Features.cs` | Partial of `ScopeCanvas`: the feature hooks outside the ladders: `EscapeModes` (the canvas's pointer modes, one per Esc) and `CollectOverlays` (builds `ScopeOverlays` with each frame) |
| `Views/Scope/ScopeCanvas.DataBlocks.cs` | Partial of `ScopeCanvas`: the block gestures, rungs of the ladders: `PressBlockHide` (Shift+click on a block or a symbol toggles `HiddenCallsigns`), `PressBlockDrag` / `MoveBlockDrag` / `ReleaseBlockDrag` (a left press on a block arms a drag that writes `ManualOffsets` past a 4 px threshold); an indicator cell or an action field keeps its own click, and so does ERAM Field A for the hide |
| `Views/Scope/ScopeFrame.DataBlocks.cs` | Partial of `ScopeFrame`: `PlaceBlock` (a manual offset wins, else a track drawn with the STARS template takes the student's leader direction while `SyncLeaderDirection` is on, else the default offset) and `IsBlockHidden` (a hidden, unselected track lays out no block, so draw and hit-test find none while the symbol keeps its own hit) |
| `Views/Scope/ScopeView.DataBlocks.cs` | Partial of `ScopeView`: repaints the canvas when `DataBlockState`'s manual offsets change, whichever surface wrote them |
| `ViewModels/ScopeViewModel.DataBlocks.cs` | Partial: the Scope's per-track block state as menus and automation reach it (`HideDataBlock`, `ShowDataBlock`, `ToggleDataBlockHidden`, `ShowAllDataBlocks`, `SetDataBlockOffset`, `ResetDataBlockPosition`) over the Scope's own `DataBlockState`; never sent, saved in a pref set or kept past `ClearTrackState` |
| `Views/Scope/ScopeCanvas.Menus.cs` | Partial of `ScopeCanvas`: the pointer menus' rungs (`PressMenuGesture`, `PressEmptyMap`, `ReleaseMenuGesture`): `AircraftCtrlClicked` (the action modifier on a target, block or indicator), `EmptySpaceClicked`, the right-click picker over stacked targets (`FindRightClickTargets`, `ActiveRightClickPicker`) and `MapRightClicked` through a `RightClickGesture` |
| `Views/Scope/ScopeFrameHits.cs` | Extension methods on `ScopeFrame`: `BlocksAt` (every block under a point, topmost first) and `TargetsNear` (every symbol within `ScopeFrame.TargetHitRadiusPx`, nearest first), the plural hit tests behind the right-click picker |
| `Views/Scope/ScopeOverlays.cs` | The partial record the feature overlays' data hangs on: built on the UI thread with the frame, carried in `ScopeRenderSnapshot.Overlays` and read as is on the render thread |
| `Views/Scope/ScopeRenderer.Features.cs` | Partial of `ScopeRenderer`: the draw slots (`DrawMapToolsUnder`, `DrawRouteOverlay`, `DrawHeadingVector`, `DrawBubbles`, `DrawMenuCue`, `DrawMapToolsOverlay`) and `DisposeFeatures` |
| `Views/Scope/ScopeCanvas.Cue.cs` | Partial of `ScopeCanvas`: `MenuCue` (the row's `MapCue`) and `CollectMenuCue`, which resolves it against the frame's tracks into `ScopeOverlays.MenuCue` |
| `Views/Scope/ScopeOverlays.Cue.cs` | Partial of `ScopeOverlays`: `MenuCue`, the `ResolvedMapCue` the frame draws |
| `Views/Scope/ScopeRenderer.Cue.cs` | Partial of `ScopeRenderer`: the `DrawMenuCue` slot, drawn by a `MapCueRenderer` it disposes with the renderer |
| `Views/Scope/ScopeView.Overlays.cs` | Partial of `ScopeView`: `AttachOverlays` and `DetachOverlays` carry `ScopeViewModel.MenuCue` and the shown ground airport (`GroundShownAirportIdProvider`) to the canvas and repaint on a cue or datablock-highlight change |
| `Views/Scope/ScopeCanvas.Bubbles.cs` | Partial of `ScopeCanvas`: the speech bubbles — `FilterVisible` (the visibility rule with the bubble preferences), `OrderBubbledLast` (bubbled tracks drawn above plain ones, dwelled last), `CollectBubbles`, `FindBubbleAt`, and the `PressBubble` / `ReleaseBubble` rungs: a left press on a bubble is recorded and a release within 5 px dismisses it (`SpeechBubble` cleared), a farther release is a pan; `PressEmptyMap` skips `EmptySpaceClicked` on a bubble so the selection stays |
| `Views/Scope/ScopeBubbles.cs` | Pure: `ScopeBubbles.Place` wraps each active bubble (`MaxLineChars`, `MaxLines`) and lays it below its block, at the symbol when the block is hidden, into `ScopeBubble` records measured on the UI thread so the hit test and the renderer share the rects; `ScopeBubbleOptions` |
| `Views/Scope/ScopeOverlays.Bubbles.cs` | Partial of `ScopeOverlays`: `Bubbles`, the placed `ScopeBubble` list |
| `Views/Scope/ScopeBubbleRenderer.cs` | Draws the bubbles: a green pill for SAY / pilot, an amber one for WARN, wrapped monospace lines |
| `Views/Scope/ScopeRenderer.Bubbles.cs` | Partial of `ScopeRenderer`: the `DrawBubbles` slot, over every block and under the menu cue and the measure layer, and its disposal |
| `ViewModels/ScopeViewModel.MapCue.cs` | Partial: `MenuCue`, the `MapCue` `IMenuHost.SetMapCue` sets |
| `ViewModels/MainViewModel.ScopeHosting.cs` | Partial of `MainViewModel`: `PrimaryScope`, `IsScopePoppedOut`, `ActiveScope`, `OpenScope` (the active Scope, else the primary; never null) and `AllScopeViews`, which `ClientMenuHost` loops over beside `AllRadarViews` |
| `Views/Scope/ScopeView.Features.cs` | Partial of `ScopeView`: per-feature `Wire*`, `Attach*` and `Detach*` hooks, `BuildViewSection` (the Scope's rows of the aircraft menu), `RunKeyHooks` and the Esc chain `HandleEscape` |
| `ViewModels/ScopeViewModel.Features.cs` | Partial of `ScopeViewModel`: `EscapeModes`, `EscapeTools` and `ClearFeatureTrackState` (run by `ClearTrackState`), each calling per-feature partial methods |
| `ViewModels/ScopeViewModel.DrawRoute.cs` | Partial: the Scope's draw-route mode — `DrawRoute` (its own `DrawRouteState`, made on first use), `EnterDrawRoute` (selects the aircraft and disarms the Rings tool), `ConfirmDrawRouteAsync`, the `SendPilotCommand` test seam, `FlyHeadingAsync` (the heading vector's `FH`) and `CloseRouteConditionPopup` (Esc closes the popup before it cancels the route); left by Esc, by a selection that leaves the drawn aircraft and by `ClearTrackState` |
| `Views/Scope/ScopeCanvas.DrawRoute.cs` | Partial of `ScopeCanvas`: the draw-route press rung (left places a waypoint unless one is within 20 px, middle on a waypoint raises `RoutePointConditionRequested`, right on a waypoint raises `RouteWaypointRightClicked`; Alt+left and the armed measuring tool go on to the measure rung), the move hook that redraws the rubber band, the cross cursor and `PointNamer` |
| `Views/Scope/ScopeOverlays.DrawRoute.cs` | Partial of `ScopeOverlays`: `ScopeDrawRouteOverlay`, one frame of the route being drawn (waypoints, conditions, the aircraft it starts from, the pointer named by FRD) |
| `Views/Scope/ScopeRenderer.DrawRoute.cs` | Partial of `ScopeRenderer`: the draw-route slot, above the pilot lens and below the measure lines |
| `Views/Scope/ScopeDrawRouteRenderer.cs` | Draws the route being drawn as the Radar does, in amber: a dashed leg from the aircraft to the first waypoint, solid legs, a dashed rubber band to the pointer labelled with its FRD, a diamond at each waypoint with its name and condition summary |
| `Views/Scope/ScopeView.Display.cs` | Partial of `ScopeView`: the aircraft menu's Display submenu (`BuildCanvasDisplay`, through `AddDisplaySection`) from the Scope's own state: Hide/Show datablock, Reset datablock position, Show/Hide nav route, Measure from/to, then Leader direction, J-ring and Cone as check-marked toggles, then Blank and Unblank, for every aircraft, live-traffic shadows included; and `AppendViewSettings`, the menu's last item "Settings for this view…" |
| `Views/Scope/ScopeView.DrawRoute.cs` | Partial of `ScopeView`: Draw route as the Scope's view-section row and the EuroScope Destination field's `StartFieldDrawRoute`, both through `AcceptsFlightCommands`, which the AHDG field shares (not a surface live-traffic shadow, not a phase that hides the flight commands); Enter (sends) and Backspace (undoes) ahead of the entry; the waypoint right-click menu (Confirm route, Delete waypoint, Delete waypoints after, Cancel route) and the condition popup |
| `Views/Scope/ScopeHeadingVector.cs` | `ScopeHeadingVectorState`, the elastic heading vector's pure state machine (`Begin`, `MovedTo`, `Released`, `Pressed`: a drag past 4 px confirms at the release, a short press arms the next left press, which confirms toward its own press point) and its helpers (`MagneticHeading`, `SnapHeadingTo5`, `TurnRadiusNm`, `FormatEta`) |
| `Views/Scope/ScopeCanvas.HeadingVector.cs` | Partial of `ScopeCanvas`: the heading-vector press (first rung of the press ladder), release and move rungs, `EnterHeadingVector`, `CancelHeadingVector`, the Esc step in `EscapeModes`, the cross cursor and the `HeadingVectorConfirmed` event; an armed vector's left press confirms whatever is under it (another aircraft, Alt+left and the armed measuring tool included, none reaching a later rung) and a right press cancels |
| `Views/Scope/ScopeOverlays.HeadingVector.cs` | Partial of `ScopeOverlays`: `HeadingVector` and the `ScopeHeadingVectorOverlay` record (the aircraft's position, heading and ground speed, and the cursor) |
| `Views/Scope/ScopeRenderer.HeadingVector.cs` | Partial of `ScopeRenderer`: the `DrawHeadingVector` slot, drawn by a `ScopeHeadingVectorRenderer` it disposes with the renderer |
| `Views/Scope/ScopeHeadingVectorRenderer.cs` | Draws the heading vector as the Radar's heading preview does, in amber: the standard-rate turn arc, the line to the snapped endpoint and the "heading, distance, time" label |
| `Views/Scope/ScopeView.HeadingVector.cs` | Partial of `ScopeView`: `StartFieldHeadingVector` (the EuroScope AHDG field's `ScopeFieldAction.StartHeadingVector`, which cancels a route being drawn and is inert where `AcceptsFlightCommands` refuses), the cancel when a route starts or the view detaches, and the confirm, which sends `FH {heading}` through the view model's pilot-command seam |
| `Views/Scope/ScopeWaypointConditionPopup.cs` | The waypoint-condition popup a middle click on a drawn waypoint opens, as the Radar's: crossing altitude and commands boxes with OK, Clear and Cancel, in the canvas's overlay layer |
| `ViewModels/MainViewModel.ScopeFeatures.cs` | Partial of `MainViewModel`: `RunFeatureVerb`, which offers a `.scope` verb to each feature's verbs after every built-in verb |
| `ViewModels/MainViewModel.ScopeMapTools.cs` | Partial of `MainViewModel`: the map tools' `.scope` verbs `mva`, `lock`, `center`, `rr`, `wx`, `fix` and `dcnf` (`RunMapToolsVerb`); the marker verbs `.ff`, `.marker`, `.markers` and `.nomarkers` (`TryHandleScopeMarkerCommand`), which act on the open Scope; `ScopeWeatherLines`, the METAR lines the overlay draws |
| `ViewModels/ScopeViewModel.MapTools.cs` | Partial of `ScopeViewModel`: LOCK (`IsPanZoomLocked`), OFF CNTR (`Recenter`, `IsOffCenter` against `OffCenterThresholdNm`), the range ring (`ShowRangeRing`, `RangeRingSizeNm`, `RangeRingCenter` / `RangeRingCenterShown`, `IsPlacingRangeRing` for PLACE RR), WX (`ShowWeather`, `WeatherLines`), DCNF (`DeconflictMode`, seeded from `UserPreferences.RadarDeconflictMode` and never written back) and the map-tool settings a pref set captures and applies |
| `ViewModels/ScopeViewModel.Markers.cs` | Partial of `ScopeViewModel`: the FIX overlay's switch (`ShowFixes`, `ShownFixes`) and the pinned markers (kept as `MarkerTokens`, fix names or FRDs, and placed as `Markers` once the navigation data loads; `ToggleMarker`, `AddMarker`, `RemoveNearestMarker`, `ClearMarkers`, `HasMarkerNear` and `MarkerPickRadiusNm` for the point menu); a track reset or map-source seed leaves the markers alone |
| `Views/Scope/Prefs/ScopePrefMapTools.cs` | The `mapTools` object of a pref-set file (`mva`, `lock`, `rangeRing`, `weather`, `fixesAndMarkers`, `deconflict`; the last four null at their defaults, FIX off and no marker, the mode a new Scope takes) with `ScopePrefRangeRing` and `ScopePrefFixesAndMarkers`; `ScopePrefSet.MapTools` is null in a set from an older build |
| `Views/Scope/ScopeCanvas.MapTools.cs` | Partial of `ScopeCanvas`: the Ctrl-hover MVA readout's state (raw Control, the pointer) and its `ScopeOverlays.MvaHover`; the range ring, the FIX overlay (`ShownFixes`, `NearestFix` for the fix within `FixHoverRadiusPx` of the pointer, the selected aircraft's `ProgrammedFixes` through `ProgrammedFixResolver`), the pinned markers and the weather lines it hands the overlays, and PLACE RR's left press (`PressRangeRing`, raising `RangeRingPlaced`) ahead of aircraft hit-testing |
| `Views/Scope/ScopeDeconfliction.cs` | The Scope's DCNF over the shared `DatablockDeconfliction` engine: `Build` lays a frame out as without it, hands the engine each visible block (pinned when manually dragged, priority when selected) and lays the frame out again at the offsets it gives back; `ResolvedOffsets` seeds the next run, and a hit-test's build (`step` false) reuses them in Free-form; `NameOf`, `ParseMode` and `Next` serve `.scope dcnf` and the DCNF button |
| `Views/Scope/ScopeMvaReadout.cs` | Pure: the readout text, `MVA <floor> (<sector>)`, from `MvaDatabase.FindSector` |
| `Views/Scope/ScopeOverlays.MapTools.cs` | Partial of `ScopeOverlays`: `MvaHover`, `RangeRing`, `Fixes`, `Markers` and `Weather` with the `ScopeMvaHover`, `ScopeRangeRing`, `ScopeFixLayer`, `ScopeMarker` and `ScopeWeatherText` records |
| `Views/Scope/ScopeMapToolsRenderer.cs` | Draws under the tracks the range ring (gray, RINGS brightness; `RingRadii`), then at MAP B brightness the FIX overlay (gray crosses, larger teal crosses and labels for the programmed fixes, a white label on the hovered fix) and the pinned markers (yellow diamonds with their names), and, over every other layer, the weather lines (green mono growing upward; `WeatherBaselines`) and the MVA hover label |
| `Views/Scope/ScopeRenderer.MapTools.cs` | Partial of `ScopeRenderer`: the `DrawMapToolsUnder` and `DrawMapToolsOverlay` slots and its disposal |
| `Views/Scope/ScopeView.MapTools.cs` | Partial of `ScopeView`: feeds Control key events to the canvas, applies LOCK to `ScopeCanvas.IsPanZoomEnabled`, and syncs the range ring, the FIX overlay, the pinned markers, PLACE RR and the weather lines (their inset above the ERAM entry boxes, `WeatherBottomInset`) to the canvas |
| `Views/Scope/Toolbar/` | The Scope's navy toolbar (replaced the Template menu) |
| `Views/Scope/Toolbar/ScopeToolbarButtons.cs` | The data-driven button model: ids, labels, kinds (Command, Toggle, Menu, Spinner); PROFILE (its tray: STARS, ERAM, MIXED), TEMPLATE, RANGE, OTHERS, VECTOR ("VECTOR 1m OWN"), HIST ("HIST 5"), TOOLS (its tray: J-RING, CONE, DRI 5 and DRI 3 toggles, lit while armed, and the SIZE spinner), AUTO, MAPS (CLR ALL, then a toggle per video map; the tray's children come from `ScopeViewModel.TrayChildren`), TDM (a bar-level toggle, `TdmCommand`), MVA (a bar-level toggle, `MvaCommandStem`, lit by `ScopeViewModel.IsMapToolToggleOn`), DISPLAY (its tray: FIX, a toggle on `FixCommandStem`, WX, a toggle on `WxCommandStem`, LOCK, a toggle on `LockCommandStem`, DCNF, a button on `DcnfCommand` that steps the mode and keeps the tray open, its value S or F, lit while on, and OFF CNTR, a command on `CenterCommand` lit by `ScopeViewModel.IsOffCenter`), RR (a full tile, `ScopeViewModel.MapToolValue` showing the size or OFF; its tray: SHOW, a toggle on `RrCommandStem`, PLACE RR on `RrPlaceCommand` lit while armed, the SIZE spinner on `RrSizeCommand` and RR CNTR on `RrCenterCommand`), DB (toggles 4TH LINE, DWELL, ATPA DIST and the CHAR spinner), VIEWS (its tray toggles SSA, LA/CA/MCI, RPO QUEUE and CRR), PILOT LENS (`ScopeButtonId.Lens`, a full tile; its tray toggles `LensOn`, `LensPath`, `LensQueue` and `LensCheckIn`, each lit by `ScopeViewModel.IsLensToggleOn`; the tile is lit while the lens is on or its tray is open), BRITE (its tray holds a spinner per brightness group), PREF (its tray: a button per pref set, then SAVE, SAVE AS, RENAME, DELETE; `CanTearOff` is false for the set buttons) and DEL TEAROFF (last; it enters and runs delete mode) today, in that bar order; a button's accessible name is its label plus value (`RANGE 40`); every button carries its own dot command (`ScopeButton.Command`) |
| `Views/Scope/Toolbar/ScopeButtonControl.cs` | The reusable toolbar button: the tile: the Button plus its 4 px tear-off strip over the tile's left edge, full height (gold under the pointer), and the corner "▸" mark of a menu; the bar, the trays and a torn-off copy all build it |
| `Views/Scope/Toolbar/ScopeTearoff.cs` | Pure: ScopeButtonKey (a button, or a map toggle by STARS id), ScopeCorner, ScopePlacement (corner and dx/dy) and ScopeTearoff |
| `Views/Scope/Toolbar/ScopeToolbarStyles.axaml` | The button styles the bar and the copies share |
| `Views/Scope/Toolbar/ScopeToolbar.axaml(.cs)` | The 45 px bar of unpadded tiles with 1 px dividers (full tiles for PROFILE, TEMPLATE, RANGE, OTHERS, VECTOR, HIST, BRITE, PREF and DEL TEAROFF; TOOLS/AUTO, MAPS/TDM, MVA/DISPLAY and DB/VIEWS stacked in 22 px half tiles; PREF counts as showing a value with or without a pref set, so it is always a full tile (a dim — without a set) whose `MinWidth` reserves nine bold characters, and loading or clearing a set moves no tile; the bar is laid out again whenever the pairing changes; a sideways scroller that never takes the wheel: a ‹ and a › tile, 45 px tall, show only while the bar is wider than the window, disabled at the matching end, each moving it three quarters of a viewport; pressing a part-hidden button does not scroll, Tab or arrow focus does; the tray stays outside the scroller at full window width) and every menu's tray (a grid of 22 px one-line tiles filled column by column in two rows, a further two-row block underneath when wider than the window; a toggle keeps the tray open; the tray overlays the scope); the wheel over RANGE, CHAR or a BRITE spinner steps it (`StepSpinner`); every action routes through the `.scope` / `.view` dot commands (`ScopeViewModel.RunCommand`) |
| `Views/Scope/Dock/` | The views dock: a 360 px column on the right of the Scope window under the bar, hidden while no open view is docked |
| `Views/Scope/Dock/ScopeDock.axaml(.cs)` | The dock column: the docked views' cards keyed by view name (SSA, LA/CA/MCI, RPO QUEUE, then CRR) |
| `Views/Scope/Dock/ScopeViewCard.axaml(.cs)` | One view's card, in the dock or floating on the scope: the title (a press starts a move), a close button (`.view {name} off`), the dock button `▸` while floating (`.view {name} dock`), and the SSA, LA/CA/MCI, RPO QUEUE and CRR body templates (a row of RPO QUEUE is a button selecting its aircraft; a CRR row takes no click) |
| `Views/Scope/Dock/SsaLines.cs` | Pure: the SSA's two lines in CRC's format (UTC `HHmm/ss` of the session clock, `MainViewModel.SessionNowUtc`, the wall clock with no scenario; altimeter, primary airport; `OK/OK/NA FUSED {range}NM` or the red `NA/NA/NA`) |
| `Views/Scope/Dock/ConflictListRows.cs` | Pure: the LA/CA/MCI rows, one `A*B   CA` per conflict pair with the callsigns sorted (the client holds CA only) |
| `ViewModels/RpoQueueViewModel.cs` | The RPO QUEUE card's view-model (view name `rpoq`): `Rows` from `RpoQueueRows` rebuilt on `MainViewModel.AircraftModelUpdated` and a `Viewer` change while open, the title `PILOT LENS · RPO QUEUE {n}` set from the row count, `SelectCommand` (a row click calls `ScopeViewModel.SelectAircraft`; nothing is sent) |
| `Views/Scope/Dock/RpoQueueRows.cs` | Pure: `RpoQueueRow` (callsign, text) and the rows: each aircraft's `ScopeCheckIn.StateOf` exception first (`{CS} calling {KIND} !`, `{CS} not checked in (VCI lit) !`, `{CS} not checked in (no VCI) !`, `{CS} checked in, VCI not set`), then each `ScopeQueueBadge.PendingLines` line as `{CS} {command} wait`, each group by callsign |
| `ViewModels/CrrViewModel.cs` | The CRR card's view-model (view name `ScopeToolbarButtons.ViewCrrName`, title `CRR`): `Rows` from `CrrRows` rebuilt on `MainViewModel.AircraftModelUpdated`, an aircraft added or removed and a `MainViewModel.CrrGroups` change while open; read-only |
| `Views/Scope/Dock/CrrRows.cs` | Pure: `CrrRow` (text, group colour or null) and the rows: each group's label in its colour, then its members (`EramCrrGroup` equal to the label) as `{callsign,-7}{distance,7}`, nearest first |
| `Views/Scope/Prefs/` | The Scope's saved display state, per facility |
| `Views/Scope/Prefs/ScopePrefSet.cs` | The saved record (name, range, centre, template, brightness, DB settings, STARS ids of the maps on, open views and their places, tear-offs; the profile's `toolbar` and `views` arrays and `kind`, the profile name `.scope pref load` applies; `bands` is reserved and null; `vectorOwnership`; `tdm`; the optional pins) and its JSON options |
| `Views/Scope/Prefs/ScopePrefSetStore.cs` | The per-facility files under `scope/` (`prefsets/<facility>.json` the named sets, `current/<facility>.json` the Scope's own state): a version per file, a bad entry dropped with a warning, a corrupt file copied to `.bak`, no overwrite of a file it could not read, a newer file's version kept (the sets file also keeps its unknown fields; `SaveCurrent` does not); the name rules (1-16 of A-Z 0-9 `-` `_`, uppercase, Windows device names refused) |
| `Views/Scope/Profiles/` | The Scope's profiles (STARS, ERAM, Mixed), room-wide |
| `Views/Scope/Profiles/ScopeZoomBands.cs` | Pure: ScopeBandPins (the values pinned by hand) and ScopeZoomBands (`Resolve` the band a range selects, `Effective` the band with the pins over it) |
| `Views/Scope/Profiles/ScopeProfile.cs` | The profile record (name, kind, bands, template, toolbar, views) and ScopeProfileKind |
| `Views/Scope/Profiles/ScopeProfilePicker.cs` | Pure: KindFor (the automatic pick from the active position), NameOf / TryParseKind, and Order (the bar and the views by a profile's lists; unnamed entries follow in the default order) |
| `Views/Scope/Profiles/ScopeProfileStore.cs` | `scope/profiles/{stars,eram,mixed}.json` under YaatPaths: written from the shipped copies when absent and never overwritten, a bad field falls back to the shipped value, a corrupt file copied to `.bak`; `Shipped(kind)` reads `Resources/ScopeProfiles/*.json` |
| `Views/Scope/ScopeTearoffLayer.cs` | The overlay over the canvas: hosts the button copies and the floating view cards and runs the move and DEL TEAROFF modes; its empty area passes clicks through to the canvas; Esc cancels a move first (the first step of the Scope's Esc order) |
| `ViewModels/ScopeViewModel.Entry.cs` | Partial: typing on the Scope — the entry text, `StageEntry` (a field menu's Shift+row-click text), `StageEramText` (a Shift+click's bare ERAM entry, staged with the clicked track as the pick), the Preview and Feedback state, the Scope's entry history (Up/Down; Ctrl+R recalls the last entry on ERAM/Mixed), the clicked track's callsign (`FillTrack`, `FillTrackAndRun`, a Field A middle click's QF) kept as the ERAM entry's pick until Esc, Ctrl+R, recalling an older history line or sending the entry drops it (Up then back Down past the newest line restores the half-typed entry with its pick), and `RunEntry`, which classifies the entry and runs it through `MainViewModel.SendTextAsync` with the Scope's own `ICommandTextSink`, adding it to the terminal command history; never touches the command line's text or status |
| `Views/Scope/ScopeEntryClassifier.cs` | Pure: reads an entry against the profile kind as a pilot command, a dot command, a STARS or ERAM dialect entry, or none, gives the Preview's kind label, and honours a leading "PILOT " as a forced pilot read; the single place that names a dialect verb; a STARS entry is read by `Entries/StarsEntryParser.cs` (`ScopeViewModel.Entry.cs` sends its translation, and refuses an unsupported STARS shape with "NOT SUPPORTED"). On ERAM and Mixed a first word in `EramEntryVerbs.All` or an implied Table 16 shape (position/leader FLID, `//FLID`, a lone FLID with optional `/OK`, sector then FLID) reads as ERAM, labelled `ERAM <verb>`, before a pilot command is tried; an entry that reads as no pilot command is implied ERAM only when it is a lone FLID or sector then FLID, so a mistyped `UAL238 FHH 270` is none. On Mixed the selected (or clicked) track's owner kind picks the dialect: a STARS-owned track reads as STARS |
| `Views/Scope/Entries/ScopeEramEntryBuilder.cs` | Pure: `Build` turns an ERAM entry and the clicked track's callsign (or null) into the `ScopeEramEntryDto` for `SendScopeEramEntry` (uppercase tokens, never parsed on the client; the last token equal to the clicked callsign is also sent as a `ScopeEramPickDto` unless the entry is an SM, SW or WX entry or the token is at or after the first token holding the free-text marker (the backtick or ERAM's clear-weather character), where it goes as plain text, so the server sees an FDB-track pick while the text stays in `Tokens`, and an edited callsign sends typed; a QS entry's last token, its flight ID, is picked even after its free text), `ResponseTo` turns the server's answer into the Scope response: acceptance feedback lines, a readout's text, or the refusal line |
| `Views/Scope/Entries/ScopeEramKeys.cs` | Internal static: CRC's ERAM keyboard on the Scope. `MapTyped` turns `[` `]` backtick `~` into ERAM's own characters (up arrow, down arrow, clear weather, overcast; `IsEramChar` keeps them where other controls are dropped), kept raw in the entry, history and send because the server finds a message's text by the clear-weather character; `ToDisplay` swaps them for the marks the Preview and response show. Typed only on ERAM and Mixed |
| `Views/Scope/Entries/StarsEntryParser.cs` | Pure: reads a STARS-dialect entry by shape (glued sigils, key words; the F-keys typed as words) into its YAAT command text, a label gloss and a `StarsEntryNeed` for the rows that need the Scope's state (bare `HND`, `*J`/`*P`, bare `MF R`); an unsupported shape reads as not supported |
| `Views/Scope/ScopeEntryLayer.axaml(.cs)` | The overlay that draws the entry areas: on STARS one unboxed Preview area at the top left, under the SSA card's default place, which shows the entry and then its response; on ERAM/Mixed the Response Area above the MCA box (Preview: label and entry with `_`; Feedback: ACCEPT, error or handler text) |
| `Views/Scope/ScopeView.Menus.cs` | Partial of `ScopeView`: `WireMenus` and the handlers for the canvas's menu gestures: Ctrl(Cmd)+click opens the flight-plan editor (`FlightPlanEditorManager.Open`), `EmptySpaceClicked` clears the selection unless the Rings tool is armed, `MapRightClicked` opens the point menu (`ScopeView.PointMenu.cs`) |
| `Views/Scope/ScopeView.PointMenu.cs` | Partial of `ScopeView`: the map right-click menu: `BuildMapContextMenu` (the point menu, then Show all datablocks and "Settings for this view…"), `BuildMapPointMenu` (the shared `AircraftMenuBuilder` point header and quick strip while an aircraft is selected, over the Scope's point section) and `BuildMapPointSection` (FRD and Copy FRD, markers (`AddMarkerItems`: Pin marker here, Remove marker near one, Clear pinned markers), Measure from/to here and Remove measurement / Clear measurements, the MVA row `MvaRow`) |
| `Views/Scope/ScopeCanvas.PointMenu.cs` | Partial of `ScopeCanvas`: `MeasurementSlotAt`, the measurement nearest a click within `MeasurePickRadiusPx`, for the menu's Remove measurement row |
| `Views/Scope/ScopeView.axaml(.cs)` | Hosts ScopeCanvas, the ScopeTearoffLayer and the ScopeEntryLayer; selection, the shared aircraft menu (with Display and Draw route in its view section and "Settings for this view…" last, see `ScopeView.Display.cs`) on right click; a Field-A click on an ERAM block also calls `ScopeViewModel.ToggleDwellLock` (it opens no menu; a left click on any other field with an action opens its pick menu or, on an ERAM altitude, ground speed or line-4 token, its popup, see Field menus; the instructor's client-only lock, `DataBlockViewState.DwellLockedCallsigns`; no hub call); the canvas taking focus makes the Scope the active measure surface; `OnTopLevelKeyDown` takes Esc (in order: cancel a tear-off move, else take the measure prompt and cancel the tool when nothing is typed, else clear the entry and response, else cancel the Scope's measurement, else disarm the Rings tool), Ctrl+M (Cmd+M on macOS; `ScopeViewModel.ArmMeasure`) and Ctrl+Shift+T (Cmd+Shift+T on macOS; flips TDM), and routes the typing keys (Enter, Backspace, Up/Down, Ctrl+R / Cmd+R) to `ScopeViewModel.Entry.cs` while the canvas has focus |
| `Views/Scope/ScopeViewer.cs` | Record ScopeViewer(Acting, EramFacilityId, EramSectorId) + None: the position the Scope is viewed from, bound to ScopeCanvas.Viewer (a change rebuilds the frame) and carried in ScopeDisplayOptions.Viewer |
| `Views/Scope/ScopeBrightness.cs` | Record ScopeBrightness (the BRITE groups, LENS last; `Default`, `Apply` scales a colour's channels, `Lift` is the DWELL rule, the `Groups` table `.scope brite` and the spinners read) and ScopeBrightnessGroup (name, range, step) |
| `Views/Scope/ScopeIntentPath.cs` | The pilot lens's intent path: `ScopeIntentPaths` (`Routes` builds a track's `ScopeIntentRoute`s, the nav route as the Radar's "Show nav route" draws it, from its `DrawnWaypoint`s, shapes and `VectorTail`; `Project` turns them into a screen `ScopeIntentPath` of segments, fixes, shapes and tail; a `ScopeIntentShape` carries `Dashed` and `Arrow` from `NavRouteShapeLook`) and `ScopeIntentPathCache` (per canvas: the routes are rebuilt only when the aircraft's route scalars change, so a frame only projects; `Sweep` drops the tracks no longer drawn) |
| `Views/Scope/ScopeTrackLayers.cs` | ScopeVectorOwnership (Own, All) and its `ScopeVectorOwnerships` text names; the history-dot and vector-line layers the renderer draws after the maps and before the targets (a callsign in `ScopeFrameInput.LocalPtlCallsigns` draws its vector whatever the ownership says) |
| `Views/Scope/ScopeCrrReadout.cs` | ERAM's continuous range readout (CRC `RdbRenderObject`, `RdbOrientation`): `ScopeCompass`, `ScopeRdbOffset`, `ScopeCrrReadoutView` (text, group colour, text box), `Content` (the distance text and group colour for a track drawn with an FDB-level ERAM block, Mixed only when ERAM-owned, whose `EramCrrGroup` is among `ScopeFrameInput.CrrGroups`; else null), `DistanceText` (F1, `>999.9` past 999.9 NM), `ColorOf` (CRC's raw ERAM Green, Coral, Yellow, White) and `Place` (the box by the ported RdbOrientation table from the nearest of 8 compass points of the block offset, exact boundaries clockwise); `ScopeRenderer.DrawCrrReadout` draws it at FDB brightness with the DWELL lift, after each track |
| `Views/Scope/ScopeRingGeometry.cs` | The Rings tool's pure geometry and records: ScopeRingTool (None, JRing, Cone, Dri5, Dri3) and its `ScopeRingTools` words, ScopeDriKind, ScopeTrackRings (a track's J-ring, cone and DRI), the screen shapes the renderer draws, CRC's SIZE grid (`SizeGrid`, `StepSize`, `IsRingSize`, `FormatSize`) and the FL230 limit on a DRI 3 (`AllowsReducedDri`) |
| `Views/Scope/ScopeCanvas.cs` | SkiaSharp canvas (MapCanvasBase): focusable, a click gives it keyboard focus and its text input feeds the entry; left click selects (a field with a `ScopeFieldAction` raises `FieldClicked` instead of `AircraftLeftClicked`; `BlockBoundsOf` gives a track's block bounds for placing its popup), right-drag pans, wheel zooms; hit-tests the same FieldRect list the renderer draws; a pointer move dwells the track under the pointer or previews the VCI it is on (`ScopeFrame.HoverAt` → `ScopeHoverHit`), leaving the canvas releases both, panning holds them; a left click on an ERAM block's callsign raises `AircraftDwellLockToggleRequested` before selecting, `TryMeasurePress` (Alt+left drag; armed left pick and right cancel) comes before that and the fill, raising `MeasurePointPicked`, `MeasureDragCompleted` and `MeasureCancelled` (pinned by `ScopeMeasurePointerTests`), and a frame carries the local lock in `ScopeFrameInput.LocalDwellLockedCallsigns`; `DbSettingsProperty` carries the Scope's DB settings into `BuildFrame` and the hover; `ShowTopDownProperty` carries TDM into `BuildFrame`'s `AircraftVisibility.FilterAircraft` call and into `ScopeFrameInput.ShowTopDown`; `TdmOnlyMapIdsProperty` (from `ScopeViewModel.TdmOnlyMapIds`) makes `DrawnVideoMaps` leave the TDM-only maps out of the frame while TDM is off; `LensProperty` and `LensPathCallsignsProperty` (layer properties) carry the pilot lens into `ScopeFrameInput` (`LensPath`, `LensPathCallsigns`, `NavDb` only while the path draws, `LensQueue`, `QueueHoveredCallsign` from the hovered queued-commands badge, and `LensCheckIn`) with the canvas's `ScopeIntentPathCache` |
| `Views/Scope/ScopeQueueBadge.cs` | The pilot lens's queued-commands badge: `ScopeQueueBadge.PendingLines` (an aircraft's `PendingCommands` lines, verbatim and in order, without the `[Active]` ones) and `SymbolGapPx`, and the `ScopeQueueBadgeView` a track carries (count, lines, text box, hovered) |
| `Views/Scope/ScopeCheckIn.cs` | The pilot lens's check-in truth: `ScopeCheckIn.StateOf` (an aircraft's `ScopeCheckInState` as a viewer sees it, the highest that holds: Calling, VciMismatch, NotCheckedIn, VciUnset, else None), `LabelOf` (the tag text) and `SymbolGapPx` |
| `Views/Scope/ScopeRenderer.cs` | Stateless renderer: video maps, targets, leaders, STARS blocks, ERAM blocks or EuroScope tags in the radar's line style (ERAM altitude arrows drawn as SKPaths); also holds ScopeFrame, ScopeFrameInput (with `LocalRings`, `HoveredCallsign`, `VciHoveredCallsign`, `DefaultLevel`, `ShowTopDown`), ScopeTrack (`Level`, `ShowsLeader`, `IsDwelled`, `Dwelled`, `IsDwellLocked` (the instructor's or the student's dwell lock, the pick tier; hover alone is not), `DwellBox`, `IntentPath` for the selected or lens-toggled track while the path draws, `QueueBadge` while the queue draws, `CheckInTag` while the check-in draws; the lens layer draws after the blocks and before the measure lines, in magenta at the LENS group), `LevelFor` (Ground for a ground target), `ScopeFrame.HoverAt` (`DwellAt` is its dwell half), `ScopeFrame.IndicatorPickAt` (the HSF indicator or VCI cell a left press picks, a `ScopeIndicatorPick`; it and the hover share `IndicatorsAt`), ScopeRenderSnapshot and ScopePalette (the user tint and selected colours the render thread resolves block and symbol colours from) |
| `Views/Scope/ScopeFieldSlot.cs` | Enum of the fixed named datablock slots a template places and a field fills (incl. `AltitudeMismatch`, `OtherAltitude`, `AircraftType`, the ERAM line-4 slots `Line4FreeText`, `Line4Heading`, `Line4Pad`, `Line4Speed` and the fallback `Line4Notes`), and `ScopeFieldSlotExtensions.IsLine4` |
| `Views/Scope/ScopeOwnerKind.cs` | Static: `TemplateIdFor` / `Resolve` map a track's owner sector code (`C…` ERAM, leading digit STARS, none ERAM) to the template a Mixed pick draws it with |
| `Views/Scope/TrackPresentation.cs` | ScopeField (slot, text, stable text, token, action, style; the action is `TrackPresenter.ActionFor`'s), TrackPresentation (template id, block level, block colour role, tint role, fields) and the ScopeBlockLevel / ScopeBlockColor / ScopeTintRole / ScopeColorToken / ScopeFieldAction (including `ToggleHsf` and `ToggleVci`) enums and the [Flags] ScopeFieldStyle (DimTail, Strikethrough, ArrowUp, ArrowDown, HsfIndicator, Vci, AltitudeMismatch) |
| `Views/Scope/EramStatusLine.cs` | Internal static: the ERAM line-3 status text (`SpcText`, the special-code label the Limited and Partial blocks also show) by strict priority (special-purpose code, handoff, recently accepted for the viewer, beacon mismatch / NONE, FRZN, CST) and whether it blinks; also the viewer's ERAM sector fallback from a C-prefixed TCP |
| `Views/Scope/EramColumn0.cs` | Internal static: the ERAM column-0 predicates (the VCI lit when the viewer's ERAM sector is on frequency for the track; `R` when a viewer is known and the track, unowned included, is not its own) |
| `Views/Scope/EramDwellLock.cs` | Internal static: the student's ERAM dwell lock as the viewer sees it (`IsDwellLocked`: the track's locked-sector list holds the viewer's ERAM facility and sector; `IsFourthLineSuppressed`: the same test on the fourth-line suppressed list; `HasEramSector`) |
| `Views/Scope/EramLine4.cs` | Internal static: the ERAM line-4 text (HSF free text, else heading and speed, else type and suffix, else a short destination; a suppressed track skips the HSF part) as `Parts`, a `Line4Parts` of one token per click region (`Text` joins them), and the HSF-indicator test |
| `Views/Scope/TrackPresenter.cs` | Pure (`ActionFor`: each field's `ScopeFieldAction` per template and slot; `EramActionFor` maps the ERAM HSF indicator to `ToggleHsf` and the VCI cell to `ToggleVci`): an AircraftModel + template id + ATPA lead/conflict peer + flash phase + selection + the MVA relation ScopeFrame.Build computed (TrackPresenterInput, ScopeAlertOptions, ScopeDisplayOptions) -> the TrackPresentation of slotted fields, the field set chosen by block level (`TrackPresenterInput.Level`; `EramFieldsBAndC` for the ERAM Partial block; `GroundFields` for the Ground level on STARS and ERAM; `EramAltitudeDiffers` for the altitude-mismatch mark) |
| `Views/Scope/ScopeTemplates.cs` | The C# templates by id ("stars", "eram", "euroscope"; `MixedId` is a routing id that `Get` rejects, resolved per track by `ScopeOwnerKind`): ScopeTemplate lines of ScopeSlotPlacement, with `LimitedLines` / `PartialLines` / `LinesAt(level)` for the collapsed levels `DwellLines` for the extra dwell line and `GroundLines` for the ground target's two lines on STARS and ERAM |
| `Views/Scope/DatablockTemplate.cs` | The engine: lays a TrackPresentation into a template's lines and returns one ScopeBlockLayout (bounds with column 0, text bounds, FieldRect list, `VciCell` the ERAM FDB's VCI cell, the hover cell and the click target), the single list drawing and hit-testing will read |
| `Views/Scope/FieldRect.cs` | One laid-out field: slot, rect, text, color token, style, and the `ScopeFieldAction` its presenter set |
| `Views/Scope/ScopeEntryFill.cs` | Internal static: the text a click fills into the preview: `Track` (a pilot entry gets the callsign first after `PILOT` and `**`, replacing a known callsign there; any other entry gets it appended), `Point` (the FRD, else ERAM `//DDMMN/DDDMMW`), `ChangesTrack` (false when the entry already names the track, so the click acts as with an empty preview) |
| `Views/Scope/ScopeMiddleClick.cs` | Internal static: `OnTrack` / `OnMap` pick the `ScopeMiddleClickAction` of a middle click (ERAM block fills the track and runs; with an empty preview Field A runs `QF` and an HSF indicator, VCI cell or field with an action does what a plain left click does (`ClickField`); the press's `ScopeMiddleHit` carries Field A and the field action; STARS and EuroScope blocks toggle the highlight, the map fills the point and runs on ERAM or Mixed with text; None when the Rings tool is armed) |
| `Views/Scope/FieldMenus/ScopeFieldMenus.cs` | Static: `Build(action, aircraft, host)` the pick menu a field click opens (one entry from `MenuCatalog` with its submenu open, the handoff entry by the track's handoff state, the assigned runway's rows built here; null when the action opens no menu or the entry does not apply), `RecordShift` (whether Shift is held at each press, release and key inside a menu or a popup's content) and `ShowNoteBox` |
| `Views/Scope/FieldMenus/ScopeDataAltitudePopup.cs` | Internal: the ERAM altitude popup, about 640 px wide, two columns: SCOPE DATA (an altitude box, the FP altitude, Interim, Local interim, Procedure, Interim + CERA and CERA only rows, then the clear rows, each showing its entry, the altitude rows disabled and elided while the box is empty) beside PILOT (the Maintain altitude panel from `AltitudeFlyout.MaintainPanelFactory`); a pick calls back with the entry, Shift is recorded through `ScopeFieldMenus.RecordShift` |
| `Views/Scope/FieldMenus/ScopeDataPopupBase.cs` | Internal abstract: the chrome the SCOPE DATA popups share (the two-column panel, the SCOPE DATA and PILOT heads, the entry boxes, set and clear rows, the live tick, opening below the clicked block (Avalonia's anchor-and-gravity placement on `ScopeCanvas.BlockBoundsOf`, flipping above or sliding left to stay in the window, and moved beside the block on opening when neither below nor above has room), closing) |
| `Views/Scope/FieldMenus/ScopeDataHsfPopup.cs` | Internal: the ERAM fourth-line popups by `HsfPopupKind` (`Heading`, `Speed`, `FreeText`, `Combined`): SCOPE DATA sections with a box opening on the stored value, Set and Clear rows from `ScopeDataEntries`, beside the PILOT panels from `HeadingFlyout.FlyPanelFactory` and `SpeedFlyout.AssignPanelFactory` (none for free text); a Set row is enabled while the box parses (`EramEntryEngine.ParseHsfHeading`, `ParseHsfSpeed`, `IsQsFreeTextCharacter`, one to eight characters), a Clear row while its value is stored, following the aircraft once a second |
| `Views/Scope/FieldMenus/ScopeDataEntries.cs` | Pure: the ERAM scope-data entry texts the field menus and the HSF and VCI clicks run: the QZ, QQ and QR altitude entries (3-digit altitudes, QQ's L, P and R forms), the QS heading, speed, clears, free-text and toggle entries, and `QN // {callsign}` for the VCI (`ToggleHsf`, `ToggleVci`); `ClearCera` is `QR 000 {callsign}`, and the altitude popup's rows take their entries from here |
| `Views/Scope/FieldMenus/ScopeEntryStaging.cs` | Pure: `Text(preview, callsign, command)`, what a Shift+row-click stages in the Scope preview: `PILOT {callsign} {command}`, or `, {command}` appended when the preview already holds a PILOT entry for that aircraft |

## YAAT Scope engine

Beside the Radar layouts, `Views/Scope/` holds the YAAT Scope datablock engine (plan: [plans/yaat-scope/README.md](plans/yaat-scope/README.md)).

The engine is pure; the window around it (`ScopeWindow` → `ScopeView` → `ScopeCanvas` over `ScopeRenderer`, view-model `ScopeViewModel`) is the main window's Scope tab, or its pop-out window (View > Windows > Sco_pe), and draws video maps, targets, leaders and STARS blocks, ERAM blocks or EuroScope tags in the radar's line style.

The scope's toolbar (`Views/Scope/Toolbar/`: `ScopeToolbarButtons` is the data-driven button model, state in `ScopeViewModel.Toolbar.cs`) has a TEMPLATE tray (`ScopeViewModel.TemplateId`, `PickTemplateCommand`) that picks among them or Mixed, starts on the room's profile's template (STARS, EuroScope when the EuroScope-mode preference is on, until the room has a profile) and is never written back to the preferences.

A RANGE button, which the wheel steps, follows the TEMPLATE tray.

Each toolbar action is also a dot command (`.scope` through `MainViewModel.TryHandleScopeCommand`, `.view` through `TryHandleViewCommand`), carried on the button (`ScopeButton.Command`) and run by `ScopeViewModel.RunCommand`.

The bar reads PROFILE (first), TEMPLATE, RANGE, OTHERS, VECTOR, HIST, TOOLS, AUTO, MAPS, TDM, MVA, DISPLAY, DB, VIEWS, PILOT LENS, BRITE, PREF, DEL TEAROFF (see the next two paragraphs for MAPS and DB).

TDM is top-down mode: a bar-level toggle (no tray) lit by `ScopeViewModel.IsTdm`, which a press flips through `.scope tdm on|off` after closing any open tray (`TryPressFlip`). `IsTdm` reaches `ScopeCanvas.ShowTopDownProperty`, which drops the kept frame, and `BuildFrame` passes it as `showTopDown` to `AircraftVisibility.FilterAircraft`, so on-ground and below-the-floor aircraft draw and hit-test only while it is on. Airborne aircraft below the floor and every EuroScope track keep the ordinary block; on STARS and ERAM (Mixed when it resolves to them) an on-ground aircraft takes the ground-target block (`ScopeBlockLevel.Ground`, `ScopeTemplate.GroundLines`): line 1 the aircraft ID, line 2 the destination when it has a flight plan, else the beacon code, then the pilot's connection type (`KATL A321`, `4521 A321`, `A321`), the same at every zoom level and on dwell (a hover or dwell lock still draws the dwell box and lifts the block on top; the leader still draws). Maps flagged `TdmOnly` (`ScopeMapToggle.TdmOnly`, `ScopeViewModel.TdmOnlyMapIds`) draw only while TDM is on, even with their toggle on; the MAPS tray, `.scope map`, CLR ALL and pref sets are unchanged. It is a saved display setting (`ScopePrefSet.Tdm`, `tdm`; a file without it loads off), not banded and not cleared by AUTO. Ctrl+Shift+T (Cmd+Shift+T on macOS) in the Scope window runs the same verb from `ScopeView.OnTopLevelKeyDown`; plain Ctrl+T does nothing there, and the chord is not in `WindowHotkeys`, whose fixed chords feed the Settings clash check against the default take-control key (Ctrl+T).

VIEWS toggles the views dock (`Views/Scope/Dock/`: `ScopeDock` and its `ScopeViewCard`s, state in `ScopeViewModel.Views.cs`), a 360 px column right of the scope that hides while no view is open.

The dock holds the SSA card (`SsaViewModel`, text from `SsaLines`, refreshed every second) and the LA/CA/MCI card (`ConflictListViewModel`, rows from `ConflictListRows` over `MainViewModel.ConflictAlerts`, so CA only and whatever `ShowConflictAlerts` says) and, third, the RPO QUEUE card (`RpoQueueViewModel`, closed by default, rows from `RpoQueueRows`: the check-in exceptions by the CHECKIN tag's `ScopeCheckIn` rules, then the queued commands still waiting on a trigger) and, fourth, the CRR card (`CrrViewModel`, closed by default and read-only, rows from `CrrRows` over `MainViewModel.CrrGroups`). Opening or closing the dock keeps the scope's scale, so the range follows the scope's width as a window resize does.

MAPS: the Scope keeps its own `ScopeViewModel.MapToggles`, `ScopeMapToggle`s built by `SeedMaps` from `MainViewModel.ScopeMaps.Maps` (`ScopeMapInfo`: id, STARS id, short and full name, brightness category) at every facility load, in STARS id order, each on only when a toggle with its map id was on before (so the first seed opens with every map off); toggling one (`SetMapOn` by STARS id, `ClearMaps`, from `.scope map`) never touches the Radar, and the map data comes from `MainViewModel.GetCachedVideoMap`. A scenario clear (`ScopeMapSource.Cleared`) empties the toggles and maps through `ClearSeededMaps`. The tray (`TrayChildren`: CLR ALL, then one toggle per map labelled `{StarsId} {ShortName}` in STARS id order) is a two-row grid like every tray, overlays the scope and never resizes it.

DB: `ScopeViewModel.DbSettings` is a `ScopeDbSettings` record (`ScopeViewModel.Display.cs`) of four settings, changed through `SetDbToggle` and `SetDbCharSize` (`.scope db`) and carried by `ScopeCanvas.DbSettingsProperty` into `BuildFrame` and `Hover`. 4TH LINE off drops ERAM line 4's type and destination (the HSF notes stay); DWELL off stops the hover dwell (a locked block still dwells, and the VCI preview still follows the pointer); ATPA DIST shows or hides the ATPA field; CHAR is the datablock point size (8 to 24). ATPA DIST and CHAR are seeded from the preferences when the Scope opens and never written back, so a later preference change does not reach an open Scope. LDR DIR, LDR, CWT, REQ ALT and PORTAL are not built (`.scope db` answers `Unknown scope DB item`).

Brightness (BRITE, a toolbar menu) is `ScopeViewModel.Brightness`, a `ScopeBrightness` that opens at `ScopeBrightness.Default`, changes only through `SetBrightness` (from `.scope brite <group> <value>`, which the BRITE spinners run) and is never sent; it is kept in a pref set and the Scope's saved state.

`ScopeCanvas.BrightnessProperty` carries it into `ScopeRenderSnapshot.Brightness`, so the render thread reads a copy.

Its groups (`ScopeBrightness.Groups`: name, range, step, getter, setter) are MAP A and MAP B (the video maps by A/B category, through `VideoMapRenderer.BrightnessA` / `BrightnessB`), TARGETS (the position symbol), FDB (the leader, the dwell box and every field that is not an alert), ALERTS (fields whose token is `ConflictAlert`, `NoLandingClearance`, `AtpaWarning` or `AtpaAlert`), HIST (the history dots) and VECTOR (the vector lines), RINGS (the Rings tool's rings and the ATPA cones) and LENS (the pilot lens's intent paths; last in the tray), all default 100 and never lifted by DWELL, and the two modifiers LINE 4 (added to FDB for the `Line4Notes` field, clamped to 0 to 100) and DWELL.

The Rings tool (TOOLS, `ScopeViewModel.Tools.cs`): opening the tray arms the band's `rings` kind (J-ring for `jring`, DRI 5 for `dri`), a kind button arms its kind and a second press of the lit one disarms, as do Esc, closing the tray and opening another. While armed, a left click on a track runs `.scope rings` for it (place, resize or remove) and does not select it. SIZE steps 1.0 to 9.9 in tenths then 10 to 30 whole, default 3.0. The ring layer draws after the video maps and before the history dots: J-rings as map-anchored circles with the size label opposite the leader, cones along the ground track at plus and minus 2 degrees with a mid-axis label on a black box, DRI 5 as a circle and DRI 3 as four arcs with gaps at N, E, S and W (refused above FL230). When ATPA DIST is on, a track's ATPA cone (blue monitor, yellow warning, orange alert) is drawn there too and hides that track's manual cone; its J-ring still draws. Rings are never saved or sent, and are cleared with their aircraft and on scenario unload, restart and rewind.

PTLs (`ScopeViewModel.Ptl.cs`): `.scope ptl <callsign>` toggles a track's PTL and `.scope ptl clear [callsign]` removes one or all; a STARS `MF R [callsign]` runs the toggle. A track with a PTL draws its vector whatever VECTOR OWN or ALL says, at the VECTOR length (0 minutes or a stationary track draws none). PTLs are the instructor's own overlay, never saved or sent, and are cleared with their aircraft and on scenario unload, restart and rewind.

The pilot lens (`ScopeViewModel.Lens.cs`): `.scope lens on|off` sets `ScopeLensSettings.Enabled`, `.scope lens path on|off` its `Path`, `.scope lens queue on|off` its `Queue` and `.scope lens checkin on|off` its `CheckIn` (`MainViewModel.TryHandleScopeCommand`, echoes `Scope lens ON`, `Scope lens path ON`, `Scope lens queue ON`, `Scope lens checkin ON`), and `.scope lens path <callsign>` toggles that track in `LensPathCallsigns`. An overlay draws only while the lens and its own switch are on (`ScopeLensSettings.DrawsPath`). `ScopeRenderer.ScopeFrame.Build` asks `ScopeIntentPathCache.PathFor` for the intent path of the selected track and each toggled one, on every drawn track whatever its block level; the routes come from `ShownRouteBuilder`, the Radar's "Show nav route" builder, through the same `DrawnWaypoint` and `VectorTail` records (straight legs, no predicted turn arcs); the cache keys only on the fix list and the assigned heading, and the builder reads no CIFP. The renderer draws it after the blocks and before the measure layer, in magenta scaled by the LENS group, so LENS 0 draws nothing: a dashed leader from the symbol, solid legs, diamonds with the fix name and crossing restrictions (a latent token dimmed), the server's shapes (looks per kind in the Nav-route overlay wire contract) and a dashed vector tail. The queued-commands badge (`ScopeQueueBadge`) draws only while the lens and QUEUE are on: `ScopeFrame.Build` gives every drawn track whose `PendingCommands` holds a line that is not `[Active]` a `ScopeTrack.QueueBadge`, the count in magenta at the LENS group (LENS 0 draws nothing) in a text box 6 px left of and 6 px below the symbol, and a hovered badge's lines draw verbatim with their `[n]` prefix on a dark box below it, over every badge; it has no click action. The check-in tag (`ScopeCheckIn`) draws only while the lens and CHECKIN are on: `ScopeFrame.Build` sets `ScopeTrack.CheckInTag` from `ScopeCheckIn.StateOf` (the aircraft's `PendingPilotRequest` and `HasMadeInitialContact` against the viewer's VCI, `EramColumn0.VciLit`, and ownership, `EramColumn0.IsViewersTrack`) and draws it as one magenta text at the LENS group (LENS 0 draws nothing), its left edge 6 px right of and its top 6 px below the symbol: `CALL <KIND>` for an open request, else `VCI NO CALL` (VCI lit, no initial contact), else `NO CALL` (viewer's track, no initial contact), else `VCI UNSET` (ERAM viewer's track, initial contact, VCI not lit); it has no hover or click. A track handed to the viewer whose pilot already called the previous sector shows `VCI UNSET`, since `HasMadeInitialContact` is not per sector. The switches are `ScopePrefSet.Lens` (`lens`: `enabled`, `path`, `queue`, `checkIn`; a set without it, or without `queue` or `checkIn`, loads off) and BRITE's `lens` key; the toggled tracks are never saved or sent.

The measure layer (`ScopeViewModel.Measure.cs`): lines tagged `RblView.Scope` in the shared `RangeBearingViewState` store (the 15-slot pool; Radar and Ground lines never draw here, and Scope lines draw nowhere else) are bound into `ScopeCanvas` (`RangeBearingLinesProperty`, `MeasureAnchorProperty`, `IsMeasuringProperty`), resolved per frame against the traffic the canvas draws and carried in `ScopeRenderSnapshot`. `ScopeRenderer` draws them last, after the tracks, through the shared `RangeBearingRenderer`, white and not scaled by BRITE. Readouts are placed by `RblReadoutPlacement` against every block and symbol of the frame (`ScopeCanvas.BuildRblObstacles`); the Scope deconflicts no block, so a readout never nudges one. Ctrl+M arms the tool (global, like the main window's) and writes the prompt to the Scope's response; a typed `.rbl A B` lands here only while the Scope is the active measure surface (`MainViewModel.ActiveMeasureView`). Scope lines are never saved and survive the Scope's closing and its unload, restart and rewind clear. Points are placed by pointer through `ScopeCanvas.TryMeasurePress`, the step of `OnPointerPressed` after the middle-click one and ahead of the fill, the Rings tool and every aircraft rung: Alt+left records the endpoint under the press (`MeasureEndpointAt`: an aircraft's block or target latches, else the map point, unlabelled) and the release (`OnPointerReleased`) places the line when it is past 5 px (`MeasureDragCompleted`) or anchors it (`MeasurePointPicked`) when it is not, leaving the tool armed; while armed (`IsMeasuring`) a left press raises `MeasurePointPicked` and a right press `MeasureCancelled`, opening no menu and starting no pan. `ScopeView` wires the three events to `ScopeViewModel.PickMeasurePoint`, `PlaceMeasurement` and `CancelMeasureFromPointer`, each of which makes the Scope the active measure surface first; the status they report goes to the status line and the Scope's response (an Error for a full store), and while the tool stays armed the response is the tool's prompt that Esc takes. Every pointer move records `MeasureCursor`, and `ResolveMeasurements` previews the half-placed line (the Alt-drag in flight, else an anchor picked on the Scope) to it.

The draw-route layer (`ScopeViewModel.DrawRoute.cs`, `ScopeCanvas.DrawRoute.cs`, `ScopeView.DrawRoute.cs`): the Radar's draw-route mode on the Scope, over the same `DrawRouteState`. The aircraft menu's Draw route and the EuroScope Destination field (`ScopeFieldAction.StartDrawRoute`) start it for an aircraft `ScopeView.AcceptsFlightCommands` allows; the canvas's draw-route press rung sits ahead of the middle-button and measuring-tool rungs and leaves Alt+left and the armed tool to the measure rung; the route reaches the render thread as `ScopeOverlays.DrawRoute` and `ScopeDrawRouteRenderer` draws it. Enter and Backspace act ahead of the entry, and the Esc chain cancels the route (or only closes the condition popup) before it clears the entry. Starting turns the Rings tool off.

The min-sep layer (`ScopeViewModel.MinSep.cs`): one pair of tracks, the Scope's own state outside the measure pool, never saved. The STARS `MIN` entry (`StarsEntryParser`, `StarsEntryNeed.MinimumSeparation`) runs on the Scope and sends nothing: `RunEntryAsync` hands it to `TryRunMinSep`, which clears the pair (bare `MIN`), answers `Must click two tracks.` (one track), or sets the pair (two tracks).

A left click on a track while the preview holds `MIN` and one track is the second track (`CompletesMinSep`, taken by `FillsOnLeftClick` even on the first track again) and runs the entry at once (`FillTrackFromClick`).

The pair subscribes to both aircraft and recomputes `Yaat.Sim.MinimumSeparation` (CRC's stepped projection: 5 s steps, at most 760, until the distance stops shrinking; planar degrees, 60 NM a degree) on each position, ground speed or track change; an invalid result, either aircraft leaving the traffic, `ClearTrackState` and `Dispose` take it down.

The result is bound into `ScopeCanvas` (`MinSepProperty`), carried in `ScopeRenderSnapshot.MinSep` and drawn by `MinSepRenderer` right after the measure lines, white, unscaled by BRITE, at the measure readout's size; CRC takes the min-sep brightness from its TLS control, which the Scope has no group for. The marker at a predicted point is a small filled dot: the Scope draws text in a monospace system font, which has no glyph for CRC's 0x10 marker.

`ScopeRenderer.DrawTrack` colours each element by `ScopeBrightness.Apply(color, percent)`, which scales each channel by percent/100, truncated, and keeps alpha as CRC's `RenderUtils.ApplyColorBrightness` does; `FieldPercent` picks a field's group. For a dwelled track every percent first goes through `ScopeBrightness.Lift`, CRC's rule `p + round(D/20 × (100 − p))`, so DWELL 20 draws a dwelled track at full brightness and DWELL 0 leaves it at its groups.

The selection border is never scaled. The defaults draw today's pixels.

PREF SETS: `ScopePrefSet` (`Views/Scope/Prefs/ScopePrefSet.cs`, with its JSON options) is the saved record: name, range, centre, template, brightness, DB settings, the STARS ids of the maps that are on, the open views and every view's place, and the tear-offs, plus the profile's `toolbar` and `views` arrays, `kind` (the profile name; `.scope pref load` applies it, the saved-state restore leaves the profile alone, and null in a set from an older build changes nothing) and `bands`, reserved: written null and ignored on load. It also carries the optional pins `pinOthersLevel`, `pinVectorMin`, `pinHistory` and `pinRings`: an unpinned value is not saved, and a bad value on load (a vector other than 0, 1, 2, 4 or 8 minutes, a history outside 0 to 10) is kept as the Scope's current one with a warning. `vectorOwnership` (own or all; missing means own) is saved always.

The dwell locks, the MVA tint, the selection and the open menu are not saved. `ScopePrefSetStore` keeps two files per facility under `YaatPaths` `scope/`: `prefsets/<facility>.json` (the named sets) and `current/<facility>.json` (the Scope's own state, the same record). It versions each file, drops a bad entry with a warning, copies a corrupt file to `.bak`, refuses to overwrite a file it could not read, and keeps a newer file's version; the sets file also keeps its unknown fields on a write, while `SaveCurrent` writes the current file with none of them (the file's version only). Neither file is in the settings bundle.

`ScopeViewModel.PrefSets.cs` holds `CapturePrefSet`, `ApplyPrefSet` (a load replaces the state: views open exactly as saved through `RestoreViewPlace`, tear-offs are replaced, an out-of-range value keeps the current one, maps the scope lacks are skipped and counted) and `ActivePrefSet` and `PrefSetNames`.

A load while the Scope waits for the Radar's maps is held as a pending set and applied once the Scope seeds from the Radar (`ApplyPendingPrefSet`).

The viewer's facility keys the files: the state is saved when the primary Scope's pop-out window closes, at app exit (`DisposePrimaryScopeAtExit`) and, in `SyncStateFacility`, for the old facility when the viewer changes facility, and restored when the Scope starts for a facility or the viewer moves to it, with the active set's name when that set still exists; a facility with no saved state keeps the screen and clears the active set. `ScopeViewModel(MainViewModel main, ScopePrefSetStore? prefStore)` takes the store, and null means no pref sets.

The PREF button (`ScopeButtonId.Pref`; tray: `PrefSet` per set, then `PrefSave`, `PrefSaveAs`, `PrefRename`, `PrefDelete`) shows `PREF` and the active name, and is a half tile under BRITE's pair until a set is active, when it becomes a full tile (`PREF` over the set name) and BRITE a full tile alone. Its clicks run `.scope pref` through `MainViewModel.TryHandleScopeCommand`; SAVE AS and RENAME type the start of the command into the command box instead. `ScopeToolbarButtons.CanTearOff` is false for the set buttons only.

PROFILES (`Views/Scope/Profiles/`): a `ScopeProfile` (name, kind, bands, template, toolbar, views) is a display file for STARS, ERAM or Mixed (`ScopeProfileKind`). `ScopeProfileStore` reads `scope/profiles/{stars,eram,mixed}.json` under `YaatPaths`, writes the shipped copy (`Resources/ScopeProfiles/*.json`) when a file is absent and never overwrites one; a bad field, a band list that does not bind or a null template falls back to the shipped value with a warning, and a file that does not parse (or holds a field of the wrong JSON type) is renamed to `.bak` and the shipped copy written in its place. `bands` (maxNm, othersLevel, vectorMin, history, rings) drive the Scope's others' level: `ScopeZoomBands.Resolve` picks the first band whose `maxNm` is null or at least the range (inclusive), and `ScopeZoomBands.Effective` lays the pins (`ScopeBandPins`, the values set by hand) over it; vectorMin and history are drawn (VECTOR and HIST, below); rings is resolved but drawn by nothing yet. `ScopeViewModel.Bands.cs` holds `Pins` and `EffectiveBand` (recomputed when the range, the profile or a pin changes), `PinOthersLevel`, `FlippedOthersLevel` and the clear that `.scope auto` runs; a profile change keeps the pins. The canvas draws a track the viewer does not own at the band's level (ERAM Partial, STARS Limited, EuroScope unchanged); with no viewer every track is Full, and Dwell and the viewer's own track stay Full. The OTHERS button (`OTHERS FULL` or `OTHERS PARTIAL`, lit while pinned) flips and pins the level, and AUTO (lit while any pin is set) runs `.scope auto`; the shipped profiles' `toolbar` lists "others", "vector", "hist" and "auto" after "range". `.scope others <full|partial>` echoes `Scope others FULL (pinned)` and a bad word `Usage: .scope others <full|partial>`; `.scope auto` echoes `Scope AUTO`. VECTOR (`VECTOR 1m OWN`) steps 0, 1, 2, 4 and 8 minutes on the wheel and pins the value; a click flips `ScopeViewModel.VectorOwnership` between OWN and ALL and never pins. HIST (`HIST 5`) steps 0 to 10 dots on the wheel and pins. VECTOR and HIST are lit while their own value is pinned (the OWN/ALL flip lights nothing). `.scope vector <0|1|2|4|8>` echoes `Scope vector 2 MIN (pinned)`, `.scope vector own|all` echoes `Scope vector OWN` / `ALL`, `.scope hist <0-10>` echoes `Scope history 5 (pinned)`; a bad value answers `Usage: .scope vector <0|1|2|4|8> | .scope vector <own|all>` or `Usage: .scope hist <0-10>`. `vectorOwnership` (own or all, missing means own) is saved in a pref set and the saved state; `ScopeTrackLayers` draws the history dots (alpha ramping to the newest) on every displayed track and a white vector line along the ground track (ground speed times the minutes; none at 0 minutes or under 1 kt; OWN is the viewer's tracks, every track with no viewer), after the maps and before the targets. `toolbar` and `views` order the bar (by `ScopeToolbarButtons.NameOf`) and the views list; entries they do not name follow in the default order and unknown names are skipped with one warning (`ScopeProfilePicker.Order`).

The PROFILE menu (`ScopeButtonId.Profile`; tray `ProfileStars`, `ProfileEram`, `ProfileMixed`) is first on the bar and shows `PROFILE` and the kind. Its picks run `.scope profile stars|eram|mixed` (`MainViewModel.PickScopeProfile`, echo `Scope profile ERAM`; a bad id answers `Unknown scope profile '<id>'`, a missing one `Usage: .scope profile <stars|eram|mixed>`).

The profile belongs to the room, not the window: `MainViewModel.ScopeProfile` is picked once, when the student position first arrives (`OnActivePositionForScopeProfile`): the profile picked by hand for the room's student position type (`UserPreferences.GetScopeProfileOverride`, the `scopeProfileByPositionType` preference keyed GND/TWR/APP/CTR, excluded from the settings bundle), else `ScopeProfilePicker.KindFor` (an ERAM sector is ERAM, a TRACON or ATCT position STARS, else Mixed). A later position change keeps it and a cleared position resets it. A hand pick (`PickScopeProfileByHand`) saves the override; a pref-set load switches the profile without saving one.

`ScopeViewModel.Profile.cs` follows `MainViewModel.ScopeProfile`: it orders the bar (`ToolbarButtons`) and the views and picks the profile's template, unless the viewer's facility has a saved state, whose template stands. A Scope opened before the room has a profile shows Mixed's order and keeps its opening template.

TEAR-OFFS: `ScopeViewModel.Tearoffs` (`ScopeViewModel.Tearoffs.cs`) holds one `ScopeTearoff` (a `ScopeButtonKey` and a `ScopePlacement`: corner and dx/dy from it) per torn-off button, where a map toggle is its own key by STARS id; `PlaceTearoff` and `DeleteTearoffs` are what `.scope tearoff` calls, and the bar's last button, DEL TEAROFF, enters the delete mode that marks copies (`ToggleTearoffMark`) and deletes them on its next press.

`ScopeButtonControl` is the one button every bar, tray and copy builds: the tile: the Button plus a 4 px strip over its left edge, the tile's full height, whose press starts the copy. A copy keeps its tile shape from drag to drop: a tray child's copy is a half tile, and a bar button's copy takes the shape the bar's pairing gives it (`ScopeToolbar.HalfTiles`, the one rule the bar and the copies share), each at its own content width; the drop places it at the dragged copy's measured size.

`ScopeTearoffLayer` is the overlay over the canvas in `ScopeView`: it hosts the button copies and the floating view cards (`ScopeViewCard`, from `ScopeViewModel.FloatingViews`), runs the placing and move modes (the item follows the pointer, a click drops it at the nearest corner, Esc cancels) and passes clicks in its empty area through to the canvas; a window resize keeps each item's corner distance.

A view's place is `ScopeDockViewModel.Place` (`SetViewPlace`; null means docked) and survives closing and reopening it; the SSA opens with a top-left place 8 px in, so the dock column (`HasDockedViews`) stays hidden until a view is docked.

`TrackPresenter` turns an `AircraftModel` (plus template id, ATPA lead/conflict peer, flash phase, selection) into a `TrackPresentation` of slotted fields; `DatablockTemplate` lays that into a template's lines (`ScopeTemplates`: "stars", "eram", "euroscope") and returns one `ScopeBlockLayout` of the bounds and one `FieldRect` list (each field's slot, rect, text, color token and `ScopeFieldStyle`), which drawing and hit-testing are to share, so the draw-vs-hit-test duplication below does not recur there: `ScopeCanvas` hit-tests the same `ScopeRenderer` frame it draws (`ScopeFrame`, built from `ScopeFrameInput` into a `ScopeRenderSnapshot`; the render thread reads no StyledProperty).

A STARS hit finds a flashing reserved field in both blink phases, EuroScope only in the on phase. Left click selects the aircraft, and on a field with an action also opens that field's pick menu (Field menus below), as does a middle click on an ERAM block with an empty preview; Ctrl (Cmd on macOS) with a left click on a target, block or indicator opens the flight-plan editor instead (`AircraftCtrlClicked`; `PressMenuGesture`, below the fill and Shift-hide rungs, so Ctrl+Shift leaves the press to the hide); a left press on empty map clears the selection (`EmptySpaceClicked`, `PressEmptyMap`, after the fill, measuring and Rings rungs, and not while the Rings tool is armed); right click opens the shared aircraft menu, or, over two or more stacked targets, the shared `RightClickPicker` first (`FindRightClickTargets`, the same order as the Radar), right-drag pans, the wheel zooms.

With text in the preview (Rings tool not armed) `ScopeCanvas.FillsOnLeftClick` makes a left press raise `AircraftFillRequested` (or `MapFillRequested` on the map) instead of any other left-click event: `ScopeView` fills the track (`ScopeViewModel.FillTrack`, `ScopeEntryFill.Track`) and selects it, or fills the point (`FillPoint`); no menu, no dwell-lock toggle. A track the entry already names (`ScopeEntryFill.ChangesTrack` false) takes the ordinary left-click path.

A middle press raises `AircraftMiddleClicked` (with a `ScopeMiddleHit`: whether it is on the ERAM Field A, and the field action under it, an HSF indicator or VCI cell first) or `MapMiddleClicked`; `ScopeView` runs the row `ScopeMiddleClick` picks (`ToggleHighlight`, `FillTrack` then `ExecuteEntry`, `RunQuickFlight`, `FillPoint` then `ExecuteEntry`). The highlight is the Scope's own `DataBlockState`, never the Radar's.

**Field menus.** `TrackPresenter.ActionFor` sets each field's `ScopeFieldAction` per template and slot (ground targets get none) and `DatablockTemplate` carries it on the `FieldRect`. `ScopeCanvas` raises `FieldClicked` instead of `AircraftLeftClicked` for a left click on a field with an action, only for a field of the clicked aircraft's own block (a lower block seen through it only selects).

Where a block lies over another track's VCI box, the press goes by CRC's two pick tiers (`ViewDataBlocks.IsDwelled`): only a dwell-locked block (`ScopeTrack.IsDwellLocked`: the instructor's local lock or the student's CRC lock) is in the priority tier, hover alone is not, and its claimed area is its `DwellBox` (the fields rect plus 2 px, without column 0), not `Block.Bounds`; that block gets the press (select, field action, Field A dwell-lock toggle) over a plain track's VCI; otherwise the indicator toggles, and within a tier the HSF indicator is tried before the VCI and the block's fields (CRC: HSF, point-out, on-frequency, fields; the Scope has no clickable point-out region). The instructor's selection is no tier.

`ScopeFrame.IndicatorsAt` applies the rule, so the hover preview and the press (`IndicatorPickAt`) agree, except that no VCI previews where a qualifying HSF indicator wins. The ERAM Field A dwell-lock toggle is the canvas's own and opens no menu. An ERAM HSF indicator or VCI cell click (lit or not; `ScopeCanvas.PressAircraft` checks `ScopeFrame.IndicatorPickAt`, with the inflated VCI box the hover uses, ahead of `HitAt`) (`ToggleHsf`, `ToggleVci`) opens no menu either.

`ScopeView` runs `ScopeDataEntries.ToggleHsf` or `ToggleVci` through `ScopeViewModel.RunEramText`, which sends it to the server's ERAM dispatch with the track as a pick, shows the answer in the response area, and joins neither the Scope's entry recall nor the preview text; with Shift held (`FieldClicked` carries the press's `KeyModifiers`) it stages the bare entry through `ScopeViewModel.StageEramText` instead, the canvas takes the focus and Enter sends it with the track as the pick.

`ScopeView` selects the track, then takes control (the EuroScope owner), opens the note box, or opens `ScopeFieldMenus.Build`'s menu at the pointer: one entry from the shared menu catalog with its submenu or flyout already open, the handoff entry chosen by the track's handoff state, the assigned-runway rows built there; a field whose entry does not apply opens no menu.

An ERAM ground speed (Field E) opens the Speed popup, and each line-4 token its own `ScopeDataHsfPopup` (`TrackPresenter.EramActionFor`: `OpenEramHeadingMenu` for the heading, `OpenEramSpeedMenu` for the speed, `OpenEramFreeTextMenu` for the free text, `OpenEramHsfMenu` for the type or destination fallback, which opens the combined Heading and Speed popup, also on a suppressed fourth line; the `Line4Pad` cells before a speed shown alone have no action); STARS and EuroScope keep their speed menus.

An ERAM altitude opens `ScopeDataAltitudePopup` instead of a pick menu, placed under the clicked block from `ScopeCanvas.BlockBoundsOf` (as every SCOPE DATA popup is): a SCOPE DATA row click runs its entry at once through `RunEramText` and Shift stages it through `StageEramText`, a PILOT row sends or, with Shift, stages `PILOT {callsign} {command}`; the popup closes either way, Esc closes it, a click outside closes it and still reaches the Scope, and only one field menu, popup or aircraft menu is open at a time (`ScopeView.ActiveFieldPopup`).

STARS and EuroScope altitudes keep the single-entry menu, and on Mixed the track's resolved template decides. A middle click on an ERAM block with an empty preview that lands on the HSF or VCI cell, or on a field with an action, does what the left click does (`ScopeMiddleClickAction.ClickField`, Shift ignored); with text in the preview it fills the track and runs the entry wherever it lands. An armed Rings tool wins over every field action.

A row click sends for the clicked aircraft through `ClientMenuHost`, as the right-click menu does; Shift held on the row stages `PILOT {callsign} {command}` in the Scope preview through `ClientMenuHost.Stage` and `ScopeViewModel.StageEntry` (`ScopeEntryStaging.Text`) instead of sending, the menu closes and Enter runs it.

Mixed (`ScopeTemplates.MixedId`) is a routing id, not a template: `ScopeTemplates.Get` throws for it and `ScopeFrame.BuildTrack` resolves it per track through `ScopeOwnerKind.Resolve` before presenting, so each track draws its owner's block. An owner sector code starting with a digit (`OwnerSectorCode`, the STARS subset) is STARS, a `C…` code ERAM, and an unowned track or an owner with no sector code ERAM.

The ERAM template's line 2 starts with the `EramAltitude` slot, which `TrackPresenter` formats from the raw ERAM inputs on the wire (assigned altitude Field B and its character 4, then reported altitude Field C; see [training-hub-contract.md](training-hub-contract.md), the `Eram*` fields) and follows with the STARS right side.

Right after the ERAM altitude, Full, Dwell and ERAM Partial line 2 carries the `AltitudeMismatch` slot, a `≠` in the block colour (`ScopeFieldStyle.AltitudeMismatch`, drawn as a path by `ScopeRenderer.NotEqualPath`, the rest of line 2 shifting one cell), while `TrackPresenter.EramAltitudeDiffers` holds: the ERAM plan altitude and the filed altitude both set and different, on a plan that is neither VFR nor OTP.

ERAM Limited and the STARS block carry no mark (the STARS line 2 is the Mode C altitude). The mark is YAAT's own: CRC shows one display at a time and has none.

Line 3 is the `StatusLine3` slot filled by `EramStatusLine.Build` (CRC's Field E, by strict priority: special-purpose code, `H` handoff, recently accepted `O`/`K` for the handing-off viewer only, beacon mismatch, `NONE`, `FRZN`, `CST`; the viewer's sector falls back to the digits of a `C`-prefixed active TCP when the server sent none), followed by the `HsfIndicator` slot (↴, drawn as a path in its `EramArrowCell`, in a fixed column because the status pads to four) whenever an HSF note is set; the status blinks only for `H`, `CST` and a correlated special-purpose code in its blink window, never in conflict.

Line 4 is the slots `Line4FreeText`, `Line4Heading`, `Line4Pad`, `Line4Speed` and the fallback `Line4Notes`, filled from `EramLine4.Parts` (one token per click region; the drawn text is the tokens joined), and the LINE 4 brightness applies to each of them (`ScopeFieldSlotExtensions.IsLine4`, `ScopeRenderer.FieldPercent`). The text is:

HSF free text, else heading and speed, else heading, else speed in its column, else type and suffix (`ScopeDisplayOptions.EramLine4Type`), else a destination of four characters or fewer (`EramLine4Destination`); both options default on until the DB FIELDS binding.

When the viewer's sector holds the track's fourth-line suppression (`EramDwellLock.IsFourthLineSuppressed`, set by a bare `QS {FLID}`), the HSF branches are skipped and the type or destination fallback shows; the HSF indicator on line 3 stays.

Column 0 is the template's `Column0` (`ScopeColumn0Mark`: slot and fixed 1-based row): the `Vci` slot on row 2, a quarter-arc path in the block colour that `EramColumn0.VciLit` lights when the viewer's ERAM sector is in the track's `EramOnFrequencySectorIds`, and the `NotYourControl` slot on row 3, an `R` that `EramColumn0.ShowsNotYourControl` shows whenever a viewer is known and the track, unowned included, is not the viewer's; with no viewer neither shows, and the VCI's off state draws nothing.

The marks sit `DatablockTemplate.ColumnGap` left of the text block and widen `ScopeBlockLayout.Bounds`, which hit-testing reads, while `ScopeBlockLayout.TextBounds` stays the text block the leader attaches to and the selection border frames. The ERAM block carries no separate squawk-mismatch line.

Block levels are a presenter input (`TrackPresenterInput.Level`, `ScopeFrameInput.DefaultLevel`; production passes Full, and Dwell for the hovered track and for a track dwell-locked for the viewer's ERAM sector or by the instructor's local lock; the owner and zoom policy is plan step 4): `ScopeTemplate.LimitedLines` / `PartialLines` / `LinesAt(level)` pick the lines and `TrackPresentation.ShowsLeader` says whether the leader draws. The Ground level (TDM, an on-ground aircraft on STARS or ERAM) overrides all of these: the ground-target lines show at every level.

ERAM Limited is the aircraft id, the padded Mode C or CERA (`090#`) and the special-code label (`EramStatusLine.SpcText`), with no leader, no column 0 and no alert lines, the whole block blinking while a special code shows (CRC's paired LDB).

ERAM Partial is the id, Field B and Field C without character 4 (`TrackPresenter.EramFieldsBAndC`) and the special-code label (CRC's enhanced LDB). STARS Limited and Partial are the Radar's collapsed lines with the leader; EuroScope shows the full tag at every level; the `(LDB)`/`(PDB)` mark shows only at Full and Dwell.

Hover: `ScopeCanvas` tracks the hovered callsign, the VCI-previewed callsign and the callsign of the queued-commands badge the pointer is on (`ScopeHoverHit.QueueHovered`), and `ScopeFrame.HoverAt` hit-tests the drawn frame in one stage at a time across all tracks, topmost first (CRC's `ViewDataBlocks.HandleMouseMove`): within 8 px of a symbol dwells that track; else inside a lens badge's text box inflated 3 px shows its pending lines without dwelling; else a block's VCI cell (`ScopeBlockLayout.VciCell`, inflated 5 px left and down and 3 px up and right as CRC's `OnFrequencyIndicatorBounds`; ERAM Full and Dwell blocks only, with a viewer ERAM sector) lights the VCI glyph without dwelling the block, unless the cell is a plain track's and a dwell-locked block's `DwellBox` holds the point, or a qualifying HSF indicator takes it (`ScopeFrame.IndicatorsAt`) (the same cell is the click target, see Field menus); else 2 px outside a block's text bounds dwells it (`ScopeHoverHit`; `DwellAt` is its dwell half). A dwelled track is dwelled at once and released on the next move off it or when the pointer leaves the canvas, and panning holds it.

A dwelled `ScopeTrack` (`IsDwelled`) draws last, on top and undimmed (a coasting block at full alpha, the squawk-mismatch tail still pulsing), with a 1 px box (`DwellBox`) in the block colour 2 px outside its text bounds; selected and dwelled shows both borders. When the ERAM and STARS altitudes differ, a dwelled ERAM or STARS block gains a line under line 2 (`ScopeTemplate.DwellLines`, the `OtherAltitude` slot) naming the other side's altitude, `STARS 350` on the ERAM block and `ERAM 240` on the STARS block; EuroScope has none. Column-0 marks stay beside their line: `R` moves down with the status line, the VCI stays on line 2's row. The dwell lock (`EramDwellLock`, CRC's `EramSectorDisplay.IsDwellLocked`) holds an ERAM block (the ERAM template, and ERAM-kind tracks under Mixed) at Dwell while the track's `EramDwellLockedSectors` holds the viewer's ERAM facility and sector: a locked track draws after the other tracks and before a hovered one, and STARS and EuroScope blocks ignore the lock. A second, instructor-only lock is client-side: a left click on the callsign (Field A) of the block it selects toggles `DataBlockViewState.DwellLockedCallsigns` through `ScopeViewModel.ToggleDwellLock` and still selects; `ScopeFrame.LevelFor` reads it through `ScopeFrameInput.LocalDwellLockedCallsigns`. It needs no viewer ERAM sector, sends nothing to the server or CRC and shows no status message, and the student's lock still holds the block for the viewer's sector (the click cannot release it). `DataBlockViewState.Clear()` releases it. Where CRC and the ERAM manual's Table 3 differ, CRC wins; a coasting track reports no Mode C, and there is no plan altitude without a flight plan.

A field's `ScopeFieldStyle` ([Flags]: `DimTail`, `Strikethrough`, `ArrowUp`, `ArrowDown`, `HsfIndicator`, `Vci`, `AltitudeMismatch`) is set by the presenter and read by the renderer, never by layout, so it changes no rect or hit-test: `DimTail` draws the STARS squawk-mismatch line's assigned code at 25% on the blink off-phase while the reported code stays solid (STARS and EuroScope; the ERAM status shows only the reported code), and `Strikethrough` strikes a standby transponder's Mode C (antialiased, every template); `ArrowUp` / `ArrowDown` mark the ERAM altitude's climb or descent arrow, `Vci` the column-0 mark and `AltitudeMismatch` the `≠` cell, which `ScopeRenderer` draws as an `SKPath` in the text's `EramArrowCell` rather than as a glyph.

`ScopeDisplayOptions` (`MarkStudentLevel`, `ShowTypeMismatchHints`), required on `TrackPresenterInput` and `ScopeFrameInput`, gate two STARS-only cues: the `(LDB)`/`(PDB)` `StudentLevelMark` after the callsign (EuroScope tags never show it) and the amber `TypeMismatch` token on the cwt/type field when the actual type differs from the filed type.

`ScopeViewModel.Viewer` (`ScopeViewer(Acting, EramFacilityId, EramSectorId)`, rebuilt from `MainViewModel.ActiveTcp`, `ActivePosition`, `ActingCallsign` and `ActingTrackFacilityId`) is the position the scope is viewed from; `ScopeCanvas.Viewer` binds it, a change rebuilds the frame, and `ScopeDisplayOptions.Viewer` carries it to the presenter, which reads it for the ERAM status's recently-accepted `O`/`K` text and the column-0 VCI and `R`.

**Feature seams.** The Scope's input and rendering take new features as partial methods, so a feature adds its own partial files rather than editing the shared ones. `ScopeCanvas.RunPressLadder` offers a press to its rungs in this order, the first to set `handled` ending it: heading vector, draw route, middle button, measuring tool, entry fill, block hide, menu gesture, block drag, range ring, aircraft, empty map, bubbles (`RunReleaseLadder` and `RunMoveHooks` work the same way, the move hooks all running). A feature implements the rung's `partial void Press…(in ScopePress, ref bool handled)` in its own `ScopeCanvas.*.cs`; the rung's place in the ladder is its place in `ScopeCanvas.Ladder.cs`, and a rung that must sit elsewhere moves there. Rendering has the same shape: a feature's data is built in its `Collect…` hook (`ScopeCanvas.CollectOverlays`) into `ScopeOverlays` on the UI thread, and drawn by its `Draw…` slot in `ScopeRenderer.Features.cs` (map tools under the tracks, the route, heading-vector, bubble and menu-cue overlays after the pilot lens and before the measure lines, map tools over everything) with its resources freed in its `Dispose…`. `ScopeView.Features.cs` (wiring, view-model attach and detach, menu rows, keys, the Esc chain), `ScopeViewModel.Features.cs` (Esc modes and tools, per-track state) and `MainViewModel.ScopeFeatures.cs` (`.scope` verbs) carry the rest. An unimplemented partial method compiles away, so the hooks cost nothing until a feature fills them.

The Radar skips a `PositionDisplayConfigDto` marked `NoStarsDisplay` (an ERAM sector's), so its DCB map shortcuts and weather airports stay as they were. The Radar layouts above stay the live path until the view replaces them.

`Services/ShownRouteBuilder.cs` is **not** part of this stack and is **not** command-input UX — it builds the
"show nav route" path segments (waypoints + the optional assigned-heading tail + crossing-restriction labels) that `RadarViewModel` assembles into the
`ShownPathEntry` overlay list drawn by `RadarRenderer.DrawShownPaths`. It only shares the `Services/` folder with other code.

## Two-thread render pipeline

`RadarCanvas` extends `MapCanvasBase`, which splits every frame across two threads
(`MapCanvasBase.cs:59-65`, `:70-78`, `:162-195`):

1. **UI thread — `CreateRenderSnapshot()`** (`MapCanvasBase.Render` calls it; `RadarCanvas` overrides at
   `RadarCanvas.cs:730`). It reads all Avalonia `StyledProperty` values and mutable canvas fields and copies them into an
   immutable `RenderSnapshot` record (`RadarCanvas.cs:697-728`). `MapCanvasBase.Render` also clones the viewport
   (`_viewport.Clone()`) and hands both to a `MapDrawOperation` (an `ICustomDrawOperation`).
2. **Render thread — `RenderFromSnapshot(SKCanvas, MapViewport, object?)`** (`RadarCanvas.cs:821`). The draw operation
   leases the SkiaSharp canvas (`ISkiaSharpApiLeaseFeature`, `MapCanvasBase.cs:184-194`) and calls `RenderFromSnapshot`,
   which unpacks the `RenderSnapshot` and forwards every field to `RadarRenderer.Render`.

### The no-StyledProperty rule

`RenderFromSnapshot` runs on the render thread and **must never read a `StyledProperty` or a mutable canvas field**.
This is stated only in the `MapCanvasBase` doc-comments (`MapCanvasBase.cs:53-65`); nothing enforces it. Because of it, mutable interaction state that the renderer needs is **defensively copied into the snapshot**: the per-callsign datablock state (`ScopeDataBlockViewState` — manual offsets, minified, hidden, highlighted, z-order) is copied into fresh `HashSet`s / `Dictionary`s, and the aircraft list is filtered + z-ordered into a new list. Reading any of it directly inside `RenderFromSnapshot` would be a nondeterministic cross-thread access.

### Session-persistent datablock state (`DataBlockViewState`)

Per-callsign datablock UI state — manual drag offsets, highlights, minified choices, hidden datablocks, z-order — lives in `RadarViewModel.DataBlockState` (`ScopeDataBlockViewState`, `ViewModels/DataBlockViewState.cs`), **not** on the canvas (issue #350). The canvas binds it via the `DataBlockState` styled property (`RadarView.axaml`) and mutates it in place; a private local instance backs bare-canvas tests and detached windows. Two rules follow:

- The `DataBlockState` property-changed handler only marks dirty — it must **never clear** the store, because the binding
  churns to null and back when the docked tab detaches/reattaches its content.
- Lifecycle clears belong to the view-model layer: scenario restart/rewind (`MainViewModel.ReplaceAircraftFromManifest`)
  and scenario unload (`ClearScenarioState`) call `DataBlockState.Clear()`. The open YAAT Scope has its own state, dropped
  with its rings, PTLs and toggled lens paths by `ScopeViewModel.ClearTrackState()` on unload, restart and every rewind, this client's own included. Because the state is shared by every
  `RadarCanvas` bound to the view-model, the embedded tab and the pop-out window see the same offsets. An extra
  Radar window (**View → New Radar Window**, `RadarViewInstance`) has its own `RadarViewModel` and therefore its own
  `DataBlockState`, center and range — see [client-mainviewmodel.md](client-mainviewmodel.md#extra-view-instances-new-radar-window--new-ground-window).

**Hidden datablocks.** `ScopeDataBlockViewState.HiddenCallsigns` is independent of the minified choice: a hidden block hides whatever form it has (full, collapsed, minified or EuroScope tag). `TargetRenderer` reads the snapshot's copy and skips the block and its leader line of a hidden aircraft unless it is selected; the target symbol, history trail, PTL and overlays stay drawn, and a speech bubble anchors at the symbol.

The canvas's hit-test (`FindDataBlockAtPoint`, `DataBlocksAtPoint`), the deconfliction inputs and the measurement-readout obstacles skip the same blocks through `RadarCanvas.IsBlockUndrawn`, so a hidden block catches no click and takes no room while its symbol still does. Shift+left on the symbol or block toggles it (see the ladder below), the Display submenu carries Hide/Show datablock, and the map's background menu carries "Show all datablocks", which clears the set; `Clear()` empties it with the rest of the state.

### The 100 ms force-repaint timer

`MapCanvasBase` starts a `DispatcherTimer` at a 100 ms interval (`InvalidateInterval`, `MapCanvasBase.cs:19,31`) that calls
`InvalidateVisual()` every tick (`OnInvalidateTick`, `:153-160`). This exists because aircraft positions update via
property changes on items **inside** the bound `ObservableCollection` (`AircraftModel` instances), which do **not** raise
styled-property change notifications on the canvas itself. Without the timer the radar would freeze between user
interactions. `MarkDirty()` (`:48-51`) triggers an immediate extra repaint when a non-bound field changes (brightness,
EuroScope mode, datablock drag, etc.).

## Viewport transform — `MapViewport`

`MapViewport` (`MapViewport.cs`) is a plain class (not a control) that converts lat/lon ↔ screen pixels with an
equirectangular projection — valid because at TRACON scale (<120 nm) the distortion vs Mercator is <0.1%
(`MapViewport.cs:3-6`). Key members:

- `LatLonToScreen(lat, lon)` / `ScreenToLatLon(x, y)` — the projection pair. Longitude is scaled by
  `cos(CenterLat)` so east-west distance matches north-south at the display center (`MapViewport.cs:26,32`).
- `Pan` / `ZoomAt` — mutate `CenterLat/CenterLon/Zoom`; `ZoomAt` keeps the point under the cursor fixed.
  Scroll-wheel zoom in `MapCanvasBase.OnPointerWheelChanged` scales its per-event step by the
  `MapCanvasBase.ScrollSensitivity` CLR property (`factor = Math.Pow(step, direction * ScrollSensitivity)`;
  1.0 = one full notch = historical speed). Views push `UserPreferences.ScrollSensitivity` into the canvas
  via `RadarView.SyncAssignmentTint` / `GroundView` prefs-apply. Discrete DCB spinners (range/PTL/history/
  brightness) and the range-ring-size step gate through `ScrollStepAccumulator` instead, so a Mac trackpad's
  burst of small-delta events doesn't race them (#275).
- `RotationDeg` — **set to the local magnetic declination** (east-positive) so the display is magnetic-north-up
  (`MapViewport.cs:19-23`). `RadarCanvas` sets it from `MagneticDeclination.GetDeclination(centerLat, centerLon)` whenever
  the center changes (`RadarCanvas.cs:669`, `:1413`).

Because the display is magnetic-north-up, any UI bearing (a fly-heading vector, a PTL) is in **magnetic** degrees and must
round-trip through `MagneticDeclination.MagneticToTrue` / `TrueToMagnetic` before projecting, so the drawn angle matches
the published bearing regardless of local declination. See `RadarRenderer.DrawVectorTail` (`RadarRenderer.cs:451`) and
`RadarCanvas.ConfirmHeadingAt` (`RadarCanvas.cs:513-515`).

### `DefaultPixelsPerDeg = 5000` is duplicated three times

The base pixels-per-degree constant is `5000.0`, defined as a `private const` in **three** places that must stay in sync:
`MapViewport.DefaultPixelsPerDeg` (`MapViewport.cs:11`), `RadarCanvas.ZoomToRange` (`RadarCanvas.cs:1429`), and
`RadarCanvas.UpdateViewRangeNm` (`RadarCanvas.cs:1552`). `ZoomToRange` converts the RANGE spinner (nm) into a viewport
`Zoom`; `UpdateViewRangeNm` does the inverse. Changing one without the others desyncs the RANGE spinner from the actual
on-screen zoom.

### Range/Zoom ⇄ Center/Viewport feedback-loop guards

The RANGE spinner (`RangeNm`, default 40 — `RadarCanvas.cs:38`), the bound center (`RadarCenterLat/Lon`), and the live
viewport sync **bidirectionally**, guarded by three flags to prevent infinite loops:

- `_initialFitDone` — gates whether a viewport change should sync back to `RangeNm`/center, and ensures the one-time
  zoom-to-range fit happens exactly once.
- `_suppressRangeFit` — set while writing `RangeNm` back from a viewport zoom, so the property-changed handler doesn't
  re-fit the zoom (`OnViewportChanged`, `RadarCanvas.cs:1460-1462`).
- `_suppressCenterSync` — set while writing `RadarCenterLat/Lon` from the viewport (or vice versa), so neither side
  re-triggers the other (`RadarCanvas.cs:664`, `:1466-1471`).

The flow: the property-changed handler (`OnPropertyChanged`, `:628-695`) syncs an incoming center into the viewport and,
on the first valid center, calls `ZoomToRange`; a RANGE-spinner change re-zooms; a user pan/zoom fires `OnViewportChanged`,
which writes the rounded `ViewRangeNm` back to `RangeNm` and the panned center back to `RadarCenterLat/Lon`.

**Initial fit is deferred** because the radar tab may be laid out at 0×0 before it is shown. `OnPropertyChanged` only
performs the fit once the viewport has pixel dimensions (`Viewport.PixelWidth >= 1 && PixelHeight >= 1`,
`RadarCanvas.cs:671`); otherwise `OnSizeChanged` (`:1436-1448`) does the fit when the canvas first gets real dimensions.

## Render order (one frame)

`RadarRenderer.Render` (`RadarRenderer.cs:274-376`) draws strictly back-to-front:

1. `canvas.Clear(BackgroundColor)` — black.
2. **Video maps** — `VideoMapRenderer.Render`, A/B brightness category per map (`RadarRenderer.cs:312`).
3. **Range rings** — concentric circles at the dedicated range-ring center/size, if `showRangeRings` (`:315-321`).
4. **Fixes** — crosses + labels; programmed fixes get a distinct color and always-on label (`:324-327`, `DrawFixes`). The source
   list is `RadarViewModel.BuildVisibleFixes`, which drops vNAS-adapted identifiers that are themselves FRD strings
   (`OAK169001`) via `FrdResolver.IsFrdIdentifier` — ~6,900 of them would otherwise carpet the scope. They remain typeable
   through the command bar and the fix flyouts' boxes (`AllFixNames` still holds them).
5. **Shown nav routes** — the "show nav route" overlay, drawn behind aircraft (`:330-333`, `DrawShownPaths`).
6. **Aircraft targets** — `TargetRenderer.Render` (`:336-349`).
7. **Heading-mode preview** — the live elastic vector when EuroScope heading mode is active; above aircraft, below the
   drawn route (`:352-359`, `Flyouts.HeadingPreviewRenderer.Render`).
8. **Drawn route overlay** — the in-progress draw-route waypoints + rubber-band line (`:362-369`).
9. **Weather overlay** — METAR text block, top-left (`:372-375`). When the position's weather airports have no METAR in the loaded weather, the block is replaced by the `WeatherNote` line (`No METAR for SFO OAK`), never every station; both go through `DrawWeatherLines`. Stations match on the FAA id (`AirportAirlines.NormalizeAirportId` strips a 4-letter id's leading K or P).
10. **Menu cue** — the map cue of the menu row under the pointer or holding focus (`MapCue`, set through `IMenuHost.SetMapCue` onto `RadarViewModel.MenuCue` and `ScopeViewModel.MenuCue`), drawn *after* `Render` returns, from `RadarCanvas.RenderFromSnapshot` via `RadarRenderer.DrawMenuCue`, which delegates to `MapCueRenderer` (the Scope's `ScopeRenderer.DrawMenuCue` slot calls the same renderer, so the cue reads alike on both), so it shows over targets and datablocks, and before the range/bearing lines.

    It is a solid `#8fc1ff` ring (radius 13 px, 2 px) on the pointed target, a dashed (5/4) 1.4 px `#8fc1ff` bearing line to it from the commanded aircraft with `{clock} o'clock · {nm} nm` in 11 px monospace right of the midpoint (left out when the cue's `ShowBearingLabel` is false), and a dashed (4/3) `#7fd1b9` ring on the aircraft the call resolves to when that is another one. A cue with no pointed target (`MapCue.TargetCallsign` null, the solo advisory flyout with no row pointed at) draws only that dashed ring: no solid ring, bearing line or datablock highlight.

    The snapshot carries it already resolved (`MapCue.ResolveAgainst`, the same filtered list the frame draws), as the range/bearing lines are: an end whose aircraft the view does not draw draws nothing. The pointed target's datablock also takes the menu highlight (`DataBlockState.SetMenuHighlight`, which `RadarCanvas` repaints on through `HighlightsChanged`). On the Scope `ScopeCanvas.CollectMenuCue` resolves it against the frame's tracks into `ScopeOverlays.MenuCue`, and `ScopeView` repaints on a cue or highlight change.
11. **Range/bearing lines** — the distance measuring tool, drawn *after* `Render` returns, from `RadarCanvas.RenderFromSnapshot` via `RadarRenderer.DrawRangeBearingLines` → the shared `Views/Map/RangeBearingRenderer`. Last so a measurement stays legible over targets and datablocks. The snapshot carries them already resolved (`ResolvedRbl`), because a latched endpoint has to be looked up against the live aircraft list on the UI thread.

    Only lines tagged `RblView.Radar` are resolved into the snapshot — measurements taken in the ground view never render here. The readout is anchored at the far endpoint, or, when that endpoint is off-screen, where the line exits the viewport (`RblLabelPlacement.Anchor`), and every candidate spot is clamped fully inside the view (`RblLabelPlacement.ClampIntoView`), so a partially visible line always shows its reading.

    `RblReadoutPlacement` then picks one of eight spots around the anchor, East first (CRC's spot, 9 px out): the spot with the least overlap with data blocks, aircraft symbols (except the symbol of the aircraft the far end is latched to) and readouts already placed, scored as clamped, and kept frame to frame unless another spot is clearly cheaper.

    When every spot is covered, it takes the spot the fixed obstacles (manually dragged blocks, EuroScope tags, other readouts) leave freest and **nudges** the auto-placed blocks under it just clear (the shortest left/right/up/down move whose leader stays within `MaxLeaderLength`); a block no such move clears counts as fixed for that spot.

    A nudged block stays nudged until it clears the readout by 6 px, and readouts placed later in the frame treat it as fixed; a later readout's nudge never moves a block onto an earlier readout or a block it nudged (`RblObstacles.PlacedThisFrame`). The half-placed (pending) line uses the same spots but never nudges. CRC does not deconflict its readout; this is a YAAT departure.

Inside step 6, `TargetRenderer` first draws **history trails** behind all symbols (`TargetRenderer.cs:198-202`), then does a
**two-pass deferred render**: aircraft with an active speech bubble are held back to a second pass so their symbol,
datablock, and bubble pill paint on top of neighboring aircraft (`:207-258`). The deferred list is allocated only when
speech bubbles are enabled.

Solo training suppresses SAY and pilot-speech bubbles (`AircraftSpeechBubble.TryBuild`'s solo-mode gate, fed by
`MainViewModel.MaybeAttachSpeechBubble`). The environment variable `YAAT_DEV_SOLO_SPEECH_BUBBLES=1` lifts that
suppression; it is a capture-only switch for recording demo videos of solo replays, not an instructor setting.

## Datablock and tag layout

Two layout families, selected by the `EuroScopeMode` preference:

**STARS — `RadarDatablockLayout.Compute`** (`RadarDatablockLayout.cs:48`). Lines, top to bottom:
1. Callsign (`*`-suffixed for VFR).
2. Altitude-hundreds + speed-tens + CWT/type, with a flashing `ID` appended while the aircraft is identing
   (see **IDENT** below).
3. Beacon-code mismatch (`SquawkLine`, e.g. `1200 0301`) — the reported code solid, then the assigned code
   dim-pulsing, when they differ (see **Beacon-code mismatch** below). Empty when the codes match / it's suppressed.
4. Owner + flashing handoff + scratchpads (`.sp1 +sp2`) + `[assignedTo]`.
   The owner text is `AircraftModel.OwnerDisplay`, which carries the student scope's position symbol after an owner in
   another facility (`3Y(1)`, during a handoff `3Y(1) >1F`) unless the symbol repeats the owner's first or last letter or digit (a centre sector `C44` stays bare); see [crc-display-state.md](crc-display-state.md#trackowner--how-ownership-broadcasts).
   The `.sp1` slot renders `DatablockText.EffectiveScratchpad1(ac)` — the controller-entered `Scratchpad1` when
   set, otherwise the server-resolved `AutoScratchpad1` (the STARS destination fallback). The two render identically,
   deliberately: CRC does not distinguish them, so the instructor mirror shows exactly what the student's scope shows.
   See [crc-display-state.md](crc-display-state.md#automatic-primary-scratchpad) for the gating rules. The same helper
   backs the EuroScope layout and `DatablockText.BuildCollapsedLines`, so all three stay in lockstep.
5. `ModeC` (rendered struck-through) when the transponder is in Standby.
6. `NoLndgClnc` warning (red, flashing).
7. Conflict alert (`ConflictLine`, red, flashing) — `CA 2.4/800` / `MCI 2.4/800`. See **Conflict alerts** below.
8. Instructor note (`Line6`, amber) at the bottom of the block.

An ATPA in-trail distance line (`AtpaLine`, steady, state-coloured) sits after the owner/scratchpad line when the aircraft is the trailing member of an ATPA pair and `ShowAtpa` is on. See **ATPA cones** below.

**EuroScope — `EuroScopeTagLayout.Layout`** (`EuroScopeTagLayout.cs:52`). Four lines plus optional squawk-mismatch / ModeC / NoLndgClnc:
1. Owner initials (or `--`) + callsign.
2. Type/CWT + destination + a flashing `ID` (`TagFieldId.Ident`) while identing.
3. Current altitude + assigned altitude `(NNN)` + assigned speed `Snnn`/`ASP` + assigned heading `Hnnn`/`AHDG`.
4. `Rrwy` + scratchpads + flashing handoff (only when at least one is set).
5. Beacon-code mismatch — a `TagFieldId.Squawk` field (`1200 0301`, reported solid + assigned dim-pulsing), only when
   the codes differ. Clicking it opens the `SquawkFlyout` via the existing dispatch.
6/7/8. `ModeC` (struck-through), `NoLndgClnc`, and the conflict alert (`TagFieldId.ConflictAlert`) as needed.

The EuroScope layout returns a `EuroScopeTagResult` carrying the tag `Bounds` plus a list of `TagFieldRect`
(`field id, rect, text`) — one per clickable field (`EuroScopeTagLayout.cs:31-35`). Empty assigned fields still emit their
identifier (`ASP`, `AHDG`, `(---)`) so the click target stays stable when nothing is assigned.

### Flash slots

The handoff indicator (both layouts), the `NoLndgClnc` warning, the conflict alert, and the `ID` ident token blink on a 500 ms cycle:
`Environment.TickCount64 / 500 % 2 == 0` (`RadarDatablockLayout.cs:66,119`; `EuroScopeTagLayout.cs:148,187`). The
`NoLndgClnc` slot **reserves its width and a line slot even when the flash is off-phase**, so the datablock height and the
hit-rect width don't pulse with the flash (`RadarDatablockLayout.cs:73-90`; `EuroScopeTagLayout.cs:181-185`). The handoff
indicator does **not** reserve when off — the STARS hit-test path compensates by always sizing line 3 as if the handoff
were present (`RadarCanvas.cs:1333-1334`, `DatablockText.BuildOwnerScratchpadLine` always includes the handoff for width).

The conflict field reserves the same way, but note **what** it measures: the reserved width comes from the *stable* text
(`DatablockText.BuildConflictLine` called unconditionally while the conflict is active), not from the currently-drawn string. The field
carries live separation values that change as the pair moves, so measuring the drawn string would collapse the rect to
zero on every off-phase. Both layouts also expose the reservation as an explicit flag — `ReserveWarningSlot` /
`ReserveConflictSlot` on the STARS layout — because the draw path has to advance its row past a reserved-but-blank
`NoLndgClnc` slot to place the conflict field, and `Line5.Length > 0` can't distinguish "inactive" from "off-phase".

### IDENT

`ID` blinks fully on/off on the shared 500 ms cycle, and it **reserves its width the same way `NoLndgClnc` reserves its
line slot** — but the reservation lives in the *string*, not in a flag. Every builder
(`Compute`'s line 2, `BuildCollapsedLines`, `BuildMinifiedLine`) appends the token unconditionally while the ident is
active, so measurement and hit-testing always see it; only `TargetRenderer` skips drawing it on the off-phase. Because it
is the **last** token on its line, skipping it shifts nothing to its right
(`RadarDatablockLayoutTests.Line2_ReservesIdWidthAcrossFlashCycle`,
`DatablockHitTestParityTests.HitTestRect_StableAcrossIdentFlashCycle`).

It is **appended after** the CWT/type token (`230 25 D/B738 ID`), not substituted for it — CRC's `BuildFdb` suppresses
`track.Category` while identing, but YAAT keeps the type because the instructor mirror is also a diagnostic surface. The
block widens for the duration.

`RadarDatablockLayout.IdentActive` tells the draw path that line 2 carries the token, and
`TargetRenderer.DrawDatablockLine` splits the line into an MVA-tinted altitude head, a plain middle, and the flashing
ident tail. All four block forms show it — STARS full, collapsed LDB/PDB, minified, and the EuroScope tag
(`TagFieldId.Ident`, whose `TagFieldRect` is emitted on both phases so the click target holds still). The **Ground view**
does not, matching CRC: `DtoConverter.ToAsdexTrack` carries no ident field.

State arrives as `AircraftModel.IsIdenting` (from `AircraftStateDto.IsIdenting`); the sim clears it after
`AircraftTransponder.IdentDurationSeconds` (18 s). See [training-hub-contract.md](training-hub-contract.md) for the wire
side and why the field must stay in `TrainingDtoFingerprint`.

### Conflict alerts

`ConflictLine` (STARS) / `TagFieldId.ConflictAlert` (EuroScope) render when
`AircraftModel.ConflictPeerCallsign` is set and the `ShowConflictAlerts` preference is on.
`TargetRenderer.DrawConflictRing` additionally strokes a geo-anchored 3 nm ring around the target, built the same way as
`DrawTpaJRing` (73 projected vertices, not a pixel-scaled circle) so the radius stays true at any zoom.

Two things about this field are unlike every other datablock field:

- **`ConflictPeerCallsign` does not come from `AircraftDto`.** Conflict alerts are room-level *pair* state delivered by
  the separate `ConflictAlertsChanged` broadcast and projected onto both members by
  `MainViewModel.ApplyConflictAlerts`. It must never be assigned in `AircraftModel.FromDto`/`UpdateFromDto`, or the next
  per-aircraft update would clobber it between conflict broadcasts. `MainViewModel.SeedConflictPeer` covers the reverse
  race (broadcast arrives before the aircraft's first update).
- **Separation is computed per frame, not carried on the wire.** The broadcast is signature-guarded and only fires when
  the pair set changes, so DTO-carried values would freeze at the moment the conflict opened. `BuildConflictLine`
  derives them from the pair's live positions instead, which is why both layouts need the peer `AircraftModel` passed in
  — resolved by `TargetRenderer.ResolveConflictPeer` (per-pass callsign index) on the draw path and
  `RadarCanvas.ResolveConflictPeer` (scan of the bound collection) on the hit-test path.

`BuildConflictLine` quantizes each altitude to hundreds **before** differencing rather than differencing and rounding —
otherwise the field can read `200` while the two truncated altitude readouts above it are `100` apart. The label follows
the P/CG split that 7110.65 §5-14-6 treats as distinct alert types: `MCI` when either member is untracked *and*
uncorrelated, `CA` otherwise. It classifies the **pair**, so both datablocks always agree.

The 3 nm radius is the detector's lateral trigger (`ConflictAlertDetector.HorizontalNm`), **not** a separation minimum —
those vary (3 / 5 / 2.5 nm under §5-5-4). Keep the two in step if the detector threshold ever changes. Two 3 nm rings
overlap from 6 nm apart, so the overlap is not the alert condition; the peer's *symbol* inside the ring is.

### Beacon-code mismatch (squawk mismatch)

`AircraftCommandApplicability.TryGetSquawkMismatch(ac, out reported, out assigned)` (`Yaat.Client.Core/ContextMenus`, over `IMenuAircraft`) is the **single shared gate** used by the STARS datablock (`Compute`), the EuroScope tag (`Layout`), **and the Ground View datablock** (`Ground/GroundRenderer.cs` `DataBlockLayout.SquawkLine`, drawn through the static `TargetColors.DrawSquawkMismatchLine` overload — see [ground-rendering.md](ground-rendering.md#datablock-composition)), so a change to the gate or the pulse treatment lands on all three surfaces at once. The same gate decides the quick-command strip's Squawk assigned code (`ShowsSquawkAssigned`, see [client-context-menus.md](client-context-menus.md#when-a-quick-command-shows)). It emulates CRC STARS (`DisplayElementTracks.BuildFdb`): when an aircraft is squawking a code that differs from its ATC-assigned code, the block shows the **reported code solid** followed by the **assigned code**, and the renderer draws the assigned token **dim-pulsing** — dimmed to ~25% brightness on the 500 ms off-phase (`TargetColors.DimColor`, mirroring CRC's `ApplyColorBrightness(color, 25)`), *not* fully blanked like the handoff / `NoLndgClnc` flash. The reported code stays solid.

The gate returns true only when **all** hold: an assigned code exists (`AssignedBeaconCode > 0`); reported ≠ assigned;
the transponder is actively squawking (`TransponderMode` is not `Standby`/`Off`); and the reported code is not a
special-purpose/emergency code (`7400/7500/7600/7700/7777`). A VFR aircraft still squawking `1200` after being assigned a
discrete code **does** surface — that is the primary case. Because the mismatch *condition* never flashes (only the
assigned token's brightness pulses), the reserved line slot and width in `Compute` stay stable across the cycle, so the
rect never pulses. Both codes format with `.ToString("0000")` (the raw octal-as-decimal beacon convention).

The assigned code reaches the client via `AircraftDto.AssignedBeaconCode` (see
[training-hub-contract.md](training-hub-contract.md)); the STARS datablock previously carried no beacon code at all in the
full block, so this line is purely additive — a normal (matching) aircraft shows nothing.

**YAAT flashes at any datablock level; CRC only in an FDB.** The mismatch line is an RPO aid, so — unlike CRC, which builds it only inside a Full Data Block — YAAT surfaces it at FDB/PDB/LDB alike (an assigned-but-not-yet-squawked code is worth flagging to the RPO regardless of who tracks the aircraft). It is therefore **not** gated on track ownership. Instead a per-aircraft latch, `AircraftDto.CommandedSquawkVfr` (server `Transponder.CommandedSquawkVfr`), turns the flash off: it latches when the pilot is told to squawk VFR (`SQVFR`/`SQV`, or `SQ 1200`) — at which point the stale assigned discrete code is noise the
RPO should ignore — and releases when a new beacon code is assigned (Flight Plan Editor recycle, `RequestNewBeaconCode`, an FP amendment, or a CRC beacon assign) or the pilot is told `SQ` any other code or `SQ` alone (back to the assigned code). "Squawk VFR" is a pilot instruction only; it does not change `AssignedCode`. `TryGetSquawkMismatch` returns false while the latch is set.

## Student-scope datablock view

When the scenario has a student position, the server projects how that student's STARS scope shows each track —
computed by `StarsDatablockClassifier` (Yaat.Sim) and carried on `AircraftStateDto.StudentDatablockColor` /
`StudentDatablockLevel` / `StudentLeaderDirection`, surfaced on `AircraftModel`. `TargetRenderer` consumes these, gated
by four `UserPreferences` toggles synced via `RadarView.SyncAssignmentTint`:

- **`SyncStudentColors`** (default on) — `DrawOneAircraft` colors the datablock from `StudentDatablockColor`
  (Owned→white, Unowned→green, Pointout→yellow, Highlighted→cyan) as the base color, below an RPO assignment tint but above
  the ground/white defaults (`TargetRenderer.cs:283-304`).
- **`MarkStudentLimitedDatablocks`** (default on) — appends `(LDB)`/`(PDB)` to the callsign line for the student's
  Limited/Partial levels.
- **`CollapseStudentDatablocks`** (default off) — renders the reduced block the student sees (`BuildCollapsedLines`) instead
  of the full block + marker.
- **`SyncStudentLeaderDirection`** (default off) — places the block in the student's leader direction via
  `ResolveBlockOffset`/`LeaderDirectionOffset`; a manual drag offset always wins, and the data block's right-click
  **Display > Reset datablock position** clears the manual offset (`RadarCanvas.ResetDataBlockOffset`).

The projection is null when there is no student position, so the renderer falls back to its prior behavior.

## Geometry — draw vs hit-test (single source of truth)

The full-datablock rectangle has **one source of truth**: `RadarDatablockLayout.Compute`. Both paths call it:

- **Draw path** — `TargetRenderer.DrawLeaderAndDataBlock` calls `RadarDatablockLayout.Compute` (STARS) or
  `EuroScopeTagLayout.Layout` (EuroScope) and paints from the result (`TargetRenderer.cs:356-506`).
- **Hit-test path** — `RadarCanvas.ComputeDataBlockPlacement` (`ComputeDataBlockRect` is a thin `.Rect` wrapper) routes the
  full block through `ComputeStableRectAtOrigin` → `ComputeStableFullRectAtOrigin`, which now just calls
  `RadarDatablockLayout.Compute(ac, 0, 0, HitTestStyle, …).Rect` (no hand-mirrored line-string re-derivation). It returns
  `(Offset, Rect)`: the rect feeds hit-testing, and the offset feeds drag-start so a leader-placed block whose position
  hasn't been dragged doesn't jump on the first drag.

`Compute` reserves the owner/handoff slot **stably** (`ReserveOwnerSlot` + the stable `line3` width), mirroring how it
already reserves the `NoLndgClnc` slot — so the rect (and thus the selection border, leader endpoint, and hit area) never
pulses with the 500 ms handoff flash. The draw loop steps its row by `ReserveOwnerSlot`, not by `Line3`'s current
emptiness, so `ModeC`/`NoLndgClnc` don't jump during the flash off-phase. `RadarDatablockLayoutTests` guards this
(`HandoffOnly_ReservesOwnerSlot…`, `OwnerHandoff_RectStableAcrossFlashCycle`) plus the translation invariant
(`Compute_RectIsTranslationInvariant`) that deconfliction relies on.

`ComputeStableRectAtOrigin` is shared by `ComputeDataBlockPlacement` **and** the deconfliction input assembly
(`BuildDeconflictItems`), so hit-test geometry and the deconfliction layout never diverge.

**The student-scope additions ARE shared too.** Block placement (`ResolveBlockOffset`), the minified/collapsed
line strings (`BuildMinifiedLine`/`BuildCollapsedLines`), the `(LDB)`/`(PDB)` marker (`StudentLevelMarker`), and the reduced
rect (`ReducedRect`) are pure static helpers (`BuildMinifiedLine` and `ReducedRect` on `RadarDatablockLayout`, the rest on `DatablockText`) that **both** the draw path and
`ComputeDataBlockPlacement` call. Only the reduced (minified/collapsed) block still builds its line strings on each path,
but through the same shared `ReducedRect`/`BuildMinifiedLine`/`BuildCollapsedLines` helpers.

**EuroScope mode avoids the re-derivation.** When EuroScope mode is on (and the block isn't minified),
`ComputeDataBlockRect` returns the `Bounds` the renderer cached during the last frame
(`LastEuroScopeTags`, `RadarCanvas.cs:1287-1292`). The renderer populates `_lastEuroScopeTags[callsign]` on every draw
(`TargetRenderer.cs:475`). The trade-off: a frame in which an aircraft's tag was **not** rendered (it was filtered out)
leaves stale or absent bounds, so `FindTagFieldAtPoint` returns `None` for it until the next frame draws it.

## Datablock deconfliction

`DatablockDeconfliction` (`Views/Map/DatablockDeconfliction.cs`) is a **pure, UI-agnostic** helper shared by the radar and
ground views that repositions overlapping datablocks so labels stay readable. It is **opt-in** per view via a 3-way
`DatablockDeconflictMode` (`Off` / `CompassSnap` / `FreeForm`, persisted globally as `UserPreferences.RadarDeconflictMode`
/ `GroundDeconflictMode`, cycled by the DCNF button). `CompassSnap` greedily snaps each block to one of the eight STARS
leader directions with previous-frame hysteresis (no jitter), and — when the base ring can't separate a dense cluster —
onto the same directions at successively longer leader rings (`LeaderExtraRings` × `LeaderRingStep`); the leader line is
drawn to the block edge by the renderer, so it stretches to follow. `FreeForm` runs damped rect repulsion seeded from the
prior frame. Its separation step does **not** blindly use the minimum-translation axis: for every colliding pair (and for a
movable block against a pinned one) it compares the minimum translation with a *hop* along the other axis that also
returns the block to its preferred placement, and takes whichever leaves the block(s) closer to preferred
(`PairSeparation` / `Separation` / `MoveCost`). Pure minimum translation always separates same-row blocks along X, so a
block whose anchor was travelling along that row — a departure rolling past a holding aircraft — was shoved back exactly as
fast as its anchor advanced and dragged hundreds of pixels from its aircraft, taking the other block with it (#402). A
hard `ClampLeader` cap at the outermost compass ring (`LeaderGap + LeaderExtraRings × LeaderRingStep`) is the safety net
behind that choice, and `SymbolPush` expels anchors using the padded rect so a block parks beside its symbol rather than
hugging it. Both bias the layout toward the aircraft's lateral order — a soft order-inversion penalty in `CompassSnap`'s
cost and a gentle restoring force in `FreeForm` keep repositioned blocks reading in the same left-to-right / top-to-bottom
order as their anchors — and both are deterministic given the same inputs + previous result.

**Where it runs (and why):** the pass runs on the **UI thread** inside `RadarCanvas.CreateRenderSnapshot` (and the ground
equivalent), not on the render thread. The hit-test path (`ComputeDataBlockPlacement` / `FindDataBlockAtPoint`) is also
UI-thread and must read the **same** resolved offsets the draw used, or clicking/dragging a deconflicted block breaks. The
result lives in a per-canvas `_resolvedDeconflictOffsets` dictionary: written once per snapshot build, copied immutably into
the snapshot for the render thread, and read live by the UI-thread hit-test. It also persists across frames as the
stability seed for the next pass. When the mode is `Off` the pass is skipped entirely and the map is empty — existing
placement is untouched.

**Offset precedence** (extended in `DatablockText.ResolveBlockOffset`): **manual drag > deconfliction > leader
direction > default**. Manually-dragged blocks are *pinned* (immovable obstacles others avoid); EuroScope tags are pinned
for v1 because their per-field hit rects are cached from the draw. A non-overlapping block resolves to its preferred
offset, so deconfliction only moves labels that actually collide. `BuildDeconflictItems` assembles the input list (anchor,
rect-at-origin, preferred offset, pinned/priority flags) using the same `ComputeStableRectAtOrigin` the hit-test uses.

**On the Scope**, `ScopeDeconfliction` (`Views/Scope/ScopeDeconfliction.cs`) runs the same engine: `ScopeCanvas` builds each frame through it, so the rings, the CRR readout and the hit-test follow the moved block, a manually dragged block is pinned and a hidden block is left out. Free-form advances one damped step per drawn frame (a hit-test's build reuses the last frame's offsets, so a click lands where the block is drawn); Snap runs on every build. The Scope's mode (`ScopeViewModel.DeconflictMode`) is its own: a new Scope takes `UserPreferences.RadarDeconflictMode` and never writes it back, and a pref set keeps it.

**Free-form limit (shared engine):** a same-row pair whose anchors are closer than about the block width less twice its height settles crossed, with a sliver of overlap, on the Radar, Ground and Scope alike; `CompassSnap` separates them.

**Readout nudges** sit on top of that precedence: after the deconfliction pass, `RadarCanvas` places the range/bearing readouts (`RblReadoutPlacement.PlaceAll`, see step 11 above) and keeps the nudges they gave auto-placed blocks in a separate `_rblNudgeOffsets` map (callsign → `RblNudge`: the delta and the readout slot that made it, so each readout's hysteresis keeps only its own nudges), never in `_resolvedDeconflictOffsets`, so a nudge does not seed the next deconfliction pass.

The snapshot ships the deconflicted offsets with every nudge folded in, and the UI-thread hit-test reads the same layer (`DeconflictOffsetFor`, `ComputeDataBlockPlacement(includeNudge: true)`), so a nudged block is clicked and dragged where it is drawn. A manually dragged block is never nudged. `GroundCanvas` keeps the same layer (`ResolvedDataBlockOffset` with the nudge included).

## Hit-testing and visibility

`RadarCanvas` exposes four "find" helpers, all of which re-run the same filter + z-order so what's clickable matches what's
drawn:

- `FindDataBlockAtPoint` (`:1259`) — topmost datablock whose rect contains the point.
- `FindAircraftAtPoint` (`:1367`) — nearest position symbol within a 28 px radius.
- `FindTagFieldAtPoint` (`:1206`) — EuroScope per-field hit (aircraft + `TagFieldId`); `(null, None)` outside any field.
- `FindBubbleAircraftAtPoint` (`:449`) — aircraft whose speech-bubble rect contains the point (uses `LastBubbleRects`).

`SortByZOrder` orders by the shared state's `DataBlockZOrder` so the last-surfaced (topmost) datablock wins a hit;
`SurfaceDataBlock` bumps a callsign to the top on any interaction. `DataBlockState.MinifiedCallsigns` toggles a callsign
between full and single-line (altitude + CWT) datablock (`ToggleMinifiedDataBlock`).

**`FilterAircraft` is the single source of which aircraft are drawable AND hit-testable** (`AircraftVisibility`, pure + static so it's
unit-testable). It hides delayed aircraft always, and on-ground aircraft in the non-top-down (radar) view — **except**
ground aircraft with an active speech bubble whose airport isn't shown on a ground view, so a SAY/pilot/WARN prompt for a
taxiing aircraft isn't missed (`ShouldSurfaceGroundBubble`; the `AlwaysShowGroundBubblesOnRadar` preference forces
all ground bubbles to surface). It also hides an airborne aircraft whose displayed altitude still rounds to `000`
(`AircraftModel.BelowDisplayFloor`, server-stamped from `FieldElevationResolver`: AGL < 100 ft, field-elevation adjusted) in
the non-top-down view, so a departure doesn't appear on the radar before it climbs above the acquisition floor — matching
CRC STARS' coast/skip below the same floor. Every `Find*` helper and `CreateRenderSnapshot` re-invokes `FilterAircraft`, so a
visibility change goes in one place — but you must preserve that draw/hit symmetry.

### Ghost / unsupported tracks — per-view visibility

`FilterAircraft` above is the **radar** view's filter; the Ground View and Aircraft List each have their *own* filter, and ghost / "unsupported" STARS tracks (`AircraftGhostTrack.IsUnsupported`) render differently on each — don't assume a shared rule. Two ghost kinds: pure **phantom** (CRC `DA`/`VP`, no real body, `IsOverlay=false`) and **overlay** (`GHOST`/AID slew on a real aircraft, `IsOverlay=true`).

| Surface | Filter | Rule |
|---|---|---|
| Radar / STARS | `AircraftVisibility.FilterAircraft` | Hides airborne below the acquisition floor via server-stamped `BelowDisplayFloor`; otherwise shows ghosts. |
| YAAT Scope | `ScopeCanvas.BuildFrame` calling `AircraftVisibility.FilterAircraft` | Same rule as Radar with `showTopDown` taken from `ScopeCanvas.ShowTopDown` (the Scope's TDM; `ScopeCanvas.DrawnVideoMaps` drops TDM-only maps the same way); the speech-bubble switches come from the preferences through `ScopeCanvas.FilterVisible`, so a ground aircraft carrying a bubble surfaces by the Radar's rule against `GroundShownAirportIdProvider`. |
| Ground / Tower-Cab | `GroundCanvas.FilterActiveAircraft` (render + both hit-testers) | Out-the-window picture: shows real aircraft, drops **only** pure phantoms (`IsUnsupported && !IsGhostOverlay`). An overlay is a real aircraft → shown airborne or on-ground (no `IsOnGround` coupling, so no flicker at rotation); the ≤10 nm / ≤ min(ceiling, 6000 ft) AGL bound lives in `GroundRenderer.IsAirborneVisible`, not the filter. |
| Aircraft List | `MainViewModel.IsAircraftVisible` | Hides pure phantoms, keeps overlays. |

**DTO position split:** the CRC **STARS** track uses the ghost's pinned position (`ac.Ghost.Latitude ?? ac.Position.Lat`), but the YAAT **SignalR** `AircraftStateDto` always sends the *real* `ac.Position` plus `IsUnsupported`/`IsGhostOverlay` booleans — so the YAAT client only ever draws the real position and handles ghosts by flag, never as a separate object.

An overlay can stay `IsUnsupported` while airborne below the floor until it auto-resolves at the 100 ft STARS floor (`CrcVisibilityTracker.EvaluateStars`).

## Pointer-handler priority ladder

`RadarCanvas.OnPointerPressed` (`:864-1080`) is a strict ladder of early-returns guarded by `e.Handled`. Inserting a mode at
the wrong rung silently steals clicks from a lower one. The order, top to bottom:

1. **Heading-mode click-to-confirm** — when heading mode is active and the button was already released, a left click
   confirms / a right click cancels (`:873-888`).
2. **Distance measuring** (the Radar and Ground here; the Scope's own `ScopeCanvas.TryMeasurePress` puts the same gestures ahead of its fill, Rings and aircraft rungs, see the measure layer) — Alt+left starts a drag measurement; while the tool is armed (`IsMeasuring`) a left click picks
   an endpoint and a right click cancels. Exclusive, so it sits above route drawing and every aircraft rung.
3. **Draw-route placement** — left click places a waypoint, middle click sets a condition, right click on a waypoint edits
   it (`:890-931`). The Scope's rung is `ScopeCanvas.DrawRoute.cs`, second in `RunPressLadder`.
4. **Shift+left hide toggle** — Shift+left on a drawn datablock or a symbol toggles that datablock hidden or shown (`TryToggleHiddenOnShiftClick`), without selecting or dragging. It sits below route drawing, so Shift+left there still places a waypoint.
5. **Middle-click highlight** — toggle the cyan highlight on the hit datablock/aircraft (`:933-948`).
6. **EuroScope field left-click** — if a `TagFieldId` is hit and `EuroScopeFieldClicked` has a subscriber, fire and return
   (`:954-964`).
7. **EuroScope field right-click** — owner-cell handoff/drop; returns `true` to suppress the fallback menu (`:968-980`).
8. **Datablock** — left click selects (Ctrl = open FP editor) and begins a drag; right click opens the aircraft context
   menu (`:982-1013`).
9. **Aircraft symbol (right)** — right click on a symbol opens the context menu (`:1015-1023`).

   Rungs 8 and 9 first gather every aircraft the right click hits (`FindRightClickTargets`: each datablock containing the
   point, topmost first, then each symbol within `AircraftHitRadiusPx` = 28 px by distance, an aircraft hit both ways
   listed once). Two or more open the shared `Views/Map/RightClickPicker` (labels `"{callsign} ({type})"` from
   `DisplayAircraftType`, else the bare callsign); choosing one raises its `AircraftRightClicked` with the original
   point after the picker closes. One or none falls through to the rungs unchanged. `docs/ground-rendering.md` ("Right-click
   vs right-drag") has the ground version, which adds parking/spot/helipad markers.
9. **Range-ring placement (left)** — places the range ring when in placing mode (`:1037-1046`).
10. **Aircraft symbol (left)** — select / Ctrl-open (`:1048-1062`).
11. **Bubble dismiss / empty-space** — record a bubble-press for release-side dismiss, else fire `EmptySpaceClicked`
    (`:1064-1077`).
12. **Base pan/zoom** — `base.OnPointerPressed` starts a right-button pan (`:1079`).

Two drag thresholds disambiguate clicks from drags: a `25 px²` right-button threshold decides quick-right-click (open
map menu) vs right-drag (pan), and a `16 px²` datablock-drag threshold (`:1090`) decides select vs reposition. The same
`16 px²` value (`HeadingModeState.DragThresholdPxSq`) decides drag-confirm vs click-confirm in heading mode.

The right-button click-vs-drag decision lives in `Views/Map/RightClickGesture`, shared with `GroundCanvas`: press
records the position and lets panning start, movement past the threshold latches the press as a drag, and `Release()`
returns the press position when a menu is owed or null when the gesture was a pan. Note the map menu is the only radar
right-click that defers — a right-click on an aircraft or datablock still opens its menu on **press**.

## EuroScope interactive tag mode

When `EuroScopeMode` is on, clicking a tag field opens a contextual editor instead of the full aircraft menu.
`RadarCanvas.FindTagFieldAtPoint` returns the hit `TagFieldId`; the canvas raises `EuroScopeFieldClicked`
(`RadarCanvas.cs:1250`), which `RadarView.axaml.cs:107` dispatches by field (`OnEuroScopeFieldClicked`):

| `TagFieldId` | Action |
|---|---|
| `Owner` | Take control (`TakeControlAsync`); right-click → RPO control menu |
| `CurrentAltitude` / `AssignedAltitude` | `RadarAltitudeFlyout` (FL010–FL400 picker; selection dispatches CM or DM by current alt) |
| `AssignedHeading` | `EnterHeadingMode` — live elastic vector (the Scope's EuroScope tag: `ScopeFieldAction.StartHeadingVector`) |
| `CurrentSpeed` / `AssignedSpeed` | `RadarSpeedFlyout` (80..350 kt picker; the menu's Assign speed flyout is `ContextMenus/SpeedFlyout`) |
| `Destination` | Enter draw-route mode (the Scope's EuroScope tag: `ScopeFieldAction.StartDrawRoute`) |
| `Scratchpad1` / `Scratchpad2` | `ScratchpadFlyout` text popup |
| `Squawk` | `SquawkFlyout` |
| `AssignedRunway` | `RunwayFlyout` |
| `Handoff` | `HandoffFlyout` text popup |

**Heading mode has two confirm paths** (`Flyouts/HeadingMode.cs:14-18`, driven from `RadarCanvas`): drag-style — mouse-down
on `AHDG`, drag past `DragThresholdPxSq` (16 px²) while held, release confirms; click-style — mouse-down then release
without dragging leaves the mode armed (`ButtonHeld = false`) and a subsequent left-click on the map confirms. Both call
`ConfirmHeadingAt`, which computes the true bearing to the clicked point, converts true→magnetic via `TrueToMagnetic`, then
snaps to the nearest 5°, and raises
`HeadingModeConfirmed(callsign, magneticHeading)` → `RadarVm.FlyHeadingAsync` (`RadarView.axaml.cs:200-208`). Escape exits
(`RadarCanvas.OnKeyDown`, `:1491`).

## Context menus

The right-click aircraft menu is the one the ground view and the aircraft list show too. `OnAircraftRightClicked` → `BuildAircraftContextMenu` (`RadarView.ContextMenus.cs`) runs `AircraftMenuBuilder.Build` (`Yaat.Client.Core/ContextMenus/`) over a `ClientMenuHost`, with the right-clicked callsign, the earlier selection as the sender of the For section's items, and the radar's **view section** (`BuildViewSection`): Display ▸ (`BuildCanvasDisplay`: Mini/Full datablock, Reset datablock position, Show/Hide nav route, Measure from/to {callsign}; Leader direction, J-ring, Cone; Blank, Unblank), then Draw route, which a surface live-traffic shadow and an aircraft whose phase hides the flight commands (`AircraftMenuBuilder.HidesFlightCommands`: on the ground, landing or rolling) go without. The menu's top level is the header (title, Command…, Note…), the **For section** while another aircraft is selected, the aircraft's **quick commands** (the icon strip, then the text entries; [client-context-menus.md](client-context-menus.md)), Track, Data Block, Squawk, the view section, Favorites, **All Commands ▸**, then Delete and the RPO items; everything but the view section comes from the Core catalog and is the same on every view. With no main view model the radar logs a warning and opens no menu. The quick commands are **situation-driven**: `QuickCommandResolver` takes the server-classified situation's list (`QuickCommandDefaults`) and keeps the entries that fit the aircraft. All Commands holds the full tree: the ground-movement block, then Heading, Altitude, Speed, Navigation, Hold, Approach, Procedures, Tower and Pattern in that fixed order (all but Tower left out while `HidesFlightCommands` holds), Preset taxi route, Draw taxi route…, Ask pilot, Coordination, Edit flight plan, then Warp… and Release to live feed. Within the **Tower** and **Pattern** groups, individual *items* are filtered by `AircraftCommandApplicability` (departure clearances only for ground departures, landing/option clearances only while a landing is pending with VFR options hidden for IFR, runway-exit items only after touchdown, pattern maneuvers gated per leg). `SharedMenuGroups.Tower` and `SharedMenuGroups.Pattern` return `null` when nothing applies, so the group is dropped. Every item in All Commands shows by its own predicate, never by the view or the situation flags.

### Smart-default convention

Each phase action exposes a **one-click top-level item** resolved from aircraft state, with the scrollable submenu as the
*override* — not the default. Promoted here from the `feedback_smart_defaults_in_menus` memory:

- "Cleared visual approach" (`approach.cleared-visual`, IFR aircraft only) resolves the runway via `MenuCatalog.SmartVisualRunway` (`Yaat.Client.Core/ContextMenus/`) — `AssignedRunway`, else the runway of `ActiveApproachId`, else `ExpectedApproach` — and opens the visual-only list (`ClearedVisualSpec`): one `Visual RWY n` row per runway end, the room's landing runways first, the others behind a `Non-arrival runways…` row; that runway's row takes the focus when the room lands on it.
- "Join STAR" resolves the filed STAR via `MenuCatalog.FiledStar` by scanning the filed route for a STAR known at the destination; `JoinStarFlyout.Tabs` puts it first as the selected tab, the destination's other STARs following as tabs.
- The runway and STAR pickers are flyouts with every choice in them, no "override" companion item; the airway and fix pickers are flyouts with a box (Join airway, Cross fix, Depart fix, Hold at fix: [client-context-menus.md](client-context-menus.md)).

When adding a new phase action, add **both** the smart-default item and the override path.

### Per-aircraft scoped pickers (never global)

Pickers enumerate **only this aircraft's** data — never the global `NavigationDatabase` fix/airway lists, which run to tens
of thousands of entries:

All in `Yaat.Client.Core/ContextMenus/MenuCatalog.cs` unless named:

- `RouteFixes` — `IMenuAircraft.RouteFixNames()`: CIFP fixes in the filed route plus the active DCT queue (`NavigationRoute`), deduped; the Cross fix and Depart fix flyouts list it as their Route rows (`FixValueFlyout.cs`, [client-context-menus.md](client-context-menus.md)).
- `FixList.DirectToRoute` (`Yaat.Client.Core/ContextMenus/FixList.cs`) — the Direct to flyout's route rows for an aircraft with a navigating-to fix: `NavigationRoute` from that fix on, then the filed route's fixes after the last occurrence of the nav route's last fix, then the destination; each once, ignoring case, keeping the nav route's spelling, the departure airport never listed. With no navigating-to fix the list is `RouteFixNames()`. Typing in the flyout's box adds a bounded search of every fix (`FixList.Matches`: the 50 nearest the aircraft, however far); see [client-context-menus.md](client-context-menus.md) (Direct to flyout). Tests: `Yaat.Client.UI.Tests/Views/FixListDirectToRouteTests.cs`.
- `SpeedValues` — the Assign speed flyout's chips (a `SpeedChoices`: the knots, and the Mach hundredths; `RNS` and `RFAS` are the flyout's own rows, `FinalApproachKnots` the second's note): knots in tens from the type's `AircraftPerformance.ApproachSpeed` rounded up to the highest of its cruise, climb and descent schedule at the altitude and its profile knot fields (`AircraftProfileDatabase.Get`), plus 20 kt (10 for pistons), capped at 250 below 10,000 ft unless waived. At FL240 and above, a type with a Mach value in its profile (else the jet baseline) adds Mach chips (0.01 steps, 0.60–0.92), and the top chip's IAS caps the knots. With no filed type: 150–350. Tests: `Yaat.Client.UI.Tests/Views/MenuCatalogSpeedTests.cs`.
- `FiledAirways` — airway IDs found in the filed route.
- `JoinStarFlyout.Tabs` — STARs for the aircraft's destination airport (`NavigationDatabase.GetStars`), one tab each; `RunwayDesignators.ForAirport` (`Yaat.Client.Core/ContextMenus/`) — its runway ends in display form, sorted by `RunwayDesignatorComparer`.

New pickers must follow this rule (echoes the `feedback_no_global_navdata_pickers` memory). See
[aircraft-data-model.md](aircraft-data-model.md) for the `AircraftModel` fields (`Route`, `NavigationRoute`, `Destination`,
`AssignedRunway`, `ActiveApproachId`, `ExpectedApproach`) these helpers read.

### Map right-click

Right-clicking empty map space (`OnMapRightClicked` → `BuildMapPointMenu`) opens the **point menu**. The radar's point section (`BuildMapPointSection`) is a bold fix-radial-distance row from `FrdResolver.ToFrd(lat, lon, fixes, navDb)` (client helper `RadarViewModel.TryToFrd`) with "Copy FRD" and the scope-marker pins (Pin marker here, Remove marker near a pin, Clear pinned markers), the measuring items, then a dimmed MVA row (`MvaRow`: `MVA 2,000 ft (sector {name})`, or `MVA 2,000 ft` for a sector with no name). Where no MVA sector covers the point there is no MVA row, with or without a selection.

With an aircraft selected, `AircraftMenuBuilder.Build` with a `MenuPoint` at the click opens with the point header (`SharedMenuGroups.AddPointHeader`): the bold title `{callsign} · {type} → this point`, then a dimmed row `{FRD} · {d} nm, bearing {brg} from the aircraft` (great-circle distance in whole nm, one decimal under 1 nm; the bearing magnetic at the aircraft's position, three digits), without the FRD where `DescribePoint` cannot name the point. When the header carries the FRD (`SharedMenuGroups.PointHeaderShowsFrd`) the section leaves out its bold FRD row; Copy FRD and the pins stay.

For an airborne aircraft it may command (`SharedMenuGroups.IsRadarPoint` and `IsAirborneControllable`) the shared `point.*` items follow as an icon strip of Fly heading, Direct to, Hold left, Hold right and Warp here (the label line under the title names the pointed-at icon, Warp here with its FRD), each where its predicate allows it. The text rows under the strip are Fly heading `<deg>` (the true bearing to the point converted to magnetic, snapped to 5°: `MenuCatalog.PointFlyHeading`), a dimmed `FH {hdg} · {left|right} turn, {n}°` row (the shorter turn from the aircraft's magnetic heading, whole degrees; none under 1°), then Append direct to (while it navigates to a fix); then the section.

A ground aircraft selected gets no strip and only the Warp here ({FRD}) text row, which opens the warp popup: the taxi items need a taxi node, which only the ground view's clicks carry. With nothing selected, or with no main view model (logged), the section is the whole menu, its FRD row included.

The shared items name the point through `ClientMenuHost.DescribePoint` (read from `MainViewModel.ScopeMaps`, as `GetFieldElevation` is, on Radar and Scope menus alike), the same `ToFrd`, so that one FRD string feeds the header, Pin marker, Direct to, Append direct, the holds and Warp here alike, and anything wrong with it propagates to all of them — see [navigation-database.md](navigation-database.md) for why `ToFrd` refuses to anchor on an FRD-named fix.

## DCB and brightness

The DCB (display control bar) buttons are wired in `RadarView.axaml.cs`. `BriteButtons` maps each `BriteTarget`
(`DCB, BKC, MapA, MapB, FDB, … HST, WX, WXC`) to a button + text block (`:410-428`). Clicking a BRITE button latches it as
the active adjust target; the mouse wheel then steps that target's brightness (`OnDcbPointerWheelChanged`, `:535-567`).
RANGE / range-ring-size / PTL-length / history-count use the same latch-then-scroll pattern.

**Hiding the DCB:** `Ctrl+F8` toggles the whole bar via `RadarViewModel.ToggleDcbVisible` (`IsDcbVisible`, persisted as
`UserPreferences.RadarDcbVisible`), mirroring CRC's `StarsSpecialKey.Dcb`. The `DcbContainer` border binds
`IsVisible="{Binding IsDcbVisible}"`; collapsing it lets `RadarCanvas` (the `DockPanel` fill) reclaim the strip. Hiding
resets the sub-menu to `Main`. The keybind is a fixed binding dispatched by `WindowHotkeys.OnWindowKeyDown`.

Brightness flows to the canvas via `SyncCanvasBrightness` (map A/B, range-ring, history) and the per-map A/B category lookup
via `SetBrightnessLookup` (`:75`). `SyncAssignmentTint` (`:494-518`) pushes the rest of the radar-relevant
`UserPreferences` into the canvas: `LocalUserInitials`, assignment/unassigned/selected tint colors, `EuroScopeMode`,
`FlashNoLandingClearance`, `ShowSpeechBubbles`, `AlwaysShowGroundBubblesOnRadar`, `DatablockTextSize`, and the flyout font
size. The tint colors drive `TargetRenderer`'s per-aircraft symbol/datablock color (assigned-to-me vs others).

## Adding a datablock field or interactive tag field

For a new datablock line or EuroScope tag field, touch all of:

1. **Draw** — add the line/field to `RadarDatablockLayout.Compute` (STARS) and/or `EuroScopeTagLayout.Layout` (EuroScope),
   including width/line-slot reservation if it flashes.
2. **Hit-test** — mirror the new line in `RadarCanvas.ComputeDataBlockRect` so the click rect still matches (STARS only;
   EuroScope reuses the cached `LastEuroScopeTags` bounds).
3. **`TagFieldId`** — for an interactive EuroScope field, add the enum member (`EuroScopeTagLayout.cs:10-29`) and emit a
   `TagFieldRect` in `Layout`.
4. **Dispatch** — handle the new `TagFieldId` in `OnEuroScopeFieldClicked` (and, if right-clickable,
   `OnEuroScopeFieldRightClicked`) in `RadarView.axaml.cs`, opening the relevant flyout.

The data shown comes from `AircraftModel` (the client mirror of the wire `AircraftDto`); a brand-new field also needs the
underlying state on the wire — see [aircraft-data-model.md](aircraft-data-model.md) and
[crc-display-state.md](crc-display-state.md) for how a field reaches the client. This doc owns only the on-screen rendering.

## Nav-route overlay wire contract

The Radar Display's **Show nav route** overlay (`ToggleShowPath` / `IsPathShown` / `_shownPathCallsigns` internally) draws
**server-provided fix positions verbatim** — `ShownRouteBuilder` does not re-resolve fix names through the navigation database.
Two wire channels feed it, both defined once in `Yaat.Sim` and carried on the training-hub aircraft DTO:

- **`NavigationRoute` is `List<NavRouteFixDto>`** (`Name`, `Lat`, `Lon`, `RestrictionLines`, `RestrictionTokens`), not a list of names. `AircraftModel` keeps the
  rich list as `NavRouteFixes` and derives the name-only `NavigationRoute` for `FixSuggester`, the context menu and the DataGrid.
  - **An empty `Name` is a synthetic arc-densification vertex** (RF/AF legs expanded to `ARCnn` points; `NavigationTarget.IsSyntheticArcName`
    recognizes the naming, the server-side DTO builder blanks it, `AircraftStatusDescriber` skips it). The overlay draws it as a bare polyline
    point — no diamond, label, or restriction — and the derived name list filters it out.
  - **Crossing restrictions are formatted server-side** into `RestrictionLines` by `Yaat.Sim.Data.Vnas.CrossingRestrictionLabel` (≥/≤ glyphs per
    AIM 5-4-5, `FL` at or above 18000, speed bare except an at-or-above floor `≥kts`, a window as two lines). Both the server route fingerprint
    and the client `BuildShownPathFingerprint` include the restriction text, so a CFIX-only change re-broadcasts and invalidates the overlay cache.
  - **`RestrictionTokens` carries the same lines with latency.** Each line is a list of `NavRouteRestrictionToken` (`Text`, `Latent`) from `CrossingRestrictionLabel.BuildTokenLines`; `RestrictionLines` is unchanged, so an older client still draws from it. It is null for an unrestricted fix or a synthetic arc vertex, and System.Text.Json supplies null when an older server omits it. A client that gets lines but no tokens draws no restriction label for that fix.
  - **A latent altitude or speed draws dimmed.** `DtoConverter.BuildNavigationRoute` marks an altitude latent through `AltitudeRestrictionInForce.IsShownLatent` (not in force by its source, or a published floor above a climb via's "except maintain" top, or a published ceiling below a descend via's bottom). It marks a published SID/STAR speed latent through `NavRouteSpeedLatency.LatentAlongRoute`, which walks the route fix by fix and asks `FlightPhysics.PublishedSpeedApplies` (the judge physics shares) whether an ATC speed (SPD, MACH, SMAX, a bound) or DELETE SPEED RESTRICTIONS is in force there, past crossings that revert or resume; a controller crossing speed is never latent. A queued `AT fix` speed block (`SPD n until E`, `AT N SPD`) is not read by the walk. `RadarRenderer.DrawRestrictionTokens` draws a line's tokens left to right two spaces apart, a latent one in the route colour at about 45% opacity. The server route fingerprint includes each altitude's and speed's latency, so a CM/DM, DVIA, CVIA, DSR, SPD, RNS, RPS or a revert sequencing re-sends the route for a stationary aircraft, and `BuildShownPathFingerprint` hashes the token latency.
- **`NavRouteShapes` is `List<NavRouteShapeDto>`** (`Kind`/`Points`/`Labels`/`LabelLat`/`LabelLon`), computed by `NavRouteOverlayProjector.BuildShapes` from the
  aircraft's current phase for geometry the flat route cannot express: holding racetracks (`HoldingPatternPhase`), procedure turns
  (`ProcedureTurnPhase`), open-ended SID coded-leg vectors (`DepartureProcedurePhase.Legs`, dashed, each labeled via
  `CrossingRestrictionLabel`), and the military route corridor (`MilitaryRoutePhase`, see [military-training-routes.md](military-training-routes.md)). The client renders them as `ShownShapeEntry` in `RadarViewModel.RefreshShownPaths` → `RadarRenderer.DrawShownShapes`,
  through the same `ShownPaths` plumbing. Beyond those four phase types the projector also reads the command queue and two orbit phases (below); coded-leg endpoints are nominal.
  - **A queued hold draws dashed** (`NavRouteShapeKind.PendingHoldRacetrack`): every unapplied `CommandBlock` with a `ReachFix` trigger and parsed commands contributes a racetrack per `HoldingPatternCommand`, in queue order, also while the aircraft already holds (the current hold stays a solid `HoldRacetrack`). It uses the dashed `[10, 5]` paint and turns solid when `HoldingPatternPhase` takes over. The leg with none given and the course conversion are the dispatch's own (`NavigationCommandHandler.StandardHoldLegMinutes`, `HoldInboundTrueCourse`, `ResolveHoldLeg`), so the drawn and flown holds cannot differ; the leg is judged at the altitude the same block assigns (a climb, descend or force altitude command), else `Targets.AssignedAltitude`, else the present altitude.
  - **An orbit draws as a dashed circle** (`NavRouteShapeKind.OrbitCircle`) of the projector's standard-rate turn radius at the present altitude: a `VfrHoldPhase` HFIX orbit centred on the fix (before and at the fix), and an `AirspaceBoundaryHoldPhase` in `Orbit` mode centred on its reference position. Hover, HPP and `LevelOff` draw nothing (HPP is booked as YAAT-1910). A hold-in-lieu future phase draws nothing (booked as YAAT-1907).
  - **Wire and fingerprint.** The two kinds are appended to `NavRouteShapeKind` with no other DTO change; an older client reads the unknown integer and draws them solid. The server route fingerprint (`DtoConverter.BuildNavigationRouteFingerprint`) appends `NavRouteOverlayProjector.BuildPendingShapeKey`, also when the route is empty, so adding, editing or removing a queued hold or orbit re-sends a stationary aircraft.
  - **Course and vector kinds.** `NavRouteShapeKind` also holds `RouteEndCourse`, `HeadingVector`, `FinalApproachCourse`, `ExpectedFinalApproachCourse`, `PendingDepartHeading` and `PendingJoinRadial`; the server produces all six (below). `NavRouteShapeLook.For(kind)` gives each look, and both the Radar (`RadarRenderer.DrawShownShapes`, through `DrawShownShapeStroke`) and the Scope (`ScopeRenderer.DrawIntentShape`) draw from it: `RouteEndCourse`, `HeadingVector` and `PendingDepartHeading` dashed with a filled arrowhead at the last point (the vector tail's 14x7 px head, `RadarRenderer.DrawArrowhead` / `ScopeRenderer.DrawIntentArrowhead`); `FinalApproachCourse` solid; `ExpectedFinalApproachCourse` and `PendingJoinRadial` dashed with no arrow; an unknown kind solid with no arrow.
  - **Route-end course and heading vector.** `NavRouteOverlayProjector` draws a `RouteEndCourse` (a two-point 5 NM line from the last route fix along its terminal course, FM or VM, made true) only when `FlightPhysics.ClassifyRouteEnd` returns `TerminalCourse` and no approach clearance is pending; the projector and `FlightPhysics.ApplyRouteEndLateral` share that one classifier, so the drawn and flown branch cannot differ. A `HeadingVector` (a two-point 5 NM line from the aircraft along `TargetTrueHeading`) is drawn only for an airborne aircraft with no route, no assigned FH heading and a phase of `InitialClimbPhase` or none; an assigned FH heading is still drawn by the client's own tail. The client's `ShownRouteBuilder` reads no CIFP and draws no procedure vector tail of its own; its only tail is the assigned-heading one, anchored at the aircraft on every Radar refresh as on the Scope. The server route fingerprint appends `|E{fix}:{true course}` when a route-end course is drawn. The course is not sent while a hold queued at the route's last fix fires as that fix is sequenced (the aircraft will hold, not fly on); a WAIT at that fix keeps it.
  - **Final approach course.** `NavRouteOverlayProjector` draws a solid `FinalApproachCourse` for an approach that is cleared and flown, or cleared and pending at the route's end: a line from the stored FAC anchor (else the runway threshold) out along the reciprocal of the course, to the greater of 5 NM and the aircraft's along-course distance. An aircraft with an EA (expect approach) and no clearance draws a dashed `ExpectedFinalApproachCourse` instead: a 5 NM line out from the FAC anchor, else from the first common-leg fix, to the anchor or threshold.
  - **When no final draws.** Never for a visual approach, a pattern entry or an aircraft on the ground. Not once a go-around, the missed approach (its fixes, its hold, or a hold at the clearance's `MapHold` fix), a low approach, a touch-and-go or a stop-and-go has started, until a new approach clearance: the one judge is `AirborneFollowHelper.HasEndedTheApproach`. The route fingerprint appends `|AC:{id}:{course}` (cleared), `|AP:{id}:{course}` (pending) or `|AX:{Expected}` (expected) from stored state only, so a new clearance, EA or course re-sends a stationary aircraft.
  - **Queued depart heading and join radial.** `NavRouteOverlayProjector` draws a `PendingDepartHeading` for each queued `DEPART {fix} {heading}`: a dashed 5 NM line from the fix along the depart heading, made true with the aircraft's declination. It draws a `PendingJoinRadial` for each queued `JRADO` or `JRADI`: a dashed 5 NM line from the fix out along the radial's true bearing. Both come after the final approach course and before the queued hold racetracks. The route fingerprint appends `|D{fix}:{heading}`, `|O{fix}:{radial}` (JRADO) or `|I{fix}:{radial}` (JRADI), the value as issued (magnetic), so a new, edited or removed one re-sends a stationary aircraft.
  - **Shape labels do not carry latency.** Under AIM 5-2-9 / 5-4-1 a published crossing binds only under an active "climb via" / "descend via" clearance, AIM 5-2-9.10 cancels published SID *altitude* restrictions once ATC assigns an altitude (speed and lateral survive), and an ODP's crossings (AIM 5-2-9.11) can never be cancelled. Only the fix labels on the flat route distinguish *latent* from active (above); the coded-leg labels on a shape draw as active. A hold that exists only as a published route hold-in-lieu is not drawn until it is the active phase.

DME-arc legs with no charted turn direction take the minor (≤180°) arc via `GeoMath.ResolveArcTurnRight`; all three arc expanders
(`DepartureClearanceHandler`, `ApproachCommandHandler`, `ProcedureLegResolver`) share it, so the overlay and the flown path agree.

## Instructor TPA overlays — `JRING` / `CONE`

Instructor `JRING`/`CONE` are TPA proximity overlays drawn on **YAAT's own radar only**, never projected to the student's CRC. State lives on `ac.Stars.TpaType` (1 = J-Ring, 2 = Cone) + `ac.Stars.TpaSize` (nm, 1–30, parser-validated), flows client-ward via `AircraftStateDto.TpaType`/`TpaSize` → `AircraftModel` → `TargetRenderer.DrawTpaGraphic`, and is snapshotted on `AircraftStarsStateDto` so it replays.

Geometry (from the decompiled CRC `DrawTpaCone`/`DrawTpaJRing`): a J-Ring is a geo circle of radius `TpaSize`; a Cone is a ±`UserPreferences.TpaConeHalfAngleDegrees` (default 2°) needle of length `TpaSize`, drawn along `ac.Heading` (YAAT's leader-line axis — not ground track, unlike CRC, for visual consistency with the YAAT leader). STARS TCW TPA color = `(90, 180, 255)`.

This is distinct from the **automatic ATPA cone**: `StarsTrackDto.TpaType` (Key 30, CRC `RemoteTpaType`) is reserved for the server-computed ATPA cone (`DtoConverter.MapAtpaTpaType` from `AtpaResult`) and is *never* fed from `ac.Stars.TpaType` — doing so leaked one instructor's manual cone onto every student CRC scope (fixed with #189).

CRC controllers draw their own `*J`/`*P` locally and sync via shared track state, so the CRC implied-slew `*J`/`*P` dispatch in `CrcClientState.Stars.cs` is a deliberate no-op — don't convert it into a YAAT overlay. See [crc-display-state.md](crc-display-state.md) for the Key-30 server side.

## ATPA cones

The server's `AtpaResultsChanged` broadcast (see [training-hub-contract.md](training-hub-contract.md)) is projected the same way conflict alerts are: `MainViewModel.ApplyAtpaResults` visits every aircraft and sets `AircraftModel.AtpaLeadCallsign` / `AtpaAllowedSeparationNm` / `AtpaConeState` on the **trailing** aircraft only (absent → null / 0 / `Monitor`); `SeedAtpaResult` covers an aircraft added after the broadcast.

These fields are never assigned in `FromDto`/`UpdateFromDto` and are distinct from the instructor's manual `TpaType`/`TpaSize` (the #189 footgun). Rendering is gated by the `ShowAtpa` preference (Settings › Radar, default off), plumbed like `ShowConflictAlerts`: `RadarView.SyncAssignmentTint` → `RadarCanvas` → `RadarRenderer` → `TargetRenderer.ShowAtpa`, no `RenderSnapshot` field.

- **Actual separation is computed per frame**, not carried on the wire — the broadcast is signature-guarded on pair / allowed separation / cone state, so a wire value would freeze. `DatablockText.BuildAtpaLine` reads `GeoMath.DistanceNm` between the two models; `Compute` takes the resolved lead model (`TargetRenderer.ResolvePeer` on the draw path, `RadarCanvas` on the hit-test path) exactly as `BuildConflictLine` takes the conflict peer.

  The line is drawn only when the lead resolves; it carries the in-trail distance in tenths (`3.2`) after the owner/scratchpad line, steady (not flashing), coloured Monitor = block colour, Warning = yellow, Alert = `(255, 55, 0)` — CRC's `DisplayElementTracks` FDB line-3 rule.
- **Cone geometry follows CRC's `DrawAtpaCone`**: vertex at the trailing aircraft, axis = true bearing to the lead (`GeoMath.BearingTo`; `ProjectPoint` takes true headings and the viewport applies rotation), length = `AtpaAllowedSeparationNm`, half-angle = the `TpaConeHalfAngleDegrees` preference (CRC uses ±2° for both), the allowed separation labelled at the midpoint in the state colour (Monitor = `TpaColor`).
- **Supersession**: an ATPA cone in any state replaces a manual `CONE` on that track; a manual `JRING` still draws (CRC draws the J-ring unconditionally and the manual cone only when `RemoteTpaType == None`).
- Every pair the server computes is shown; the volume's Monitor/Alert TCP adaptation (who may see which cone in STARS) is not applied on YAAT's radar.

## Pitfalls

- **The two-thread split is unenforced.** `CreateRenderSnapshot` (UI thread) must capture everything;
  `RenderFromSnapshot` (render thread) must never read a `StyledProperty` or mutable canvas field. The rule lives only in
  `MapCanvasBase` doc-comments. Mutable interaction state (the shared `DataBlockViewState` collections, the filtered
  aircraft list) is defensively copied into the snapshot for exactly this reason — don't reach past the snapshot.
- **Per-callsign datablock state must not live on the canvas.** It belongs to `RadarViewModel.DataBlockState` /
  `GroundViewModel.DataBlockState` (see "Session-persistent datablock state" above); canvas-local fields are lost on
  pop-out (fresh canvas) and were wiped by Layout-binding churn on tab switches (#350). Never clear the store from a
  canvas property-changed handler.
- **Datablock geometry is computed twice.** The draw path (`RadarDatablockLayout.Compute` /
  `EuroScopeTagLayout.Layout`) and the hit-test path (`RadarCanvas.ComputeStableRectAtOrigin`, which re-measures with its
  own `HitTestStyle`) must stay consistent. Edit one without the other and clicks miss the rect.
  `DatablockHitTestParityTests` (Yaat.Client.UI.Tests) now pins this: it asserts the two rects match at the default font
  size and at 9 / 14 / 18 px. It was written after finding that `RadarCanvas.DatablockTextSize` resized only the
  renderer's font and left the hit-test font pinned at 12 px, so every non-default `RadarDatablockFontSize` measured
  clicks against different metrics than the drawn glyphs. **Both canvases' size setters must push the new size into their
  hit-test font** (`RadarCanvas`/`GroundCanvas`), or the parity tests fail.
- **Flash slots reserve space; handoff does not.** The `NoLndgClnc` warning reserves its width and line slot even when the
  500 ms flash is off-phase so the block doesn't pulse. The handoff indicator does not reserve — the STARS hit-test path
  compensates by always sizing line 3 as if the handoff were present. Forget either compensation and the click target
  flickers with the flash.
- **`DefaultPixelsPerDeg = 5000.0` is copied in three places** (`MapViewport`, `RadarCanvas.ZoomToRange`,
  `RadarCanvas.UpdateViewRangeNm`). Change one and the RANGE spinner desyncs from the actual zoom.
- **Don't touch the Range/Zoom sync without the guards.** `_suppressRangeFit`, `_suppressCenterSync`, and `_initialFitDone`
  exist to break the bidirectional feedback loop between the RANGE spinner, the bound center, and the live viewport.
  Mis-setting a guard causes an infinite pan/zoom oscillation. Initial fit is deferred until the canvas has pixel
  dimensions because the radar tab can be laid out at 0×0 before it's shown.
- **`RotationDeg` is magnetic declination; UI bearings are magnetic.** Round-trip every fly-heading vector / PTL through
  `MagneticToTrue` / `TrueToMagnetic` before projecting, or vectors draw at the wrong angle in high-declination areas.
- **EuroScope-mode hit-testing uses last frame's cached bounds.** `ComputeDataBlockRect` / `FindTagFieldAtPoint` read
  `LastEuroScopeTags`, populated during the previous render. An aircraft filtered out of a frame has stale or absent bounds
  and won't hit-test until it draws again. STARS mode recomputes live instead.
- **`FilterAircraft` is the one place visibility lives.** Both draw and every `Find*` re-invoke it. Change what's visible
  there, but preserve the draw-equals-hit-test symmetry (especially the #169 ground-bubble exception).
- **The pointer ladder is order-sensitive.** Every rung early-returns with `e.Handled`. Insert a new interactive mode at the
  wrong position and it silently steals clicks from a lower rung.
- **`ShownRouteBuilder.cs` is not in this stack and not command-input UX** — it builds the route path segments that
  `RadarViewModel` assembles into the `ShownPathEntry` overlay drawn by `RadarRenderer.DrawShownPaths`.
- **Don't re-attempt SSAA / TAA / MSAA on the Radar or Ground views.** 2×/3× supersampling was implemented (offscreen GPU
  `SKSurface` at N× device resolution, downscaled) and **reverted** — the downscale softens the crisp edges and text more
  than the original jaggies bother. Every paint already sets `IsAntialias = true` (per-primitive analytic coverage) and text
  uses subpixel glyph positioning, which is the ceiling for crisp 2D vector content; supersampling only trades sharpness for smoothness.
  The only real lever for "crisp and not jagged" is higher pixel density (OS scaling > 100% or a higher-DPI monitor) — a
  spatial-resolution limit, not an AA-technique one. (`SKFont.Subpixel` is the SkiaSharp 3 spelling of the old
  `SKPaint.SubpixelText`.)
- **Text needs an `SKFont`, not just an `SKPaint`.** SkiaSharp 3 moved every text member off `SKPaint` (`TextSize`,
  `Typeface`, `SubpixelText`, `MeasureText`, `TextAlign`, `GetFontMetrics`) onto `SKFont`. The two travel together as
  `Views/Map/TextStyle.cs` — a non-owning `(SKFont, SKPaint)` pair with `Size`, `LineHeight` (`Size + 2`), and
  `Measure(text)`. Every layout helper takes a `TextStyle` so the draw and hit-test paths cannot drift to different
  metrics. Build fonts via `PlatformHelper.MonospaceFont(size)` / `MonospaceFontBold(size)`; the caller owns and disposes
  them. Draw with the 6-argument overload
  `canvas.DrawText(text, x, y, SKTextAlign.Left, style.Font, style.Paint)` — the 5-argument form (no `SKTextAlign`) is
  `[Obsolete]`. Alignment is now a per-draw argument, not paint state, so anything that used to set
  `SKPaint.TextAlign` must carry the alignment itself (see `GroundRenderer`'s `LabelCandidate.Align`).
- **`SKPaint.IsAntialias` does not control text antialiasing.** With an explicit `SKFont`, glyph AA comes from
  `SKFont.Edging` (default `Antialias`, which is what the radar/ground text uses). Don't "fix" text AA by touching
  `IsAntialias`, and don't reach for `SKFontEdging.SubpixelAntialias` — that's LCD subpixel AA (the old `LcdRenderText`,
  never used here) and it visibly changes text rendering.
- **Image sampling uses `SKSamplingOptions`.** `SKFilterQuality` is gone. The tower-cab blit uses
  `new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)` passed to
  `canvas.DrawImage(image, x, y, sampling, paint)`; one-shot downscales use `new SKSamplingOptions(SKCubicResampler.Mitchell)`
  (`TowerCabImageService`). Offscreen GPU rendering still goes through the Skia lease
  (`ISkiaSharpApiLeaseFeature` → `GrContext`, `SKSurface.Create`), which is unchanged in Avalonia 12.
