# Radar Display and Rendering

> Read this before touching anything under `src/Yaat.Client/Views/Radar/` or the shared `src/Yaat.Client/Views/Map/`
> base: `RadarCanvas`, `RadarRenderer`, `TargetRenderer`, `VideoMapRenderer`, the datablock/tag layout structs,
> the radar context menus, the EuroScope interactive tag flyouts, or `MapViewport`. The two-thread snapshot split and the
> draw-vs-hit-test geometry duplication are unenforced invariants — break one and you get a cross-thread race or clicks that
> miss text, with no compiler signal.

This is the mechanics layer beneath [architecture.md](architecture.md)'s one-line radar file list. It does not re-list the
files — it explains the pipeline, the projection contract, the datablock geometry, the pointer-handler ladder, and the menu
conventions. The EuroScope tag mode mirrors the upstream EuroScope behavior documented in
[euroscope/overview.md](euroscope/overview.md) (and siblings) — those docs are the external design reference; this one is the
YAAT implementation.

## File map

All paths are under `src/Yaat.Client/`.

| File | Role |
|---|---|
| `Views/Map/MapCanvasBase.cs` | Abstract base: pan/zoom, the UI-thread→render-thread snapshot split, the 100 ms force-repaint timer |
| `Views/Map/MapViewport.cs` | Equirectangular lat/lon ↔ screen projection, rotation, zoom |
| `Views/Radar/RadarCanvas.cs` | The radar `Control`: styled properties, pointer handlers, hit-testing, Range/Zoom sync, datablock drag |
| `Views/Radar/RadarRenderer.cs` | Per-frame orchestrator: clears, then draws maps → rings → fixes → paths → targets → overlays |
| `Views/Radar/TargetRenderer.cs` | Aircraft symbols, leader lines, datablocks, PTLs, history trails, speech bubbles |
| `Views/Radar/VideoMapRenderer.cs` | Video-map polylines with A/B brightness categories |
| `Views/Radar/RadarDatablockLayout.cs` | Pure layout struct for the STARS full datablock (up to 5 lines) |
| `Views/Radar/EuroScopeTagLayout.cs` | Pure layout for the EuroScope tag (4 lines + ModeC + NoLndgClnc) with per-field rects |
| `Views/Radar/RadarView.axaml.cs` | Wires canvas events; dispatches EuroScope field clicks to flyouts; DCB/brightness buttons |
| `Views/Radar/RadarView.ContextMenus.cs` | Right-click menu builders (aircraft + map FRD) |
| `Services/ContextMenuProfileService.cs` / `Services/ContextMenuProfile.cs` | Phase → primary/secondary/hidden `MenuGroup` profile |
| `Views/Radar/Flyouts/*.cs` | EuroScope interactive-tag pickers (altitude, speed, runway, squawk, scratchpad, handoff) + heading mode |

`Services/ShownRouteBuilder.cs` is **not** part of this stack and is **not** command-input UX — it builds the
"show flight path" path segments (waypoints + optional vector tail) that `RadarViewModel` assembles into the
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
This is stated only in the `MapCanvasBase` doc-comments (`MapCanvasBase.cs:53-65`); nothing enforces it. Because of it,
mutable interaction state that the renderer needs is **defensively copied into the snapshot**: `_minifiedCallsigns` and
`_highlightedCallsigns` are copied into fresh `HashSet`s, `_dataBlockOffsets` into a fresh `Dictionary`, and the aircraft
list is filtered + z-ordered into a new list (`RadarCanvas.cs:762-796`). Reading any of those directly inside
`RenderFromSnapshot` would be a nondeterministic cross-thread access.

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
4. **Fixes** — crosses + labels; programmed fixes get a distinct color and always-on label (`:324-327`, `DrawFixes`).
5. **Shown flight paths** — the "show flight path" overlay, drawn behind aircraft (`:330-333`, `DrawShownPaths`).
6. **Aircraft targets** — `TargetRenderer.Render` (`:336-349`).
7. **Heading-mode preview** — the live elastic vector when EuroScope heading mode is active; above aircraft, below the
   drawn route (`:352-359`, `Flyouts.HeadingPreviewRenderer.Render`).
8. **Drawn route overlay** — the in-progress draw-route waypoints + rubber-band line (`:362-369`).
9. **Weather overlay** — METAR text block, top-left (`:372-375`).

Inside step 6, `TargetRenderer` first draws **history trails** behind all symbols (`TargetRenderer.cs:198-202`), then does a
**two-pass deferred render**: aircraft with an active speech bubble are held back to a second pass so their symbol,
datablock, and bubble pill paint on top of neighboring aircraft (`:207-258`). The deferred list is allocated only when
speech bubbles are enabled.

## Datablock and tag layout

Two layout families, selected by the `EuroScopeMode` preference:

**STARS — `RadarDatablockLayout.Compute`** (`RadarDatablockLayout.cs:48`). Up to five lines:
1. Callsign (`*`-suffixed for VFR).
2. Altitude-hundreds + speed-tens + CWT/type.
3. Owner + flashing handoff + scratchpads (`.sp1 +sp2`) + `[assignedTo]`.
4. `ModeC` (rendered struck-through) when the transponder is in Standby.
5. `NoLndgClnc` warning (red, flashing).

**EuroScope — `EuroScopeTagLayout.Layout`** (`EuroScopeTagLayout.cs:52`). Four lines plus optional ModeC / NoLndgClnc:
1. Owner initials (or `--`) + callsign.
2. Type/CWT + destination.
3. Current altitude + assigned altitude `(NNN)` + assigned speed `Snnn`/`ASP` + assigned heading `Hnnn`/`AHDG`.
4. `Rrwy` + scratchpads + flashing handoff (only when at least one is set).
5/6. `ModeC` (struck-through) and `NoLndgClnc` as needed.

The EuroScope layout returns a `EuroScopeTagResult` carrying the tag `Bounds` plus a list of `TagFieldRect`
(`field id, rect, text`) — one per clickable field (`EuroScopeTagLayout.cs:31-35`). Empty assigned fields still emit their
identifier (`ASP`, `AHDG`, `(---)`) so the click target stays stable when nothing is assigned.

### Flash slots

The handoff indicator (both layouts) and the `NoLndgClnc` warning blink on a 500 ms cycle:
`Environment.TickCount64 / 500 % 2 == 0` (`RadarDatablockLayout.cs:66,119`; `EuroScopeTagLayout.cs:148,187`). The
`NoLndgClnc` slot **reserves its width and a line slot even when the flash is off-phase**, so the datablock height and the
hit-rect width don't pulse with the flash (`RadarDatablockLayout.cs:73-90`; `EuroScopeTagLayout.cs:181-185`). The handoff
indicator does **not** reserve when off — the STARS hit-test path compensates by always sizing line 3 as if the handoff
were present (`RadarCanvas.cs:1333-1334`, `BuildOwnerScratchpadLine` always includes the handoff for width).

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
  **Display > Reset to student position** clears the manual offset (`RadarCanvas.ResetDataBlockOffset`).

The projection is null when there is no student position, so the renderer falls back to its prior behavior.

## Geometry — draw vs hit-test (single source of truth)

The full-datablock rectangle has **one source of truth**: `RadarDatablockLayout.Compute`. Both paths call it:

- **Draw path** — `TargetRenderer.DrawLeaderAndDataBlock` calls `RadarDatablockLayout.Compute` (STARS) or
  `EuroScopeTagLayout.Layout` (EuroScope) and paints from the result (`TargetRenderer.cs:356-506`).
- **Hit-test path** — `RadarCanvas.ComputeDataBlockPlacement` (`ComputeDataBlockRect` is a thin `.Rect` wrapper) routes the
  full block through `ComputeStableRectAtOrigin` → `ComputeStableFullRectAtOrigin`, which now just calls
  `RadarDatablockLayout.Compute(ac, 0, 0, _hitTestPaint, …).Rect` (no hand-mirrored line-string re-derivation). It returns
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
rect (`ReducedRect`) are pure static helpers on `RadarDatablockLayout` that **both** the draw path and
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
leader directions with previous-frame hysteresis (no jitter); `FreeForm` runs damped rect repulsion seeded from the prior
frame. Both are deterministic given the same inputs + previous result.

**Where it runs (and why):** the pass runs on the **UI thread** inside `RadarCanvas.CreateRenderSnapshot` (and the ground
equivalent), not on the render thread. The hit-test path (`ComputeDataBlockPlacement` / `FindDataBlockAtPoint`) is also
UI-thread and must read the **same** resolved offsets the draw used, or clicking/dragging a deconflicted block breaks. The
result lives in a per-canvas `_resolvedDeconflictOffsets` dictionary: written once per snapshot build, copied immutably into
the snapshot for the render thread, and read live by the UI-thread hit-test. It also persists across frames as the
stability seed for the next pass. When the mode is `Off` the pass is skipped entirely and the map is empty — existing
placement is untouched.

**Offset precedence** (extended in `RadarDatablockLayout.ResolveBlockOffset`): **manual drag > deconfliction > leader
direction > default**. Manually-dragged blocks are *pinned* (immovable obstacles others avoid); EuroScope tags are pinned
for v1 because their per-field hit rects are cached from the draw. A non-overlapping block resolves to its preferred
offset, so deconfliction only moves labels that actually collide. `BuildDeconflictItems` assembles the input list (anchor,
rect-at-origin, preferred offset, pinned/priority flags) using the same `ComputeStableRectAtOrigin` the hit-test uses.

## Hit-testing and visibility

`RadarCanvas` exposes four "find" helpers, all of which re-run the same filter + z-order so what's clickable matches what's
drawn:

- `FindDataBlockAtPoint` (`:1259`) — topmost datablock whose rect contains the point.
- `FindAircraftAtPoint` (`:1367`) — nearest position symbol within a 28 px radius.
- `FindTagFieldAtPoint` (`:1206`) — EuroScope per-field hit (aircraft + `TagFieldId`); `(null, None)` outside any field.
- `FindBubbleAircraftAtPoint` (`:449`) — aircraft whose speech-bubble rect contains the point (uses `LastBubbleRects`).

`SortByZOrder` (`:1586`) orders by `_dataBlockZOrder` so the last-surfaced (topmost) datablock wins a hit; `SurfaceDataBlock`
bumps a callsign to the top on any interaction (`:586-590`). `_minifiedCallsigns` toggles a callsign between full and
single-line (altitude + CWT) datablock (`ToggleMinifiedDataBlock`, `:572-580`).

**`FilterAircraft` is the single source of which aircraft are drawable AND hit-testable** (`:1648`, pure + static so it's
unit-testable). It hides delayed aircraft always, and on-ground aircraft in the non-top-down (radar) view — **except**
ground aircraft with an active speech bubble whose airport isn't shown on a ground view, so a SAY/pilot/WARN prompt for a
taxiing aircraft isn't missed (`ShouldSurfaceGroundBubble`, `:1691`; the `AlwaysShowGroundBubblesOnRadar` preference forces
all ground bubbles to surface). It also hides an airborne aircraft whose displayed altitude still rounds to `000`
(`AircraftModel.BelowDisplayFloor`, server-stamped from `FieldElevationResolver`: AGL < 100 ft, field-elevation adjusted) in
the non-top-down view, so a departure doesn't appear on the radar before it climbs above the acquisition floor — matching
CRC STARS' coast/skip below the same floor. Every `Find*` helper and `CreateRenderSnapshot` re-invokes `FilterAircraft`, so a
visibility change goes in one place — but you must preserve that draw/hit symmetry.

## Pointer-handler priority ladder

`RadarCanvas.OnPointerPressed` (`:864-1080`) is a strict ladder of early-returns guarded by `e.Handled`. Inserting a mode at
the wrong rung silently steals clicks from a lower one. The order, top to bottom:

1. **Heading-mode click-to-confirm** — when heading mode is active and the button was already released, a left click
   confirms / a right click cancels (`:873-888`).
2. **Draw-route placement** — left click places a waypoint, middle click sets a condition, right click on a waypoint edits
   it (`:890-931`).
3. **Middle-click highlight** — toggle the cyan highlight on the hit datablock/aircraft (`:933-948`).
4. **EuroScope field left-click** — if a `TagFieldId` is hit and `EuroScopeFieldClicked` has a subscriber, fire and return
   (`:954-964`).
5. **EuroScope field right-click** — owner-cell handoff/drop; returns `true` to suppress the fallback menu (`:968-980`).
6. **Datablock** — left click selects (Ctrl = open FP editor) and begins a drag; right click opens the aircraft context
   menu (`:982-1013`).
7. **Aircraft symbol (right)** — right click on a symbol opens the context menu (`:1015-1023`).
8. **Range-ring placement (left)** — places the range ring when in placing mode (`:1037-1046`).
9. **Aircraft symbol (left)** — select / Ctrl-open (`:1048-1062`).
10. **Bubble dismiss / empty-space** — record a bubble-press for release-side dismiss, else fire `EmptySpaceClicked`
    (`:1064-1077`).
11. **Base pan/zoom** — `base.OnPointerPressed` starts a right-button pan (`:1079`).

Two drag thresholds disambiguate clicks from drags: a `25 px²` right-button threshold (`DragThresholdSq`, `:120`,
`:1133`) decides quick-right-click (open map menu) vs right-drag (pan), and a `16 px²` datablock-drag threshold
(`:1090`) decides select vs reposition. The same `16 px²` value (`HeadingModeState.DragThresholdPxSq`) decides
drag-confirm vs click-confirm in heading mode.

## EuroScope interactive tag mode

When `EuroScopeMode` is on, clicking a tag field opens a contextual editor instead of the full aircraft menu.
`RadarCanvas.FindTagFieldAtPoint` returns the hit `TagFieldId`; the canvas raises `EuroScopeFieldClicked`
(`RadarCanvas.cs:1250`), which `RadarView.axaml.cs:107` dispatches by field (`OnEuroScopeFieldClicked`):

| `TagFieldId` | Action |
|---|---|
| `Owner` | Take control (`TakeControlAsync`); right-click → RPO control menu |
| `CurrentAltitude` / `AssignedAltitude` | `AltitudeFlyout` (FL010–FL400 picker; selection dispatches CM or DM by current alt) |
| `AssignedHeading` | `EnterHeadingMode` — live elastic vector |
| `CurrentSpeed` / `AssignedSpeed` | `SpeedFlyout` |
| `Destination` | Enter draw-route mode |
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

The right-click aircraft menu (`RadarView.ContextMenus.cs`, `OnAircraftRightClicked`) is **phase-driven** at two levels.
`ContextMenuProfileService.GetProfile(currentPhase, isOnGround)` (`ContextMenuProfileService.cs:51`) returns a
`ContextMenuProfile` of **primary**, **secondary**, and **hidden** `MenuGroup`s — which submenu *groups* appear. The builder
adds primary groups, a separator, then the remaining (secondary) groups inline (`RadarView.ContextMenus.cs:121-136`); hidden
groups (e.g. all flight + pattern commands while on the ground or landing) are omitted entirely. Within the **Tower** and
**Pattern** groups, individual *items* are then filtered by `AircraftCommandApplicability` (departure clearances only for
ground departures, landing/option clearances only while a landing is pending with VFR options hidden for IFR, runway-exit
items only after touchdown, pattern maneuvers gated per leg). `BuildTowerSubmenu`/`BuildPatternSubmenu` return `null` when
nothing applies, so the group is dropped even if the profile listed it. A trailing always-visible block adds Track,
Data-block, Squawk, Ask-pilot, Coordination, Display, Sim-control, and RPO-control submenus (`:138-150`). The aircraft-list
(`DataGridView.ContextMenu.cs`) and ground (`GroundView.axaml.cs`) menus consult the same `AircraftCommandApplicability`
predicates so all three surfaces agree.

### Smart-default convention

Each phase action exposes a **one-click top-level item** resolved from aircraft state, with the scrollable submenu as the
*override* — not the default. Promoted here from the `feedback_smart_defaults_in_menus` memory:

- "Cleared visual approach `<rwy>`" resolves the runway via `TryGetSmartRunway` — `AssignedRunway`, else the runway of
  `ActiveApproachId`, else `ExpectedApproach` (`RadarView.ContextMenus.cs:720-756`). If a smart runway exists it's the
  top item; the picker label becomes "(other)…".
- "Join STAR `<id>`" resolves via `TryGetFiledStar` by scanning the filed route for a STAR known at the destination
  (`:780-804`).
- Single-value pickers (one filed airway, one route fix) likewise promote the lone value to a direct item and offer
  "(other)…" for the rest (`AddJoinAirwayItems` `:882`, `AddRouteFixItem` `:948`).

When adding a new phase action, add **both** the smart-default item and the override path.

### Per-aircraft scoped pickers (never global)

Pickers enumerate **only this aircraft's** data — never the global `NavigationDatabase` fix/airway lists, which run to tens
of thousands of entries:

- `GetRouteFixes` — CIFP fixes in the filed route plus the active DCT queue (`NavigationRoute`), deduped (`:910-946`).
- `GetFiledAirways` — airway IDs found in the filed route (`:856-880`).
- `GetStarIds` / `GetRunwayDesignators` — STARs / runway ends for the aircraft's destination airport (`:806-849`).

New pickers must follow this rule (echoes the `feedback_no_global_navdata_pickers` memory). See
[aircraft-data-model.md](aircraft-data-model.md) for the `AircraftModel` fields (`Route`, `NavigationRoute`, `Destination`,
`AssignedRunway`, `ActiveApproachId`, `ExpectedApproach`) these helpers read.

### Map right-click

Right-clicking empty map space (`OnMapRightClicked`, `:1187`) shows a fix-radial-distance header from
`FrdResolver.ToFrd(lat, lon, fixes)` with a "Copy FRD" item, and — when an aircraft is selected — a "Fly heading `<deg>`"
item computed from the bearing to the clicked point (snapped to 5°).

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

## Pitfalls

- **The two-thread split is unenforced.** `CreateRenderSnapshot` (UI thread) must capture everything;
  `RenderFromSnapshot` (render thread) must never read a `StyledProperty` or mutable canvas field. The rule lives only in
  `MapCanvasBase` doc-comments. Mutable interaction state (`_minifiedCallsigns`, `_highlightedCallsigns`,
  `_dataBlockOffsets`, the filtered aircraft list) is defensively copied into the snapshot for exactly this reason — don't
  reach past the snapshot.
- **Datablock geometry is computed twice and there is no parity test.** The draw path (`RadarDatablockLayout.Compute` /
  `EuroScopeTagLayout.Layout`) and the hit-test path (`RadarCanvas.ComputeDataBlockRect`, which re-measures with its own
  `_hitTestPaint`) must stay consistent. Edit one without the other and clicks miss the rect.
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
