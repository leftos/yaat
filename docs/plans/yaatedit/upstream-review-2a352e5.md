# YAATEdit upstream 37b8392..2a352e5 review (YAAT-1989)

PROTOCOL: 1 overlay: none (no `docs/agents/explore.md` in the worktree; the orchestrator brief stood in; CLAUDE.md not read separately); 2 README: `docs/README.md` exists, not opened (the prompt named the plan and architecture first); 3 Task Index: main `docs/architecture.md` has no authoring row, the worktree's `docs/architecture.md` rows for scenario authoring and the browser found (lines 280-283); 4 design docs:

`docs/plans/yaatedit/README.md` read in full; 5 source: done on both sides; 6 tree search: done (rg), the docs do not cover the strip editor or YAAT Check parity; MCP: linear-ro `list` for repo yaat read (open issues only; closed M1/M2 sub-issues are not listed, so "done" below is judged from code on feat/yaatedit).

Anchors: worktree root `X:/dev/yaat.wt/feat-yaatedit/yaat` (call it W), upstream `X:/dev/_ext/YAATEdit` (call it U) at 2a352e5. Nothing was built or run; every "would throw" below is by reading code and is marked unmeasured.

## Short answer

- 28 upstream commits in the range (`git rev-list --count`). About 15 are moot inside YAAT (submodule, Yaat.Sim replacement, installer, docs, gitignore, perf already done by M1.2, cleanup of Roaming data, push-path fixes).
- The port's store design (server mirror apart from working copy, nothing deleted on refresh: `LocalScenarioStore.ApplyServerBody`, `MarkGoneFromServer` flag-only) already prevents the 33fbb3d data loss and the de4eb62 overwrite. The aircraftIds drop (bf70b8f) cannot happen: `FlightStripConfiguration.AircraftIds` is typed in `W/src/Yaat.Sim/Scenarios/ScenarioModels.cs` (class at ~521) and `ScenarioDocument` keeps unknown fields.

  Not shared: 33fbb3d, bf70b8f push-drop, 2a352e5 (no push, no sidecar split).
- One JSON-compat defect IS possibly shared, on import only: an older YAATEdit file with `"timeOffset": null` (or null generator numbers) is accepted by `LocalScenarioStore.CheckScenario` (a JsonNode check) but `ScenarioDocument.Read` deserializes into non-nullable `int` members, which System.Text.Json refuses (unmeasured, standard behaviour). Q15 sends YAATEdit users through exactly this import path. New issue A.
- Three small residual hardening gaps: no backup before an overwrite by import-keep-id, by merge Apply, or by delete (upstream now backs up every overwrite; its delete moves to `_backups/`). New issue B (decision D2).
- Real features the port lacks: the flight strip editor (largest, new issue C), preset-command autocomplete (D), scenario-sidecar authoring (E), YAAT Check advisories beyond `ScenarioEditChecks` (F), facility filter in the browser (G), fleet-aware type pick in Apply Route (fold into YAAT-2504), lazy video maps in the composer (fold into YAAT-2502).
- No open Linear issue owns the strip editor, sidecar authoring, autocomplete, or the null-import defect.

## Per-commit sections

### Group 1: Batch Refresh rules (ecb85dc, df2156d, 4476c8b)

- Behaviour: Batch Refresh mirrors the new aircraft type into the flight plan's filed type (`Flightplan.AircraftType`); a duplicate callsign is never auto-renamed: if every real-world callsign on the picked route is already used the aircraft is skipped entirely and the holder untouched; the status bar counts the unchanged. (df2156d added auto-rename, 4476c8b replaced it at the user's request.)
- YAAT: partly. `W/src/Yaat.Sim/Authoring/Stamping/RouteStamp.cs` takes `context.TakenCallsigns` (RouteStamp.cs:29,270,310 `RandomCallsign` excludes taken) and creates a flight plan carrying the type when absent (RouteStamp.cs:139). Not verified: that a type change on an existing plan rewrites `FlightPlan.AircraftType`, and that "no free callsign" skips the aircraft rather than keeping the old/duplicate (batch half is YAAT-2504, in progress).
- Owner: YAAT-2504 (M3.7). Action: fold in (exact text in the table).

### Group 2: store, sync and data loss (8e3f37b, de4eb62, 33fbb3d)

- 8e3f37b: gzip/brotli HttpClient, 16 pooled connections, scenarios fetched 8 at a time, parallel local load; ZOA 131 scenarios 46 s to 5 s; a timed-out scenario is skipped, not fatal. YAAT: done in M1.2 (`W/src/Yaat.Sim/Authoring/ScenarioDownloader.cs` uses `Parallel` with `MaxInFlight`, failures reported in `DownloadReport.Failed`, plan section 4 row 1). Moot.
- de4eb62: background sync at most every 6 h; never overwrite a scenario edited locally (`_sync.json` ModifiedIds, persisted modified dots). YAAT: the never-overwrite half is structural (mirror vs working copy; `ApplyServerBody` never touches a working copy and reports ServerChanged). The 6 h TTL is absent: `ScenarioBrowserViewModel.Resync.cs` starts a resync on open/Refresh without an age check (rg for TimeSpan/TTL found none there). Decision D3.
- 33fbb3d (data loss): a local scenario whose id matched a server scenario was deleted on launch/Re-download (the file was the server copy's file too); Re-download overwrote local files and dropped unsaved edits. Fix:

  `ProtectedIds`, backups before any replace, deletes go to `_backups/`, refuse on empty server list, merge duplicates into the server list marked edited (`MergeLocalDuplicatesAsync`), recover unlisted `{id}.json` on launch (`RecoverUnlistedScenariosAsync`), Re-download saves the open scenario's unsaved edits first.

  YAAT: the root cause cannot occur (separate `server/` mirror and `local/` working-copy folders, `StoredId`, `ImportOverMirror` which makes the import the mirror's working copy based on the current mirror; `MarkGoneFromServer` only flags, so an empty server list deletes nothing; `Load` re-adds unlisted mirror files, `LocalScenarioStore.cs:404`).

  Residual: no `_backups` anywhere (rg backup in `LocalScenarioStore*.cs`: none), so import keep-id over an existing working copy (`ImportKeepingId`, Authoring.cs ~273), merge Apply (`ApplyMerge`, LocalScenarioStore.cs ~297) and `DeleteWorkingCopy` (Authoring.cs:191, confirmed in the UI by `ConfirmDeleteAsync`) discard the previous text with no copy. Unsaved-edit save before resync: YAAT-2961 (open) and YAAT-2788 touch the same area.

  Owner: none for the backup. New issue B.

### Group 3: Yaat.Sim integration, JSON compat and tests (3415b36, e8e7863, d5dbdd4, 85921b7, 4b20672)

- 3415b36 / e8e7863 / d5dbdd4: submodule, replace duplicated nav/CIFP/geo/specs code with Yaat.Sim; JSON Search and Replace validates routes with `ScenarioValidator` (SIDs too, any token, closest-transition suggestion); YAAT-unreadable nulls fixed:

  `PresetCommand.timeOffset` always written (blank = 0, `TimeOffsetJson`), optional generator numbers (`startTimeOffset`, `maxDistance`, `intervalDistance`, `maxTime`, `intervalTime`) omitted when null (`JsonIgnore WhenWritingNull`); reading still accepts null so old files load.

  Evidence upstream: 78 of 140 cached ZOA scenarios were unloadable in YAAT, all 252 now round-trip; tests in `tests/YAATEdit.Tests/YaatCompatibilityTests.cs` (corpus over the local cache).
- YAAT write side: moot. `PresetCommand.TimeOffset` is `int` (ScenarioModels.cs:201,217), optional generator numbers are `int?` with WhenWritingNull (ScenarioModels.cs ~477-486), and `ScenarioDocument` writes only members the source held or that differ from default.
- YAAT read side: the shared defect. `ScenarioDocument.Read` (`ScenarioDocument.cs:47-53`) uses `ScenarioLoader.JsonOptions` into the same non-nullable members; `CheckScenario` (`LocalScenarioStore.cs:848`) only parses JsonNode and checks id and name; the import (`ImportScenario`, Authoring.cs:222) therefore stores a file that the editor, `ScenarioLoader` (test in room) and `WorkingCopyExporter` cannot read.

  `ScenarioDocumentRoundTripTests` has no null-timeOffset case (rg: only `Read_JsonNull_ThrowsJsonException`, a whole-document null). The plan's Q15 ("a local-only YAATEdit scenario reaches YAAT by exporting it to JSON and importing that file") makes this the migration path; files saved before 2026-11-25 by YAATEdit carry the nulls (upstream: "files saved by older YAATEdit versions"). Unmeasured: no test was run. Owner: none. New issue A.
- Test parity: the plan's M1 round-trip test list (section 7) covers unknown fields; add upstream's null cases (see A).
- Route validation in Search and Replace: owner YAAT-2510 (M3.13) and YAAT-2511 (M3.14). Plan row for nav-path fixup already says "SIDs as well as STARs, on YAAT's procedure-version resolution". Nothing to add except: S&R route validation must use `ScenarioValidator`, any token (fold into YAAT-2510).
- 85921b7 / 4b20672 (CLAUDE.md notes on YAAT download/caching, fields, validator, JSON-compat rules): moot as docs; the rules they record are covered by A.

### Group 4: YAAT Check, fleet, facility filter, generators (f3fa02b, 038eb2d, 732e1eb)

- f3fa02b: "YAAT Check" (F7 / Tools / Aircraft toolbar; run automatically before Export/Push/Stage). Categories:

  YAAT-readability (can the loader read the file), YAAT `ScenarioValidator`, ground maps via `ScenarioResourceManifest.MapRequiredAirportIds`, parking spots missing from the vNAS map, unknown aircraft types, duplicate callsigns, fleet realism (airline vs type via `AirlineFleets`/`AirportAirlines`), hold-for-release, ARWY (active runways, reported from the scenario sidecar when present), rating, facility prefix naming, generator sanity; one-click fixes.

  Plus `FleetService`: fleet-aware type picking (`PickTypeForAirline`) for Apply Route and Batch Refresh; parses YAAT's "Exported Scenario - Needs Review" clipboard text.
- YAAT: partly. `W/src/Yaat.Sim/Authoring/ScenarioEditChecks.cs` (lines ~101-345) runs `ScenarioValidator` per aircraft (parse failures, procedure issues, transition substitutions, type mismatch), duplicate callsigns, auto-track position, positions not in config, starting-condition fields, first TAXI preset routing, shared stands.

  Absent in the port (rg in `src/Yaat.Sim/Authoring` for GroundMapCheck, AirlineFleets, hold-for-release, rating: none): ground-map-missing and parking-spot-not-on-map, unknown aircraft type, fleet realism, hold-for-release, ARWY info, rating and facility-prefix naming, generator sanity. `GroundMapCheck` (`Yaat.Sim/Scenarios/GroundMapCheck.cs`) and `ScenarioResourceManifest` exist in Sim and are reusable.

  Fleet-aware type pick: absent (rg airline|fleet in `Authoring`: only `GateRanking` and route-source prefixes). The "needs review" clipboard parser is moot (YAAT is the exporter; `ScenarioExportReviewWindow` exists). Export gating on validation is done (`WorkingCopyExporter` refuses errors, plan Q1). Owner: none for the advisories; YAAT-2504 for fleet type pick. New issue F.
- 038eb2d: scenario list facility filter matching YAAT's Load Scenario dialog (facility = code after the first dash, e.g. "S1-OAK ..." gives OAK). YAAT: `LoadScenarioWindow.axaml.cs` has `_facilityFilter` and `_artccFacilityFilter`; the Scenario Editor browser has none (rg facility in `ScenarioBrowserViewModel.cs`: none). Owner: none. New issue G (small feature).
- 732e1eb: VFR arrival and overflight generator editors, with the YAAT-only fields. YAAT: done in M1.4 (`ScenarioVfrArrivalGeneratorRowViewModel.cs`, `ScenarioOverflightGeneratorRowViewModel.cs`, `ScenarioGeneralViewModel.Generators.cs`). Moot. The upstream "generator sanity" checks belong to F.

### Group 5: Route Builder performance (d3ff9d3, 2088048)

- d3ff9d3: video maps loaded lazily (only enabled maps; `VideoMap.IsLoaded`, `EnsureVideoMapGeometryAsync`) via Yaat.Sim `HttpFileCache` (HEAD/Last-Modified freshness) into a Local-AppData cache; ZOA was 632 maps / 249 MB parsed on every open. 2088048: canvas draws video maps as batched SKPaths with bbox line culling.
- YAAT: composer M3.5 in progress (YAAT-2502). `RouteComposerViewModel.cs` holds `VideoMapData Data` (line 55); I did not verify lazy loading or batching. The plan says the composer uses `VideoMapService` (YAAT's own cache). Action: fold into YAAT-2502 as an acceptance check (only enabled maps load; batched path; culling).

### Group 6: sidecar, cheatsheet autocomplete, cleanup, installer (01c7879, 61455da, e8f9296, 2a352e5)

- 01c7879: `YaatCommandBox`: verb autocomplete from `docs/command-cheatsheet.json`, suggesting only where a verb belongs (start; after `;` `,` THEN AND; after `WAIT n`, `AT x`, `LV n`); preset boxes live-validate with `CommandParser.ParseCompound` (red underline plus reason tooltip); initialization-trigger boxes autocomplete only. YAAT: live validation done (`ScenarioPresetRowViewModel` issue text, `ScenarioEditChecks`); autocomplete absent:

  `ScenarioAircraftView.axaml:344` `PresetCommandBox` is a plain box, and the init-trigger rows likewise. The main client already has `Services/CommandInputController.cs`, `ArgumentSuggester.cs`, `AddCommandSuggester.cs`, `Commands/CommandSchemeParser.cs` (in-process, so no cheatsheet JSON is needed). Owner: none. New issue D.

  Also "YAAT sidecar awareness" (ARWY info, sidecar load warnings, duplicate warns the sidecar will not follow): belongs to E.
- 61455da (a) Scenario-sidecar export (`ScenarioSidecarExportWindow`): builds `Data/ARTCCs/{ARTCC}/Scenarios/{scenarioId}.json` (`activeRunways`, `studentLowestPosition`) prefilled from YAAT's existing sidecar or the scenario's `ARWY` init triggers, validated with `ActiveRunwayListParser`, round-tripped through YAAT's loader. YAAT: absent as an authoring surface.

  Reader exists (`W/src/Yaat.Sim/Data/ScenarioSidecarLoader.cs`, `ScenarioSidecar` has exactly `ActiveRunways` and `StudentLowestPosition`; load-in-room by scenario id per plan line 160). The plan's sidecar checkout (Settings folder, `AirportSidecarWriter`) is airport-only. New issue E.
  (b) Import YAAT preset taxi routes from the airport sidecar (`taxiRoutes`) into the dropped Taxi Route Builder: moot (plan: Taxi Route Builder dropped by ruling; preset TAXI commands are checked by `ScenarioEditChecks.AddTaxiPresets`). Option in D4.
  (c) Old Roaming data cleanup (`LegacyDataCleanup`): moot (no migration, Q15; YAAT's own caches). (d) Re-download confirm (Cancel / Keep my edits / Discard edits): moot, a refresh never touches a working copy; "Server changed" plus the merge window (YAAT-2912/2913) is the equivalent.
- e8f9296: Inno Setup per-user installer, app icon, v1.0.0. Moot (YAAT has its own installer: `docs/installer-release.md`).
- 2a352e5: YAAT-only scenario data (`vfrArrivalGenerators`, `overflightGenerators`) goes to the YAAT scenario sidecar `{root}/{ARTCC}/Scenarios/{id}.json` (setting "YAAT Sidecar Folder"), never to vNAS data admin; `ScenarioSidecarStore.OwnedKeys`, written on every local save (not for `fromServer`) and before push, restored after a re-download, every other key kept. The push path used to send the generators to data admin (a bug fixed here).

  Upstream admits YAAT does not yet read generators from the sidecar. YAAT: the push half is moot (export-only; `WorkingCopyExporter` keeps the generators in the JSON and warns, plan Q7; the clean export drops them, Q1).

  The remaining rule: a scenario that arrives from the server has no generators, and if vNAS drops YAAT-only fields on upload a re-download would lose them; the port keeps them in the working copy (never overwritten), but a room that loads from vNAS by id gets no generators. Whether `ScenarioSidecar` should carry the two generator arrays (a YAAT feature) is D1. Not a defect today.

### Group 7: the flight strip editor (bf70b8f, a9139ab, f4c6f60, 892863d)

Commit trail: bf70b8f inline editor in the Aircraft tab plus the aircraftIds fix; a9139ab half-strips; f4c6f60 own window plus prepopulation; 892863d strip commands from any preset. Upstream files:

`U/Services/FlightStripPresets.cs` (~44 KB, pure logic, 97 tests total in suite), `U/Views/FlightStripEditor.cs` (35 KB control), `U/Views/FlightStripWindow.cs` (non-modal window, one per editor window, 980x760, title "Flight Strip Editor: {callsign}", shares the VM and follows `SelectedAircraft` live; a popup flyout was dropped because it closed on click-back).

**Scenario JSON it touches**
1. `aircraft[].presetCommands[]` (id, command, `timeOffset` seconds after spawn): the editor owns single-command strip presets and rewrites them live; the preset list also gained a visible T+ column.
2. `flightStripConfigurations[]` (vNAS: id, facilityId, bay, rack 0-based, `aircraftIds`): the spawn bay. Set by `SetSpawnPlacement` (find an entry with the same facility/bay/rack else create one with a new ULID; add the aircraft id; remove the id from other entries; drop an emptied entry).
3. Read only: `flightplan` (type, rules, cruise altitude, departure, destination, route, remarks), `aircraftType`, `primaryAirport`, scenario `primaryAirportId`, `studentPositionId`, and the cached ARTCC config (`ArtccConfigRoot`: facilities, bays, `displayDestinationAirportIds`).
4. Bug fixed in bf70b8f: `aircraftIds` was kept in extra data, so a push dropped it and Duplicate Scenario left it pointing at old aircraft ids (now typed and remapped on duplicate).

**Command model (`StripStep`, 11 kinds) and wire form**
- Full strip: `Move` = `STRIP FAC/Bay/rack[/slot]` (1-based); `Annotate` = `AN box text` (boxes 8a, 8b, 10-18; `,` and `;` stripped because they split compounds; `?` becomes a check mark in YAAT); `Offset` = `STRIPO`; `Delete` = `STRIPD`. Arrival strips use the id form (`AN ARRIVAL_CS 10 X`, since YAAT's bare form only targets `STRIP_{callsign}`), so renaming an aircraft means re-saving its strip steps once (known gap upstream).
- Half-strips (aircraft-scoped; YAAT keys them on the callsign on line 1, so at most one per aircraft): `HalfCreate` = `HSC FAC/Bay/rack l2\l3...` (at most 5 user lines, `\` separates lines, always written with the rack so multi-word bay names parse); `HalfAmend` = `HSA l2\l3...` (replaces lines; a first token containing `/` is read by YAAT as a bay scope, so the checker warns); `HalfMove` = `HSM FAC/Bay/rack[/slot]`; `HalfOffset` = `HSO`;

  `HalfSlide` = `HSS`; `HalfDelete` = `HSD`. Half-strips skip the printer, so they work for any student position.
- `Other`: SCAN, SEP..., RACKSORT, BLANK shown only, never modelled.
- Keyed, bay-scoped and `HSTRIP_` forms stay ordinary presets. Order written/fired: by time, then move, annotate, offset, delete.
- External steps (892863d): strip commands inside presets the editor does not own are shown locked (padlock, source preset, condition), counted in strip state and checks, never rewritten by `Write`. Time rules: after `WAIT n` = +n s; after `AT`, `LV`, `WAITD`, `UNTIL`, `BEHIND`, `WAIT 2NM` = the condition (earliest); in `;` or `,` chains; callsign-keyed `HSD/HSO/HSS/HSM`. Edit them in Preset Commands.

**UI (what to scope)**
- Aircraft tab button "Flight Strip Editor..." opens the window. Header "Flight Strip"; spawn row (bay picker "FAC / Bay name" with "(other facility)" for external, rack, "starts in printer" default text); a "view at time" box (seconds or m:ss, "0:00") that redraws the strip as it is at that time (annotation boxes and location after every step at or before it).
- The CRC full strip drawn: col1 = fields 1/3/4, col2 = 5/6/7, col3 = 8/8A/8B, col4 = 9, cols 5-7 = boxes 10-18 numbered down each column. Arrival strips: 6 previous fix, 7 coordination fix, 8 ETA, 9 rules/9A destination plus remarks. Printed boxes carry tooltips (5 beacon, 4 CID assigned by YAAT, 6 proposed time, 7 requested altitude, 8 departure airport).

  Blue boxes are the editable annotation boxes (8A-8B, 10-18): type, Enter/Tab commits a step at the view time (`SetBox` updates the step at that time or adds one).
- Strip timeline list ("written to the preset commands below"): one row per step (time, kind, arguments, a "show strip at this time" button, delete), locked rows for external steps; an "Add move" row (bay, rack, slot with 0 = append). A half-strip panel: up to 5 line boxes, create-in-bay button (HSC), amend, move (HSM), offset, slide, delete; a status line ("No half-strip at this time...") and its own checks.
- Checks panel (`FlightStripPresets.Check`): annotate or offset while the strip is still in the printer; a step after STRIPD; unknown bay or rack beyond the bay's rack count; bay not reachable from the student position; a second half-strip for the aircraft; HSA/HSM/HSD with no half-strip; an HSA first token with `/`; commands YAAT's `CommandParser.ParseCompound` rejects;

  Approach student gets the strip only on the takeoff roll (steps before 60 s); arrival strips print about 20 min before landing so earlier steps fail.

**Prepopulation rules (`Printed`)**
1. Flight plan present: box 3 = equipment `CWT/type/suffix` (`WakeTurbulenceData`, `FlightPlanNormalization.SplitTypeAndSuffix`), e.g.

   `F/B738/L`; departures: 6 `P----`, 7 altitude per `FlightPlanAltitude.Format` (350, VFR/045, OTP/085), 8 departure airport (plus destination when the student facility sets `displayDestinationAirportIds`), 9 = departure + route + destination with remarks on a new line; arrivals: 8 `ETA`, 9 = rules newline destination + remarks. Beacon 5 shows `####`, CID 4 shows `###` (assigned by YAAT at runtime).
2. No flight plan but a `DA` preset: its abbreviated flight plan (beacon 4 octal digits, altitude 3 digits, letter-led type, `.V/.P/.E` rules, default VFR; `DEP*DEST` token gives airports, otherwise the aircraft's airport) fills the boxes and the beacon is shown; the earliest DA preset wins (also found behind `WAIT n`). The detail line says which source was used.
3. Neither: placeholders (`P` + spawn delay, "(no flight plan)").
- Arrival = flight plan files to the scenario's primary airport from elsewhere (K-prefix tolerant). Default placement (when no `flightStripConfigurations` entry): TWR student, first "Ground..." bay rack 1; GND/DEL/CTR: printer (`AN`/`STRIPO` fail until a move); APP: on takeoff roll; arrivals about 20 min out and only if the facility enables arrival strips (`DefaultPlacement`, `DefaultIsPrinter`, from `AtcPositionTypeClassifier`; bays from `GetAllCommandTargetableStripBays(studentCallsign)`).

**YAAT status of the strip editor: lacks.** The port has only the General tab's strip placement rows (`W/src/Yaat.Client/ViewModels/Authoring/StripPlacementRowViewModel.cs`, aircraft ids typed as text through `ScenarioIdListText`; `ScenarioGeneralViewModel.Lists.cs:80`), and nothing that edits strip presets (rg HSC/STRIPO under `Authoring`: none).

It also does not prune `AircraftIds` when an aircraft is removed (no `AircraftIds` reference outside the strip row; unverified in the delete path). Everything the upstream control re-implements is in-process in YAAT, so the port should reuse, not copy:
- Drawing: `W/src/Yaat.Client.Strips/Views/VStrips/FlightStripControl.axaml.cs` (full and half strips, field index map).
- Printed fields: `W/src/Yaat.Sim/Simulation/Strips/StripMutations.cs` (`FormatEquipment` is internal, line 1083; fields filled at ~461 and ~680): expose a pure "printed fields for a scenario aircraft" function in Yaat.Sim so the preview cannot drift from the print.
- Bays and access: `ArtccConfigResolver.GetAllCommandTargetableStripBays` (`Data/Vnas/ArtccConfigResolver.cs:1106`), `AtcPositionTypeClassifier`, `SimulationEngine.Strips.cs` (`PrintSpawnStrip` line 234, `TryPlaceConfiguredStrip` line 324).
- Command grammar: `CommandParser` (`ParseHalfStripCreate` line 3706) and `StripCommandHandler`; checks can run the real handlers' preconditions instead of upstream's copied rules.
- Doc: `docs/flight-strips.md` (not read in this run).
Proposed milestone: beside M1.5 (the Aircraft tab; M1 is 9 of 9 done, so a follow-up), feature. Aviation review: print-rule claims (APP takeoff roll, arrival 20 min) must be re-derived from YAAT's own code, not copied.

### Group 8: housekeeping (574f3bc, b2a6802, 799851f, e0aa02d)

CLAUDE.md records, session logs, optimization notes, `.gitignore`, untracking bin/obj/.vs. Moot. Two upstream decisions worth keeping as acceptance rules: Batch Refresh never auto-renames callsigns (Group 1) and the "visual review highlight of changed aircraft" idea was rejected by the owner (do not propose it).

## Table: commit | behaviour | YAAT status | owner issue | proposed action

| commit | behaviour | YAAT status | owner issue | proposed action |
|---|---|---|---|---|
| 574f3bc | CLAUDE.md project record | moot | none | nothing |
| ecb85dc | Batch Refresh mirrors type into filed type | partly (RouteStamp.cs:139 creates plan with type; update of an existing plan unverified) | YAAT-2504 | fold: "Apply Route and batch refresh set `FlightPlan.AircraftType` equal to the stamped `AircraftType` on an existing plan, and the Filed Type box refreshes; test it" |
| df2156d | Batch Refresh auto-rename duplicates | superseded by 4476c8b | none | nothing |
| 4476c8b | skip aircraft whose route callsigns are all in use; never auto-rename | partly (taken set in RouteStamp.cs:270,310) | YAAT-2504 | fold: "when no free callsign exists for the picked route the aircraft is skipped unchanged (no rename, no duplicate), the holder untouched, and the summary counts the skipped" |
| b2a6802 | session log | moot | none | nothing |
| 8e3f37b | gzip, pooled, 8-parallel download and parallel local load | done (ScenarioDownloader, M1.2) | none | nothing |
| 85921b7 | notes on YAAT caching/fields | moot | none | nothing |
| 3415b36 | Yaat.Sim submodule | moot (we are YAAT) | none | nothing |
| e8e7863 | replace duplicate code with Yaat.Sim; YAAT-unreadable nulls; S&R uses ScenarioValidator | write side moot; import read side lacks null tolerance | none (S&R: YAAT-2510) | new issue A (defect); fold into YAAT-2510: S&R route checks call `ScenarioValidator`, SIDs and any token |
| f3fa02b | YAAT Check plus fleet-aware type picking | partly (ScenarioEditChecks covers validator, dups, auto-track, positions, kind fields, TAXI, stands); no fleet/ground-map/hold/ARWY/rating/prefix/generator checks; no fleet pick | none; YAAT-2504 for fleet pick | new issue F (feature); fold fleet pick into YAAT-2504 |
| 038eb2d | facility filter in scenario list | lacks in the browser (exists in LoadScenarioWindow) | none | new issue G (feature, small) |
| 732e1eb | VFR arrival and overflight generator editors | done (M1.4 row view models) | none | nothing |
| 4b20672 | CLAUDE.md integration notes | moot | none | nothing |
| d3ff9d3 | lazy video maps, HttpFileCache freshness | unverified in composer | YAAT-2502 | fold: acceptance check "only enabled maps load, via VideoMapService/HttpFileCache; open cost does not scale with the ARTCC's map count" |
| de4eb62 | 6 h sync TTL; never overwrite local edits | overwrite protection structural; TTL absent | none | decision D3; default nothing |
| d5dbdd4 | JSON-compat and fleet/naming tests | partial: round-trip tests exist (ScenarioDocumentRoundTripTests); no null-read cases | none | covered by issue A |
| 2088048 | batched video-map paths, line culling | unverified | YAAT-2502 | fold with d3ff9d3 |
| 799851f | notes | moot | none | nothing |
| e0aa02d | .gitignore | moot | none | nothing |
| 01c7879 | cheatsheet autocomplete; sidecar awareness | autocomplete lacks; live parse done; sidecar awareness lacks | none | new issue D (autocomplete); sidecar awareness into E |
| 61455da | sidecar export, preset taxi-route import, old-data cleanup, Re-download confirm | sidecar export lacks; the other three moot | none | new issue E; nothing for the rest (D4) |
| e8f9296 | installer, v1.0.0 | moot | none | nothing |
| bf70b8f | strip editor (Aircraft tab); aircraftIds on push | editor lacks; aircraftIds typed in ScenarioModels (no loss) | none | new issue C (feature); the aircraftIds fix is moot |
| a9139ab | half-strips HSC/HSA/HSM/HSO/HSS/HSD | lacks | none | part of C |
| f4c6f60 | strip editor window; prepopulate from flight plan or DA | lacks | none | part of C |
| 33fbb3d | local scenario data loss on refresh/re-download | structurally avoided (mirror vs working copy); no backups on overwrite or delete | none | new issue B (hardening) |
| 892863d | strip editor shows commands from any preset | lacks | none | part of C |
| 2a352e5 | YAAT-only data to the scenario sidecar, not data admin | push half moot; sidecar generators idea open | none | decision D1; sidecar authoring in E |

The table lists 28 commit rows, one per commit in `git log 37b8392..2a352e5`.

## Proposed new issues

- **A. Defect: import a YAATEdit-written scenario with null numbers.** Title: "Scenario Editor import: read YAATEdit scenarios whose timeOffset or generator numbers are null". Files: `src/Yaat.Sim/Authoring/LocalScenarioStore.Authoring.cs` (`ImportScenario`, `ImportKeepingId`) or a normaliser called from `ScenarioBrowserViewModel.Import.cs`;

  `src/Yaat.Sim/Authoring/ScenarioDocument.cs` (`ReadOptions`); tests `tests/Yaat.Sim.Tests/Authoring/ScenarioDocumentRoundTripTests.cs`, `LocalScenarioStoreAuthoringTests.cs`. Rule: `presetCommands[].timeOffset: null` becomes 0; null optional generator numbers (`startTimeOffset`, `maxDistance`, `intervalDistance`, `maxTime`, `intervalTime`) are dropped; report the count fixed to the author.

  First write a failing test with a pre-2026-11-25 file (ask the creator for one, or build from the diff in e8e7863). Milestone: beside M1.3 (import), defect fix, vNext+1.
- **B. Hardening: backup before an overwrite or delete of a working copy.** Files: `LocalScenarioStore.Authoring.cs` (`ImportKeepingId`, `DeleteWorkingCopy`), `LocalScenarioStore.cs` (`ApplyMerge`), `StoreFiles.cs`, store tests. Feature/defect-prevention; beside M2.2 (merge) / M1.3. See D2.
- **C. Feature: Flight Strip editor in the Scenario Editor aircraft tab.** Scope above (Group 7).

  Files: new `src/Yaat.Sim/Authoring/Strips/` (step model, preset read/write, checks, spawn placement over `FlightStripConfigurations`, printed-fields function exposed from `StripMutations`), `src/Yaat.Client/ViewModels/Authoring/` (strip editor VM), `src/Yaat.Client/Views/Authoring/` (window reusing `FlightStripControl`), `ScenarioAircraftView.axaml` (button;

  T+ column already there), `ScenarioEditChecks.cs` (strip findings), `docs/flight-strips.md`, `USER_GUIDE.md`. Tests: pure round-trips of every command form against `CommandParser`, external-step reading, placement edits, printed fields against `StripMutations` output. Milestone: beside M1.5 follow-up (M1 closed). Gate: `csharp-reviewer`, aviation review for print rules. Split suggestion:

  C1 Yaat.Sim model plus checks, C2 full strip and spawn bay UI, C3 half-strips, C4 prepopulation and external steps.
- **D. Feature: command autocomplete in the preset and trigger boxes.** Files: `ScenarioAircraftView.axaml` (`PresetCommandBox`), `ScenarioPresetRowViewModel.cs`, `InitializationTriggerRowViewModel.cs`, `ScenarioGeneralView.axaml`, reuse `Services/CommandInputController.cs`, `ArgumentSuggester.cs`, `CommandSchemeParser`. Beside M1.5.
- **E. Feature: scenario sidecar authoring.** Files: new `src/Yaat.Sim/Authoring/ScenarioSidecarWriter.cs` beside `AirportSidecarWriter` (M4.6), a Scenario Editor window prefilled from the existing `ScenarioSidecarLoader` entry or the scenario's `ARWY` triggers, validated by `ActiveRunwayListParser`; the sidecar-checkout setting from M4.6; warn that Duplicate/NewId loses the sidecar (sidecars key on the vNAS scenario id). Depends on YAAT-2518.

  Milestone M4 (with M4.6) or M2 if the checkout setting is split out.
- **F. Feature: Scenario Editor check parity with YAAT Check.** Add to `ScenarioEditChecks`: ground map missing for `ScenarioResourceManifest.MapRequiredAirportIds` airports (reuse `GroundMapCheck`), parking spot not on the map, unknown type, fleet realism (`AirlineFleets`, `AirportAirlines`; aviation review), hold-for-release, ARWY info, rating and facility-prefix naming, generator sanity. Milestone beside M1.6 (closed): follow-up.
- **G. Feature: facility filter on the Scenario Editor server and local lists.** Files `ScenarioBrowserViewModel.cs`, `ScenarioBrowserView.axaml`; same derivation as `LoadScenarioWindow`. Beside M1.3.

## DEPENDS ON

- C needs: nothing new from other open issues; benefits from YAAT-2518 not at all. C touches `ScenarioModels.cs` only through typed use; no yaat-server change, but `tools/test-all.ps1` is needed if `StripMutations` is made public (yaat-server compiles Yaat.Sim).
- E depends on YAAT-2518 (M4.6) for the sidecar-checkout setting and writer pattern.
- A, B, D, F, G independent. B adjacent to YAAT-2913/2961/2788 (same store and browser files; avoid parallel edits).
- Folds depend on YAAT-2504 and YAAT-2502 finishing in progress.

## OPEN DECISIONS

- **D1. Should YAAT's `ScenarioSidecar` carry `vfrArrivalGenerators`/`overflightGenerators`?** Options: (a) no, generators stay in the scenario JSON; (b) yes, the loader merges them into a scenario that arrives without them, and E writes them. Recommendation: (a) now; vNAS upload loss is unmeasured (Q7 warning exists). Revisit when vNAS is seen dropping them. The creator's own note says YAAT does not read them from the sidecar yet.
- **D2. Backups.** Options: (a) none, the author's confirm dialogs suffice; (b) one `{id}.json.bak` before each overwrite or delete; (c) timestamped `_backups/` as upstream. Recommendation (b) in issue B, small, atomic via `StoreFiles`; upstream lost a user's data with no backup, which is the evidence.
- **D3. Resync TTL (6 h).** Options: none; TTL in the browser; HEAD/version check only. Recommendation: none, `ApplyServerBody` already skips an unchanged body; only add a TTL if resync cost on open is measured as a problem.
- **D4. Preset taxi routes from airport sidecars into the editor.** Options: none (dropped by ruling); offer a picker of sidecar `taxiRoutes` when adding a TAXI preset. Recommendation: none; `ScenarioEditChecks` plus YAAT's pathing replace it.
- **D5. Strip editor scope.** Options: full port (C1-C4); only spawn bay plus presets list view; half-strips later. Recommendation: C1+C2 first, then C3, C4, since C2 covers the data-loss-relevant `aircraftIds` path and C3/C4 are additive.
- **D6. Strip printed-field source.** Options: copy upstream's formatters; expose a public function from `StripMutations`. Recommendation: expose from Sim, never copy (upstream copies only because it cannot call the sim).

## DOC DRIFT

- `W/docs/plans/yaatedit/README.md:69` says "duplicate (fresh ids)"; `LocalScenarioStore.DuplicateWorkingCopy` (`LocalScenarioStore.Authoring.cs:135`) rewrites only the scenario id (`RewriteId`), keeping aircraft and strip ids. Either the plan or the code is wrong; the code is safe for `aircraftIds` as it is. Owner: the plan.
- `W/docs/plans/yaatedit/README.md:80` ("Flight strip configuration: native, Scenario strip configurations, ArtccConfigRoot bays, M1.4") describes only the General tab table, not the strip editor upstream now ships; the capability map does not name the baseline commit (37b8392 appears nowhere in the plan). Owner: the plan; add rows for the strip editor, YAAT Check, preset autocomplete and scenario-sidecar authoring.
- `W/docs/plans/yaatedit/README.md:160` says test in room applies the scenario sidecar by origin id; nothing in the plan lets an author write that sidecar (see E).
- Upstream `U/CLAUDE.md` says "Reviewed 2026-11-25 at commit 880a98d" for YAAT facts; the claims checked here (`ScenarioSidecarLoader` reads only `activeRunways` and `studentLowestPosition`) still hold on feat/yaatedit (`ScenarioSidecarLoader.cs:13-28`). No upstream fix needed.
- `U/CLAUDE.md` records that the half-strip and STRIP rules are mirrored from YAAT; they can drift from `StripCommandHandler`/`SimulationEngine.Strips.cs`, which is why D6 says expose, do not copy.

## TASK INDEX GAP

The authoring answer took the plan, the scenario-authoring rows (architecture.md lines 280-283), five source reads and a Linear scan. Missing rows in `W/docs/architecture.md`:
- "Edit what the Scenario Editor's live checks report (YAAT Check parity)": `src/Yaat.Sim/Authoring/ScenarioEditChecks.cs` -> `ScenarioEditInputs` -> `src/Yaat.Client/ViewModels/Authoring/ScenarioValidationViewModel.cs` -> `ScenarioAircraftViewModel.Issues.cs` -> tests `tests/Yaat.Sim.Tests/Authoring/ScenarioEditChecksTests.cs`.
- "Change strip placement or strip presets in a scenario": `docs/flight-strips.md` -> `src/Yaat.Sim/Simulation/SimulationEngine.Strips.cs` (`PrintSpawnStrip`, `TryPlaceConfiguredStrip`) -> `Simulation/Strips/StripMutations.cs` -> `Data/Vnas/ArtccConfigResolver.cs` (`GetAllCommandTargetableStripBays`) -> `src/Yaat.Client/ViewModels/Authoring/StripPlacementRowViewModel.cs` -> `src/Yaat.Client.Strips/Views/VStrips/FlightStripControl.axaml.cs`.
- "Scenario sidecar (activeRunways, studentLowestPosition)": `src/Yaat.Sim/Data/ScenarioSidecarLoader.cs` -> `src/Yaat.Sim/Data/ARTCCs/README.md` -> `ActiveRunwayListParser`.
- No `docs/agents/explore.md` overlay exists in the worktree; the main checkout's architecture.md has no authoring rows at all (the feature branch carries them).

MISSING ENTRY POINT: none in the worktree (`docs/README.md`, `docs/architecture.md` with a Task Index exist); the main checkout's `docs/architecture.md` lacks the authoring rows until feat/yaatedit merges (YAAT-2671). Glossary: "Server mirror", "Working copy", "Unexported changes" are in the plan's section 3; whether they reached `docs/CONTEXT.md` was not checked.
