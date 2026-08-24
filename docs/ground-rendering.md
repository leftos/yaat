# Ground View Rendering

The **Ground View** is the top-down airport surface map (taxiways, runways, parking, aircraft) used for tower/ground control. It is a **distinct control tree** from the en-route Radar View — a separate canvas, renderer, and view-model — though both extend the same `MapCanvasBase` and share its two-thread snapshot pipeline, pan/zoom, and datablock deconfliction.

> Read [`radar-rendering.md`](radar-rendering.md) first for the shared `MapCanvasBase` mechanics: the **two-thread render pipeline**, the **no-StyledProperty rule** (styled-property values must be copied into the immutable render snapshot before the render thread touches them), the 100 ms force-repaint timer, `MapViewport`, and the `ScrollSensitivity` scroll-zoom scaling. This doc covers only what is ground-specific.

Ground's Shift+wheel rotation (`GroundCanvas.OnPointerWheelChanged`) scales its per-notch degrees by the inherited `MapCanvasBase.ScrollSensitivity`, pushed from `GroundView.ApplyFontSizePreferences` / `SyncSpeechBubblePreferences` (#275).

For taxi-route *resolution and following* (the pathfinder and navigator), see [`ground/README.md`](ground/README.md). This doc is about *drawing* the ground view and the client-side route overlays.

## File map

| File | Role |
|------|------|
| `Views/Ground/GroundCanvas.cs` | `MapCanvasBase` subclass: `StyledProperty` inputs, pointer input, hit-testing, per-frame `RenderSnapshot` assembly, per-callsign canvas-local display state |
| `Views/Ground/GroundRenderer.cs` | Stateless SkiaSharp drawing: 3 background layers + route overlays + nodes + aircraft + datablocks. Owns the `SKPaint`s. `Render(...)` is the whole frame |
| `Views/Ground/GroundView.axaml` + `.axaml.cs` | The user control: binds VM → canvas styled properties, wires canvas events, builds the right-click context menus, hosts the layer/label toolbar |
| `Views/Ground/GroundViewWindow.axaml.cs` | Pop-out window host — shares the same `MainViewModel.Ground` view-model instance |
| `ViewModels/GroundViewModel.cs` | Ground-view state: layout load, per-scenario view settings, taxi-route overlays, draw-route mode, display prefs |

## Render order (one frame)

`GroundRenderer.Render(...)` paints strictly back-to-front:

1. **Background clear**
2. **Layer 1 — satellite image** (`ShowSatelliteImage`, brightness-scaled)
3. **Layer 2 — tower-cab video map overlay** (`ShowVideoMapOverlay`)
4. **Runways** (drawn when GND *or* MAP is on; labels + threshold markers only when GND is on and an aircraft is selected), then **ADW marks** under the same condition plus the `ADW` toggle — see below
5. **Layer 3 — YAAT ground layout** (only when `ShowYaatLayout`), brightness-scaled, in this sub-order:
   - `DrawEdges` (taxiway/ramp infrastructure)
   - `DrawPreviewRoute` → `DrawShownTaxiRoutes` → **`DrawHoverRoute`** → `DrawDrawnRoute` → `DrawDrawHoverPreview` (the five route overlays — see below)
   - `DrawNodes` (hold-short / parking / spot icons) then `DrawLabels`
6. **Aircraft symbols** then **datablocks** (always drawn, independent of the layout toggle)
7. **Hovered-only labels** (a second `DrawLabels` pass for hover-revealed hidden elements)
8. **Range/bearing lines** — the distance measuring tool, drawn *after* `Render` returns, from `GroundCanvas.RenderFromSnapshot` via `GroundRenderer.DrawRangeBearingLines` → the shared `Views/Map/RangeBearingRenderer`. Unlike the five route overlays these are **not** graph-snapped: a measurement is a plain two-point line between arbitrary lat/lons, so it cannot use `DrawRoute`. The snapshot carries them already resolved (`ResolvedRbl`) because an endpoint latched to an aircraft has to be looked up against the live aircraft list on the UI thread. Only lines tagged `RblView.Ground` are resolved into the snapshot — radar-view measurements never render here. When a line's far endpoint is off-screen, `RblLabelPlacement` pulls the label back to where the line exits the viewport and clamps it fully inside.

The route overlays sit **between infrastructure and aircraft** so routes never occlude the aircraft symbols. `DrawHoverRoute` is drawn **last of the overlays** so the transient hover highlight paints on top of any persistent shown route.

## Datablock composition

`DataBlockLayout.Compute(AircraftModel, …)` is the **single source of truth** for both the draw pass (`DrawOneDataBlock`) and the click hit-test (`GroundCanvas.FindDataBlockAtPoint`), so a new line or callsign suffix grows the click rect automatically. Line 1 is the callsign, plus a trailing `*` when pre-armed for auto-delete (`AutoDeletePending`) and a ` {runway} #N` departure-queue suffix (e.g. `28R #2`) when `AircraftModel.RunwayQueuePosition > 0` — the runway comes from `AircraftModel.RunwayQueueRunway` and is omitted defensively if blank. An **intersection departure** names its entry taxiway too (`28R@E #2`, from `AircraftModel.RunwayQueueIntersection`); a full-length departure leaves it empty and reads `28R #2`. All three are **server-computed** (`RunwayDepartureQueue` + `RunwayEntryPoint` in Yaat.Sim, wired through `AircraftStateDto.RunwayQueuePosition` / `RunwayQueueRunway` / `RunwayQueueIntersection`); the client only displays them — no client-side ranking or classification, mirroring the `SmartStatus` and taxi-route-reconstruction contracts. Line 2 is `cwt/type fix`; line 3 the altitude (airborne only); then the **beacon-code mismatch** slot (`DataBlockLayout.SquawkLine`, e.g. `1200 0301`) — gated by the radar's `RadarDatablockLayout.TryGetSquawkMismatch` and drawn by the shared `TargetRenderer.DrawSquawkMismatchLine` (reported solid, assigned dim-pulsing on the 500 ms cycle, animated by `MapCanvasBase`'s 10 Hz repaint), so the two views can never disagree on when or how a mismatch shows; then line 4 (hold / `→yield` / `SqStby` — mutually exclusive with the mismatch line because the gate returns false on Standby); then the amber note line.

### Session-persistent datablock state (`DataBlockViewState`)

Per-callsign datablock UI state — manual drag offsets, highlights, hide/show choices (and the `StartWithAllHidden`
inversion mode), z-order — lives in `GroundViewModel.DataBlockState` (`GroundDataBlockViewState`,
`ViewModels/DataBlockViewState.cs`), **not** on the canvas (issue #350). The canvas binds it via the `DataBlockState`
styled property (`GroundView.axaml`) and mutates it in place; a private local instance backs bare-canvas tests and
detached windows. Two rules follow:

- The canvas's `Layout`-changed handler (and the `DataBlockState` handler itself) must **never clear** the store — the
  bindings churn to null and back when the docked tab detaches/reattaches its content, which is exactly what used to wipe
  dragged datablock positions on every tab switch.
- Lifecycle clears belong to the view-model layer: `GroundViewModel.LoadLayoutAsync` / `SetLayoutForTesting` /
  `ClearLayout` and the scenario restart/rewind path (`MainViewModel.ReplaceAircraftFromManifest`) call
  `DataBlockState.Clear()`. Because the state is shared by every `GroundCanvas` bound to the view-model, the embedded tab
  and the pop-out window see the same offsets. `SetStartWithAllHidden` only resets the per-callsign choices when the mode
  actually flips, so a pop-out re-applying the preference on load can't wipe them.

## Route overlays

All five overlays flow VM → `GroundCanvas` `StyledProperty` → `RenderSnapshot` → `GroundRenderer`, and all funnel through one primitive, `GroundRenderer.DrawRoute(canvas, vp, layout, TaxiRoute?, SKPaint)`, which walks `TaxiRoute.Segments` (straight `GroundEdge`s and `GroundArc` fillets) and projects each node via `MapViewport.LatLonToScreen`.

| Overlay | Canvas property | Fed by | Paint |
|---------|-----------------|--------|-------|
| Command-build preview | `PreviewRoute` | context-menu `PointerEntered` while building a TAXI/hold-short command | dashed blue |
| Shown taxi routes | `ShownTaxiRoutes` (`IReadOnlyList<ShownTaxiRouteEntry>`) | the taxi-route display feature (below) | 8 rotating colors |
| **Hover route** | `HoverTaxiRoute` | mouse-hover over an aircraft (below) | solid white, stroke 5 |
| Draw-mode route | `DrawnRoutePreview` + `DrawWaypoints` | interactive "Draw taxi route…" mode | — |
| Draw-mode hover | `DrawHoverPreview` | node hover during draw mode | — |

`ShownTaxiRouteEntry(Callsign, Route, Color)` pairs a resolved route with its palette color; `GroundRenderer` maps the color back to the matching pre-built `SKPaint`.

## ADW markings

An **Arrival/Departure Window** is one of the facility-directive aids 7110.65 §3-9-9.b permits in lieu of
applying the §3-9-8 intersecting-runway provisions, where converging centerlines cross within 1 NM of a
departure end. The facility publishes a window on the arrival runway's final approach course; a converging
departure must have begun its takeoff roll before the arrival enters it, must not begin one while the
arrival is inside it, and a takeoff clearance already issued gets cancelled if the roll hasn't started in
time. `GroundRenderer.DrawAdwMarks` draws the two ends of each published window as flat-yellow ticks
perpendicular to the final approach course, matching how CRC's ASDE-X depicts them.

**ADW does not change IFR separation standards.** What it protects is the arrival's *missed approach* from
the converging departure — and only because the directive also requires the go-around to hold runway
heading through the departure's flight path. §3-9-9.c wake-turbulence intervals apply independently and are
untouched by any of this.

**The marks are a reference, not a rule.** Nothing in the simulation reads them: no separation logic, no
pilot behavior, no scoring, and YAAT's own go-around does not know it is supposed to hold runway heading.
They also carry no applicability conditions — the directive that publishes a window sets its own (at KMIA:
1,000 ft ceiling / 3 SM visibility, arrivals between 120 and 170 kt groundspeed entering the window, no
intersection departures while ADW is in use).

**Reading them.** Marks are drawn config-agnostically, exactly as CRC's static overlay does — all of KMIA's
eight ticks are on screen at once, but only one direction of a pairing is live in any given flow. Which
tick applies also depends on the *departure* runway, and the ticks are unlabelled (the reference has no
text): at KMIA on RWY 30, the tick ~350 ft inside the pavement end is the RWY 26L window, and the one
~2,800 ft in is the RWY 26R window. Finally, no mark does not mean no conflict — KMIA's 12/30 centerline
also crosses 9/27's within 1 NM of RWY 27's departure end, and the SOP publishes no window for that pair.

Where the geometry comes from:

- **Data** — the `adw` section of the per-airport sidecar (`Data/ARTCCs/{ARTCC}/Airports/{airport}.json`),
  carrying the published ranges verbatim. See [`Data/ARTCCs/README.md`](../src/Yaat.Sim/Data/ARTCCs/README.md#adw).
  These are facility-directive values — never derive one from runway geometry, and always cite the
  directive in `notes`.
- **Resolution** — `Data/Airport/AdwResolver.cs` (Yaat.Sim), same per-layout-cached shape as
  `BlockedTurnResolver`. Ranges are signed against the outbound direction: **positive is outbound onto
  final, negative is past the threshold and down the runway**, measured from the *landing* threshold
  (`GroundRunway.LandingThresholdForEnd`, which applies the vNAS map's `threshold` displacement — the
  LineString endpoints are pavement ends). The final approach course is taken as the runway centerline
  bearing, which is exact for a straight-in and would be wrong for an offset LDA/PRM final (~860 ft of
  lateral error at 2.7 nm for a 3° offset). Every published ADW to date is on a straight-in.
- **Wire** — resolved server-side in `DtoConverter.ToGroundLayoutDto` and shipped as
  `GroundLayoutDto.AdwMarks`, so the client needs neither the sidecar nor the displacement data. Same
  posture as the hidden-arc resolution alongside it.
- **Drawing** — the inner mark spans exactly the runway width (what makes it read as "on the runway"); the
  outer mark is a fixed ~547 ft tick, the same convention the SFO final-approach overlay uses. Stroke width
  scales in world feet with a screen-pixel floor, like `DrawHoldShortBar`.

The outer marks sit a few miles out on final, well off the airport diagram at normal zoom. `MapViewport`
has no geographic bounds, so they draw correctly and simply come into view as the user zooms out.

Toggle: the `ADW` button on the label/filter toolbar, persisted globally as
`UserPreferences.GroundShowAdwMarkings` and per-scenario as `SavedGroundSettings.ShowAdwMarkings`
(default on — airports without an `adw` section ship no marks, so it is inert elsewhere).

## Taxi-route display feature

Three ways an aircraft's remaining taxi route gets drawn on the ground view, all client-local (no server round-trip), all managed by `GroundViewModel`:

1. **Hover (opt-out, default on)** — `GroundShowTaxiRouteOnHover`. Moving the cursor over an aircraft draws its route transiently in white.
2. **Show all (opt-in, default off)** — `GroundShowAllTaxiRoutes`. Every taxiing aircraft's route is drawn at once.
3. **Manual per-aircraft override** — the right-click **Taxi route** submenu (`GroundView.axaml.cs`), a radio group of `TaxiRouteDisplayMode`: `AlwaysShow`, `AlwaysHide`, `Follow` (track the global setting — the default).

### State and effective visibility

`GroundViewModel` keeps two per-session, per-callsign sets (never persisted, never sent to the server, cleared on layout change via `ClearShownTaxiRoutes`):

- `_shownTaxiRouteCallsigns` — `AlwaysShow` pins.
- `_taxiRouteHiddenCallsigns` — `AlwaysHide` pins.

An aircraft in neither is `Follow`. `GetTaxiRouteMode` / `SetTaxiRouteMode` read/write these sets; the two are mutually exclusive per callsign.

Effective persistent visibility (the transient hover route is separate and overlaid on top):

```
AlwaysShow           -> drawn
AlwaysHide           -> not drawn
Follow               -> ShowAllTaxiRoutes && HasActiveTaxiRoute
```

`IsTaxiRouteVisible(callsign)` is the per-aircraft form. The pure, layout-free set computation is the `static GroundViewModel.ComputeVisibleTaxiRouteCallsigns(forcedShown, forcedHidden, showAll, allAircraft)` helper (unit-tested in `GroundViewModelTaxiRouteVisibilityTests`).

`RefreshShownTaxiRoutes()` rebuilds the `ShownTaxiRoutes` list each call: compute the effective callsign set, allocate stable palette colors (`AllocateRouteColors` — keeps a callsign's color while drawn, reclaims it when dropped, lowest-free-slot for newcomers, cycling past 8), resolve each route's geometry, then refresh the hover route. It runs on **every aircraft-update batch** (`MainViewModel.Aircraft.cs`), so the drawn set and each route's remaining geometry stay live as aircraft taxi.

`SetHoveredAircraft(callsign?)` gates on `ShowTaxiRouteOnHover`, resolves the hovered aircraft's route into `HoverTaxiRoute`, and is refreshed alongside the shown routes so the hover route stays current while the cursor lingers. Hover **ignores** `AlwaysHide` — it is a transient, explicit gesture.

## Contract: routes are reconstructed client-side, not echoed as geometry

**The client never receives taxi-route geometry over the wire.** The `AircraftUpdated`/`AircraftSpawned` DTO carries only:

- `AircraftModel.TaxiRoute` — a formatted taxiway-name string (e.g. `"S T U W W1"`), from `AssignedTaxiRoute.FormatTaxiwaySequence()` server-side.
- `AircraftModel.CurrentTaxiway` — the taxiway the aircraft is on now.
- `AircraftModel.HasActiveTaxiRoute` — whether an incomplete route exists.
- `AircraftModel.Position` — live lat/lon.
- `AircraftModel.AssignedRunway` — the runway the taxi route holds short of (departures). The formatted `TaxiRoute` string lists only taxiways taxied *along*, never the held-short runway, so this is the only channel for it.

Whenever a route must be drawn, `GroundViewModel.ResolveRemainingRoute(ac)` **reconstructs the geometry locally**: it parses the taxiway-name string, finds the aircraft's nearest ground node, trims the sequence to start at `CurrentTaxiway`, and re-runs `TaxiPathfinder.ResolveExplicitPath` against the client's cached `AirportGroundLayout` (`_domainLayout`). It passes `AssignedRunway` (when set) as `ExplicitPathOptions.DestinationRunway` so the reconstruction **truncates at the runway hold-short** — the same hint the server used to build the route. Without it the resolver has no runway terminus and walks the last taxiway to its full physical extent, drawing past the hold-short bar (the `TAXI D C B 28R` "highlights all of B past 28R" bug). When that resolution fails, it hands the structured failure to `RampLaneReposition.TryPlan` with the aircraft's live position, heading and `CurrentTaxiway` — while the pilot is cutting across a ramp onto a parallel lane the map does not connect (SFO M3 → M4, issue #396) the nearest graph node is still on the old lane, and this reconstructs the same free-space `VirtualNode` leg the server planned so the overlay follows the crossing. `DrawRoute` walks segment node references, so a virtual node draws like any other.

**This reconstruction depends on the following DTO fields being broadcast live.** All are in the server's `TrainingDtoFingerprint` (`yaat-server` `AircraftChangeTracker.cs`), so any change to them fires an `AircraftUpdated` that the client refreshes on:

- `Lat` / `Lon` — change every tick as the aircraft moves; this alone re-trims the drawn route to the aircraft's advancing position.
- `TaxiRoute` (the formatted string) — changes on re-clearance and drops to `""` when the route completes (which is also how "show all" stops drawing a finished aircraft).
- `CurrentTaxiway` — changes as the aircraft crosses junctions.
- `AssignedRunway` — changes on re-clearance; drives the hold-short truncation above.

Consequences to respect when changing this area:

- **The drawn route is a client reconstruction, not a mirror of the server's `TaxiRoute` object.** It is geometrically correct only while the client's cached layout matches the server's. It does not reflect the server's `CurrentSegmentIndex` or hold-short offsets.
- **Do not rely on any field for live route updates unless it is in `TrainingDtoFingerprint`.** If a future field must drive a mid-taxi redraw, add it to that fingerprint or the client will only see it on join.
- **Refresh cadence is the aircraft-update batch.** There is no separate timer; if `RefreshShownTaxiRoutes` stops being called from the update handler, drawn routes freeze.
- **The committed node list is trimmed server-side, so the drawn route and the flown route can differ at the head.** `GroundViewModel.StartDrawRoute` anchors the node list at the aircraft's node when drawing *starts*, and the aircraft keeps taxiing while the controller draws and picks a menu entry. `GroundCommandHandler.TrimPassedNodeRefPrefix` drops the nodes it has already passed (see [ground/pathfinder.md](ground/pathfinder.md#node-reference-paths-taxi-1124-352--the-drawn-route-contract)); do not try to re-anchor client-side as well — the server is the authority and the race cannot be closed from here.

## Pointer input and hit-testing

`GroundCanvas` hit-tests three things, all against the render viewport:

- `FindAircraftAtPoint` — nearest aircraft symbol within a 28 px radius (iterates `VisibleAircraft()`).
- `FindDataBlockAtPoint` — topmost datablock rectangle under the point.
- `FindNodeAtPoint` / `FindRunwayThresholdAtPoint` — ground graph nodes and runway thresholds.

Aircraft hit-testing runs on **click** (`OnPointerPressed`) and on **hover** (`OnPointerMoved` → `UpdateHoveredAircraft`, using `FindDataBlockAtPoint(pos) ?? FindAircraftAtPoint(pos)`, suppressed while `IsDrawingRoute`). Node hover (`UpdateHoveredNode`) drives the draw-route preview and cursor. When the hovered aircraft changes, `GroundCanvas` raises `HoveredAircraftChanged(callsign?)` and `MarkDirty()`; `OnPointerExited` raises `null`.

### Events raised by `GroundCanvas`

`NodeRightClicked`, `AircraftRightClicked`, `AircraftLeftClicked`, `AircraftCtrlClicked`, `EmptySpaceClicked`, `RunwayThresholdClicked`, `RunwayThresholdRightClicked`, `DrawNodeClicked`, `DrawNodeFinished`, `DrawNodeHovered`, **`HoveredAircraftChanged`**, `MeasurePointPicked`, `MeasureDragCompleted`, `MeasureCancelled`. `GroundView.axaml.cs` subscribes/unsubscribes them all in `OnLoaded`/`OnUnloaded`.

The three measuring events sit at the **top** of `OnPointerPressed`, above the datablock and route-drawing rungs: Alt+left starts a drag measurement and, while the tool is armed (`IsMeasuring`), a left click picks an endpoint. `MeasureEndpointAt` deliberately does **not** snap to the ground graph — measuring a wingtip-to-hold-bar gap means using the exact point clicked.

### Right-click vs right-drag

The right button does two jobs — a **click** opens a context menu, a **drag** pans — and they are indistinguishable at press time. `OnPointerPressed` therefore decides nothing: it hands the press to the shared `Views/Map/RightClickGesture`, lets `base.OnPointerPressed` start the pan, and returns. `OnPointerMoved` feeds movement in; past 5 px the press latches as a drag and cannot revert. `OnPointerReleased` asks `RightClickGesture.Release()`, which returns the **press** position when a menu is owed (so a pixel of jitter can't slide the menu off a small node) and null when the gesture was a pan. `RadarCanvas` uses the same helper.

Because of this, **`HandleRightClick` is the single place every ground right-click menu is decided**, and it is only ever called from the release path — never on press. It resolves, in order: cancel a half-placed measurement → finish a drawn route at a node → datablock → aircraft symbol → node → runway threshold (needs a selection) → snap to nearest node. That last fallback is unconditional: with an aircraft selected the menu carries the taxi-route and "Warp here" items, and with nothing selected it still carries the measuring items. Deciding on press instead is what used to make a right-drag starting on a datablock, aircraft, or node fail to pan at all.

### Context menus

`GroundView.axaml.cs` rebuilds the aircraft context menu from scratch on each right-click, gating items by phase via the same `AircraftCommandApplicability` predicates the radar and aircraft-list menus use (so all three agree). The **Taxi route** submenu is a `MenuItemToggleType.Radio` group whose checked item reflects `GetTaxiRouteMode(callsign)`; selecting one calls `SetTaxiRouteMode`.

## Settings propagation

The two display prefs are **global** client preferences, distinct from the **per-scenario** `SavedGroundSettings` bundle (pan/zoom/rotation/label filters/lock) that `GroundViewModel.CaptureSettings`/`ApplySettings` persist per scenario.

- Stored in `UserPreferences` (`GroundShowTaxiRouteOnHover` default `true`, `GroundShowAllTaxiRoutes` default `false`), written via `SetGroundTaxiRouteDisplay`.
- Surfaced as checkboxes in the Settings window (`SettingsViewModel` + `SettingsWindow.axaml`, Display tab).
- Seeded into the live `GroundViewModel` in its constructor, and re-applied to `vm.Ground.*` in the Settings dialog's post-save block (`MainWindow.axaml.cs`). `ShowAllTaxiRoutes`'s change handler calls `RefreshShownTaxiRoutes()` so toggling redraws immediately; `ShowTaxiRouteOnHover`'s clears the hover route when turned off.
- From `GroundViewModel` the flags reach the renderer only where they matter: `ShowAllTaxiRoutes` gates the shown-route set; `ShowTaxiRouteOnHover` gates `SetHoveredAircraft`. Neither is a `GroundCanvas` styled property — the effect is entirely in the VM's route resolution.

The pop-out Ground View window shares the same `MainViewModel.Ground` instance, so all of the above applies to it automatically.

## Pitfalls

- **New route/rendering flags must be threaded into `RenderSnapshot`.** A `GroundCanvas` `StyledProperty` read directly on the render thread violates the no-StyledProperty rule; copy it into the snapshot record in `CreateRenderSnapshot`.
- **Colors flicker if allocation isn't stable.** `AllocateRouteColors` deliberately preserves a callsign's palette index across refreshes; assigning by iteration order would reshuffle colors every aircraft-update batch.
- **`_domainLayout` must be loaded before any route resolves.** `ResolveRemainingRoute` returns `null` with no layout; tests use `SetLayoutForTesting`.
- **Per-session vs global state.** The show/hide override sets are per-session (in `GroundViewModel.DataBlockState`, cleared on layout load/unload by the VM — never by the canvas); the two settings are global `UserPreferences`. Don't conflate them with the per-scenario `SavedGroundSettings`.
- **Per-callsign datablock state must not live on the canvas.** Canvas-local fields are lost on pop-out (fresh canvas) and were wiped by Layout-binding churn on tab switches (#350). See "Session-persistent datablock state" above.
- **Text draws through a `TextStyle`, not a bare `SKPaint`.** SkiaSharp 3 keeps text state on `SKFont`; `GroundRenderer` pairs each text paint with a font (`_taxiLabelFont`, `_nodeLabelFont`, `_dataBlockTextFont`, …) and `DataBlockLayout.Compute` takes a `Views/Map/TextStyle`. `LabelTextSize`/`DatablockTextSize` resize the **fonts**, and `GroundCanvas`'s hit-test font must be resized alongside the renderer's or ground datablock clicks miss at non-default sizes (guarded for the radar by `DatablockHitTestParityTests`). See [radar-rendering.md](radar-rendering.md#pitfalls) for the full `SKFont` contract.
- **Alignment is a draw argument now.** `SKPaint.TextAlign` is gone, so `LabelCandidate` carries its own `Align` and `DrawLabels` passes it to `canvas.DrawText(..., label.Align, style.Font, style.Paint)`. Runway labels are centered; everything else is left-aligned. Add a new label kind without setting `Align` and it silently left-aligns.
- **The tower-cab blit is mipmapped on purpose.** `new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)` — dropping the mipmap mode makes Skia walk the full 8K source per output pixel at typical airport zoom (~30% GPU).
