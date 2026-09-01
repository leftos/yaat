# 10 — Facility SOP Knowledge (AI-parseable)

Part of [Controller AI + Soak Harness](README.md). Codifies airport/facility SOP and LOA content —
the layer of controller knowledge *above* generic 7110.65 — into schema-validated data the position
brains consume. Surveyed 2026-09-01 from the ZOA reference library (via the `zoa-reference-cli`
`sop` catalog): OAK ATCT SOP, SFO ATCT SOP, NORCAL TRACON (NCT) SOP, Oakland Center SOP, Fresno
ATCT/TRACON SOP, ZOA↔NCT LOA.

## What the SOPs offer (survey findings)

A controller working a ZOA facility — human or AI — is expected to know, per tier:

**ATCT SOPs** (OAK 18 pp, SFO 34 pp; FAT's tower half similar):
- Positions table: callsign, radio callsign, frequency per position.
- **Named runway configurations** with cross-airport coupling (OAK 1-6: SFOW/OAKE/SFOE defined in
  terms of *both* SFO's and OAK's runways; OAK 4-2.c: "When SFO ATCT is in SFOE configuration, OAK
  shall use the runway 12/10 configuration").
- **Runway selection rules**: wind thresholds (OAK 4-2: < 10 kt → 30/28 configuration; ≥ 10 kt →
  most nearly aligned; notify NCT + SFO on change).
- **Per-configuration IFR departure tables** (OAK 2-2): destination/route class → DP or heading,
  departure sector, initial altitude, per aircraft class (J/T/P). Plus non-DP heading tables.
- **Release rules** (OAK 4-1.d): aircraft auto-released *except* enumerated DPs/headings per
  configuration, which require verbal CFR — exactly the HFR/REL machinery's knowledge gap.
- **Missed-approach tables** (OAK 4-4.d): per runway per configuration → heading + altitude, and
  which radar sector to coordinate with.
- **Additional authorized headings** per runway (OAK 4-5, requires NCT approval).
- **Runway assignment policy** (OAK 3-4: jets/heavy turboprops not on 28L/R; SFO 3-4: by parking
  side, turn direction, DP, with intersection-departure carve-outs).
- **Multiple-runway-crossing approvals** (OAK 3-3: 28R/L at Bravo; SFO 3-3: enumerated taxi routes,
  and taxiways where it is *not* authorized) — feeds the [04](04-ground-brain.md) per-runway
  crossing rule's ≤ 1,300 ft exception, which is facility-approval-gated.
- **Preferred pattern runways + pattern altitudes + restrictions** (OAK 4-1.h: 28L preferred at
  600 ft, pattern must stay north of runway 30's traffic).
- **GC/LC jurisdiction splits, config-dependent** (SFO 3-1/4-2: the "West End", taxiways that
  change hands by configuration) — refines [02](02-positions-and-handoffs.md)'s generic phase-based
  jurisdiction inference.
- Same-runway quirks (OAK 4-1.f: "runway 12/10 are considered the same runway for jet departures"),
  reduced-separation authorizations (OAK 4-1.g: 2.5 NM on runway 30 final within 10 NM), VFR
  routes/transitions keyed to real-world landmarks, pushback/spot procedures (SFO 3-5), gate holds,
  noise abatement (explicitly optional).

**TRACON SOP** (NCT, 144 pp): areas → sectors → sector IDs → owned airports (1-5); composite
**traffic flow plans** (1-6: SFOW/SFOE and mixed variants defined across SFO+OAK+SJC
simultaneously); and — the bulk of the document — **per-sector × per-configuration entry/exit route
contract tables** (e.g. Grove-SFOW: route class → aircraft class → altitude → heading/instructions
for each neighboring sector). Plus scratchpad conventions, automated point-out exceptions, P-ACP,
vector gates, V2I, DVAs.

**Center SOP** (ZOA, 18 pp): area definitions + combined ops; 3 NM TBDM reduced separation up to
FL230 with a 5 NM obligation toward facilities not applying it; default intra-facility
transfer-of-control provisions (turns ≤ 15°, speed, beacon, scratchpad); airport group definitions;
per-area daily procedures + LOA summaries.

**LOAs** (ZOA↔NCT sampled, 25 pp): boundary contracts — OPD STAR assignment by airport/runway
configuration; "established on and descending via the arrival by fix X" conditions; automated
handoff initiation points (e.g. "no later than 10 NM south of NRRLI and prior to descending out of
FL200"); transfer-of-communications deadlines; control-for provisions (RV ± 30°, climb/descent);
lateral-separation obligations at the boundary.

## Locked decisions

- **Repo JSON, schema-validated.** Per-facility knowledge files checked into yaat and loaded like
  the `AircraftProfileOverrides` correction layer; every referenced runway, DP, STAR, and fix must
  resolve against navdata/layouts at load (fail fast, and a test enforces it).
- **LLM-assisted offline extraction.** A Python tool drafts knowledge JSON from SOP PDF text;
  a human reviews the diff before commit. No runtime LLM (matches the core no-LLM decision and the
  existing offline-curation-via-Python-tool pattern).
- **v1 scope: tower tier, OAK then SFO.** TRACON/Center/LOA contracts codify later, with CA5/CA6.
- **Knowledge is an overlay, never a requirement.** Brains run generic 7110.65-conservative
  behavior everywhere ([04](04-ground-brain.md)/[05](05-tower-brain.md) as written); facility
  knowledge refines decisions where present. Soak works at any airport.

## Design

### Schema (`FacilityOps`)

`src/Yaat.Sim/Data/FacilityOps/` — one JSON file per facility (`KOAK.json`, `KSFO.json`; later
`NCT.json`, `ZOA.json`), deserialized into records under `Yaat.Sim.ControllerAi.Knowledge`:

```
FacilityOps
  facilityId, airportId(s)
  runwayConfigurations[]        name (SFOW/OAKE/…), per-airport runway sets,
                                selection { windThresholdKt, calmConfig, coupledTo }  // "SFO SFOE ⇒ OAK 10s/12"
  departureTables[]             config → { routeClass/destGroup, aircraftClass(J/T/P), dp|heading,
                                departureSector, initialAltitude }
  releaseRules[]                config → auto-release default + CFR-required exceptions (DPs/headings)
  missedApproachTables[]        config → runway → { heading, altitude, coordinateWith }
  authorizedHeadings[]          runway → heading ranges (approval source)
  runwayAssignmentPolicy[]      class/weight/parking-side constraints + deviation allowances
  multipleRunwayCrossings[]     approved taxi routes (and explicit not-authorized taxiways)
  patternPreferences[]          config → { preferredRunway, patternAltitude, restrictions }
  jurisdictionOverrides[]       config → GC/LC surface splits (SFO West End style)
  sameRunwayQuirks[]            e.g. "10/12 treated as one runway for jet departures (config X)"
  separationAuthorizations[]    e.g. 2.5 NM on RWY 30 final within 10 NM
  // later tiers:
  sectorContracts[]             per-sector × config entry/exit rows (route, class, altitude, heading)
  loaContracts[]                boundary: STAR-by-config, handoff-initiation point, comms deadline,
                                control-for provisions
```

Aircraft classes reuse the SOPs' own P/T/J definitions (cruise-speed thresholds, ZOA SOP 1-8) —
resolved from the existing performance database, not re-declared per aircraft.

Schema principles: every entry carries a `source` citation (doc + section, e.g. `"OAK ATCT 4-4"`)
so triage can trace a brain decision to its SOP line; enums over free text wherever the brains
branch on a value; free-text-only knowledge (noise abatement prose, VFR landmark routes) is **out
of scope** until a brain actually needs it — no speculative fields.

### Loader + overlay contract

- `FacilityOpsDatabase` (static, `Initialize`/`SetInstance` like `NavigationDatabase`): loads,
  schema-validates, and cross-validates against navdata + ground layouts (unknown runway/DP/fix =
  load error, not a silent skip).
- Brains query through `AiTickContext.World` ([01](01-architecture.md)); every consult site follows
  **overlay semantics**: `knowledge?.MissedApproach(config, runway) ?? genericRule()`. The generic
  path stays the tested baseline; knowledge presence must never be load-bearing for safety rules.
- Where knowledge contradicts a 7110.65 gate, the **more conservative wins** and the conflict files
  an anomaly (a knowledge-file bug is a finding too).
- The active `runwayConfiguration` becomes shared AI state ([04](04-ground-brain.md) rule 1 —
  facility-level runway-in-use), resolved from scenario/room config or the selection rules, and is
  what departure tables, missed-approach tables, and release rules key on.

### Extraction tool

`tools/facility_ops_extract.py` (yaat repo): PDF text extraction (pymupdf) → per-section prompts →
draft JSON against the schema → writes to a review file; the human diffs against the committed
knowledge file and commits. Determinism/repeatability is not required of the tool — the committed
JSON is the artifact; the tool is scaffolding. Source PDFs are not committed (VATSIM artifacts,
"For Simulation Use Only"); the `source` citations + the zoa-reference-cli catalog re-locate them.

### Consumers (which brain reads what, v1)

| Knowledge | Consumer |
|---|---|
| runwayConfigurations + selection | Ground rule 1 / shared AI state ([04](04-ground-brain.md)) |
| departureTables | Ground (runway/DP sanity), later Clearance Delivery emulation |
| releaseRules | Local CTO gate 1 ([05](05-tower-brain.md)) — which departures need CFR vs auto-release |
| missedApproachTables | Local GA rule — issue the SOP heading/altitude instead of generic runway-heading |
| multipleRunwayCrossings | Ground rule 2 — enables the ≤ 1,300 ft multi-crossing exception where approved |
| patternPreferences | Local pattern management (CA7) |
| jurisdictionOverrides | `PositionJurisdiction` refinement ([02](02-positions-and-handoffs.md)) |
| runwayAssignmentPolicy | Ground runway assignment |
| sectorContracts / loaContracts | Approach/Center brains (CA5/CA6) |

## Milestones (K series, interleaved in the README table)

| # | Milestone | Contents |
|---|---|---|
| K1 | Schema + OAK tower knowledge | `FacilityOps` records + `FacilityOpsDatabase` + validation tests; hand-authored `KOAK.json` (configs, selection, departure tables, release rules, missed-approach tables, crossings, pattern prefs); overlay consult sites in the Ground/Tower brains. Lands with CA1/CA2 so the first soak fields run SOP-correct OAK behavior |
| K2 | Extraction tool + SFO | `tools/facility_ops_extract.py`; `KSFO.json` (adds jurisdictionOverrides, runwayAssignmentPolicy at SFO's complexity level) — proves the schema against the second, harder tower |
| K3 | TRACON/Center/LOA contracts | `sectorContracts` + `loaContracts` schema halves; `NCT.json`/`ZOA.json`/LOA files scoped to what CA5/CA6 consume (entry/exit contracts, handoff initiation points, transfer-of-control provisions) |

## Risks and open questions

- **Staleness**: SOPs revise; committed JSON drifts. Mitigation: `source` citations + a
  low-ceremony re-extraction diff via the K2 tool when a SOP updates. No auto-sync.
- **Schema creep**: the NCT tables are large and idiosyncratic; K3 must scope `sectorContracts` to
  the rows the Approach brain actually consumes, not transcribe all 144 pages.
- **Config detection**: scenarios don't declare a named runway configuration today; K1 derives it
  from scenario runway-in-use + the selection rules, but a scenario knob
  (`ControllerAiConfig.RunwayConfiguration`) may be worth adding for determinism.
- **Licensing/provenance**: ZOA SOPs are VATSIM training artifacts marked "For Simulation Use
  Only" — YAAT's use (a VATSIM training tool) matches, but keep the PDFs out of the repo and the
  extracted JSON limited to operational facts.
- **Cross-airport coupling** (OAK config follows SFO's) means the shared AI state is
  ARTCC-scoped, not airport-scoped — the `AiControllerService` owns one configuration map for all
  airports in the session.
