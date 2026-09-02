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
| K1 | Schema + OAK tower knowledge (runway-selection subset) | **Shipped 2026-09-01 as K1-lite** (with CA1): `FacilityOps` records + `FacilityOpsDatabase` + validation tests; `KOAK.json` with `runwayConfigurations` / `runwaySelection` / `runwayAssignmentPolicy` and the P/T/J classes; the Ground brain's runway-in-use consult site. Subsystem doc: [`docs/facility-ops-knowledge.md`](../../facility-ops-knowledge.md) |
| K1b | OAK tower knowledge, remainder | With CA2: `departureTables`, `releaseRules`, `missedApproachTables`, `multipleRunwayCrossings`, `patternPreferences`, `jurisdictionOverrides`, `sameRunwayQuirks`, `separationAuthorizations` — the sections the Tower brain consumes |
| K2 | Extraction tool + SFO | `tools/facility_ops_extract.py`; `KSFO.json` (adds jurisdictionOverrides, runwayAssignmentPolicy at SFO's complexity level) — proves the schema against the second, harder tower |
| K3 | TRACON/Center/LOA contracts | `sectorContracts` + `loaContracts` schema halves; `NCT.json`/`ZOA.json`/LOA files scoped to what CA5/CA6 consume (entry/exit contracts, handoff initiation points, transfer-of-control provisions) |

## Risks and open questions

- **Staleness**: SOPs revise; committed JSON drifts. Mitigation: `source` citations + a
  low-ceremony re-extraction diff via the K2 tool when a SOP updates. No auto-sync.
- **Schema creep**: the NCT tables are large and idiosyncratic; K3 must scope `sectorContracts` to
  the rows the Approach brain actually consumes, not transcribe all 144 pages.
- **Config detection** — resolved in K1-lite: `ControllerAiConfig.RunwayConfigurations` (airport id → configuration
  name; SoakRunner `--runway-config KSFO=SFOE`) fixes a configuration for the session and tells a coupling rule what the
  partner is doing; without it the file's own selection rules decide from the wind, and a partner with its own file
  answers from its own selection.
- **Licensing/provenance**: ZOA SOPs are VATSIM training artifacts marked "For Simulation Use
  Only" — YAAT's use (a VATSIM training tool) matches, but keep the PDFs out of the repo and the
  extracted JSON limited to operational facts.
- **Cross-airport coupling** (OAK config follows SFO's) means the shared AI state is
  ARTCC-scoped, not airport-scoped — the `AiControllerService` owns one configuration map for all
  airports in the session.

## K1-lite implementation notes (2026-09-01)

- A **configuration is the unit of knowledge**: `RunwayConfiguration.runways` carries every airport's departure/arrival
  sets the plan names (OAK's SFOW/OAKE/SFOE transcribe SFO's runways too), so a partner override expressed as a
  configuration resolves directly and a coupling can be validated against the partner's runways.
- Selection precedence = SOP order: partner coupling (4-2.c) > calm configuration (4-2.a) > wind-aligned candidate
  (4-2.b, best headwind over the configuration's departure runways; ties to the calm configuration, then declared order).
  SFOE is never wind-selected — only the SFO coupling produces it.
- "Conservative wins" is a **usability gate**, not a re-choice: 7110.65 §3-5-1.b.1 lets a facility's runway-use rule
  stand ("operationally advantageous"); `RunwayUsabilityGate` prunes the departure runways carrying more than 10 kt
  (dry — the common transport-category certificated limit) / 5 kt (precipitation reported — FAA Order 8400.9's unwaived
  figure) of tailwind, held to the gust and to the whole gust for a variable wind, and only when none survives files
  `KnowledgeConflict` and lets the generic rule decide. (SFO ATCT SOP 1-5 waives 8400.9 the *other* way — 10 kt on
  not-dry runways — so the pair is not "the local number"; it is the certificated limit dry and the order's figure wet.)
  Arrival runways and crosswind are not gated (K1b).
- The active configuration is ARTCC-scoped shared AI state on `AiControllerService.RunwayInUse` (`RunwayInUseState`,
  one decision per airport, never snapshotted), **held with hysteresis** — re-decided only when the reported wind moves
  30° / 5 kt (gust-inclusive) from the wind it was made in or the precipitation state flips — because a weather
  timeline hands the world a new `WeatherProfile` every second and a runway change is a supervisor decision (§3-5-1.a,
  OAK 4-2.b.ii). A partner's configuration comes from the session knob or the partner's own file, with a recursion guard
  for two files coupling to each other (the state takes its knowledge lookup as a constructor argument, so the guard is
  tested with two in-memory files).
- The assignment policy is a request (3-4.b): no `strength` field. An aircraft the policy constrains gets the longest
  remaining pavement, then the nearest threshold (a B738 in OAKE gets 12, not 5,448 ft 10L); everyone else nearest first.
- No `KSFO.json` yet (K2): OAK's coupling reads `RunwayConfigurations["KSFO"]`, else stays inert — harmless, since OAKE
  and SFOE use the same OAK runways. Aircraft classes come from the type's profile cruise TAS (type-intrinsic), never
  the filed speed; MTOW from the FAA ACD record, with "class T counts as over 17,000 lb" when it is missing.
- The extraction tool stays K2; `KOAK.json` was transcribed by hand from the cached OAK ATCT SOP v1.7 (04SEP2025) text
  and verified against it by the aviation review (1-6, 3-4, 4-2 exact; NCT 1-7 verbatim). SFO's sets in OAK's file
  follow SFO's plans (west: land 28s, depart 01s + 28s; east: land 19s, depart 10s + 19s) so K2's `KSFO.json` can read
  them.
- K1b traceability — SOP lines this subset touches but does not encode: 4-2.b.ii (notify NCT/SFO on a configuration
  change), 4-3.b (LC coordinates any runway other than the designated active — live now that the assigner spreads
  departures over a configuration's three runways), 3-2 (Ground taxis everyone full length), 3-3 (28R/L at Bravo).
  33 is a real SFOW departure/pattern runway (2-2.b.ii, 4-1.h.i, 4-5) even though it sits outside every 1-6
  configuration — `departureTables`/`patternPreferences` must not inherit "33 does not exist".
