# YAAT vStrips: CRC Parity

## Status

**Rounds 1–6 landed (client + server), plus several rounds of user-testing
polish**: CWT equipment format, inline route with middle `***` trim,
facility-gated destination field, initial strip state on room join, rack
hit-test fix for drag-drop reliability, printer UX (request-focus, modal
pass-through), drop-preview gap (shifting strips) with append line, and
misc. UX tweaks. Drag-drop now works reliably across racks and bays after
the rack Border background fix.

Remaining work is listed under "Still open" at the bottom — manual visual
parity and drag-drop verification against a running client.

---

## What shipped

### Round 1 — Visual custom control + fixed-width racks

- New `src/Yaat.Client.Core/Views/VStrips/FlightStripControl.axaml[.cs]`.
  - 7 × 3 grid (not 5 as originally planned — the annotation column is three
    actual columns, not a `UniformGrid`), equal-height rows, fixed column widths.
  - Column ratios derived from pixel-measuring `docs/crc/img/printer.png`
    (DAL713 strip, 681 × 82 px): 22 / 8.5 / 13.5 / 37.9 / 6 / 6 / 6.
  - At the user-spec natural size 535 × 66 px: column widths
    `118 / 46 / 72 / * / 32 / 32 / 33`, row heights `23 / 23 / 23`.
  - Col 1 row 1 exception: `DockPanel` stacks callsign (top, 13 pt bold) over
    revision (bottom, 8 pt bold) without the row growing.
  - All printed text is **bold** for controller-desk legibility.
  - Col 1 row 3 puts `CID + barcode` side-by-side. Barcode is a deterministic
    glyph derived from `strip.Id.GetHashCode()`; uses `canvas.Bounds.Width`
    so bars fill whatever width is available after the CID.
  - Col 3 row 1 shows `field 8` (departure airport, optionally + destination —
    see server note below).
  - Col 4 (route + remarks) spans all three rows. Two layout paths:
    - `HasRemarks = false` → single TextBlock spanning 3 rows, `MaxLines=3`,
      `TextTrimming=CharacterEllipsis`, `LineHeight=22`.
    - `HasRemarks = true` → route TextBlock spans rows 1–2 (`MaxLines=2`),
      remarks TextBlock anchors row 3.
  - Cols 5–7 are the 3 × 3 annotation grid. Each cell's `Tag` carries the
    canonical box number 1..9 that `AN {box}` writes to `FieldValues[box+9]`.
  - Offset = negative left margin applied from code-behind when
    `IsOffset = true` (matches `docs/crc/img/offset.png`).
  - Disconnected = diagonal red stroke drawn on an overlay `Canvas`, sized
    to `Bounds` so it tracks resizes.
- `src/Yaat.Client/App.axaml` carries the CRC palette resources
  (`StripCellBrush #EEEBE0`, `StripBorderBrush #BFBBAE`, separator colors,
  `StripScriptFont`).
- `StripItemView.axaml[.cs]` deleted (replaced by `FlightStripControl`).
- Rack Border width: `547` (strip 535 + padding). Racks scroll horizontally
  in the parent `ScrollViewer` when many don't fit.

### Round 1.5 — Zoom + bottom-up rendering

Not in the original plan but requested during review.

- `VStripsViewModel.ZoomScale` observable property (default 0.8, range 0.5–1.5).
  `ZoomInCommand` / `ZoomOutCommand` step by 0.1. `ZoomLabel` exposes the
  percent display for the header.
- Header has `−` / `80%` / `+` controls between the trash icon and the
  printer toggle.
- Racks area wrapped in a `LayoutTransformControl` bound to `ZoomScale` so
  strips + racks + padding all scale together; the header does not.
- **Bottom-up rendering**: rack inner `ItemsControl` uses a `DockPanel`
  with `LastChildFill="False"` and each `ContentPresenter` is styled with
  `DockPanel.Dock="Bottom"`. Item index 0 docks first → bottom; later items
  stack upward. Paired with the server change below, this gives CRC bottom-up
  FIFO: strip #1 lands at the bottom, strip #2 above it, strip #3 above #2.

### Round 2 — Context menus

- Right-click on a strip opens a `MenuFlyout` with Offset / Delete / Push-to /
  strip-type-specific items (Slide for half-strips, etc.).
- Right-click on empty rack space opens Add Half-Strip / Add Separator
  (submenu: Handwritten / White / Red / Green) / Add Blank. (Context menu for
  empty rack space is wired in `ShowEmptyRackMenu`; see remaining work list —
  verify in session.)

### Round 3 — Inline editing + `IsSelected`

- New `src/Yaat.Client.Core/Views/VStrips/InlineTextEditPopup.axaml[.cs]` —
  one reusable `Popup`-anchored single-line editor (`Enter` commits, `Esc`
  cancels). `Open(anchor, initial, onCommit)`.
- `StripItemViewModel.IsSelected` (`[ObservableProperty]`) flipped from
  `VStripsViewModel.OnSelectedStripChanged` so the `FlightStripControl`
  draws a yellow ring on the selected strip without extra plumbing.
- Clicking an annotation cell opens the editor; commit emits
  `AnnotateAsync(strip, box, text)` which builds `AN {box} {text}`.
- Separator label editing uses **delete+create** pattern
  (`EditSeparatorLabelAsync`) — the server-side `SEPE` canonical wasn't
  added in this round; see follow-ups.
- Half-strip line amend wired via `AmendHalfStripAsync` →
  `HSA {key} {line…}`.

### Round 4 — Keyboard shortcuts

- Arrow keys navigate selection; Ctrl+arrow moves a strip.
- Shift+arrow toggles offset; Ctrl+click selects without dragging;
  Alt+click deletes.
- Ctrl+Shift+H/S adds half-strip / separator above selection.
- Ctrl+1..9 edits annotation box 10..18.
- Ctrl+Alt+1..9 jumps-to-bay (or pushes strip to bay N when a strip is
  selected).
- `PageUp`/`PageDown` bay switching, `Ctrl+Alt+←/→` facility switching
  (already wired pre-plan).

### Round 5 — Printer panel parity

- Printer panel rebuilt as a centered modal matching
  `docs/crc/img/printer.png`:
  - Request-Strip input + green "Request Strip" button
  - "Print Blank Strip" button
  - Departure + Arrival carousels (single "Flight Strip Printer" section when
    `HasTwoPrinters == false`)
  - `<` / `>` arrows flanking a single-strip preview
  - `N/M` counter
  - Move-to-Bay / Delete buttons below each carousel
- `StripPrinterViewModel` split `DepartureQueue` / `ArrivalQueue` with
  `VisibleDepartureIndex` / `VisibleArrivalIndex`. Client-side demux in
  `ReplaceAll` (arrivals → ArrivalQueue, else → DepartureQueue).
- The printer modal backdrop is **fully transparent** (was `#CC000000`) so
  "Move to Bay" rack updates are visible without closing the printer first.
- `MoveVisiblePrinterStripToBayAsync(kind)` + `DeleteVisiblePrinterStripAsync`
  wired for both queues.
- `RequestStripAsync` stub: button renders and logs but the RPC isn't
  implemented yet — see follow-ups.

### Round 6 — Polish

- Disconnected ✗ overlay drawn in code-behind, tracks bounds.
- Offset rendering via negative outer margin.
- Drag ghost: absolutely-positioned `Canvas` above every control renders a
  clone of the dragged strip tracking the pointer (`DragGhostCanvas` in
  `VStripsView.axaml`).
- Drag-drop reliability: root-level handler `OnRootDrop` walks up from
  `e.Source` to find a rack `Border` by `Tag`. More reliable than per-rack
  `Loaded` handlers which didn't always fire in DataTemplate-generated visuals.
- `IsStripAlreadyAt` no-op guard in `MoveStripAsync` avoids emitting a
  redundant STRIP canonical when a drag releases on the strip's current slot.

### Visual-parity polish from user testing

- **Octal beacon bug** fixed: `GenerateBeaconCode` stores the squawk as a
  decimal int whose digits are already 0–7 (e.g. 3447). The old
  `FormatBeacon` did `Convert.ToString((int)beacon, 8)` — a *base conversion*
  that turned 3447 into "6567", silently mislabelling every strip. Fix:
  `beacon.ToString("D4")` keeps the digits as assigned. Test updated to
  assert the new behaviour with `AssignedBeaconCode = 1234u` → "1234".
- **Route wrapped with dep + dest**: `FormatRouteField` prepends
  `ac.Departure` and appends `ac.Destination` (token-aware, so "KBOSTON"
  isn't mistaken for "KBOS"), producing e.g. `KBOS ... KLAX`. Remarks
  separated by `\n`; client `StripItemViewModel.RouteText` / `.Remarks` /
  `.HasRemarks` split on the first newline.
- **1-based rack/index on the wire, null index = append.** User feedback:
  "users don't really think in 0-based indices". `ResolveStripTokens` on
  the server expects 1-based tokens, rejects `< 1`, converts internally to
  0-based. `Index` is nullable — null means "the caller omitted the index
  token entirely". `HandleStripMoveAsync` treats null as "append to the
  tail of the rack" (CRC bottom-up first-available slot; accounts for
  already-in-this-rack by subtracting 1). Client
  `VStripsCanonicalBuilder.BuildStripMove` takes `int? index`; null drops
  the trailing token on the wire (`STRIP Ground 1`). `BuildHSC / BuildHSM /
  BuildSEP / BuildBLANK` all pass through `OneBased(n)` for rack+index.
- **`MoveStripAsync(strip, destBay, rack, int? index)`** — callers that
  want append semantics now pass `index: null`. Printer "Move to Bay",
  bay-button drop, "Push to <bay>" context menu, and the Ctrl+Alt+N
  keyboard shortcut all use null. Rack drop with a specific slot still
  passes an int.
- **Whitespace-insensitive bay matching** already worked
  (`ResolveStripTokens` strips spaces from both sides); test added to
  prevent regression — `STRIP Ground2 1` resolves to bay "Ground 2".

---

## Key learnings (save your future self the pain)

- **Reference = printer.png, not departure-strip.png.** The red "1..9 / 10..18"
  numbers in `departure-strip.png` and `arrival-strip.png` are *documentation
  overlays*, not rendered strip text. Use the DAL713 render inside
  `docs/crc/img/printer.png` as the pixel ground truth.
- **DockPanel with `Dock=Bottom` is the bottom-up list panel.** No custom
  panel needed: first item docks to the bottom, subsequent items dock into
  the shrinking remaining space (stacking upward). Set via
  `ItemsControl.Styles { Selector: "ItemsControl > ContentPresenter" }`.
- **`int[]` has `.Length`, not `.Count` — `.Count` silently resolves to the
  LINQ extension method group and fails with CS0019.** Hit this when
  computing the append index from `state.Bays[bayId][rackKey]` which is
  `List<string>[]`.
- **Avalonia `LayoutTransformControl` is the right scale primitive.** Unlike
  `RenderTransform`, it reallocates layout bounds under the transform, so
  a wrapping `ScrollViewer` honors the scaled size.
- **Root-level drop handlers are more reliable than per-template Loaded
  hooks** for `DataTemplate`-generated visuals. The `OnRootDrop` walk finds
  the rack `Border` via `Tag = StripRackViewModel` regardless of where the
  pointer released (strip, padding, empty space).
- **No-op guard matters.** Without `IsStripAlreadyAt`, every drag that
  releases on the strip's existing slot emits a redundant STRIP canonical
  — harmless server-side but noisy in the command log.
- **Beacon codes are stored as decimal ints with octal-valid digits, not as
  binary octal values.** `SimulationWorld.GenerateBeaconCode` builds
  `3447` (decimal) one digit at a time via `rng.Next(0, 8)`, so the display
  is just `ToString("D4")`. `Convert.ToString(_, 8)` would treat 3447 as a
  base-10 number to convert, producing a wrong string.
- **Strip height limit: 66 px at 100 % zoom, 13 pt font bold** fits three
  rows comfortably with 1 px vertical padding each.
- **Route trimming**: at 12 pt bold, a ~200 px route column fits roughly
  26 chars per wrapped line. `MaxLines=2` (when remarks present) truncates
  mid-route for long flights — see the "Route trim preserves tail" follow-up.

---

## Remaining follow-ups

All build cleanly; these are enhancements / deferred work, not blockers.

- [x] **Server: pack destination airport into strip field 8, gated on
  facility flag**. `StripMutations.BuildDepartureStripFields` (and
  `RequestDepartureStripForAircraft`, `RefreshStripForAircraft`) take an
  optional `displayDestinationAirportIds` parameter. When true, field 8
  packs `"{ac.Departure} {ac.Destination}"` as a single space-separated
  string. When false (default), field 8 holds just the departure. The
  flag is sourced from the facility's `FlightStripsConfig` (new
  `ArtccConfigService.GetFacility` helper); threaded through all four
  call sites (scenario auto-print, yaat-client `RequestStrip` RPC,
  amend-refresh, CRC client path). `FlightStripsConfig` now also parses
  the full JSON set: `displayDestinationAirportIds`, `displayBarcodes`,
  `enableArrivalStrips`, `enableSeparateArrDepPrinters`, `lockSeparators`.
  Col 3 row 1 in `FlightStripControl.axaml` binds to `Field8A`. Covered
  by `BuildDepartureStripFields_DestPackingGatedOnFacilityFlag`
  (all four dep/dest permutations + both flag states). Task #13.
- [x] **Server: wire revision counter into `FieldIdxRevision`**.
  `AircraftState.RevisionNumber` added and bumped in
  `SimulationEngine.AmendFlightPlan`; serialized via
  `AircraftSnapshotDto.RevisionNumber`; rendered by both
  `BuildDepartureStripFields` and `BuildArrivalStripFields`. New
  `StripMutations.RefreshStripForAircraft` rebuilds field values in place
  (preserving annotation boxes 10..18) and `RoomEngine.AmendFlightPlan`
  broadcasts the surgical update via `StripBroadcaster.BroadcastItemsAsync`.
  Tests in `StripMutationsTests`. Task #14.
- [x] **Add `SEPE` canonical command** (atomic separator label edit).
  `SeparatorEditCommand` + `CanonicalCommandType.SeparatorEdit` added to
  Yaat.Sim, parser routes `SEPE` args, registry + describer emit the
  canonical form. Server `StripCommandHandler.HandleSeparatorEditAsync`
  supports locator-by-position (default) and locator-by-old-label
  (fallback), mutates `FieldValues[0]` under the state gate, broadcasts a
  surgical `StripItemsChanged`. Client `EditSeparatorLabelAsync` replaced
  with a single `VStripsCanonicalBuilder.BuildSeparatorEdit` dispatch.
  Tests: `VStripsCanonicalBuilderTests.BuildSeparatorEdit_…`,
  `CrcStripDispatchTests.Dispatch_SepEdit_ByPosition_…` and `_ByOldLabel_…`.
  Task #17.
- [x] **Implement `RequestFlightStripForAircraft` SignalR RPC**.
  Server exposes `TrainingHub.RequestFlightStripForAircraft(callsign)` →
  `RoomEngine.RequestFlightStripForAircraft` which resolves the student's
  own (non-external) bay facility, delegates to the existing idempotent
  `StripMutations.RequestDepartureStripForAircraft`, and broadcasts both
  the item and full state. Client `ServerConnection.RequestFlightStripForAircraftAsync`
  wraps the invoke; `VStripsViewModel.RequestStripAsync` replaces the
  prior log-only stub with a real call (with success/failure logging).
  Tests in `CrcStripDispatchTests`: unknown-callsign failure,
  empty-callsign failure, idempotent double-request success. Task #18.
- [x] **Server: emit push-warning broadcast** when a strip is pushed to an
  external bay that has no connected controller. `StripCommandHandler`
  picked up `CrcClientManager`; `HandleStripMoveAsync` looks up the
  resolved accessible bay's `IsExternal` flag and, when true, checks
  whether any CRC client in the room holds a primary or secondary
  position at the target facility. Missing → appends a "WARNING: no
  controller connected at {facilityId}" to the command-result message,
  which surfaces on the yaat-client via the existing `StatusText` /
  terminal-entry response path. Tests:
  `CrcStripDispatchTests.StripMove_ExternalBay_NoController_…` and
  `StripMove_OwnBay_NoWarning`. Task #19.
- [x] **Rack context menu: separators + half-strips.** Verified:
  `ShowEmptyRackMenu` (`VStripsView.axaml.cs:537-577`) exposes Add Half-Strip,
  Add Separator (Handwritten/White/Red/Green, with locked-facility fallback to
  handwritten-only per docs/crc/vstrips.md:195), and Add Blank Strip. Task #28.
- [x] **Same-bay inter-rack drag-drop.** Root cause was
  `ComputeDropIndex` returning a *visual top-down* index while the rack
  DockPanel renders bottom-up (strip[0] at visual bottom). A drop at the
  visual top targeted model index 0 (bottom slot), which looked like
  "nothing happened" for inter-rack moves. Pure flip extracted into
  `VStripsView.ComputeDropModelIndex(posY, hostHeight, count)` →
  `count - visualIdx`. Covered by `VStripsDropIndexTests` (7 cases). Also
  fixed two stale `VStripsViewModelTests` that still asserted 0-based wire
  output after the 1-based migration. Task #29.
- [x] **Route trim preserves tail — inline with `***` middle-trim.** The
  3-column sub-grid approach was reverted per user feedback: "the route
  is trimmed to the middle when the departure/destination occupy
  separate auto columns, constraining the route". Replaced with a single
  `TextBlock` bound to no data (code-behind owns `Text`) — the new
  `FitRouteBlock` helper in `FlightStripControl.axaml.cs` measures the
  full `"DEP … route … DEST"` string against `block.Bounds.Width` via a
  standalone `TextLayout`, and if the result exceeds `MaxLines` (2 with
  remarks / 3 without), progressively drops tokens from the tail end of
  the route body and inserts `***` until the string fits — keeping the
  destination visible at the end of the last line. Two fit-timing
  gotchas addressed: (a) removed the `Text` binding so the XAML binding
  can't race `FitRouteBlock` and intermittently restore untrimmed text;
  (b) `RefreshRouteBlocks` schedules a deferred `Dispatcher.UIThread.Post
  (Background)` because `Bounds` is 0 at initial DataContextChanged for
  items inside an `ItemsControl` and the outer Grid fixes the column
  width so setting Text never triggers a size change on its own. Task #30.
- [x] **Persist primary Strips window pop-out state / position.** Student
  entry's `IsPoppedOut` now wires through `_preferences.SetPoppedOut("VStrips", …)`
  in `MainViewModel.Strips.cs:OnStripsEntryPropertyChanged` (guarded by
  `entry.IsStudentEntry`) and is restored on ctor at
  `MainViewModel.cs:712`. Geometry was already persisted via
  `WindowGeometryHelper` with per-facility key. Non-student per-facility
  entries remain session-scoped (out of scope per plan). Task #32.
- [x] **Equipment field format: CWT letter prefix, no duplication.**
  `FormatEquipment` in `StripMutations.cs` was double-prefixing weight
  class + equipment suffix (`H/H/B763/L/L`) whenever the scenario
  supplied `AircraftType` with a pre-existing `H/`. Fix: use
  `AircraftState.BaseAircraftType` (which strips the legacy FAA weight
  prefix) and format as `"{CWT}/{base}/{suffix}"` where CWT is the
  full letter A-I from `WakeTurbulenceData.GetCwt`, not the legacy `H/`
  shorthand. Unknown types drop the CWT prefix entirely. Tests in
  `StripMutationsTests`: `PopulatesExpectedIndices` updated, plus
  `HeavyAircraft_PrefixesCwtLetter`, `ScenarioPrefixedType_DoesNotDuplicate`,
  `UnknownCwt_DropsPrefix`.
- [x] **Zoom controls moved left of trash.** UX feedback — zoom +/-
  cluster now sits in column 2, trash zone in column 3 of the header
  Grid. Pointer travel from bay buttons to zoom is shorter than the
  reach to the trash.
- [x] **Focus requested strip in printer carousel.** After "Request Strip"
  succeeds, `VStripsViewModel.RequestStripAsync` calls
  `Printer.RequestFocusOnCallsign(trimmed)` which sets a pending-focus
  callsign and jumps `VisibleDepartureIndex` / `VisibleArrivalIndex` to
  the matching entry immediately (or on the next `ReplaceAll` if the
  broadcast hasn't landed yet). User no longer has to arrow through the
  queue to find the just-printed strip.
- [x] **Initial strip state broadcast on room join.** New
  `StripBroadcaster.SendInitialStateToClientAsync(room, connectionId)`
  emits both `StripItemsChanged` (seeds `ItemsById`) and
  `FlightStripsStateChanged` (populates racks + printer queue) to a
  single just-joined connection. Called from `TrainingHub.JoinRoom`
  after adding to the SignalR group. Without this, clients joining an
  in-progress room saw empty racks and a 0/0 printer.
- [x] **Re-apply cached strip state after ApplyBayConfig.** Companion
  fix to the above: the server-initiated broadcasts arrive on the
  client BEFORE `JoinRoom` returns its `RoomStateDto`, so the first
  `ReconcileFullState` runs against empty bays (config hasn't been
  applied yet) and the state is lost. `VStripsViewModel` now caches
  the latest `_lastReceivedFullState` and `_lastReceivedItems` and
  re-applies them at the end of `ApplyBayConfig`, so racks populate
  on first load without requiring a user action like "Move to Bay" to
  trigger a fresh broadcast.
- [x] **Drag-drop rack hit-test fix (the big one).** Every failed drop
  in the user's logs had `source=ScrollContentPresenter` — the pointer
  was in empty rack space, but the rack `Border` had no `Background`
  set, so Avalonia's hit-test passed straight through to the ancestor
  `ScrollContentPresenter`. `OnRootDrop`'s walk-up-to-rack then failed.
  Successful drops only happened when the pointer was directly over a
  strip's children (Border/Rectangle/TextBlock). Fix: add
  `Background="Transparent"` to the rack Border in `VStripsView.axaml`
  — invisible but a solid hit-test target. Drops in padding, empty
  rack areas, and between-rack drops all work reliably now.
  Information-level drag-drop logs kept in place
  (`Strip drag start: ...`, `OnRootDrop fired: ...`, `Strip drag end:
  effect=...`) so future issues can be diagnosed from
  `%LOCALAPPDATA%/yaat/yaat-client.log`.
- [x] **`OnRootDrop` uses explicit InputHitTest.** Alongside the
  background fix, the drop handler now calls
  `this.InputHitTest(e.GetPosition(this))` as the primary hit target
  (falling back to `e.Source` as Visual) — Avalonia can set `e.Source`
  to a root-level visual for some drop events.
- [x] **Printer modal no longer blocks racks behind.** The outer
  transparent wrapper around the printer modal was hit-testable
  (`Background="Transparent"` is a solid hit target); removed the
  background entirely so pointer and drag events pass through the
  outer margins to the racks while the inner opaque modal stays
  interactive. Earlier attempt with `IsHitTestVisible="False"` on the
  wrapper broke the modal because Avalonia propagates non-hit-testable
  to all descendants regardless of their own setting — reverted.
- [x] **Atomic SEPE command for separator label edits** (was Task #17):
  see entry above.
- [x] **RequestFlightStripForAircraft RPC** (was Task #18): see entry above.
- [x] **External-bay push warning** (was Task #19): see entry above.
- [x] **Drop-preview gap with shifting strips.** During a rack drag,
  `VStripsView.UpdateDropPreview` walks the hit-tested rack's
  `ContentPresenter`s and finds the one bound to `rack.Strips[index]`,
  then bumps its `Margin.Bottom` by the dragged strip's rendered
  height. The rack's bottom-up `DockPanel` cascades that shift so every
  strip visually above it moves up by the same amount, leaving a
  visible gap below the target strip where the drop will land. When
  the target index equals `rack.Strips.Count` (append-at-top) there is
  no strip to shift, so instead a thin yellow line is added as a
  sibling overlay inside the rack's new `RackContent` grid, anchored
  to the topmost strip's top edge via a computed `Margin.Top`. The
  preview clears whenever the hit leaves all racks, the rack/index
  changes, or the drag ends (drop or cancel). No-op when hovering
  the dragged strip's own origin slot so the rack doesn't flicker.

---

## Still open

- [ ] **Verify visual parity end-to-end against `docs/crc/img/printer.png`**
  by running the client and side-by-side comparing. Task #15.
- [ ] **End-to-end drag-drop verification** across within-rack reorder,
  between-racks same-bay, between-bays, printer → rack, rack → trash.
  Previous attempts in the plan were closed optimistically; the
  underlying rack hit-test bug resurfaced in later testing. Re-verify
  now that the `Background="Transparent"` fix has landed. Task #16.
- [ ] **User-test the new drop preview** — confirm the margin-shift gap
  feels right and the append-line position is legible across zoom levels
  and mixed strip heights (full / half / separator).

---

## Quick reference for a fresh instance

### Critical files (client)

- `src/Yaat.Client.Core/Views/VStrips/FlightStripControl.axaml[.cs]` — 7×3
  strip layout; DockPanel trick for callsign+revision; code-behind draws
  barcode + disconnected overlay + applies offset margin.
- `src/Yaat.Client.Core/Views/VStrips/VStripsView.axaml[.cs]` — racks area,
  drag-ghost canvas, printer modal, root-level drop handler, zoom wiring,
  keyboard shortcuts, InlineEditor instance.
- `src/Yaat.Client.Core/Views/VStrips/InlineTextEditPopup.axaml[.cs]` —
  reusable `Popup` editor.
- `src/Yaat.Client.Core/ViewModels/VStripsViewModel.cs` — `MoveStripAsync`
  with nullable index, `MoveVisiblePrinterStripToBayAsync`, zoom commands,
  selection nav, separator delete+create flow.
- `src/Yaat.Client.Core/ViewModels/VStripsCanonicalBuilder.cs` — every
  builder uses `OneBased(n)` for wire emission.
- `src/Yaat.Client.Core/ViewModels/StripItemViewModel.cs` — `RouteText` /
  `Remarks` / `HasRemarks` split on `\n`; `Revision` half-size hook.
- `src/Yaat.Client.Core/ViewModels/StripPrinterViewModel.cs` — Departure /
  Arrival carousel indexes + counters.
- `src/Yaat.Client/App.axaml` — CRC palette resources.

### Critical files (server)

- `src/Yaat.Server/Simulation/StripMutations.cs` — `FormatBeacon` (decimal,
  not `Convert.ToString(_, 8)`); `FormatRouteField` / `FormatDestRemarks`
  (wrap route with dep+dest, separate remarks with `\n`);
  `ResolveStripTokens` (1-based input, nullable Index, whitespace-insensitive
  bay match); `AppendStripToBay` (used by half-strip create).
- `src/Yaat.Server/Simulation/StripCommandHandler.cs` — `HandleStripMoveAsync`
  computes append index under the state gate (already-in-this-rack aware).
- `tests/Yaat.Server.Tests/StripMutationsTests.cs` — updated for nullable
  Index, 1-based wire, beacon-as-decimal, whitespace-insensitive bay match.

### Build / test

```powershell
dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log
timeout 30 dotnet test 2>&1 | tee .tmp/test.log
# server, run from yaat-server/ :
dotnet build yaat-server.slnx -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build-server.log
timeout 30 dotnet test 2>&1 | tee .tmp/test-server.log
```

If the server or client is running, file-lock errors on `Yaat.Server.exe` /
`Yaat.Client.Core.dll` are expected and non-fatal to compilation. Filter
with `grep -E "error CS"` to see real errors only.
