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
4. **Runways** (drawn when GND *or* MAP is on; labels + threshold markers only when GND is on and an aircraft is selected)
5. **Layer 3 — YAAT ground layout** (only when `ShowYaatLayout`), brightness-scaled, in this sub-order:
   - `DrawEdges` (taxiway/ramp infrastructure)
   - `DrawPreviewRoute` → `DrawShownTaxiRoutes` → **`DrawHoverRoute`** → `DrawDrawnRoute` → `DrawDrawHoverPreview` (the five route overlays — see below)
   - `DrawNodes` (hold-short / parking / spot icons) then `DrawLabels`
6. **Aircraft symbols** then **datablocks** (always drawn, independent of the layout toggle)
7. **Hovered-only labels** (a second `DrawLabels` pass for hover-revealed hidden elements)

The route overlays sit **between infrastructure and aircraft** so routes never occlude the aircraft symbols. `DrawHoverRoute` is drawn **last of the overlays** so the transient hover highlight paints on top of any persistent shown route.

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

Whenever a route must be drawn, `GroundViewModel.ResolveRemainingRoute(ac)` **reconstructs the geometry locally**: it parses the taxiway-name string, finds the aircraft's nearest ground node, trims the sequence to start at `CurrentTaxiway`, and re-runs `TaxiPathfinder.ResolveExplicitPath` against the client's cached `AirportGroundLayout` (`_domainLayout`). It passes `AssignedRunway` (when set) as `ExplicitPathOptions.DestinationRunway` so the reconstruction **truncates at the runway hold-short** — the same hint the server used to build the route. Without it the resolver has no runway terminus and walks the last taxiway to its full physical extent, drawing past the hold-short bar (the `TAXI D C B 28R` "highlights all of B past 28R" bug).

**This reconstruction depends on the following DTO fields being broadcast live.** All are in the server's `TrainingDtoFingerprint` (`yaat-server` `AircraftChangeTracker.cs`), so any change to them fires an `AircraftUpdated` that the client refreshes on:

- `Lat` / `Lon` — change every tick as the aircraft moves; this alone re-trims the drawn route to the aircraft's advancing position.
- `TaxiRoute` (the formatted string) — changes on re-clearance and drops to `""` when the route completes (which is also how "show all" stops drawing a finished aircraft).
- `CurrentTaxiway` — changes as the aircraft crosses junctions.
- `AssignedRunway` — changes on re-clearance; drives the hold-short truncation above.

Consequences to respect when changing this area:

- **The drawn route is a client reconstruction, not a mirror of the server's `TaxiRoute` object.** It is geometrically correct only while the client's cached layout matches the server's. It does not reflect the server's `CurrentSegmentIndex` or hold-short offsets.
- **Do not rely on any field for live route updates unless it is in `TrainingDtoFingerprint`.** If a future field must drive a mid-taxi redraw, add it to that fingerprint or the client will only see it on join.
- **Refresh cadence is the aircraft-update batch.** There is no separate timer; if `RefreshShownTaxiRoutes` stops being called from the update handler, drawn routes freeze.

## Pointer input and hit-testing

`GroundCanvas` hit-tests three things, all against the render viewport:

- `FindAircraftAtPoint` — nearest aircraft symbol within a 28 px radius (iterates `VisibleAircraft()`).
- `FindDataBlockAtPoint` — topmost datablock rectangle under the point.
- `FindNodeAtPoint` / `FindRunwayThresholdAtPoint` — ground graph nodes and runway thresholds.

Aircraft hit-testing runs on **click** (`OnPointerPressed`) and on **hover** (`OnPointerMoved` → `UpdateHoveredAircraft`, using `FindDataBlockAtPoint(pos) ?? FindAircraftAtPoint(pos)`, suppressed while `IsDrawingRoute`). Node hover (`UpdateHoveredNode`) drives the draw-route preview and cursor. When the hovered aircraft changes, `GroundCanvas` raises `HoveredAircraftChanged(callsign?)` and `MarkDirty()`; `OnPointerExited` raises `null`.

### Events raised by `GroundCanvas`

`NodeRightClicked`, `AircraftRightClicked`, `AircraftLeftClicked`, `AircraftCtrlClicked`, `EmptySpaceClicked`, `RunwayThresholdClicked`, `RunwayThresholdRightClicked`, `DrawNodeClicked`, `DrawNodeFinished`, `DrawNodeHovered`, **`HoveredAircraftChanged`**. `GroundView.axaml.cs` subscribes/unsubscribes them all in `OnLoaded`/`OnUnloaded`.

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
- **Per-session vs global state.** The show/hide override sets are per-session and cleared on layout change; the two settings are global `UserPreferences`. Don't conflate them with the per-scenario `SavedGroundSettings`.
- **Text draws through a `TextStyle`, not a bare `SKPaint`.** SkiaSharp 3 keeps text state on `SKFont`; `GroundRenderer` pairs each text paint with a font (`_taxiLabelFont`, `_nodeLabelFont`, `_dataBlockTextFont`, …) and `DataBlockLayout.Compute` takes a `Views/Map/TextStyle`. `LabelTextSize`/`DatablockTextSize` resize the **fonts**, and `GroundCanvas`'s hit-test font must be resized alongside the renderer's or ground datablock clicks miss at non-default sizes (guarded for the radar by `DatablockHitTestParityTests`). See [radar-rendering.md](radar-rendering.md#pitfalls) for the full `SKFont` contract.
- **Alignment is a draw argument now.** `SKPaint.TextAlign` is gone, so `LabelCandidate` carries its own `Align` and `DrawLabels` passes it to `canvas.DrawText(..., label.Align, style.Font, style.Paint)`. Runway labels are centered; everything else is left-aligned. Add a new label kind without setting `Align` and it silently left-aligns.
- **The tower-cab blit is mipmapped on purpose.** `new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)` — dropping the mipmap mode makes Skia walk the full 8K source per output pixel at typical airport zoom (~30% GPU).
