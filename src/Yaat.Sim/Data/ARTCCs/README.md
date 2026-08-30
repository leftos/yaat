# Per-ARTCC user-submitted data

Each ARTCC sits at the top level; categories of user-submitted data live underneath:

```
ARTCCs/
  ZOA/
    CustomFixes/
      oak-landmarks.json
    FixPronunciations/
      ambiguous.json
      visual.json
    Airports/
      oak.json
    InitialContactTransfers/
      zoa-initial-contact-transfers.json
    WakeDirectives/
      oak-wake-directives.json
    Procedures/
      koak-nimi.cifp
    SurfaceTempData/
      SFO.json
  ZMA/
    Airports/
      fll.json
```

Each loader scans `ARTCCs/*/{Category}/*.json` (`Procedures/` uses `*.cifp`; `SurfaceTempData/` is looked up by facility id rather than scanned). Files whose category folder doesn't match the loader are ignored — `Data/ARTCCs/ZOA/CustomFixes/foo.json` is read by `CustomFixLoader`, not by `AirportSidecarLoader`.

The categories below describe the JSON schema for each. None are required; an ARTCC folder may contain any subset.

---

## CustomFixes

Custom fix/landmark definitions that supplement the standard NavData from VNAS — facility-specific reference points, training waypoints, local landmarks.

Each file is a JSON array of fix definitions. Position is specified via either `lat`/`lon` or `frd` (not both).

### Lat/Lon format

```json
[
  {
    "name": "San Mateo Bridge Toll Plaza",
    "aliases": ["VP915", "TOLLPLAZA"],
    "lat": 37.61814825135482,
    "lon": -122.15262493420477
  }
]
```

### FRD (Fix-Radial-Distance) format

```json
[
  {
    "name": "10nm East of OAK",
    "aliases": ["OAK10E"],
    "frd": "OAK090010"
  }
]
```

FRD strings follow the format `{FIX}{radial:3}{distance:3}` — e.g., `OAK090010` means the OAK VOR, 090 radial, 10nm. The fix name is resolved against NavData at load time.

### Field reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Display name (informational) |
| `aliases` | string[] | Yes (min 1) | Identifiers for fix lookup. First alias is primary. |
| `lat` | number | If no `frd` | Latitude in decimal degrees (WGS84) |
| `lon` | number | If no `frd` | Longitude in decimal degrees (WGS84) |
| `frd` | string | If no `lat`/`lon` | Fix-Radial-Distance reference |
| `spokenPatterns` | string[] | No | Natural-language phrases for speech recognition |

### Speech recognition patterns

Custom fixes often have verbose multi-word names that controllers say naturally on the radio — "direct to the runway 30 numbers", "proceed to the toll plaza". The speech recognition pipeline can't pick these up through the normal `{fix}` rule capture because that only matches a single token. Adding a `spokenPatterns` array lets the pipeline collapse the multi-word phrase to the canonical alias before rule matching runs.

```json
{
  "name": "Oakland Runway 30 Numbers",
  "aliases": ["OAK30NUM"],
  "spokenPatterns": [
    "runway 30 numbers",
    "the runway 30 numbers",
    "oakland runway 30 numbers",
    "30 numbers"
  ],
  "lat": 37.70208081559119,
  "lon": -122.21521095379472
}
```

#### Pattern guidelines

- **Write numbers as digits.** The phraseology normalizer converts spoken numbers ("three zero") to digit form ("30") before custom-fix matching runs. Patterns must match the post-normalization tokens.
- **Include natural prefixed variants** like "the ..." and airport-prefixed forms like "oakland ...". Each variant is matched independently — longest match wins when multiple patterns overlap.
- **Keep patterns distinctive.** A pattern like "the approach" or "final" would collide with existing phraseology and swallow tokens that belong to real rules. Prefer compound phrases that are unambiguous to the specific fix.
- **Case is ignored.** Patterns are lowercased at load time and matched case-insensitively against normalized tokens.
- **One alias per pattern.** The first alias in the `aliases` array is used as the canonical form. If you need multiple aliases to have speech patterns, add the patterns to the entry with the primary alias.

When matched, the spoken phrase is replaced with the canonical alias as a single token. Downstream `{fix}` rule captures (e.g. `direct to {fix}`) see `OAK30NUM` and produce `DCT OAK30NUM` in the command input.

---

## FixPronunciations

Phonetic pronunciation hints for fixes whose spelling invites mispronunciation. At PTT time, any hint whose fix name matches a programmed fix on the selected aircraft is injected into Whisper's `initial_prompt` alongside the canonical spelling, giving the decoder bias tokens for both forms. `PhoneticFixMatcher` already normalizes either spelling back to the canonical fix, so downstream code sees the same `MapResult` regardless of which form Whisper produced.

Each file is a JSON array of pronunciation entries. Use lowercase space-separated phonetic spellings — Whisper's decoder biases on sub-word tokens, so "see rah" is more effective than "SEE-RAH" or "seerah".

```json
[
  {
    "fix": "SYRAH",
    "pronunciations": ["see rah"]
  },
  {
    "fix": "CEPIN",
    "pronunciations": ["seppin"]
  }
]
```

- `fix` — canonical fix name (case-insensitive; stored uppercase internally).
- `pronunciations` — array of phonetic variants. Multiple entries are useful for regional pronunciation differences (e.g., `["see rah", "sih rah"]`).

### When to add a hint

Add a hint only when Whisper is likely to misrecognize the fix name:

- The spelling is non-obvious (`SYRAH` → "sigh-rah" vs "see-rah").
- The canonical spelling looks like an unrelated common word (`NIKLZ` → "nickels").
- The fix is made-up letters that Whisper tokenizes character-by-character.

Don't add hints for fixes whose spelling already decodes naturally — unnecessary prompt tokens dilute Whisper's bias.

---

## Airports

The unified per-airport ground sidecar. One JSON file per airport (`Airports/{airport}.json`) carrying every per-airport ground-routing override. Each file is scoped to one airport via `airportId` (ICAO or FAA — `KOAK` and `OAK` both match). All sections are optional; a file may carry any subset. Multiple files for the same airport are merged.

```json
{
  "airportId": "KOAK",
  "avoidTaxiways": [
    { "name": "S", "notes": "Perimeter/cargo ramp lead; not used for routine auto-taxi." }
  ],
  "taxiRoutes": [
    { "name": "TERMINAL to 30", "path": "T U W", "destinationRunway": "30", "tags": ["dep", "30"] }
  ]
}
```

Restart YAAT to pick up edits to these JSONs.

### `avoidTaxiways`

Taxiways the **automatic** pathfinder should avoid in route suggestions — the routes generated for the right-click "taxi to…" menu, `TAXIAUTO`/`TAXIALL`, the auto-extension of an explicit path into a parking, and any other auto-route. Use this where a taxiway is technically usable but locally undesirable for routine routing (e.g. a perimeter/cargo lead such as taxiway S at OAK).

The avoidance is **strict but not absolute**: an avoided taxiway is never used when any avoiding route to the destination exists, but it *is* used when the destination is only reachable through it (e.g. parking spots that hang off it). Explicit controller commands that name the taxiway — `TAXI S …` — are obeyed verbatim and never re-routed.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `avoidTaxiways[].name` | string | Yes | Taxiway name to avoid, matched case-insensitively against the route's taxiway names |
| `avoidTaxiways[].notes` | string | No | Human-readable rationale (SOP reference, condition). Informational only |

Names are case-insensitive and de-duplicated per file. An entry with a blank name is skipped with a warning.

### `taxiRoutes`

Per-airport preset taxi routes surfaced in the right-click "Preset taxi route" submenu on the ground view. Each preset is a one-click shortcut for an SOP-aligned `TAXI` command — useful where the auto-router doesn't follow local best practice. The `path` is whatever you'd type after `TAXI` in the command bar.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `taxiRoutes[].name` | string | Yes | Display name in the menu |
| `taxiRoutes[].path` | string | Yes | Whitespace-separated taxiway names |
| `taxiRoutes[].destinationRunway` | string | No | Runway hold-short (e.g. `"10R"`) |
| `taxiRoutes[].destinationParking` | string | No | Parking destination (e.g. `"G7"`) |
| `taxiRoutes[].destinationSpot` | string | No | Spot destination |
| `taxiRoutes[].tags` | string[] | No | Reserved for future menu filtering |

At most one of the three `destination*` fields may be set on a single route. Routes whose path can't be walked from the aircraft's current position are silently dropped from the menu — so a KOAK route won't surface when right-clicking an aircraft at KSFO.

### `implicitConnectors`

Short named connector taxiways that should be treated as authorized **only when the controller's cleared sequence places their two `between` taxiways adjacent**. A connector like SFO's `LF` is a letter-only taxiway, so by default a `TAXI L F` that bridges L and F via LF draws an "unauthorized taxiway" penalty/warning even though LF is the obvious connector. Listing it here authorizes it contextually — for `TAXI L F` (and `TAXI F L`) but not for `TAXI L A F`.

```json
"implicitConnectors": [
  { "connector": "LF", "between": ["L", "F"], "notes": "LF bridges L and F." }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `implicitConnectors[].connector` | string | Yes | Connector taxiway name to authorize, e.g. `"LF"` |
| `implicitConnectors[].between` | string[] | Yes | Exactly two taxiway names the connector bridges (unordered) |
| `implicitConnectors[].notes` | string | No | Human-readable rationale. Informational only |

Authorization is scoped to explicit `TAXI` clearances (it has no effect on auto-routes). Names are case-insensitive. An entry whose `between` is not exactly two non-blank names is skipped with a warning.

### `oneWayEdges`

Taxiway segments that may only be taxied in one direction. Each constraint is an ordered `path` of waypoints; the **allowed travel direction is the order of the path** (first → last). `point` is `[lon, lat]` (GeoJSON order — copy-pasteable straight from the airport map), and `taxiway` is the taxiway you *expect* that vertex to land on (a validation hint — a warning is logged if a future map shifts the vertex off it).

```json
"oneWayEdges": [
  {
    "notes": "Taxiway A one-way NE-bound between the T9 crossing and the B-row",
    "block": "reverse",
    "path": [
      { "point": [-122.392652, 37.619842], "taxiway": "A" },
      { "point": [-122.392258, 37.620439], "taxiway": "A" }
    ]
  }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `oneWayEdges[].path` | object[] | Yes (≥2) | Ordered waypoints; allowed direction is first → last |
| `oneWayEdges[].path[].point` | number[2] | Yes | `[lon, lat]` of the vertex (GeoJSON order) |
| `oneWayEdges[].path[].taxiway` | string | No | Expected taxiway at this vertex (validation hint) |
| `oneWayEdges[].block` | string | No | `"reverse"` (default — one-way, forbid against-order) or `"both"` (closed segment / forbidden turn) |
| `oneWayEdges[].notes` | string | No | Human-readable rationale. Informational only |

Each waypoint is snapped to the nearest graph node; consecutive waypoints that are directly connected forbid that one edge, while two endpoints on the same taxiway have the whole span between them filled by a taxiway-restricted search. Consecutive waypoints **need not share a taxiway**, so the same construct expresses one-way transitions and forbidden turns across a junction; a path of N points traces a curve.

**Enforcement.** Auto-routing (`TAXIAUTO`, right-click "taxi to…") never travels a one-way the wrong way — except, like avoided taxiways, when a destination is *only* reachable against it, in which case the route resolves with a warning. An explicit `TAXI` clearance that names the wrong-way taxiway is honored but flagged with a "Taxiing X against one-way direction" warning.

One-way taxiway restrictions are **local SOP / facility conventions** — they are not codified in FAA 7110.65. Author them from an ARTCC-approved SOP or LOA, not from a regulation reference.

### `adw`

Arrival/Departure Windows — one of the facility-directive aids 7110.65 §3-9-9.b permits in lieu of applying
the §3-9-8 intersecting-runway provisions, where converging centerlines cross within 1 NM of a departure
end. The window protects the arrival's missed approach from the converging departure; it does **not**
change IFR separation standards. Each entry is one published window for one arrival/departure runway pair.
YAAT draws the two ends of the window as reference marks in Ground View and does nothing else with them:
no separation logic, no pilot behavior, no scoring.

```json
"adw": [
  {
    "arrivalRunway": "26R",
    "departureRunway": "30",
    "outerNm": 2.7,
    "innerNm": -0.1,
    "notes": "ZMA D11 Miami ATCT SOP 3-9.F — published as one row for arrivals 26L/26R."
  }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `adw[].arrivalRunway` | string | Yes | Landing runway end the window is measured along, e.g. `"26R"`. Zero-pad-normalized at load, so `"9"` and `"09"` are the same end |
| `adw[].departureRunway` | string | Yes | The converging runway end whose takeoff roll this window gates |
| `adw[].outerNm` | number | Yes | Outer range in nm from the landing threshold, outbound along the final approach course |
| `adw[].innerNm` | number | Yes | Inner range in nm from the same threshold; **negative means past the threshold, on the runway** |
| `adw[].notes` | string | No | The facility directive this window is published in. Informational, but do not author an entry without one |

Both ranges are signed against the outbound (final approach course) direction, so `2.7 / -0.1` reads as
"from 0.1 nm down the runway out to 2.7 nm on final". `outerNm` must be positive and exceed `innerNm`, the
two runways must differ, and both ranges must be within ±15 nm; an entry that fails any check is skipped
with a warning.

The origin is the **displaced** landing threshold — the airport map's runway LineString endpoints are
pavement ends, and the map's `threshold` property (e.g. `"0 - 957"`) is applied before the ranges are
measured. That matters: KMIA RWY 30's 957 ft displacement is larger than its own inner offsets. It also
means an entry is only as good as vNAS's displacement value; nothing here can detect a wrong one.

The final approach course is taken as the runway centerline bearing. That is exact for a straight-in
approach and wrong for an offset one — a 3° LDA offset displaces the true outer point ~860 ft laterally at
2.7 nm, a 10° offset ~2,850 ft. Every published ADW to date is on a straight-in; if one ever isn't, the
course needs to come from the procedure, not the pavement.

A published row covering two arrival runways (`"Arriving Runway 26L/26R"`) becomes **one entry per arrival
runway** — the geometry is per-runway.

ADWs are **facility-directive data**, not something to derive from runway geometry. Author them only from an
ARTCC-approved SOP or facility directive, and cite it in `notes`. The marks themselves carry no
applicability conditions, so anyone reading them should know the directive's own limits — at KMIA those are
1,000 ft ceiling / 3 SM visibility, arrivals between 120 and 170 kt groundspeed entering the window, and no
intersection departures while ADW is in use.

Worked example: [`ZMA/Airports/mia.json`](ZMA/Airports/mia.json).

### `exitDirections`

Default exit (turn-off) direction overrides, one per landing runway **end**. The airport map's
`turnoff` property carries a single value per physical runway and YAAT derives the reciprocal end by
flipping it — both landing directions vacate toward the same physical side. Where that assumption is
wrong (KMIA authors `turnoff: left` on `8L - 26R`, so 8L arrivals correctly vacate left but 26R
arrivals get the flipped right, while the facility wants 26R left too), this section pins the side
for a specific end without touching the other one.

```json
"exitDirections": [
  { "runway": "26R", "side": "left", "notes": "MIA facility request — 26R arrivals vacate left." }
]
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `exitDirections[].runway` | string | Yes | Landing runway end the override applies to, e.g. `"26R"`. Zero-pad-normalized at load, so `"9"` and `"09"` are the same end |
| `exitDirections[].side` | string | Yes | `"left"` or `"right"`, relative to the landing aircraft's nose at rollout |
| `exitDirections[].notes` | string | No | Facility rationale (SOP reference, request provenance). Informational only |

An override beats both the map-authored `turnoff` and the layout heuristics (high-speed exits,
parking proximity). It only sets the **default**: an explicit `EL`/`ER`/`EXIT` command on an
aircraft still wins, and when no exit on the overridden side can be reached, the exit search still
falls back to the other side rather than rolling out forever. An entry with a blank runway or a side
other than `left`/`right` is skipped with a warning; a duplicate runway within one file warns and the
last entry wins.

---

## Procedures

Verbatim ARINC 424 CIFP records pinning a SID, STAR, or approach that the **current** FAA CIFP no longer
carries. Unlike every other category here, these are not hand-authored JSON — they are unmodified records
copied out of a published CIFP cycle, so `CifpParser` reads a fragment exactly as it reads a full cycle file.

### When to add one

The FAA occasionally drops a still-charted procedure from the CIFP dataset. KOAK's NIMITZ SID (`NIMI5`) is
the motivating case: it vanished at cycle 2605 while remaining charted and flown, taking its published 315°
initial turn with it. YAAT already recovers such a procedure by walking cached prior AIRAC cycles, but that
only works for **~12 months** (`CifpPathResolver.MaxSupplementaryLookbackCycles`) and only on a machine that
happens to hold the right cycle — a freshly deployed server has one cycle and recovers nothing.

A committed fragment is permanent and identical on every deployment. Add one when a procedure your facility
actually uses has fallen out of the current cycle — and do it **while a cycle that still has it is reachable**.

### Generating a fragment

Never assemble one by hand. `tools/stash-procedure.py` finds the newest AIRAC source that still carries the
procedure and writes the fragment to the right ARTCC folder:

```bash
python tools/stash-procedure.py NIMI --airport KOAK              # find it and write the fragment
python tools/stash-procedure.py NIMI --airport KOAK --dry-run    # just report which cycles have it
python tools/stash-procedure.py BDEGA4 --airport KSFO --kind star --artcc ZOA
```

A bare name (`NIMI`) matches every version (`NIMI5`, `NIMI6`); an exact id matches only itself. The tool
searches, newest first: your local CIFP cache (`%LOCALAPPDATA%/yaat/cache/cifp/`), the repo's bundled cycle,
anything passed via `--search-path`, and — with `--fetch` — the FAA's server (which generally only serves the
current and next cycle, so past cycles usually 404). The owning ARTCC is inferred from existing
`Data/ARTCCs/*/` content referencing the airport; pass `--artcc` when that is ambiguous.

`--dry-run` is also how you check whether the FAA has **republished** a procedure: if it now appears in the
current cycle, the fragment is dead weight and should be deleted (YAAT logs a warning when this happens).

### Precedence

Procedure resolution is **current FAA cycle → ARTCC fragment → cached prior cycles**. The current cycle always
wins, so a republished procedure automatically takes over — including across a version bump (`NIMI5` → `NIMI7`),
which matches on the base name. Sitting above the prior-cycle chain is what makes the result deterministic
regardless of what a given machine has cached.

When a procedure resolves from a fragment, the instructor sees an advisory naming the supplying ARTCC.

### Caveats

- **A pin does not expire — re-verify it against the chart.** The prior-cycle chain is self-limiting (it stops
  resolving after ~12 months), but a fragment resolves forever. Two things are caught for you: republication in
  the CIFP (the load-time shadow warning, and `--dry-run`) and a version bump (the current cycle wins on base
  name). Nothing catches a **chart amendment while the CIFP still omits the procedure** — if NIMITZ's initial
  turn changes from 315°, the pin keeps flying 315° silently. Whoever commits a fragment owns re-checking it
  against the published chart, not just watching for republication.
- **Terminal waypoints travel with the fragment.** `CifpParser` resolves RF arc-center fixes from the *same
  file*, so a procedure with arc legs needs the airport's `PC` terminal-waypoint records too. The tool emits
  them automatically.
- **Do not invent records from a chart.** A fragment must be extracted from a real published CIFP cycle.
  Hand-written ARINC 424 is unreviewable and will encode subtly wrong altitudes, courses, or path terminators.
  KOAK's three vector SIDs show why: OAK6, QUAKE2, and NIMI5 all chart as "climb heading X, then…", yet they
  use three different path terminators (`VD`, `VD`, `CA`), OAK6 and QUAKE2 disagree by 6° on runway 30's
  course, the coded course is 278.2° where the runway bearing is 278.0°, and the altitude is 409 ft MSL rather
  than "400". None of that is knowable from the chart.
- Lines that aren't valid CIFP records (the `#` provenance header, blanks) are ignored by the parser.
- Restart YAAT to pick up edits.

---

## InitialContactTransfers

Facility-specific SOP rules for solo-training pilot initial contact. When a pilot's track is owned by another TCP, these rules decide whether the pilot can initiate contact with the student when a handoff is initiated, only after it is accepted, or without a track handoff.

Rules may match broad position-type pairs such as `APP` → `TWR`, or exact callsigns such as `SFO_APP` → `SFO_TWR`. If an ARTCC has no JSON rules in this category, YAAT uses fallback defaults matching the common training model: `APP` / `CTR` → `TWR` on handoff initiated, and `APP` → `APP` / `CTR` → `APP` on handoff accepted.

Each file is a JSON array of transfer rules:

```json
[
  {
    "fromPositionType": "APP",
    "toPositionType": "TWR",
    "contactAllowedWhen": "handoffInitiated"
  },
  {
    "fromPositionType": "CTR",
    "toPositionType": "TWR",
    "contactAllowedWhen": "handoffInitiated"
  },
  {
    "fromPositionType": "APP",
    "toPositionType": "APP",
    "contactAllowedWhen": "handoffAccepted"
  },
  {
    "airportId": "SFO",
    "fromPositionType": "APP",
    "toCallsign": "SFO_TWR",
    "contactAllowedWhen": "noHandoffNecessary",
    "notes": "NCT/SFO LOA: approach may transfer arrivals to SFO Tower without a STARS track handoff."
  }
]
```

### Field reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `airportId` | string | No | Airport the rule applies to; FAA and ICAO forms are normalized. Omit for ARTCC-wide behavior. |
| `fromPositionType` | string | If no `fromCallsign` | Originating controller position type (`APP`, `DEP`, `TWR`, `LC`, etc.; aliases normalize to `APP`, `TWR`, or `GND`). |
| `fromCallsign` | string | If no `fromPositionType` | Exact originating controller callsign, e.g. `"SFO_APP"`. |
| `toPositionType` | string | If no `toCallsign` | Student/controller position type receiving communications. |
| `toCallsign` | string | If no `toPositionType` | Exact receiving controller callsign, e.g. `"SFO_TWR"`. |
| `contactAllowedWhen` | string | Yes | One of `handoffInitiated`, `handoffAccepted`, or `noHandoffNecessary`. |
| `notes` | string | No | Human-readable SOP note/source. |

Restart YAAT to pick up edits to initial-contact transfer JSONs.

---

## WakeDirectives

Facility-specific solo-training Session Report rules for local wake waivers and wake-advisory directives. These rules do not change aircraft behavior or controller command parsing. They only adjust Session Report scoring for wake contexts that YAAT has already identified from runway, approach, and CWT geometry.

Each file is a JSON array of directive rules:

```json
[
  {
    "id": "example-local-wake-waiver",
    "airportId": "OAK",
    "runways": ["28R"],
    "operation": "departureBehindDeparture",
    "relation": "sameRunway",
    "precedingCwt": ["B"],
    "succeedingCwt": ["F"],
    "sourceRuleReferences": ["7110.65 §3-9-6(f)"],
    "effects": ["suppressWakeInterval", "requireWakeAdvisory"],
    "ruleReference": "7110.65 §2-1-20; facility directive",
    "notes": "Example only: replace with an ARTCC-approved SOP/LOA reference before use."
  }
]
```

### Field reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Stable directive identifier; must be unique within a file. |
| `airportId` | string | No | Airport the directive applies to; FAA and ICAO forms are normalized. Omit for ARTCC-wide behavior. |
| `runways` | string[] | No | Runway designators to match against either aircraft in the wake pair. Omit or empty for any runway. |
| `operation` | string | No | One of `any`, `departureBehindDeparture`, `departureBehindLanding`, `arrivalBehindDeparture`, `arrivalBehindLanding`, or `approachBehindArrival`. Defaults to `any`. |
| `relation` | string | No | One of `any`, `sameRunway`, `closeParallel`, `intersecting`, `projectedConverging`, or `oppositeDirection`. Defaults to `any`. |
| `precedingCwt` | string[] | No | Optional CWT category filter for the preceding aircraft (`A` through `I`). |
| `succeedingCwt` | string[] | No | Optional CWT category filter for the succeeding aircraft (`A` through `I`). |
| `sourceRuleReferences` | string[] | No | Optional filter for the underlying FAA rule reference generated by YAAT, e.g. `7110.65 §3-9-6(f)`. |
| `effects` | string[] | Yes | One or more of `suppressWakeInterval`, `requireWakeAdvisory`, or `suppressWakeAdvisory`. |
| `ruleReference` | string | No | Reference text included with directive-required advisory findings. |
| `notes` | string | No | Human-readable SOP/LOA note or provenance. |

`suppressWakeInterval` suppresses the Runway / Wake interval finding for a matching context. `requireWakeAdvisory` creates an Advisory / Visual missing-`CWT` finding for a matching context even when the generic wake interval is already satisfied. `suppressWakeAdvisory` suppresses only the missing-advisory finding; it does not suppress a Runway / Wake interval finding.

Do not add real facility waivers from memory. Checked-in rules should cite an ARTCC-approved local SOP, LOA, or facility directive.

Restart YAAT to pick up edits to wake directive JSONs.

---

## SurfaceTempData

Geometry drawn on a facility's CRC surface display — the ASDE-X or SAAB SAID "temp data" objects a
controller creates with the drawing tools: restricted areas, closed areas, and text labels. vZOA uses
this for the SFO 28L/28R extended final-approach centerlines and their per-mile marks, so local control
can confirm at a glance which parallel an arrival is lined up on.

**One file per facility, named for the vNAS facility id** (`SFO.json`, `OAK.json`, `NCT.json`) — not
scanned like the other categories, because the server looks the file up by the facility the CRC client
subscribed to.

These are the *defaults*: the server seeds every room from them the first time someone opens that
facility's display. Anything controllers draw afterwards is kept in the server's own writable facility
store and layered on top; deleting a seeded object records a tombstone there rather than editing this
file. Committing a change here therefore updates the baseline for rooms that have not overridden it.

To produce a file, draw the geometry in CRC and use **Tools → Export ASDE-X / SAID Temp Data...**
in the YAAT client, which writes exactly this schema, one file per facility.

```json
{
  "asdex": {
    "items": [
      {
        "id": "sfo-28r-final",
        "presetId": 1,
        "type": "RestrictedArea",
        "area": [[37.613517, -122.357130], [37.53, -122.20]]
      },
      {
        "id": "sfo-28r-m05",
        "presetId": 1,
        "type": "Text",
        "location": [37.59, -122.28],
        "line1": "5"
      }
    ],
    "presets": [
      { "id": 1, "dataId": "sfo-28r-final", "name": "FINALS", "active": true }
    ]
  },
  "said": {}
}
```

Both top-level sections are optional; a facility runs one surface system or the other.

### Item fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Stable identifier, unique within the facility. Controllers delete by this id, so keep it stable across edits. |
| `type` | string | Yes | `RestrictedArea`, `ClosedArea`, or `Text`. |
| `area` | number[][] | For areas | Outline as `[[lat, lon], …]`. CRC closes the ring itself but **fills** it, so the outline must enclose real area — draw a *line* as a thin quad, never as an out-and-back point list. A zero-area ring tessellates to nothing and throws in the client; the loader drops such rings. |
| `location` | number[] | For text | `[lat, lon]` anchor. |
| `line1`, `line2` | string | For text | The label text; `line2` renders below `line1`. |
| `presetId` | number | No | The SET (1–88) this object belongs to. Objects in a SET render only while that SET is active, so a controller can toggle the whole group off. Omit for an object that always renders. |

### Preset fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | number | Yes | SET number, 1–88. Chosen by the author, exactly as a controller would type it in CRC. |
| `dataId` | string | Yes | Id of the item this SET holds. |
| `name` | string | Yes | SET label, 1–7 characters (CRC's limit). |
| `active` | bool | Yes | Whether the SET starts toggled on. |

Coordinates are decimal degrees, latitude first. Restart the server to pick up edits.

### Worked example — `ZOA/SurfaceTempData/SFO.json`

Three groups, one per flow, each in its own SET so a controller shows only the configuration in use.
All start active, matching the reference, where every group is on screen at once:

```bash
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --runways 28L 28R --set 1 --set-name 28FINAL
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --append --runways 10L 10R --set 2 --set-name 10FINAL --length 2.5
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --append --runways 19L --set 3 --set-name 19FINAL --length 2.5
```

Each runway gets its extended final-approach centerline out from the **landing** threshold with a mark
across it at each mile. 19L is alone — the reference draws no 19R overlay. Notes for anyone changing it:

- **The mark shape is copied from the reporter's screenshot**, measured against its own scale (the 750 ft
  between the parallels spans 22 px there). Each runway carries its own mark, centred on that runway's
  centerline and crossing it on both sides, ~547 ft long, leaving ~204 ft of the channel between the two
  runways unmarked. Reference measures 545–648 ft with a 136–307 ft gap. **The two runways' marks must
  never join into one bar** — the gap is what lets a controller tell the parallels apart.
- **No text.** The reference has none: no distance numerals, no runway ids. The marks are self-evident
  once you know they are a mile apart.
- **7 nm on the 28s** covers the approach gate, which sits ~1 nm outside the FAF and no closer than 5 nm
  from the threshold (7110.65 PCG, *approach gate*); vectors to intercept are issued at least 2 nm outside
  that (§5-9-1.a.1). Everything local control reads happens inside it, and drawing further only adds
  clutter. The reference is clipped by its inset window at 4.84 nm, so its true length is unknown.
- **2.5 nm on the 10s and 19L is approximate.** It is scaled off the whole-airport reference rather than
  measured against a known distance in it, so treat it as ±0.2 nm. Direction, mark shape, and the 1 nm
  spacing are verified; only the cutoff is an estimate. Rerun the generator with a different `--length`
  to correct it.
- **The origin is the displaced threshold.** The airport map's runway LineString endpoints are *pavement*
  ends — CIFP puts KSFO 28L/28R's landing thresholds ~300 ft downfield, which is what the map's
  `threshold: "0 - 300"` property records. The generator applies it.
- **West flow only.** These lines are meaningless — and actively misleading — on 10L/10R or 1L/1R
  operations, which is why the SET is named for the flow.
- **This is the *visual* reference, not the SOIA course.** SFO 28L/28R at 750 ft is a §5-9-9.a
  simultaneous-offset installation. When PRM/SOIA is running, an aircraft correctly flying the LDA PRM
  28L is on a 2.5–3.0° offset course that only converges onto this drawn centerline in the visual
  segment. It will look misaligned against the line and it is not — do not "fix" that.

The underlying requirement is 7110.65 §7-4-4.c.1: on parallels less than 2,500 ft apart, an aircraft must
report the preceding aircraft in sight **on the adjacent extended runway centerline** before visual
separation is applied. That report is what the overlay exists to support.
