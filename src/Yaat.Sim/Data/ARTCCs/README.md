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
    TaxiRoutes/
      koak-routes.json
    AvoidTaxiways/
      oak.json
    InitialContactTransfers/
      zoa-initial-contact-transfers.json
    WakeDirectives/
      oak-wake-directives.json
  ZMA/
    TaxiRoutes/
      kfll-routes.json
```

Each loader scans `ARTCCs/*/{Category}/*.json`. Files whose category folder doesn't match the loader are ignored — `Data/ARTCCs/ZOA/CustomFixes/foo.json` is read by `CustomFixLoader`, not by `TaxiRouteLoader`.

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

## TaxiRoutes

Per-airport preset taxi routes surfaced in the right-click "Preset taxi route" submenu on the ground view. Each preset is a one-click shortcut for an SOP-aligned `TAXI` command — useful where the auto-router doesn't follow local best practice.

Each file is a JSON object scoped to one airport. The `path` is whatever you'd type after `TAXI` in the command bar:

```json
{
  "airportId": "KFLL",
  "routes": [
    {
      "name": "DEP 10R via T-T3-B",
      "path": "T T3 B",
      "destinationRunway": "10R",
      "tags": ["dep", "10R"]
    }
  ]
}
```

### Field reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `airportId` | string | Yes (file-level) | ICAO of the airport these routes apply to |
| `routes[].name` | string | Yes | Display name in the menu |
| `routes[].path` | string | Yes | Whitespace-separated taxiway names |
| `routes[].destinationRunway` | string | No | Runway hold-short (e.g. `"10R"`) |
| `routes[].destinationParking` | string | No | Parking destination (e.g. `"G7"`) |
| `routes[].destinationSpot` | string | No | Spot destination |
| `routes[].tags` | string[] | No | Reserved for future menu filtering |

At most one of the three `destination*` fields may be set on a single route. Routes whose path can't be walked from the aircraft's current position are silently dropped from the menu — so a KOAK route won't surface when right-clicking an aircraft at KSFO.

Restart YAAT to pick up edits to route JSONs.

---

## AvoidTaxiways

Per-airport taxiways the **automatic** pathfinder should avoid in route suggestions — the routes generated for the right-click "taxi to…" menu, `TAXIAUTO`/`TAXIALL`, the auto-extension of an explicit path into a parking, and any other auto-route. Use this where a taxiway is technically usable but locally undesirable for routine routing (e.g. a perimeter/cargo lead such as taxiway S at OAK).

The avoidance is **strict but not absolute**: an avoided taxiway is never used when any avoiding route to the destination exists, but it *is* used when the destination is only reachable through it (e.g. parking spots that hang off it). Explicit controller commands that name the taxiway — `TAXI S …` — are obeyed verbatim and never re-routed.

Each file is a JSON object scoped to one airport:

```json
{
  "airportId": "KOAK",
  "taxiways": [
    {
      "name": "S",
      "notes": "Perimeter/cargo ramp lead; not used for routine auto-taxi."
    }
  ]
}
```

### Field reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `airportId` | string | Yes (file-level) | ICAO or FAA ident of the airport (`KOAK` and `OAK` both match) |
| `taxiways[].name` | string | Yes | Taxiway name to avoid, matched case-insensitively against the route's taxiway names |
| `taxiways[].notes` | string | No | Human-readable rationale (SOP reference, condition). Informational only |

Names are case-insensitive and de-duplicated per file. Multiple files for the same airport are merged. An entry with a blank name, or a file with no valid entries, is skipped with a warning.

Restart YAAT to pick up edits to avoid-taxiway JSONs.

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
