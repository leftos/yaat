# Facility SOP knowledge (`FacilityOps`)

The layer of controller knowledge *above* generic 7110.65 — a facility's own runway configurations, selection rules,
and assignment policy — codified as checked-in JSON the controller AI consults as an **overlay**: the brains run their
generic, 7110.65-conservative rules everywhere and refine decisions where a knowledge file exists. Design and roadmap:
[`docs/plans/controller-ai/10-facility-knowledge.md`](plans/controller-ai/10-facility-knowledge.md).

## Files and lifecycle

- One file per facility under `src/Yaat.Sim/Data/FacilityOps/` (`KOAK.json` today), copied to the output directory as
  content (the same mechanism as `AircraftProfileOverrides.json`).
- `FacilityOpsDatabase` (`src/Yaat.Sim/ControllerAi/Knowledge/`) is a process-wide static like `NavigationDatabase`:
  `Initialize(directory, navigation)` at startup — `ServerDataBootstrap.InitializeNavigationAsync` on the server and
  the headless tools, `TestVnasData.EnsureInitialized` in tests — `SetInstance` for hand-built sets, `For(airportId)`
  (FAA or ICAO form) at every consult site. No file for an airport ⇒ `null` ⇒ the generic rule.
- Parsing is strict (`FacilityOpsJson.Options`: unknown properties and unknown enum values are errors) and
  `FacilityOpsValidator` cross-checks against navdata: every airport and runway must resolve, configuration names must
  be declared, a partner coupling must name runways at the partner, every entry needs a `source` citation. A failure
  throws `FacilityOpsValidationException` listing every problem — **a bad runway id stops the server**, by design.
  `FacilityOpsTests.EveryCommittedFile_Validates…` runs the same check in CI.

## Schema (version 1)

```
FacilityOps           { schemaVersion, facilityId, airportId, sourceDocument, runwayConfigurations[], runwaySelection?, runwayAssignmentPolicy[] }
RunwayConfiguration   { name, runways: { <airportId>: { departure[], arrival[] } }, source }
RunwaySelectionPolicy { calmWindBelowKt, calmConfiguration, windAlignedCandidates[], partnerCouplings[], source }
PartnerCoupling       { partnerAirportId, partnerConfiguration, useConfiguration, source }
RunwayAssignmentRule  { id, runways[], effect: Exclude, applies: AircraftPredicate, source }
AircraftPredicate     { category?: Jet|Turboprop|Piston|Helicopter, sopClass?: P|T|J, mtowOverLb?, engineCount? }
```

A **configuration is the unit of knowledge**: it carries every airport's runway sets it names (OAK's SFOW/OAKE/SFOE
transcribe SFO's runways too), so a partner given as a configuration name resolves directly and the coupling rule can
be validated. Sections the plan defines but nothing consumes yet (departure tables, release rules, missed-approach
tables, crossing approvals, pattern preferences, jurisdiction overrides, sector/LOA contracts) are simply absent —
additive later, no renames.

**Aircraft classes** (`SopAircraftClassifier`, NCT SOP 1-7): a jet or a four-engine turboprop is **J**, another non-jet
with a profile cruise TAS ≥ 180 kt is **T**, the rest **P** — from the type's performance profile (type-intrinsic,
never the filed speed), the category baseline when no profile exists. Predicates match on every stated field; an
unknown MTOW counts as "over" for a class-T aircraft (the heavier turboprops the rule is after), an unknown engine
count never matches.

## How the Ground brain uses it

`RunwayInUseState` (on `AiControllerService`, one decision per airport) resolves in this order and then **holds the
decision** while the reported wind stays within 30° / 5 kt of the wind it was made in and the precipitation state is
unchanged — a runway change is a supervisor decision (7110.65 §3-5-1.a; OAK 4-2.b.ii makes it a coordinated event), and
a weather timeline hands the world a new profile every second, so the memo keys on the wind, never on the object:

1. `ControllerAiConfig.RunwayInUse` — the session's runway designator for the primary airport (SoakRunner `--runway`).
2. `ControllerAiConfig.RunwayConfigurations[airport]` — a named configuration from the airport's file (`--runway-config
   KOAK=OAKE`), kept as set; when it fails the usability gate the impossible tailwind goes on the ledger as an
   informational `KnowledgeConflict`. The same map answers the coupling rule's question about a partner (`KSFO=SFOE`).
3. **Knowledge:** `FacilityRunwaySelector.Select` in SOP order — a partner coupling first (`KSFO` in `SFOE` ⇒ `SFOE`;
   the partner's own file decides when it has one), then the calm configuration below `calmWindBelowKt`, then the
   wind-aligned candidate with the best headwind over its departure runways (ties to the calm configuration, then the
   declared order). Headwind is `S·cos Δ` with `S` fixed, so the best headwind over a set *is* the smallest angle over
   that set — literally the SOP's "most nearly aligned" (a facility whose candidates are not reciprocal sets will need
   its own formulation). Thresholds read the **gust** when one is reported (05G18 is not a calm day), and a variable
   wind is calm for selection but a full-speed tailwind on every end for the gate. Then `RunwayUsabilityGate` prunes
   any departure runway carrying more tailwind than **10 kt dry** (the common transport-category certificated limit)
   / **5 kt with precipitation reported** (FAA Order 8400.9's unwaived figure) — and only when *none* survives files
   `AiAnomalyKind.KnowledgeConflict` and lets the generic rule decide. Arrival runways are not gated. **The more
   conservative wins**, and a knowledge-file bug is a finding.
4. Generic `RunwayInUseResolver` (7110.65 §3-5-1).

Per aircraft, `FacilityRunwayAssigner.AssignDepartureRunway` applies the assignment policy over the configuration's
departure runways (OAK 3-4: jets, turboprops over 17,000 lb and four-engine recips stay off the 28s); when nothing is
left the whole set stands (the policy is a request — 3-4.b's deviation clause). An aircraft the policy constrains gets
the **longest** remaining pavement first (OAKE keeps 10L at 5,448 ft next to 12 at 10,513 ft; OAK 4-1.f only says
jets may use either), everyone else the nearest departure threshold, and the designator breaks the tie. The rationale
strings name the SOP paragraph that fired, so a soak finding traces to the line.

## Authoring

- Transcribe operational facts only, each with its `source` (`"OAK ATCT SOP 4-2.c"`); the PDFs stay out of the repo
  (VATSIM training artifacts, "For Simulation Use Only") — `sourceDocument` + the `zoa-reference-cli` `sop` catalog
  re-locate them.
- Enums over free text wherever a brain branches; no speculative fields.
- Run `dotnet test tests/Yaat.Sim.Tests -- --filter-class "*.Knowledge.*"` — `FacilityOpsTests` and
  `OakRunwayKnowledgeTests` pin the OAK transcription (4-2 selection, 4-2.c coupling, 3-4 assignment, the gate).
