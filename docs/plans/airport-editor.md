# Yaat.AirportEditor — Standalone Airport GeoJSON Editor

## Context

Airport ground layouts for YAAT are defined as GeoJSON files in `X:\dev\vzoa\training-files\atctrainer-airport-files\`. Currently edited in QGIS, which is cumbersome and not purpose-built for the ATCTrainer format. This plan creates a standalone Avalonia desktop app that understands the 5 feature types natively, provides full CRUD with vertex snapping, and can read/write both split per-layer files and combined GeoJSON.

## Data Format Summary

5 feature types stored as GeoJSON Features:
- **Taxiway** (LineString): `{ type, name, circular }`
- **Parking** (Point): `{ type, name, heading }`
- **Helipad** (Point): `{ type, name, heading }`
- **Spot** (Point): `{ type, name }` — named intersection/hold points
- **Runway** (LineString): `{ type, name, threshold, turnoff, holdShortDistance?, patternSize?, patternAltitude?, noTurnoff? }`

Split files have `name` + `crs` top-level FeatureCollection properties. Combined files have only `type` + `features`.

## Key Architectural Decisions

**Raw features as source of truth, not parsed graph.** `GeoJsonParser` performs destructive one-way processing (snapping, intersection detection, edge splitting, RAMP edges). The editor preserves exact original coordinates and serializes back to the same format. The parsed `AirportGroundLayout` is used only for validation.

**Copy `MapViewport` + `MapCanvasBase` into editor project.** Both depend on Avalonia types — can't go in Yaat.Sim. Only 318 lines total; a shared UI library would be over-engineering.

**Middle-mouse-button for pan.** Right-click is needed for context menus (QGIS convention too). Override `MapCanvasBase`'s right-drag pan to middle-drag.

**Property edits routed through undo stack.** `PropertyPanelViewModel` intercepts changes, packages old+new values, calls `MainViewModel.UpdateProperty` which pushes an `UndoableAction`.

**Coordinate convention:** `(Lon, Lat)` tuples in EditorFeature (matching GeoJSON `[lon, lat]`). Swap to `(lat, lon)` only at `GeoMath` call boundary.

## Project Structure

```
src/Yaat.AirportEditor/
  Yaat.AirportEditor.csproj        # net10.0 WinExe, refs Yaat.Sim
  Program.cs / App.axaml / App.axaml.cs

  Logging/
    AppLog.cs                       # Copy from Client, log to yaat-airport-editor.log

  Models/
    EditorFeature.cs                # Abstract base + 5 sealed subclasses (TaxiwayFeature, etc.)
    EditorDocument.cs               # Feature list, airport code, dirty flag, source tracking
    EditorMode.cs                   # Enum: Select, DrawTaxiway, DrawRunway, PlaceParking/Spot/Helipad
    UndoableAction.cs               # Record: Description, Do, Undo
    UndoStack.cs                    # Dual-stack undo/redo, max 200 depth
    ValidationResult.cs             # Issues list with severity

  Services/
    GeoJsonReader.cs                # OpenFolder (split) / OpenFile (combined) → EditorDocument
    GeoJsonWriter.cs                # SaveFolder (split) / ExportCombined; atomic writes
    SnapEngine.cs                   # Find nearest snappable point within threshold (0.00003°)
    LayoutValidator.cs              # Run GeoJsonParser.Parse, report connectivity issues
    EditorPreferences.cs            # Window geometry, recent files, snap threshold

  Views/Map/
    MapViewport.cs                  # Copy from Client (unchanged)
    MapCanvasBase.cs                # Copy from Client, pan moved to middle-mouse

  Views/Editor/
    EditorCanvas.cs                 # Subclasses MapCanvasBase; mode state machine; hit-testing; snap
    EditorRenderer.cs               # Stateless SkiaSharp; renders from EditorDocument features directly
    EditorView.axaml/.cs            # UserControl wrapping canvas; context menu wiring

  Views/
    MainWindow.axaml/.cs            # Menu bar + EditorView + PropertyPanel + status bar
    PropertyPanel.axaml/.cs         # DataTemplate per feature type
    ValidationWindow.axaml/.cs      # Validation results list

  ViewModels/
    MainViewModel.cs                # Root: document, undo, mode, file/edit/view/tool commands
    PropertyPanelViewModel.cs       # Intermediary for undo-aware property edits

tests/Yaat.AirportEditor.Tests/
  Yaat.AirportEditor.Tests.csproj
  GeoJsonRoundTripTests.cs
  UndoStackTests.cs
  SnapEngineTests.cs
  LayoutValidatorTests.cs
```

## Reused Infrastructure

| Source file | Reuse strategy |
|---|---|
| `Yaat.Client/Views/Map/MapViewport.cs` | Copy as-is (123 lines) |
| `Yaat.Client/Views/Map/MapCanvasBase.cs` | Copy, change pan to middle-mouse (195 lines) |
| `Yaat.Client/Views/Ground/GroundRenderer.cs` | Template for EditorRenderer — strip aircraft/route, add selection/ghost/snap/handles |
| `Yaat.Client/Views/Ground/GroundCanvas.cs` | Template for EditorCanvas — same StyledProperty/snapshot/hit-test patterns |
| `Yaat.Client/Views/Ground/GroundView.axaml.cs` | Template for context menu helpers (CreateMenuItem, ShowContextMenu) |
| `Yaat.Client/Services/UserPreferences.cs` | Template for EditorPreferences (atomic write pattern) |
| `Yaat.Sim/Data/Airport/GeoJsonParser.cs` | Called by validator (read-only); snap threshold constant (0.00003°) |
| `Yaat.Sim/GeoMath.cs` | Haversine distance, bearing |

## Menu Bar

**File:** New Airport, Open Folder (Ctrl+O), Open File, Save (Ctrl+S), Save As, Export Combined, Recent Files, Exit
**Edit:** Undo (Ctrl+Z), Redo (Ctrl+Y), Delete Selected (Del), Select All (Ctrl+A)
**View:** Zoom In (+), Zoom Out (-), Fit to Airport (Ctrl+Shift+F), Show Labels (toggle), Show Vertex IDs (toggle), Show Snap Guides (toggle)
**Add:** Taxiway (T), Parking (P), Spot (S), Helipad (H), Runway (R) — enters placement mode
**Tools:** Validate Layout (Ctrl+Shift+V), Statistics

## Context Menus

**Point feature (parking/spot/helipad):** Edit Properties, Delete
**Taxiway vertex:** Delete Vertex (disabled if only 2 vertices)
**Taxiway edge:** Edit Properties, Insert Vertex Here, Split Taxiway Here, Delete
**Runway:** Edit Properties, Delete
**Empty space:** Add Parking Here, Add Spot Here, Add Helipad Here

## Editor Canvas Modes

| Mode | Left-click | Double-click | Right-click | Esc |
|---|---|---|---|---|
| Select | Hit-test → select; drag vertex/point | — | Context menu | Deselect |
| DrawTaxiway | Add vertex (snapped) | Finish (≥2 pts) | Cancel | Cancel |
| DrawRunway | Click 1: start, click 2: finish | — | Cancel | Cancel |
| PlaceParking/Spot/Helipad | Place at cursor (snapped) | — | Cancel | Cancel |

**Snap:** During place/draw/drag, `SnapEngine` finds nearest taxiway endpoint/vertex, spot, or runway endpoint within 0.00003° (~10ft). Visual crosshair drawn at snap target. Snap toggled via View menu.

**Hit-testing priority:** (1) line vertices 8px, (2) point features 12px, (3) line segments 6px perpendicular distance.

**Rendering additions over GroundRenderer:** Selection highlight (bright outline), vertex handles on selected lines (white circles, larger on hover), ghost dashed line during draw, snap crosshair, mode HUD text (bottom-left).

## Undo/Redo Actions

| Action | Undo captures |
|---|---|
| AddFeature | Feature reference → remove by Id |
| DeleteFeature | Feature + insertion index → re-insert |
| MovePoint | Feature Id, old lon/lat |
| MoveVertex | Feature Id, vertex index, old lon/lat |
| InsertVertex | Feature Id, index → remove |
| DeleteVertex | Feature Id, index, old lon/lat → re-insert |
| EditProperty | Feature Id, property name, old value |
| SplitTaxiway | Two halves → remove both + re-insert original |

## Implementation Chunks

### Chunk 1 — Project scaffold, data model, file I/O
- [x] Create `Yaat.AirportEditor.csproj` (Avalonia 11.2.5, CommunityToolkit.Mvvm, ref Yaat.Sim)
- [ ] `Program.cs`, `App.axaml/.cs` (Fluent dark), `AppLog.cs`
- [ ] `EditorFeature.cs` — abstract base + 5 sealed subclasses
- [ ] `EditorDocument.cs` — feature list, airport code, dirty flag, source tracking, GetBounds()
- [ ] `EditorMode.cs`, `UndoableAction.cs`, `UndoStack.cs`
- [ ] `GeoJsonReader.cs` — OpenFolder + OpenFile
- [ ] `GeoJsonWriter.cs` — SaveFolder + ExportCombined (atomic writes)
- [ ] `EditorPreferences.cs` — window geometry, recent files, snap threshold
- [ ] `MainWindow.axaml/.cs` — bare window with title
- [ ] `MainViewModel.cs` — file open commands only
- [ ] Add to `yaat.slnx`
- [ ] `tests/Yaat.AirportEditor.Tests/` — GeoJsonRoundTripTests, UndoStackTests
- [ ] Verify: `dotnet build` 0W 0E, round-trip test passes on OAK files

### Chunk 2 — Map canvas + basic rendering
- [ ] Copy `MapViewport.cs` (unchanged)
- [ ] Copy `MapCanvasBase.cs` (pan → middle-mouse)
- [ ] `EditorRenderer.cs` — runways, taxiways, points, labels with overlap culling
- [ ] `EditorCanvas.cs` — StyledProperties (Document, Mode, SelectedFeature), CreateRenderSnapshot, FitToLayout
- [ ] `EditorView.axaml/.cs` — UserControl wrapping canvas
- [ ] Wire `MainWindow` → `EditorView`, `MainViewModel.OpenFolder/OpenFile` set Document
- [ ] Verify: open OAK folder → see airport rendered, middle-drag pan, scroll zoom

### Chunk 3 — Selection, hit-testing, property panel
- [ ] `SnapEngine.cs`
- [ ] `EditorCanvas` hit-testing: point features (12px), line vertices (8px), line segments (6px)
- [ ] Selection model: click-to-select, SelectionChanged event, highlight in renderer
- [ ] `EditorRenderer` additions: selection highlight, vertex handles on selected line, hover highlight
- [ ] `PropertyPanelViewModel.cs` — intermediary with undo-aware property changes
- [ ] `PropertyPanel.axaml/.cs` — DataTemplate per feature type (all properties editable)
- [ ] Context menus: Edit Properties, Delete
- [ ] `MainViewModel` additions: SelectedFeature, UpdateProperty, DeleteFeature, Undo, Redo
- [ ] Wire `MainWindow` menu bar (Edit menu: Undo/Redo/Delete/SelectAll)
- [ ] `tests/SnapEngineTests.cs`

### Chunk 4 — Draw/place modes, vertex editing
- [ ] `EditorCanvas` draw modes: DrawTaxiway (click vertices, dbl-click finish), DrawRunway (2 clicks), PlaceParking/Spot/Helipad (single click)
- [ ] Vertex drag: left-drag on selected line vertex → MoveVertex through undo stack
- [ ] Point drag: left-drag on parking/spot/helipad → MovePoint through undo stack
- [ ] Snap integration: snap indicator crosshair, snapped placement
- [ ] `EditorRenderer` additions: ghost preview (dashed line), snap crosshair, mode HUD text
- [ ] Full context menus: Insert Vertex, Delete Vertex, Split Taxiway, Add Here (empty space)
- [ ] `MainViewModel` additions: AddFeature, MovePoint, MoveVertex, InsertVertex, DeleteVertex, SplitTaxiway, EnterMode, EscapeMode
- [ ] Wire Add menu + keyboard shortcuts (T/P/S/H/R, Esc)

### Chunk 5 — Save/export, validation, preferences
- [ ] `MainViewModel` Save/SaveAs/ExportCombined wired to file dialogs
- [ ] Dirty guard on window close (save prompt)
- [ ] `LayoutValidator.cs` — runs GeoJsonParser.Parse, reports disconnected parking, duplicate names, unnamed features, single-point taxiways
- [ ] `ValidationWindow.axaml/.cs` — issue list with severity icons
- [ ] Recent files submenu (EditorPreferences)
- [ ] Window geometry save/restore
- [ ] Complete View menu toggles (labels, vertex IDs, snap guides)
- [ ] `tests/LayoutValidatorTests.cs`

### Chunk 6 — Polish and integration
- [ ] New Airport dialog (prompt for airport code)
- [ ] Statistics dialog (feature counts)
- [ ] Test: full editing workflow (open OAK → edit → save → re-open → verify)
- [ ] Test: undo/redo multi-step sequence
- [ ] `dotnet build` 0W 0E for entire solution
- [ ] `dotnet test` all projects pass

## Verification

1. `dotnet build` — 0 warnings, 0 errors across entire solution
2. `dotnet test` — all existing + new tests pass
3. Manual: `dotnet run --project src/Yaat.AirportEditor` → open `X:\dev\vzoa\training-files\atctrainer-airport-files\oak\` → see OAK rendered
4. Manual: draw a new taxiway with vertex snapping → save → re-open → verify persistence
5. Manual: open `oak.geojson` (combined) → export combined → diff output matches input
6. Manual: Ctrl+Z/Y undo/redo through add/move/delete sequence
7. Manual: validate layout → see connectivity report
