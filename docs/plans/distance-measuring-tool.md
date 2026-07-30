# Distance measuring tool (Range/Bearing Lines)

A CRC STARS `*T`-equivalent ruler that works in **both** the Radar View and the Ground View, with
endpoints that can **latch onto an aircraft** and travel with it.

## Reference: CRC STARS `*T`

Decompiled at `X:\dev\crc-decompiled\CRC`:

- `Vatsim.Nas.Crc.Ui.Displays.Stars/RblData.cs` — an RBL is `ItemA`/`ItemB`, each either a `GeoPoint`
  (fixed) or a `Track` (latched, re-read every frame).
- `…Stars/StarsDisplayContext.cs:99-109,449-488` — `RblData?[15]`, `PendingRblAnchor`,
  `AddRangeBearingLine`, `ClearRangeBearingLine(index)`, `ClearRangeBearingLines()`, and a private
  `ClearRangeBearingLines(trackId)` that drops any RBL referencing a dropped track.
- `…Stars.Elements/DisplayElementRangeBearingLines.cs` — render + label. Label is
  `{bearing}/{dist:0.00}[/{minutes}]-{slot}`, drawn at **ItemB's** screen point offset `(+9, 0)`.
  Skips the line when a latched track `IsCoastPhase2`. Distance clamps to `###.##` above 999.99;
  minutes clamp to `##` above 99.
- Minutes-to-go appear **only** when exactly one endpoint is a track with groundspeed > 0 and the
  other is a fixed point. Track↔track and point↔point get no time field.

## Decisions (confirmed with the user)

| Question | Decision |
| --- | --- |
| Activation | **All four**: right-click menu item, modifier+drag, dot-command, keyboard shortcut |
| Persistence | **Multiple, numbered, max 15** (CRC-exact), cleared individually or all at once |
| Units | **Auto**: Ground View shows feet below 1 NM then NM; Radar View always NM to 2 dp |
| Readout | **CRC-exact**: magnetic bearing / distance, minutes-to-go only when exactly one end is a moving aircraft |

Deviation from decompiled CRC, deliberate: the bearing is zero-padded to three digits (`087`, not
`87`). CRC renders the raw int; three digits is standard ATC bearing format and is what was approved.
Bearing 0 renders as `360`, matching CRC's `NavCalc.NormalizeHeading`.

## Architecture

**One store shared by both views.** A measurement drawn on the ground shows up on the radar and vice
versa, slot numbers stay consistent across views, and `.rbl 3` is unambiguous. Each view renders the
same lines with its own unit formatting. Lines whose endpoints fall outside a view's extent simply
clip.

New file `src/Yaat.Client/Views/Map/RangeBearingLines.cs` (next to `DatablockDeconfliction.cs`, the
existing precedent for view-agnostic logic shared by both renderers):

- `RblEndpoint` — `Callsign` (non-null ⇒ latched) or a fixed `LatLon`, plus a display label.
- `RangeBearingLine(int Slot, RblEndpoint A, RblEndpoint B)`.
- `RangeBearingLineStore` — 15 slots, `PendingAnchor`, `IsArmed`, `Changed` event,
  `Arm/Disarm/SetAnchor/Complete/Remove/Clear/PruneMissing`.
- `RangeBearingLineFormatter.Format(...)` — the label, with a `RblUnits` switch for ground feet.
- `RangeBearingLineResolver.Resolve(...)` — endpoints → live `LatLon` + label, dropping lines whose
  latched aircraft is gone.

Owned by `MainViewModel`, injected into both child VMs with the existing `SetXxx` pattern
(`MainViewModel.cs:1371-1378`).

### Radar wiring

`StyledProperty` on `RadarCanvas` → read in `CreateRenderSnapshot()` (`RadarCanvas.cs:884`) → new
field on the `RenderSnapshot` record (`RadarCanvas.cs:849`) → `RenderFromSnapshot` → a new
`RadarRenderer.DrawRangeBearingLines`. Modelled on the existing draw-route rubber band
(`RadarRenderer.DrawRubberBandFromOrigin`, `RadarRenderer.cs:823`). Never read a `StyledProperty` on
the render thread.

Pointer: a new rung in `RadarCanvas.OnPointerPressed` (`:1044`) beside `IsPlacingRangeRing`; Escape in
`OnKeyDown` (`:1778`).

### Ground wiring

Same snapshot path: `GroundCanvas.RenderSnapshot` (`:646`) → `CreateRenderSnapshot()` (`:680`) →
`GroundRenderer`. Ground has no free-click placement mode today, so this is new plumbing shaped like
radar's range-ring placement. The existing `GroundRenderer.DrawRoute` is graph-snapped and unusable
here — the ruler needs a plain two-point line via `MapViewport.LatLonToScreen`.

### Shared math

- `GeoMath.DistanceNm` / `GeoMath.BearingTo` (`src/Yaat.Sim/GeoMath.cs:14,30`) — true bearing.
- `MagneticDeclination.TrueToMagnetic` (`src/Yaat.Sim/MagneticDeclination.cs:51`) — both views are
  magnetic-north-up, so the label must be magnetic.
- `GeoMath.FeetPerNm` (`GeoMath.cs`) — ground unit switch.

## Tasks

- [x] Core model, store, formatter, resolver in `Views/Map/RangeBearingLines.cs`
- [x] Own the store in `MainViewModel`; inject into `RadarViewModel` + `GroundViewModel` via the
      shared observable `RangeBearingViewState`
- [x] Radar: snapshot plumbing + `RadarRenderer.DrawRangeBearingLines`
- [x] Radar: four activation paths — `RBL`/`CLR RBL` on the AUX DCB page, Alt+drag, `.rbl`/`.norbl`,
      Ctrl+M; plus "Measure from here" on the map menu and "Measure from {callsign}" under Display
- [x] Ground: snapshot plumbing + `GroundRenderer.DrawRangeBearingLines`
- [x] Ground: same four paths — `RBL` toolbar button, Alt+drag, the shared dot-commands and hotkey,
      plus the node and aircraft context menus
- [x] Prune lines when a latched aircraft disappears (`RemoveAircraftFromList` → `PruneMissing`)
- [x] Tests: 43 across `RangeBearingLineTests` (formatting, clamps, slots, prune, resolver) and
      `RangeBearingViewStateTests` (pick/complete/cancel, cross-view sharing, status reporting)
- [x] Docs: `USER_GUIDE.md` (new "Measuring distance and bearing" section + both view sections),
      `COMMANDS.md`, `docs/architecture.md`, `docs/radar-rendering.md`, `docs/ground-rendering.md`

## Follow-up: separating right-click from right-drag on the ground

The ground view fired its context menus on **press**, so a right-drag that started on a datablock,
aircraft, or node never panned — and with an aircraft selected the snap-to-nearest-node fallback made
that everywhere. The radar already deferred its *map* menu to release; the ground did not.

Fixed by extracting that decision into `Views/Map/RightClickGesture` and using it in both canvases:

- Press records the position and lets `base.OnPointerPressed` start the pan. Nothing is decided.
- Movement past 5 px latches the press as a drag; it cannot revert, so panning out and back does not
  end in a menu.
- `Release()` returns the **press** position when a menu is owed (jitter can't slide the menu off a
  small node) or null when the gesture was a pan.

Consequences:

- `GroundCanvas.HandleRightClick` is now the single place every ground right-click menu is decided,
  and is only called from the release path. The datablock right-click, the draw-route finish, and the
  measure-cancel all moved into it from `OnPointerPressed`.
- The snap-to-nearest-node fallback is no longer gated on having an aircraft selected — it only cost
  panning before, and now it doesn't. This is what makes "Measure from here" reachable on open surface
  with nothing selected.
- `RadarCanvas` now anchors its map menu at the press position rather than the release position. The
  two are within 5 px by definition, since anything further is a drag.

## Not done

- Not persisted. Measurements are session state — they do not survive a scenario switch or restart,
  unlike scope markers (`SavedRadarSettings`). Nothing in CRC persists RBLs either.
- The radar still opens an **aircraft/datablock** right-click menu on press, so a right-drag starting
  on a target does not pan there. Only the radar's map menu defers. Left alone deliberately: the ask
  was about the ground view, and radar targets are small enough that it rarely bites.
