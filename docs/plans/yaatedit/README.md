# YAATEdit, rebuilt natively in YAAT

Linear: YAAT-1989 (the design item), which also absorbs YAAT-200 (the airport GeoJSON editor). This plan replaces the standalone airport-editor plan, kept as [airport-editor-detail.md](airport-editor-detail.md): its feature model, menus, canvas modes and undo actions carry over into milestone M4 below, while its standalone project and its copied canvas classes do not.

Read [`docs/architecture.md`](../../architecture.md) (Layers, Integration Footguns) before briefing any part.

The subsystem docs each part leans on: [scenario-loading-and-generation.md](../../scenario-loading-and-generation.md), [scenario-validation.md](../../scenario-validation.md), [navigation-database.md](../../navigation-database.md), [ground-layout-generation.md](../../ground-layout-generation.md), [radar-rendering.md](../../radar-rendering.md), and [ARTCC_CUSTOMIZATION.md](../../../ARTCC_CUSTOMIZATION.md) "The airport map".

## 1. Goal

YAATEdit, the standalone scenario and airport-ground editor written by RY from ZOA, is subsumed into YAAT. Its capabilities are rebuilt natively: on YAAT's own models (`Scenario` and its siblings in `ScenarioModels`, `AirportGroundLayout`, `NavigationDatabase`), its parsers and validators (`ScenarioValidator`, `GeoJsonParser`, `CommandParser`, `RouteExpander`) and its client views (`MapCanvasBase`, the Settings window, `WindowGeometryHelper`), so the editors feel like part of YAAT.

None of YAATEdit's code or technical debt is imported. Its design, its feature set and its user-guide text are the input; its source is read only to understand behaviour.

An instructor can then, without leaving YAAT:

- download an ARTCC's vNAS training scenarios, edit local copies, test one in a YAAT room and export it as JSON to upload on the vNAS site;
- give aircraft realistic routes, callsigns, types and gates, time their spawns against a workload curve, and check the result in metrics;
- search and replace across an ARTCC's scenarios, find a local N-number, and print an RPO sheet;
- edit an airport's vNAS ground map, seeing live what YAAT's ground graph will make of it, then export it for vNAS data-admin and its gate tags for a yaat pull request.

**Credit.** Wherever credit appears (changelog, USER_GUIDE, the About box, docs), YAATEdit's author is credited only as "RY from ZOA", never by full name or GitHub handle.

## 2. Decisions

These are the owner's rulings on YAAT-1989, stated as the design.

- **Native rebuild.** YAATEdit's design and feature set are redone on YAAT's codebase. Its code is not imported and it is not shipped or launched as an external app. YAATEdit itself stays with its author; YAAT asks nothing of it (no archive request).
- **Basis for reuse.** YAATEdit's repository carries no licence file; its author's explicit permission, given to the owner, is the basis for reusing its design, its user-guide text and any of its example data.
- **Credit.** "RY from ZOA" only, as in the Goal.
- **Home.** The editors are windows in `Yaat.Client`, opened from the Tools menu (Scenario Editor, Airport Editor), drawn on the client's `MapCanvasBase` and views. Their UI-free logic lives in `Yaat.Sim` with its tests in `Yaat.Sim.Tests`. There is no separate editor project or app.
- **Scenarios are vNAS training scenarios, local-first.** The editor downloads an ARTCC's scenarios, edits local copies with every unknown field preserved, tests them in a YAAT room, then exports them as JSON for the author to upload on the vNAS site. It is built on YAAT's `ScenarioModels`, not a copy of them.
- **Export only.** YAAT never publishes to vNAS. The author exports a working copy as vNAS JSON and uploads it on the vNAS site themselves. YAAT holds no vNAS token and sends nothing to the vNAS data-admin API; it only reads data-api. YAATEdit's direct push, its manual API sender and its browser-extension relay (local HTTP bridge plus Chrome and Firefox extensions) are not rebuilt.
- **YAAT-only parts.** A working copy may use parts vNAS does not define (YAAT's own generator kinds). Export warns, naming each one, and keeps them as they are.
- **Route data.** The Route Finder draws on YAAT-owned sources (live-traffic and SWIM history, preferred routes, navdata) plus the csko flight-plans API. There is no FlightAware scraping and no FlightAware AeroAPI.
- **SWIM route history.** yaat-server keeps every SFDPS flight plan it observes, in a rolling window whose length is set by the server's storage budget.
- **Ground editing.** The Airport Editor edits the vNAS (ATCTrainer) airport GeoJSON YAAT already consumes: taxiways, runways, parking, spots and helipads. Raw GeoJSON features are the source of truth (the decision carried from [airport-editor-detail.md](airport-editor-detail.md)), validated live through `GeoJsonParser` and the `AirportGroundLayout` it builds. Gate tags (airline, size) live in the `Data/ARTCCs` sidecars. YAATEdit's own Bezier ground-layout format is not rebuilt.
- **Where an edited airport map goes.** The editor exports the map as GeoJSON for the author to upload in vNAS data-admin. It writes sidecar changes (gate tags, movement-area keys) into a yaat repo checkout the author names in Settings, for a pull request. It never writes the vNAS cache or the running app's bundled data.
- **Dropped.** The Taxi Route Builder's library of text templates: YAAT's ground graph and taxi pathing replace it.
- **Kept.** The N-number finder, the Scenario Scripter's Excel RPO sheet, and JSON search and replace across an ARTCC's scenarios.
- **Order.** Scenario editor first (browse, cache, edit, test in room), then export, then routes and spawn timing and metrics, then the airport editor. All four are in release vNext+1.
- **User guide.** YAATEdit's guide becomes USER_GUIDE.md sections, rewritten for the native windows: each milestone lands its own section.

## 3. Terms

Each term gets a glossary entry in [`docs/CONTEXT.md`](../../CONTEXT.md) in the commit that first uses it outside this plan.

- **Server mirror**: the editor's on-disk copy of an ARTCC's scenarios exactly as vNAS last served them, refreshed on download.
- **Working copy**: a locally edited scenario, kept apart from the server mirror and never deleted by a refresh. It is either derived from a server scenario (it records that scenario's id and the mirror version it started from) or local-only.
- **Export**: writing a working copy as vNAS JSON to a file the author uploads on the vNAS site; YAAT itself never sends a scenario to vNAS.
- **Unexported changes**: a working copy edited since it was last exported, or never exported; the browser marks it and closing asks first.
- **YAAT-only part**: a part of a working copy that vNAS does not define, such as YAAT's own generator kinds; export warns about it and keeps it.
- **Sidecar checkout**: the yaat repo checkout, named in Settings, into which the Airport Editor writes sidecar changes for a pull request.
- **Route source**: one provider of candidate routes for a departure and destination pair (csko, SWIM history, preferred routes, navdata), behind one interface.
- **Stamp profile**: a saved set of values (starting condition, nav path, flight-plan fields, presets with tokens, auto-track) that Apply Route writes onto an aircraft in one step. It is kept apart from the route geometry it is applied with.
- **Workload curve**: a set of control points, workload percent over scenario time, that the spawn planner turns into spawn times.
- **RPO sheet**: the Excel workbook a remote pilot operator reads during a session: arrivals on the left, departures on the right, one sheet per scenario.

## 4. Capability map

One row per YAATEdit capability. Verdicts: **native** (rebuilt with the same intent), **differently** (rebuilt with a changed design, said why), **dropped**. The last column names the milestone part (section 6).

| YAATEdit capability | Verdict | Builds on in YAAT | Part |
|---|---|---|---|
| ARTCC picker, download an ARTCC's scenarios | differently: the ARTCC ids are committed in `VnasArtccList` with a manual refresh of data-api's `/api/artccs`, not a hard-coded list in each place; downloads run bounded-parallel (`ScenarioDownloader`); a scenario that fails is listed, never skipped silently | `TrainingDataService` endpoints moved into `Yaat.Sim` (`VnasTrainingClient`), `HttpFileCache`, `YaatPaths` | M1.2, M1.3 |
| Background resync on open | native | `VnasTrainingClient`, `LocalScenarioStore` | M1.3 |
| Server and local-only lists, modified dot | native | `LocalScenarioStore` index | M1.3 |
| Auto-delete a local copy whose id reappears on the server | differently: nothing is deleted on refresh; a working copy whose server version changed is marked "server changed" and the author keeps or replaces it | `LocalScenarioStore` base-version tracking | M1.3 |
| New, duplicate (fresh ids), delete local scenario | native | `ScenarioDocument`, a ULID-style id generator in `Yaat.Sim` | M1.3 |
| Import JSON with id-conflict choice (new id, keep id, skip) | native | `ScenarioDocument` | M1.3 |
| Export raw local JSON | native (Save As) | `ScenarioDocument` | M1.3 |
| "Clean" export for ATC Trainer (unknown fields stripped) | differently: one Export JSON in the vNAS shape, for upload on the vNAS site; stripping depends on open question Q1 | `WorkingCopyExporter`, `ScenarioDocument` | M2.1 |
| Switch ARTCC, several editor windows | differently: one editor window with an ARTCC selector | Scenario Editor window | M1.3 |
| Unpushed-changes warning on exit or switch | differently: it warns about unexported changes, since YAAT does not push | `LocalScenarioStore` sidecar, last-export record | M2.1, M2.3 |
| Window position memory | native | `WindowGeometryHelper`, `UserPreferences` | M1.3 |
| General tab fields (name, ARTCC, student position, rating, primary airport and approach, auto-delete) | native | `Scenario` | M1.4 |
| ATC positions with pickers from the ARTCC and its neighbours | native | `ArtccConfigRoot`, `ArtccConfigResolver` | M1.4 |
| Initialization triggers with H:M:S entry | native, one shared time parser instead of YAATEdit's three | `InitializationTrigger`, new `ScenarioTime` | M1.4 |
| Arrival generator editor | native, extended to YAAT's own generator kinds | `ScenarioGeneratorConfig`, `VfrArrivalGeneratorConfig`, `OverflightGeneratorConfig`; field layout from `ArrivalGeneratorsEditorWindow` | M1.4 |
| Flight strip configuration | native | `Scenario` strip configurations, `ArtccConfigRoot` bays | M1.4 |
| Save with auto-track warning | differently: warnings come from live validation, not a save-time dialog | `ScenarioValidator`, `ScenarioEditChecks` | M1.6 |
| Apply step, auto-apply on switch, zero-fill setting | differently: edits apply to the document as typed; Save writes the working copy | view models | M1.4, M1.5 |
| Aircraft grid (sortable, column layout remembered) | native | `UserPreferences` | M1.5 |
| Aircraft editor (callsign, type autocomplete, transponder, spawn delay, taxi time, airport, approach, difficulty, altitude profile, RPO note) | native | `ScenarioAircraft`, `AircraftTypeCatalog` (the vNAS AircraftSpecs types, retained after `VnasDataService` loads them) | M1.5 |
| Starting conditions (parking, on runway, coordinates, fix or FRD, on final) | native | `StartingConditions`, `SpawnParser`, `FrdResolver` | M1.5 |
| Flight plan | native | `ScenarioFlightPlan`; field layout from `FlightPlanEditorWindow`; `RouteExpander` for checks | M1.5 |
| Preset commands with time offsets, reorder, duplicate | native, with every preset parsed live | `PresetCommand`, `CommandParser.ParseCompound`, `CommandDescriber` | M1.5, M1.6 |
| Auto-track configuration per aircraft | native | `AutoTrackConditions` | M1.5 |
| Parking Picker (click a stand on a map) | native, from the vNAS map parsed locally, no room needed | `AirportLayoutDownloader`, `GeoJsonParser`, `AirportGroundLayout`, `MapCanvasBase` | M1.7 |
| Test the scenario | native (YAATEdit had none; the ruling adds it) | `MainViewModel.LoadScenarioFromJsonAsync`, `MainViewModel.CanLoadScenario` | M1.8 |
| Direct push (PUT, POST to create, id reconciled) | dropped (ruling): authors export JSON and upload it on the vNAS site | `WorkingCopyExporter` instead | M2.1 |
| Manual API sender window | dropped: a raw request tool is not an author's job | none | none |
| Push ARTCC config back to vNAS | dropped (ruling): YAAT never publishes to vNAS | none | none |
| Browser-extension staging, push log | dropped (ruling); the push log becomes a per-scenario last-export record behind the unexported-changes marker | `LocalScenarioStore` sidecar | M2.1 |
| API token in settings | dropped (ruling): YAAT holds no vNAS token | none | none |
| Route Finder per aircraft | differently: one search over every route source, each route checked against navdata before it is offered | `IRouteSource`, `RouteFinder`, `RouteExpander` | M3.1 to M3.4 |
| Route Matcher window | differently: merged into the Route Finder panel (YAATEdit reached one search from four windows) | `RouteFinder` | M3.4 |
| csko flight-plans API | native, cached, failures shown | `CskoRouteSource` | M3.1 |
| FlightAware HTML scraping, AeroAPI gate observations | dropped (ruling) | SWIM history and sidecar gate tags instead | none |
| Route Builder map (fixes, VORs, airports, video maps, custom fixes, nearest-fix menu, FRD input) | differently: a route composer canvas on YAAT's own navdata and video maps, without its own navdata download | `NavigationDatabase` (`FixesNear`, airways), `VideoMapService`, `CustomFixLoader`, `FrdResolver`, `MagneticDeclination`, `MapCanvasBase` | M3.5 |
| Satellite and OSM tile underlay | native, Airport Editor only (Q10 settled; the vNAS tower-cab image, else USGS NAIPPlus, per the YAAT-2742 owner ruling) | none | M4.3 |
| Route profile library (routes with flat `Profile*` fields) | differently: route geometry and stamp profile are separate saved things | `StampProfile`, route library under `YaatPaths` | M3.5, M3.6 |
| Apply Route (stamp, randomize from pools, callsign prefix, heavy-only, VFR registry pick) | differently: stamping is one pure, tested function; heavy means the type's wake category, not a prefix list | `RouteStamp`, `NNumberIndex`, AircraftSpecs wake category | M3.6, M3.7 |
| STAR alias substitution | differently: token-aware on route tokens, never a substring replace | `RouteExpander` tokenisation | M3.6 |
| Batch refresh of callsigns and routes | native, as Apply Route over a selection | `RouteStamp`, `RouteFinder` | M3.7 |
| Assign gate (random, by area, graphically) | differently: ranking uses the layout's stands, sidecar gate tags, wake category and occupancy by spawn and taxi time; observed-gate tiers come from no paid API | `GateRanking`, `AirportGroundLayout`, sidecar `parkingAreas` | M3.8 |
| Departure taxi route picker (text templates) | dropped (ruling); preset `TAXI` commands are checked against the ground graph instead | taxi pathfinder via `ScenarioEditChecks` | M1.6 |
| Spawn Interval Planner (time bands, jitter, random order, saved plans) | native | `SpawnSchedule` | M3.9, M3.10 |
| Workload-curve spawn generation (walk the curve, fill-time) | native | `SpawnSchedule` | M3.9, M3.10 |
| Spawn-origin deconfliction | native, but a schedule pushed past the scenario length is reported, never silently clamped | `SpawnSchedule` | M3.9 |
| Scenario metrics (counts, rates per interval and hour, cumulative difficulty tiers) | native | `ScenarioMetrics`, `ScenarioRatingClassifier`, `DepartureSpawnClassifier` | M3.11 |
| N-number finder and its registry database | differently: the index is built on first use with progress shown, not at every launch | `NNumberIndex`, `HttpFileCache` | M3.12 |
| JSON search and replace across an ARTCC | native, over working copies, field-aware | `ScenarioSearch`, `LocalScenarioStore` | M3.13 |
| Nav-path procedure fixup against current CIFP | differently: built on YAAT's own procedure-version resolution, SIDs as well as STARs, and the fix is applied to the document | `ScenarioValidator` procedure issues, `NavigationDatabase` procedure-version resolution | M3.14 |
| Scenario Scripter (Excel RPO sheet) | native, reading working copies, not the server | `RpoSheet`, `LocalScenarioStore` | M3.15 |
| Airport Ground Editor (vertices, Bezier segments, areas) | differently: edits the vNAS GeoJSON features, with undo, and shows YAAT's parsed graph live | `GeoJsonParser`, `AirportGroundLayout`, `LayoutValidator`, `MapCanvasBase`, `GroundRenderer` as template | M4.1 to M4.5 |
| Import gates from vNAS into the layout | differently: the vNAS map is the source itself; gate tags sit beside it in the sidecar and survive a map refresh by stand name | sidecar `parkingAreas` | M4.6 |
| Save the ground layout | differently: the map exports as GeoJSON for upload in vNAS data-admin, and sidecar changes are written into the sidecar checkout for a pull request | `GeoJsonMapWriter`, `AirportSidecarWriter` | M4.6 |
| Auto layout synthesised from vNAS GeoJSON | dropped: `GeoJsonParser` already builds the graph | none | none |
| Movement-area polygons | differently: YAAT's map has none; movement areas are the sidecar keys `movementAreaTaxiways` and `nonMovementTaxilanes` | `AirportSidecarDefinition` | M4.6 |
| Fetch VFR traffic from OpenSky | dropped: unwired in YAATEdit, and the N-number finder covers the need | none | none |
| Python helper tools | dropped | none | none |

## 5. Architecture

### Yaat.Sim (UI-free, tested in Yaat.Sim.Tests)

Editor logic lives under `src/Yaat.Sim/Authoring/`, so the boundary between authoring and the simulation stays visible. Nothing here ticks, so ADR 0001's tick contract does not apply; every random choice takes a `Random` the caller seeds, so a test reproduces it.

- **Scenario document.** Each class in `ScenarioModels` gains a `[JsonExtensionData]` dictionary, so a vNAS field YAAT does not model survives a read and a write. `ScenarioDocument` is the public reader and writer around the loader's `ScenarioLoader.JsonOptions` and the exporter's writer settings; the editor edits the typed model. `ScenarioLoader` ignores the extension data. Equality for round trips is JSON-semantic, not byte order.
- **Scenario time.** `ScenarioTime` is the single H:M:S parser and formatter (right-to-left digit fill, so "130" is one minute thirty).
- **vNAS client.** `VnasTrainingClient` reads scenario summaries and scenarios from data-api (ARTCC configs and AircraftSpecs follow in later items); `TrainingDataService`'s scenario calls moved into it and it keeps only weather. `VnasArtccList` holds the ARTCC ids committed, with a manual refresh of data-api's large `/api/artccs` cached in `scenarios/artccs.json`. `LoadScenarioWindow` does not read data-api when connected: the scenario list comes from the hub with its rating gate, and offline (not connected, or the hub list failed) it lists the server mirror in `LocalScenarioStore`, where a Download for offline use button fills it through `ScenarioDownloader`. It only reads: no YAAT code sends anything to vNAS, and no call takes a token.
- **Local scenario store.** `LocalScenarioStore` keeps, per ARTCC under `YaatPaths.Combine("scenarios", artccId)`, the server mirror, the working copies and an index (origin server id, the mirror version a copy started from, modified flag); each working copy's sidecar holds its last-export record (when, and the content hash then). A working copy has unexported changes when its content hash differs from the last export's, or from its base when never exported. Working copies are user data, never evicted by a cache policy. It never writes under `cache/`. Its authoring operations (create, duplicate, delete, import with an id-conflict choice, `FindIdConflict`) live in `LocalScenarioStore.Authoring.cs`, with ULID ids from `ScenarioIdGenerator`.
- **Export.** `WorkingCopyExporter` (distinct from `ScenarioExporter`, which builds a scenario from a room) writes a working copy as vNAS JSON through `ScenarioDocument`, every field kept, to a file the author picks. It returns the YAAT-only parts it found so the view can warn, refuses a copy with validation errors, and records the export in the working copy's sidecar (`LocalScenarioStore.RecordExport`).
- **Server changes.** `ScenarioDiff` compares a working copy's base version with the newer server version a re-download brought, field by field, so the editor can show what changed on the server.
- **Validation.** `ScenarioEditChecks` runs `ScenarioValidator` and adds editor checks: positions that exist in the ARTCC config, auto-track without a position, duplicate callsigns, fields a starting-condition kind needs, preset `TAXI` commands the taxi pathfinder cannot route. It returns issues keyed by aircraft and field so the view can mark them. It reads navdata through `NavigationDatabase.InstanceOrNull` and reports "navdata not loaded" rather than throwing.
- **Routes.** `IRouteSource` takes a `RouteQuery` (departure, destination, optional fix, airline prefix, type, wake category) and returns observed or published routes. Implementations: `CskoRouteSource`, `PreferredRouteSource`, `NavdataRouteSource` (the procedures valid for the pair), and `RouteHistorySource` (SWIM history from yaat-server). `RouteFinder` merges them, drops duplicates and checks each route with `RouteExpander` before it is offered.
- **Stamping.** `StampProfile` is the saved set of values; `RouteStamp.Apply(aircraft, route, profile, pools, random)` is one pure function that returns the changed aircraft and a list of what changed, expanding the preset tokens (callsign, type, route, remarks, altitude, departure, arrival).
- **Gates.** `GateRanking` ranks a layout's stands for an aircraft: size from the type's wake category and the area's `maxAdg`, airline from the sidecar gate tags, occupancy from the other aircraft's spawn and taxi times.
- **Spawn timing and metrics.** `SpawnSchedule` (time bands with jitter, workload curve walk, fill-time placement by inverse CDF, origin deconfliction with an overrun report, both remedies offered (extend the scenario, compress the intervals) and a compress that breaks the requested separation flagged) and `ScenarioMetrics` (counts and rates, with the cumulative difficulty tiers) are pure functions over a scenario. The reference for M3.9 is YAATEdit's `X:/dev/_ext/YAATEdit/Views/SpawnIntervalWindow.axaml.cs`.
- **Bulk tools.** `ScenarioSearch` (field-aware search and replace over working copies, plain or regex), `ProcedureFixup` (rewrites a nav path or route from the validator's procedure-version findings), `RpoSheet` (the row model of the Excel sheet; the file writer is the client's), `NNumberIndex` (built from the FAA registry download on first use, queried by type and distance from an airport).
- **Airport map.** `AirportMapDocument` holds the raw GeoJSON features (taxiway, runway, parking, spot, helipad) with their exact coordinates and properties, reads and writes a combined file only (split per-layer files are YAAT-2674), and writes atomically. `UndoStack`, `SnapEngine` and `MapEdits` (move, insert and delete vertex, add and delete feature, set property, split taxiway, new feature) are pure.

  `SnapEngine` snaps within `SnapOptions.ThresholdFt` feet (10 by default; a target exactly at the threshold snaps, 0 turns snapping off) with a toggle per target kind, never targets the vertex being dragged, and breaks a tie in the order parking, spot, helipad, runway vertex, taxiway vertex.

  `LayoutValidator` lives in `src/Yaat.Sim/Data/Airport/` (moved from `tools/Yaat.LayoutInspector`), and LayoutInspector calls it there. `AirportMapValidation` runs `GeoJsonParser.ParseWithDiagnostics` and `LayoutValidator` over the document with caller-supplied `LayoutInputs` and returns the parsed `AirportGroundLayout` with its issues, each carrying a code (`unknown-feature-type`, `malformed-feature`, `parse-failure` or a `LayoutValidator` code) and a resolved feature index.
- **Sidecar writing.** `AirportSidecarWriter` writes the gate tags (and the movement-area keys) of one airport's sidecar, keeping every other key and the file's key order. Gate tags extend the existing `parkingAreas` entries, which already carry stands and `maxAdg`, with an `airlines` list.

### Yaat.Client.Core

- **Preferences.** Editor preferences (grid columns, last ARTCC, spawn planner and finder filters, the sidecar checkout folder) are `UserPreferences` entries, each with its `SettingsSearchCatalog` entry where it shows in Settings.

### Yaat.Client (windows, canvases, view models)

- **Tools menu.** Scenario Editor and Airport Editor open non-modal windows, each restored with `WindowGeometryHelper`. Neither needs a server connection; only Test in Room does.
- **Scenario Editor.** `ScenarioEditorWindow` with small view models (browser sidebar, general tab, aircraft grid, aircraft detail, validation panel) instead of one large one. Satellite windows: spawn planner (with a workload graph control), metrics, search and replace, N-number finder, route composer, Apply Route, RPO export.
- **Canvases.** `ParkingPickerCanvas`, `RouteComposerCanvas` and `AirportEditorCanvas` subclass `MapCanvasBase`. Each copies selection, hover, ghost and snap state into its render snapshot, never reading a styled property in `RenderFromSnapshot` (radar-rendering.md "The no-StyledProperty rule"), and keeps draw and hit-test geometry in one place.

  `ParkingPickerCanvas` draws from `AirportGroundLayout` directly, since `GroundCanvas` needs the server-built `GroundLayoutDto`. The airport editor pans with the middle button, so `MapCanvasBase` has a protected `PanButton` (the picker keeps the default right button; the airport editor sets the middle one); right-click opens context menus.
- **Test in room.** The editor sends the working copy's JSON through `MainViewModel.LoadScenarioFromJsonAsync`, enabled only when `MainViewModel.CanLoadScenario` holds (connected, in a room, allowed to load, no load in flight). A copy derived from a server scenario passes that scenario's id, so its scenario sidecar applies; a local-only scenario passes none.
- **Excel.** The RPO sheet's `.xlsx` writer sits in `Yaat.Client` behind open question Q9, so no spreadsheet package reaches `Yaat.Sim`.
- **Heavy work off the UI thread.** GeoJSON parses, the N-number index build and ARTCC downloads run off the UI thread and report progress; results are marshalled back with `Dispatcher.UIThread.Post`.

### yaat-server

- **Test in room needs nothing new.** The hub already loads a scenario from JSON; the server fetches the scenario's ARTCCs and airports through `ScenarioResourceManifest` as for any load.
- **SWIM route history (M3.3).** The one server change: yaat-server already ingests SFDPS flight plans for live traffic. It keeps a rolling record of observed flight plans (departure, destination, route, type, airline callsign) and serves it read-only to clients over HTTP.

  It keeps every observed plan in a rolling window whose length is set by the server's storage budget. Under the thin-server rule, the record format, the window trimming and the query logic (`RouteHistoryIndex`) live in `Yaat.Sim`; the server only stores and serves.
- **Editors never need the server otherwise.** Editing, validation, the parking picker, routes from the other sources and the airport editor all run on `Yaat.Sim` and data-api alone.

### Airport map: raw GeoJSON is the source of truth

`GeoJsonParser` is destructive and one-way (it snaps, splits edges, adds ramp edges and fillets), so the editor never edits the parsed graph. It edits `AirportMapDocument`'s raw features, keeps their coordinates and properties exactly, and writes the same format back.

After each edit (debounced, off the UI thread) `AirportMapValidation` parses the document as YAAT will and the canvas can overlay the result: graph nodes and edges, ramp edges, disconnected stands and the parser's issues. A pathfinding probe (pick two points, run YAAT's taxi pathfinder over the parsed layout) shows how YAAT will taxi on the edited map.

The editor reads a map from the vNAS cache through `AirportLayoutDownloader` or from a file or folder, and never writes `cache/airports/{FAA}.geojson`: that cache is shared with the server, LayoutInspector and the precompute tooling.

An edited map leaves the editor two ways. Export GeoJSON writes the map for the author to upload in vNAS data-admin. Sidecar changes (gate tags, movement-area keys) are written by `AirportSidecarWriter` into the sidecar checkout named in Settings, under its `src/Yaat.Sim/Data/ARTCCs/`, for the author to open a pull request; the editor refuses a folder that is not a yaat checkout and never runs git itself.

## 6. Build order

Each part lands alone, is sized for one implementer brief, and has a user-visible outcome. Every part that adds or moves a file updates the owning `## File map`.

Two Task Index rows land in `docs/architecture.md`, the first with M1.1 and the second with M4.1: "Add or change a scenario authoring surface (scenario JSON in and out, no room)" and "Edit or author an airport map (vNAS GeoJSON features, gate-tag sidecars)", each pointing at this plan, the owning subsystem doc and the `Authoring` folder.

### M1. Scenario editor: browse, cache, edit, test in room

| Part | Outcome | Main files |
|---|---|---|
| M1.1 Scenario JSON round trip | A vNAS scenario YAAT reads and writes keeps every field vNAS sent, known to YAAT or not. | `src/Yaat.Sim/Scenarios/ScenarioModels.cs`, `src/Yaat.Sim/Authoring/ScenarioDocument.cs`, `src/Yaat.Sim/Authoring/ScenarioTime.cs` |
| M1.2 vNAS training client and local store | Scenarios downloaded for an ARTCC stay on disk and open with no network. | `src/Yaat.Sim/Data/Vnas/VnasTrainingClient.cs`, `src/Yaat.Sim/Authoring/LocalScenarioStore.cs`, `src/Yaat.Client/Services/TrainingDataService.cs`, `src/Yaat.Client/Views/LoadScenarioWindow.axaml.cs` |
| M1.3 Scenario Editor window and browser | Tools > Scenario Editor lists an ARTCC's server and local scenarios, marks modified and server-changed ones, and creates, duplicates, deletes and imports them, or saves a copy to a file. | `src/Yaat.Client/Views/Authoring/ScenarioEditorWindow.axaml`, `src/Yaat.Client/ViewModels/Authoring/ScenarioBrowserViewModel.cs`, `src/Yaat.Client/ViewModels/Authoring/ArtccEditAccessSource.cs`, `src/Yaat.Sim/Authoring/LocalScenarioStore.Authoring.cs`, `src/Yaat.Sim/Authoring/ScenarioIdGenerator.cs`, `src/Yaat.Client/Views/MainWindow.axaml` |
| M1.4 General tab | The author edits a scenario's settings, ATC positions (picked from the ARTCC and its neighbours), triggers, generators and strip bays. | `src/Yaat.Client/ViewModels/Authoring/ScenarioGeneralViewModel.cs`, `src/Yaat.Client/Views/Authoring/ScenarioGeneralView.axaml` |
| M1.5 Aircraft grid and editor | The author adds, duplicates and edits aircraft: type, starting condition, flight plan, presets and auto-track. | `src/Yaat.Client/ViewModels/Authoring/ScenarioAircraftViewModel.cs`, `src/Yaat.Client/Views/Authoring/ScenarioAircraftView.axaml` |
| M1.6 Live validation | Every bad preset, unknown procedure, missing position or unroutable taxi shows beside its field as the author types. | `src/Yaat.Sim/Authoring/ScenarioEditChecks.cs`, `src/Yaat.Client/ViewModels/Authoring/ScenarioValidationViewModel.cs` |
| M1.7 Parking picker | The author picks an aircraft's parking by clicking a stand on the airport map. | `src/Yaat.Client/Views/Authoring/ParkingPickerCanvas.cs`, `src/Yaat.Client/Views/Authoring/ParkingPickerWindow.axaml` |
| M1.8 Test in room and guide | Test in Room loads the working copy into the connected room; USER_GUIDE gains its Scenario Editor section. | `src/Yaat.Client/ViewModels/Authoring/ScenarioBrowserViewModel.cs`, `src/Yaat.Client/ViewModels/MainViewModel.Scenario.cs`, `USER_GUIDE.md` |

### M2. Export

| Part | Outcome | Main files |
|---|---|---|
| M2.1 Export JSON | Export JSON writes a working copy, or every copy with unexported changes, as vNAS JSON for upload on the vNAS site; it warns about YAAT-only parts and keeps them, refuses a copy with validation errors, and the browser marks copies with unexported changes with the Unexported badge, read from the working copy's sidecar export record, not the store index. | `src/Yaat.Sim/Authoring/WorkingCopyExporter.cs`, `src/Yaat.Sim/Authoring/LocalScenarioStore.cs`, `src/Yaat.Client/ViewModels/Authoring/ScenarioExportViewModel.cs`, `src/Yaat.Client/ViewModels/Authoring/ScenarioBrowserViewModel.cs` |
| M2.2 Server changes on re-download | When a re-download finds that the server changed a scenario a working copy came from, the editor shows what changed field by field, and the author keeps the copy, replaces it, or starts a fresh copy beside it. | `src/Yaat.Sim/Authoring/ScenarioDiff.cs`, `src/Yaat.Sim/Authoring/ScenarioMerge.cs`, `src/Yaat.Client/ViewModels/Authoring/ScenarioMergeViewModel.cs` |
| M2.3 Unexported-changes warning and guide | Closing the editor or switching ARTCC with unexported changes asks first; USER_GUIDE gains its Exporting section, with the vNAS upload steps. | `src/Yaat.Client/Views/Authoring/ScenarioEditorWindow.axaml.cs`, `USER_GUIDE.md` |

### M3. Routes, spawn timing and metrics

| Part | Outcome | Main files |
|---|---|---|
| M3.1 Route sources: interface, csko, navdata | The editor can ask for real routes between two airports from csko and from navdata, each checked against YAAT's navdata. | `src/Yaat.Sim/Authoring/Routes/IRouteSource.cs`, `RouteQuery.cs`, `FoundRoute.cs`, `RouteFinder.cs`, `CskoRouteSource.cs`, `NavdataRouteSource.cs`, `NavdataRoutePairer.cs`, `PublishedRouteInfo.cs` |
| M3.2 Preferred routes source | Published preferred routes join the results. | `src/Yaat.Sim/Authoring/Routes/PreferredRouteSource.cs`, `PreferredRouteParser.cs`, `PreferredRouteInfo.cs` |
| M3.3 SWIM route history (yaat-server and yaat) | Routes, types and airline callsigns observed in live traffic join the results when connected to a YAAT server. | `src/Yaat.Sim/Authoring/Routes/RouteHistoryIndex.cs`, `RouteHistorySource.cs`; yaat-server `src/Yaat.Server/LiveTraffic/Swim/` and a read-only endpoint |
| M3.4 Route Finder panel | In the aircraft editor, Find Route lists candidate routes from every source and applies the chosen route, cruise altitude and, optionally, a real callsign and type. | `src/Yaat.Client/ViewModels/Authoring/RouteFinderViewModel.cs`, `src/Yaat.Client/Views/Authoring/RouteFinderView.axaml` |
| M3.5 Route composer | The author composes and saves named routes by clicking fixes, coordinates or fix-radial-distance points on a map with video maps. | `src/Yaat.Client/Views/Authoring/RouteComposerCanvas.cs`, `RouteComposerWindow.axaml`, `src/Yaat.Sim/Authoring/Routes/RouteLibrary.cs` |
| M3.6 Stamp profiles and stamping | A stamp profile writes starting condition, nav path, flight plan, token-expanded presets and auto-track onto an aircraft in one step, randomised from pools when asked. | `src/Yaat.Sim/Authoring/Stamping/StampProfile.cs`, `RouteStamp.cs` |
| M3.7 Apply Route and batch refresh | Apply Route stamps the selected aircraft with a preview of each change; Refresh Callsigns and Routes does the same across the scenario. | `src/Yaat.Client/ViewModels/Authoring/ApplyRouteViewModel.cs`, `src/Yaat.Client/Views/Authoring/ApplyRouteWindow.axaml` |
| M3.8 Gate ranking and Assign Gate | Assign Gate picks a fitting, free stand at random, within an area, or from the map, honouring airline tags and size. | `src/Yaat.Sim/Authoring/Gates/GateRanking.cs`, `src/Yaat.Sim/Data/AirportSidecarDefinition.cs`, `src/Yaat.Client/Views/Authoring/ParkingPickerCanvas.cs` |
| M3.9 Spawn schedule logic | Bands, workload curve, fill-time and origin deconfliction produce spawn times, with any overrun past the scenario length reported. | `src/Yaat.Sim/Authoring/Spawns/SpawnSchedule.cs`, `WorkloadCurve.cs`, `SpawnTypes.cs` |
| M3.10 Spawn planner window | The author filters aircraft, shapes a workload curve or bands, previews the times on a graph and applies them; plans save and load. | `src/Yaat.Client/ViewModels/Authoring/SpawnPlannerViewModel.cs`, `src/Yaat.Client/Views/Authoring/WorkloadGraph.cs` |
| M3.11 Scenario metrics | A metrics window shows live counts, interval and hourly rates and difficulty tiers for the open scenario. | `src/Yaat.Sim/Authoring/ScenarioMetrics.cs`, `src/Yaat.Client/Views/Authoring/ScenarioMetricsWindow.axaml` |
| M3.12 N-number finder | Find N-Number lists US-registered aircraft near the primary airport by type and picks a callsign and type; the registry index builds on first use with progress. | `src/Yaat.Sim/Authoring/NNumberIndex.cs`, `src/Yaat.Client/Views/Authoring/NNumberFinderWindow.axaml` |
| M3.13 Search and replace across an ARTCC | The author filters every aircraft across the ARTCC's working copies, replaces values (plain or regex) with a preview, and saves the changed scenarios. | `src/Yaat.Sim/Authoring/ScenarioSearch.cs`, `src/Yaat.Client/Views/Authoring/ScenarioSearchWindow.axaml` |
| M3.14 Procedure fixup | Out-of-date SIDs and STARs in nav paths and routes are listed and fixed in one step, across one scenario or the ARTCC. | `src/Yaat.Sim/Authoring/ProcedureFixup.cs`, `src/Yaat.Client/Views/Authoring/ProcedureFixupWindow.axaml` |
| M3.15 Excel RPO sheet | Export RPO Sheet writes one workbook (summary plus a sheet per chosen scenario, arrivals left, departures right) from working copies; USER_GUIDE gains its Routes and Timing section. | `src/Yaat.Sim/Authoring/RpoSheet.cs`, `src/Yaat.Client/Services/RpoSheetWriter.cs`, `USER_GUIDE.md` |

### M4. Airport editor

| Part | Outcome | Main files |
|---|---|---|
| M4.1 Airport map document | An airport map opened and saved by YAAT as a combined file is unchanged. | `src/Yaat.Sim/Authoring/AirportMap/AirportMapDocument.cs`, `MapFeature.cs`, `GeoJsonMapReader.cs`, `GeoJsonMapWriter.cs`, `UndoStack.cs` |
| M4.2 Validation and snapping in Yaat.Sim | LayoutInspector and the editor share one validator; snapping and edit operations are ready for the canvas. | `src/Yaat.Sim/Data/Airport/LayoutValidator.cs` (moved), `tools/Yaat.LayoutInspector/`, `src/Yaat.Sim/Authoring/AirportMap/SnapEngine.cs`, `AirportMapValidation.cs`, `MapEdits.cs` |
| M4.3 Airport Editor window and canvas | Tools > Airport Editor opens an airport from the vNAS cache, a file or a folder and shows it with selection and a property panel. | `src/Yaat.Client/Views/Authoring/AirportEditorWindow.axaml`, `AirportEditorCanvas.cs`, `AirportEditorRenderer.cs`, `src/Yaat.Client/Views/Map/MapCanvasBase.cs` |
| M4.4 Drawing and editing with undo | The author draws taxiways and runways, places parking, spots and helipads, drags, inserts, deletes and splits, and undoes any of it. | `src/Yaat.Client/Views/Authoring/AirportEditorCanvas.cs`, `src/Yaat.Client/ViewModels/Authoring/AirportEditorViewModel.cs` |
| M4.5 Live graph validation | As the author edits, the canvas shows the graph YAAT builds, its issues, and a taxi route between two picked points. | `src/Yaat.Sim/Authoring/AirportMap/AirportMapValidation.cs`, `src/Yaat.Client/Views/Authoring/AirportEditorRenderer.cs` |
| M4.6 Gate tags, movement areas and save | The author tags stands with airlines and size and marks movement-area taxiways, exports the map as GeoJSON for vNAS data-admin, writes the sidecar into the sidecar checkout for a pull request, and is asked before closing unsaved work; USER_GUIDE gains its Airport Editor section. | `src/Yaat.Sim/Authoring/AirportMap/AirportSidecarWriter.cs`, `src/Yaat.Sim/Data/AirportSidecarDefinition.cs`, the Settings window and `SettingsSearchCatalog`, `src/Yaat.Sim/Data/ARTCCs/README.md`, `ARTCC_CUSTOMIZATION.md`, `USER_GUIDE.md` |

## Fillet overrides

The Airport Editor previews the fillets YAAT generates at each taxiway corner and lets the author override them per corner: remove a corner's fillet, change its radius, force one where the generator skipped the corner, and pin its tangent points on each taxiway. The simulation uses the overrides: the ground graph it builds, and with it taxi paths, turns and conflict geometry, follows them.

Overrides are stored in the airport's `Data/ARTCCs` sidecar, in their own section, keyed by the corner's pre-fillet apex position plus its two taxiway names and arm bearings, and resolved against the pre-fillet layout as `blockedTurns` is (`BlockedTurnResolver`). Node and corner ids renumber on every load, so they are never the key. An override whose apex no longer matches a corner after a vNAS map update is reported as orphaned and never applied elsewhere.

`Yaat.Sim` passes the overrides through `LayoutInputs` into the fillet generator: suppress and radius apply where corners are planned (`CornerSpec.RequestedRadiusFt` is the radius ceiling), a forced corner bypasses the generator's skips, and pinned tangent cuts are fixed points the shared-arm pass and the stable-anchor redirect never move. A public per-corner report gives each corner's outcome (built, skipped with its reason, overridden, orphaned), its achieved radius and its warnings, including the skips that raise no warning today. The sidecar section joins `AirportSidecarHash`, so the precompute layout cache invalidates when overrides change.

Built as YAAT-2519 (the `Yaat.Sim` half) and YAAT-2520 (the editor tools), after M4.3, M4.5 and M4.6.

## 7. Tests that matter

Tests follow `tests/CLAUDE.md` and [`docs/test-harness.md`](../../test-harness.md): real navdata through `TestVnasData.EnsureInitialized()`, `YAAT_APPDATA_DIR` pointed at a per-test folder for anything under `YaatPaths`, HTTP stubbed at the `HttpMessageHandler` (the only mocked boundary), seeded `Random` for every random choice, and no wall-clock assertion.

**M1.**

- Round trip: every scenario in `docs/atctrainer-scenario-examples/` and the scenario corpus the loader tests use reads and writes back JSON-equal, including a field added by hand that YAAT does not model.
- The loader and `ScenarioResourceManifest` give the same result before and after the extension-data change (the existing corpus tests stay green).
- `ScenarioTime`: empty, one digit, right-to-left fill, overflowing minutes and seconds, garbage.
- Store: a refresh never deletes or overwrites a working copy; a changed server version marks the copy; corrupt files are reported by name, not skipped; ids stay unique on duplicate and import.
- Client: a failed scenario in a bulk download is reported and the rest still land.
- `ScenarioEditChecks`: each check fires on a real scenario edited to break it, and stays quiet on the untouched corpus.
- UI: Test in Room is disabled with no room or a load in flight, and passes the working copy's JSON and origin id (`Yaat.Client.UI.Tests`).

**M2.**

- Exporter: the written file is JSON-equal to the working copy, a field YAAT does not model included; a copy using YAAT-only generator kinds exports with them kept and the warning names each; a validation error blocks export.
- Marker: an export clears the copy's unexported-changes mark, a later edit sets it again, and exporting every marked copy writes exactly those copies.
- Re-download: a server version newer than a copy's base marks the copy, leaves it untouched, and `ScenarioDiff` lists exactly the fields changed on a real scenario edited by hand.
- UI: closing or switching ARTCC with unexported changes asks first, and with none closes at once (`Yaat.Client.UI.Tests`).

**M3.**

- `RouteHistoryIndex`: every flight plan inside the rolling window is kept and queryable, and one older than the window is trimmed.
- `SpawnSchedule`: bands with seeded jitter; fill-time places exactly the requested aircraft; a flat curve gives even spacing; deconfliction separates same-origin spawns and reports overrun; an empty filter yields nothing.
- `ScenarioMetrics`: one aircraft, a tight cluster, departures timed by taxi time, cumulative tiers.
- `RouteStamp`: every token expands; an alias replaces whole route tokens only (a longer procedure name containing the alias is untouched); the change list matches the diff.
- Route sources parse committed real responses (a recorded csko reply, a preferred-routes extract); `RouteFinder` drops a route `RouteExpander` cannot expand on real navdata; a source that fails leaves the others' results.
- `GateRanking`: a heavy never gets a stand whose area `maxAdg` is too small; airline tags win over untagged; an occupied stand is skipped by spawn and taxi time.
- `ProcedureFixup`: a nav path naming a superseded STAR and one naming a superseded SID are rewritten to the current version on the committed CIFP.
- `ScenarioSearch`, `RpoSheet` rows, and `NNumberIndex` over a committed real extract of the FAA registry.
- Aviation review through the `aviation-review-gate` skill for gate fit, spawn-origin separation, and stamped altitudes and routes.

**M4.**

- A combined OAK file round-trips JSON-equal, coordinates exact.
- Undo and redo through a mixed sequence returns the document to each earlier state.
- `SnapEngine` snaps within its threshold and not beyond.
- `LayoutValidator` gives LayoutInspector's existing results after the move (`Yaat.LayoutInspector.Tests` stays green).
- `AirportMapValidation` on a real map with a stand broken loose reports it; the editor never writes the vNAS cache path.
- Export GeoJSON writes a file `GeoJsonParser` reads back to the same layout as the document.
- `AirportSidecarWriter` keeps every other key and their order; YAAT's sidecar loader reads what it writes; a sidecar checkout folder that is not a yaat checkout is refused.
- Aviation review for the gate-size rules and any validation rule that judges a taxiway or runway.

## 8. Open questions for the owner

Each is asked through an interview before the part it gates is briefed.

- **Q1. Export shape for ATC Trainer: settled.** Two exports: the vNAS JSON with every field kept, and also a clean export that strips the fields YAAT does not model, as YAATEdit had. The clean export also drops the YAAT-only parts (VFR arrival and overflight generators, `liveSession`, generator `enabled`), and its warning names each one it dropped. Exporting a copy open with unsaved edits asks Save / Discard / Cancel first, as closing the window does.
- **Q2. Token source and publishing: settled.** YAAT does not publish to vNAS: authors export JSON and upload it on the vNAS site. No token is held, no desktop-PUT probe is run, and the publish parts are dropped.
- **Q3. Token storage off Windows: moot.** No vNAS token is held (Q2).
- **Q4. Push ARTCC config: moot.** YAAT never publishes to vNAS (Q2), so YAATEdit's ARTCC config push is dropped.
- **Q5. SWIM route history: settled.** yaat-server keeps every SFDPS flight plan it observes in a rolling window, the window length set from the server's storage budget.
- **Q6. Preferred-route data: settled.** The FAA's published preferred routes, downloaded and cached per AIRAC cycle, if the file is still published when M3.2 is briefed; when it is unavailable the source says so and the other sources still answer.
- **Q7. YAAT-only fields in an exported scenario: settled.** Export warns when the working copy uses YAAT-only parts (YAAT's own generators) and keeps them as is; if vNAS turns out to drop unknown fields on upload, the warning says so.
- **Q8. What "near" means for the N-number finder: settled.** Distance from the owner's ZIP code centroid (an extra GeoNames download), labelled "registered near".
- **Q9. Excel writer dependency: settled.** ClosedXML in `Yaat.Client`, its current stable version looked up when M3.15 is briefed; no spreadsheet package reaches `Yaat.Sim`.
- **Q10. Map imagery under the editors: settled.** Imagery under the Airport Editor, the vNAS tower-cab image where there is one, else USGS NAIPPlus (the YAAT-2742 owner ruling), its licence terms for a desktop app checked before M4.3 is built; no imagery under the route composer (video maps and the vNAS map only). The mock is `docs/plans/canvases/yaat-2515-imagery/`. Gates M3.5 and M4.3.
- **Q11. Gate tags at runtime.** Should YAAT's own arrival-parking choice in the simulation also honour the new airline tags? This changes existing behaviour. Options: (a) yes, as its own item with aviation review; (b) editor-only. Recommendation: (a), booked after M4.6.
- **Q12. Where an edited airport map goes: settled.** The editor exports GeoJSON for upload in vNAS data-admin, and writes sidecars (gate tags) into a yaat repo checkout named in Settings, for a pull request.
- **Q13. Testing an edited map in a room: settled.** No in-room test for now; the editor's graph overlay and taxi probe stand in. A hub call that pins an uploaded map for a room's next load (a server change through `ResourcePin`) is not planned. Gates M4.5.
- **Q14. Spawn deconfliction rule: settled.** A conflict is the same origin (the departure airport for ground starts, the fix for airborne starts), as YAATEdit; an overrun is reported and both remedies are offered (extend the scenario, or compress the intervals); reviewed by `aviation-sim-expert`. Two owner rulings (YAAT-2506): On Final starts share an origin per destination runway, and spacing also keeps clear of aircraft the run does not schedule, whose delays never change.
- **Q15. Migrating YAATEdit users' data: settled.** No import: authors re-download from vNAS, and a local-only YAATEdit scenario reaches YAAT by exporting it to JSON and importing that file.
- **Q16. Undo in the scenario editor: settled.** A full undo stack (undo and redo over every edit) in the scenario editor, the General tab and the aircraft grid and editor included.

## 9. Risks

- **The server copy changes under a working copy.** Another author may change a scenario on vNAS after a working copy was taken from it, and an upload of the export would overwrite that change. A re-download marks the copy and shows what changed (M2.2); nothing local is overwritten, and the author decides before exporting.
- **vNAS and YAAT-only parts.** vNAS may drop YAAT-only parts on upload. Export warns and keeps them (Q7); if vNAS is seen dropping them, the warning says so.
- **Round-trip change reaches yaat-server.** Adding extension data to `ScenarioModels` changes types yaat-server compiles; `tools/test-all.ps1` must run for M1.1, since yaat's own build does not compile the server.
- **New scenario fields and the manifest.** Any field the editor adds that names an airport or ARTCC must join `ScenarioResourceManifest`, or a test-in-room load never prefetches it.
- **Third-party route data.** csko is a hobby service with a limited window and no promise of availability; it is cached, its failure shown, and never the only source.
- **Server scope creep.** SWIM history is the one server feature; keeping its logic in `Yaat.Sim` holds the thin-server rule, and the rolling window bounds its storage.
- **Writing into a repo checkout.** The sidecar checkout may hold the author's own work; the writer touches only the one airport's sidecar, keeps every other key, refuses a folder that is not a yaat checkout, and never runs git.
- **Navdata not ready.** The client initializes `NavigationDatabase` at startup; an editor opened earlier reads `InstanceOrNull` and says navdata is loading, never throws.
- **Magnetic headings.** Scenario headings are magnetic; the loader converts them with the magnetic model date. The editor stores what the author types and never writes a true heading.
- **Shared caches.** The airport cache and the precompute cache are shared; the editor never writes the cache, and a sidecar change the parser reads (movement-area keys) needs the precompute check before it is submitted.
- **Canvas threading.** Editor state read from styled properties in `RenderFromSnapshot` races the render thread; each canvas copies its state into the snapshot.
- **Large downloads.** The FAA registry download is large; it happens only when the author first opens the N-number finder, with progress and cancel.
- **God view model.** YAATEdit's single scenario view model mixed every concern; the editor is split into small view models from M1.3 on.
- **A fifth ARTCC list.** Scenario ARTCC lists are already hard-coded in several places; the editor reads data-api's list instead of adding another.
- **Release size.** All four milestones sit in vNext+1; the order lets the release ship with the earlier milestones if the later ones slip, which is the owner's call.
