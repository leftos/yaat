# ARTCC Customization Guide

How an ARTCC's own data shapes what YAAT does: the airport maps and configuration you already maintain in vNAS, and the facility-specific files that live in the YAAT repository. This guide is for facility engineers and training staff, not developers.

## Table of Contents

- [What this guide covers](#what-this-guide-covers)
- [The airport map (vNAS training airports)](#the-airport-map-vnas-training-airports)
- [Airport sidecars](#airport-sidecars)
- [The other ARTCC folders](#the-other-artcc-folders)
- [vNAS data admin settings YAAT uses](#vnas-data-admin-settings-yaat-uses)
- [Not for ARTCC staff](#not-for-artcc-staff)
- [Submitting a change](#submitting-a-change)
- [Glossary](#glossary)

## What this guide covers

Facility data reaches YAAT by two routes, and which one applies decides how long a change takes.

| Data | Where you edit it | How it reaches users |
|------|-------------------|----------------------|
| Airport ground map (parking, taxiways, runways, spots) | vNAS data admin, training airports | Live. The YAAT server re-reads the map at most every 30 minutes; a room that is already running keeps the map it started with, a new room gets the fresh one |
| ARTCC config (positions, STARS, ERAM, ASDE-X, strips, TDLS, video maps) | vNAS data admin | Live. The server re-reads it every 30 minutes; the desktop client caches the parts it reads for up to 6 hours |
| Training scenarios and weather | vNAS data admin | Live. A scenario is fetched in full each time it is loaded; the scenario list is cached for 5 minutes |
| Video maps and tower cab imagery | vNAS data admin | Live. The client re-checks video maps every 30 minutes |
| Airport sidecars, custom fixes, fix pronunciations, initial-contact transfers, wake directives, CIFP fragments, surface temp data | Files in this repository under `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/` | Needs a YAAT release (the client installer) and a yaat-server deploy. Both read the files once at startup |

If a behaviour looks wrong, first decide which route owns it. A taxiway that is missing from the map is a vNAS fix. A taxiway the auto-router should avoid is a sidecar entry.

## The airport map (vNAS training airports)

YAAT downloads each airport's map from the vNAS training-airports endpoint and builds the ground network from it. The server fetches and parses the map; clients receive the finished layout from the server. Coordinates are `[longitude, latitude]`. Every feature needs `type`, `name` and a geometry; a feature missing one can stop the airport map loading. The authoring details are in [`docs/ground-layout-generation.md`](docs/ground-layout-generation.md).

If the map is unreachable, YAAT falls back to its last downloaded copy. An airport with no map is reported to the instructor and has no ground layout.

### What YAAT reads

| Feature `type` | Properties YAAT reads | What it changes in the sim |
|----------------|-----------------------|----------------------------|
| `parking` (Point) | `name`; `heading` (degrees true, optional, defaults to 0) | The parking spot an aircraft can start at, be taxied to (`@name`) and park on, facing `heading`. A spot is connected to the nearest taxiway edge within about 0.15 nm; one farther away is left unconnected. Names that are letters followed by digits (`A12`) also join the spot named by the letters (`A`) when it is within about 0.24 nm |
| `helipad` (Point) | `name`, `heading` | Like parking, with a larger connection radius |
| `spot` (Point) | `name` | A named point usable as a taxi destination (`$name`) and hold position |
| `taxiway` (LineString) | `name` | The edge name used by `TAXI` clearances, route readouts and the movement-area classification. The name `RAMP` is special. Vertices within about 10 ft snap together and crossings of different taxiways become junctions. Two separate pavements must not share a name |
| `runway` (LineString) | `name` in the form `28R-10L` (both ends, in that order) | Runway geometry, centerline edges, hold-short lines at every crossing taxiway, landing rollout and exits |
| `runway` | `holdShortDistance` (feet from the centerline) | Where hold-short lines sit. Without it YAAT computes the offset from the runway width |
| `runway` | `threshold`, for example `"0 - 957"` (displaced-threshold feet per end, in the order of the name) | The landing datum for approaches, flare, pattern and landing spacing, and the origin of ADW marks. Absent means no displacement |
| `runway` | `turnoff`, `"left"` or `"right"` relative to the first-named end | Default exit side for arrivals. The second end gets the opposite side; correct it per end with the `exitDirections` sidecar section |
| `runway` | `noTurnoff`: two lists of taxiway names, one per end in name order | Taxiways arrivals on that end must not use as exits |
| `runway` | `patternAltitude` (feet AGL), `patternSize` (nm) | The airport's established traffic pattern when the controller gives none. Absent means the aircraft category defaults |

A feature with any other `type` is ignored and logged.

### What YAAT ignores

These properties appear in real maps and look as if they should matter, but YAAT does not read them:

- On taxiways: `holdShortDistance`, `circular`, `turnoff`, `heading` and `threshold`. Hold-short distance, exits and thresholds are read from `runway` features only.
- On parking: `turnoff` and `threshold`.
- `heading` on anything but parking and helipads. Runway heading comes from the line itself.
- Polygon features of any kind, and hold-line, apron, ramp-boundary or movement-area features. None exist for YAAT.

Movement area is not drawn in the map. YAAT infers it per taxiway name: single-letter taxiways and runways are movement area, a name with a runway hold-short on it is movement area, a name with a parking gate one edge off it is a ramp taxilane, and so on. The full rule list is in [`docs/ground/pathfinder.md`](docs/ground/pathfinder.md); a one-off exception is a sidecar entry (below).

Hold-short lines are computed from the runway width or `holdShortDistance`, and the fillet arcs at junctions are generated by YAAT.

To check what YAAT made of a map, use the layout inspector (`tools/Yaat.LayoutInspector`, or ask a maintainer to run it) rather than guessing from the sim.

## Airport sidecars

An airport sidecar is a JSON file that adds local ground rules the map cannot express. It lives at `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/Airports/{airport}.json`, one file per airport, keyed by `airportId` (ICAO or FAA, `KOAK` and `OAK` both work). Every section is optional and a file may carry any subset. Key case does not matter. Restart YAAT to pick up edits.

The full field tables are in [`src/Yaat.Sim/Data/ARTCCs/README.md`](src/Yaat.Sim/Data/ARTCCs/README.md); the examples below are minimal. Cite the SOP or LOA behind each entry in its `notes`. A file that cannot be read, or an entry that fails its checks, is skipped with a warning in the log and the rest still loads.

### avoidTaxiways

The automatic router (right-click "taxi to...", `TAXIAUTO`, `TAXIALL`) stops using the taxiway unless the destination can only be reached through it. An explicit `TAXI S ...` is obeyed as typed. Details: [`avoidTaxiways`](src/Yaat.Sim/Data/ARTCCs/README.md#avoidtaxiways).

```json
"avoidTaxiways": [ { "name": "S", "notes": "Cargo ramp lead; not used for routine taxi." } ]
```

### taxiRoutes

Adds entries to the Ground view's right-click "Preset taxi route" menu: one click sends the `TAXI` command you wrote. A preset that cannot be walked from the aircraft's position is left out of the menu without a message, so test each one in the sim. Details: [`taxiRoutes`](src/Yaat.Sim/Data/ARTCCs/README.md#taxiroutes).

```json
"taxiRoutes": [ { "name": "TERMINAL to 30", "path": "T U W", "destinationRunway": "30" } ]
```

### implicitConnectors

A short connector taxiway (such as `LF` between L and F) is normally flagged as unauthorized when the clearance does not name it. Listing it makes `TAXI L F` legal without naming `LF`; `TAXI L A F` is still flagged. It affects explicit clearances only. Details: [`implicitConnectors`](src/Yaat.Sim/Data/ARTCCs/README.md#implicitconnectors).

```json
"implicitConnectors": [ { "connector": "LF", "between": ["L", "F"] } ]
```

### oneWayEdges

Marks taxiway stretches that may be taxied one way only. The allowed direction is the order of the points (first to last). Auto-routing never goes against it unless the destination is reachable no other way; an explicit `TAXI` that does is allowed with a warning. `exemptWakeClasses` lets a wake class ignore the restriction. These are local conventions, so source them from an SOP or LOA. Details: [`oneWayEdges`](src/Yaat.Sim/Data/ARTCCs/README.md#onewayedges).

```json
"oneWayEdges": [
  {
    "notes": "Taxiway A one-way northeast-bound",
    "path": [ { "point": [-122.392652, 37.619842], "taxiway": "A" }, { "point": [-122.392258, 37.620439], "taxiway": "A" } ]
  }
]
```

### blockedTurns

Forbids one corner outright, in both directions: neither the auto-router nor an explicit `TAXI` may turn through it, and Ground view hides the corner's fillet arc. The path is at least three points with the corner's apex in the middle. Details: [`blockedTurns`](src/Yaat.Sim/Data/ARTCCs/README.md#blockedturns).

```json
"blockedTurns": [
  {
    "notes": "No direct sharp turn between L and F; use LF.",
    "path": [ { "point": [-122.373393, 37.614943], "taxiway": "L" }, { "point": [-122.372604, 37.616132], "taxiway": "L" }, { "point": [-122.371012, 37.615463], "taxiway": "F" } ]
  }
]
```

### adw

Draws the two ends of a published Arrival/Departure Window as reference marks in Ground view. Nothing else acts on them: no separation logic, no pilot behaviour, no scoring. Ranges are measured from the displaced landing threshold, so the map's `threshold` property must be right. Author an entry only from a facility directive. Details: [`adw`](src/Yaat.Sim/Data/ARTCCs/README.md#adw).

```json
"adw": [ { "arrivalRunway": "26R", "departureRunway": "30", "outerNm": 2.7, "innerNm": -0.1, "notes": "Miami ATCT SOP 3-9.F" } ]
```

### exitDirections

Sets the default turn-off side for arrivals on one landing runway end, ahead of the map's `turnoff` and YAAT's own heuristics. An explicit `EL`, `ER` or `EXIT` command still wins, and if no exit on that side is reachable the aircraft falls back to the other side. Details: [`exitDirections`](src/Yaat.Sim/Data/ARTCCs/README.md#exitdirections).

```json
"exitDirections": [ { "runway": "26R", "side": "left", "notes": "Facility request: 26R vacates left." } ]
```

### exitCapacity

Limits how many aircraft may stand on an exit taxiway between two parallel runways. When the stretch is full, an arrival treats that exit as occupied and takes the next one. The entry must resolve to exactly one hold-short-to-hold-short stretch on the live map, or it is logged as an error and ignored. Details: [`exitCapacity`](src/Yaat.Sim/Data/ARTCCs/README.md#exitcapacity).

```json
"exitCapacity": [ { "runway": "28R", "taxiway": "T", "maxAircraft": 2, "maxAircraftAboveCwt": 1, "cwtThreshold": "G" } ]
```

### movementAreaTaxiways and nonMovementTaxilanes

Overrides YAAT's inferred verdict for a taxiway name, either way: movement area, or ramp taxilane. The verdict drives how pilots behave on ramps, tug moves and routing. Use it only for a real exception that the inference rules cannot see. A name in both lists is movement area. Details: [`movementAreaTaxiways` / `nonMovementTaxilanes`](src/Yaat.Sim/Data/ARTCCs/README.md#movementareataxiways--nonmovementtaxilanes).

```json
"nonMovementTaxilanes": [ { "name": "TC", "notes": "Ramp-controlled." } ]
```

## The other ARTCC folders

Each folder sits under `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/` and is read at startup. Schemas are in [`src/Yaat.Sim/Data/ARTCCs/README.md`](src/Yaat.Sim/Data/ARTCCs/README.md).

| Folder | What it is | Effect |
|--------|------------|--------|
| `CustomFixes/` | Facility landmarks and training waypoints, each at a latitude and longitude or an FRD, with aliases and optional spoken phrases | The alias works in `DCT`, routes and fix autocomplete, and spoken phrases map to it in speech recognition. An alias that clashes with a real fix is skipped |
| `FixPronunciations/` | Phonetic hints for fixes that are easy to mishear, and optional display names | Helps speech recognition; a `displayName` also appears in operator text and pilot readbacks |
| `InitialContactTransfers/` | When a solo-training pilot may make initial contact with the student: at handoff initiated, at handoff accepted, or with no handoff | Overrides the built-in defaults for the ARTCC, by position type or exact callsign |
| `WakeDirectives/` | Local wake waivers and advisory rules | Changes Session Report scoring only; aircraft behaviour is unchanged. Add a rule only from an approved SOP or LOA |
| `Procedures/` | Verbatim CIFP records for a SID, STAR or approach the FAA dropped from the current cycle | Keeps the procedure available. Generated with `tools/stash-procedure.py`, never hand-written. It does not expire, so re-check it against the chart |
| `SurfaceTempData/` | ASDE-X and SAID temp data (restricted areas, closed areas, text) per facility, in `{FACILITY}.json` | Baseline drawings on the surface display. Export it from the client menu Tools, Export ASDE-X / SAID Temp Data |

Approach gates are computed from the CIFP, not authored: YAAT works out the distance from the final approach fix to the threshold for each approach. There is no gate file to edit. A `Procedures/` fragment feeds the computation.

## vNAS data admin settings YAAT uses

These are edited in vNAS data admin and take effect without a YAAT release (with the cache delays above). Upstream documentation is mirrored in [`docs/vnas-data-admin/`](docs/vnas-data-admin/README.md), and a full real ARTCC config is in [`docs/vnas-artcc-config-examples/zoa.json`](docs/vnas-artcc-config-examples/zoa.json).

| Group | Settings YAAT reads | What it drives |
|-------|---------------------|----------------|
| Positions | Callsign, frequency, radio name, starred, STARS TCP id, area and color set, ERAM sector id; the child facility tree and neighboring facility ids | The position picker, the student position, radio names and frequencies, CRC session positions, track ownership |
| ERAM | NAS id, neighboring STARS configurations, conflict alert floor, ASR sites | Handoff prefixes, Mode C intruder floor, reduced-separation symbol |
| STARS | Automatic consolidation, TCPs, areas (visibility center, surveillance range, underlying airports, tower lists), internal airports, lists, handoff ids, ATPA volumes, beacon code banks, scratchpad rules and 4-character scratchpad, destination display options | Airport-to-facility mapping, scope center and range, tower lists, ATPA cones, squawk pools, automatic scratchpads |
| Tower cab, ASDE-X, SAID | Aircraft visibility ceiling, tower location, video map id; ASDE-X visibility range and ceiling, fix rules, destination-as-fix; SAAB configuration | Tower cab target visibility and view center, surface display visibility and labels |
| Flight strips | Strip bays, external bays, destination airport ids, arrival strips, separate arrival and departure printers | vStrips bays, access and printing |
| TDLS | Clearance-form options: DCL operation configs, SIDs and transitions, value lists, mandatory fields, defaults | The vTDLS clearance form |
| Video maps | Map list, map groups, per-TCP defaults, ids, names, tags, brightness categories | The radar video map list and defaults |
| Scenarios and weather | Scenario aircraft, starting conditions, flight plans, preset commands, generators, ATC positions, student position, minimum rating; weather profiles | Scenario loading and the weather picker. Preset commands must parse as YAAT commands; known exceptions are in [`docs/scenario-validation-known-failures.md`](docs/scenario-validation-known-failures.md) |

YAAT also supports two scenario generator types that vNAS scenarios do not have, `vfrArrivalGenerators` and `overflightGenerators`. They are YAAT extensions, set in YAAT's own generator editor, not in the vNAS scenario format.

YAAT reads these fields but does nothing with them, so changing them has no effect: STARS `configurationPlans`, `terminalSectors`, `impliedCompoundCommands`, `rnavPatterns`, `rpcs`, `recatEnabled`, `ssaAirports`, `ldbBeaconCodesInhibited`, `pdbGroundSpeedInhibited`, `displayRequestedAltInFdb`, `useVfrPositionSymbol`; ASDE-X `defaultRotation`, `defaultZoomRange`, `runwayConfigurations[].holdShortRunwayPairs`; `defaultPositionId`; flight strips `displayBarcodes` and `lockSeparators`.

This list comes from a search for readers of each field name, so a field read indirectly would not appear.

Fix, airway and airport positions (NavData) and aircraft specifications come from vNAS configuration and the FAA, not from your ARTCC's data admin; a change to those goes to vNAS.

## Not for ARTCC staff

These are maintained by the YAAT maintainers and are not facility-specific:

- **Aircraft profile overrides** (`src/Yaat.Sim/Data/AircraftProfileOverrides.json`): per-type performance corrections, global to every facility, backed by POH or AFM figures. If a type flies visibly wrong, report it with the source numbers; see [`docs/aircraft-performance.md`](docs/aircraft-performance.md).
- **FacilityOps** (`src/Yaat.Sim/Data/FacilityOps/`): the facility SOP's runway configurations, used by the controller AI. Staff supply the SOP; a maintainer authors the file, because the schema is strict and a bad runway id stops the server. See [`docs/facility-ops-knowledge.md`](docs/facility-ops-knowledge.md).
- **Generated data**: airline fleets, airport airlines, minimum vectoring altitudes (MVA), military routes, airspace boundaries and the precompute cache are rebuilt by scripts from FAA and other sources. A wrong value is a data-source issue: file an issue instead of editing.

## Submitting a change

Anyone with a GitHub account can propose a change to the ARTCC folders.

1. Fork [leftos/yaat](https://github.com/leftos/yaat) and create a branch.
2. Edit or add the file under `src/Yaat.Sim/Data/ARTCCs/{ARTCC}/...`, following the schema in [`src/Yaat.Sim/Data/ARTCCs/README.md`](src/Yaat.Sim/Data/ARTCCs/README.md). Cite the SOP, LOA or facility directive in the entry's `notes`.
3. Run the sidecar tests: `dotnet test -- --filter-class "*Sidecar*"` for airport sidecars, and `--filter-class "*.CustomFixTests"` or `--filter-class "*.FixPronunciationTests"` for those folders. The tests check the shipped files, but a mistyped value can still load with only a log warning, so also run YAAT with your edit and read the log.
4. Open a pull request with the template: link the issue as `Refs #N` (not `Closes`), describe the test plan, and add a bullet to `CHANGELOG.md` under `## Unreleased`.

Load warnings are written only to the client log (`%LOCALAPPDATA%/yaat/yaat-client.log`) and the server log (`yaat-server.log`); nothing appears in the UI. A skipped entry is therefore easy to miss, so search the log for `Airport sidecar:` after loading the airport. Because the files ship inside the installer and the server image, your change reaches users with the next YAAT release and server deploy.

If you cannot build YAAT, open an issue instead, with the SOP or LOA text and the airport, and a maintainer can write the entry.

## Glossary

- **ADW (Arrival/Departure Window)**: A facility-published range along a landing runway's final approach course that protects an arrival's missed approach from a converging departure; YAAT only draws its ends.
- **CIFP**: The FAA's Coded Instrument Flight Procedures data, the source of SIDs, STARs and approaches.
- **CWT (Consolidated Wake Turbulence)**: The FAA wake category letters A to I, A heaviest.
- **Facility Engineer**: The vNAS data admin role that edits a facility's configuration, maps and scenarios.
- **FRD (Fix-Radial-Distance)**: A point given as a fix, a three-digit radial and a three-digit distance, such as `OAK090010`.
- **Movement area**: Taxiways and runways under ATC control, as opposed to ramp taxilanes; YAAT infers it from taxiway names unless a sidecar overrides it.
- **Sidecar**: A JSON file in this repository, beside the vNAS airport map, that adds per-airport ground rules the map cannot express.
- **TCP (Terminal Control Position)**: A STARS position identifier that owns tracks, such as `1A`.
