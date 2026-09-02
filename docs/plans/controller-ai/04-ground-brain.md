# 04 — Ground Brain v1

Part of [Controller AI + Soak Harness](README.md). Rule set for the AI Ground position (7110.65
§3-7-2 taxi operations, §3-1-3 use of active runways / GC–LC crossing approval, §3-1-4 GC→LC
notifications, §3-5-1 runway selection). Framework mechanics in [03](03-decision-framework.md);
jurisdiction and handoff flow in [02](02-positions-and-handoffs.md). Reviewed against the local
7110.65 reference by `aviation-sim-expert` 2026-09-01; corrections folded in.

## Rules (priority order)

1. **Answer ready-to-taxi.** Open `Taxi` pilot request → the **facility runway-in-use**
   (scenario/room configuration if present; else resolved once per weather update into shared AI
   state read by both Ground and Local — never per-aircraft): the usable runway most nearly aligned
   with the wind when **≥ 5 kt**, or the configured **calm-wind runway when < 5 kt** (§3-5-1.b);
   deterministic tiebreak by runway id. Alignment compares **magnetic** wind against the magnetic
   runway bearing (METAR raw wind is true — convert via declination before comparing).
   Runway-in-use designation is a supervisor/CIC function (§3-5-1.a) the AI stands in for; a
   mid-session change is a v1 non-goal. Then `TAXI <rwy>` — the existing pathfinder routes; phases
   enforce hold-shorts along the way. Ground does not micro-manage the route in v1.
2. **Runway crossings.** For **each** runway the assigned route crosses, in route order: request
   Local's approval via the coordination bus (§3-1-3.a — approval must name the runway and the
   point/intersection), issue `CROSS <rwy>` at that point on approval, and report completion
   (§3-1-3.c) before requesting the next. §3-7-2.a.3 forbids issuing a second crossing clearance
   before the first is crossed; v1 does not implement the ≤ 1,300 ft multiple-crossing exception
   (facility-approval-gated). The crosser stays under Ground's jurisdiction throughout
   ([02](02-positions-and-handoffs.md)). With a human Local: hold short + one paced terminal
   request line.
3. **Reactive sequencing only.** v1 leans on the existing ground-movement give-way physics; Ground
   intervenes only reactively. A detected mutual deadlock is an **anomaly first** — that is a sim
   bug to find, not something the brain should paper over. Active `HS`/give-way resolution is
   deferred to v1.1.
4. **Arrival taxi-in.** A rolled-out arrival `CT`'d by Local → answer its taxi request → taxi to
   parking. Where clearing the landing runway requires entering another runway, **Local retains the
   aircraft and issues the exit + crossing itself before the `CT`** (§3-10-9.b.2), and both
   positions protect the intersection (§3-10-9.c) — Ground does not answer a taxi request from an
   aircraft not yet clear in that sense. (Exact canonical for taxi-to-parking to be confirmed at
   implementation — flagged in README risks.)
5. **Hand to Local.** At hold-short of the departure runway → `CT`.

## Facility-knowledge overlay

Where a `FacilityOps` file exists ([10](10-facility-knowledge.md)), it refines these rules: the
facility's own runway-selection rules and configuration coupling replace the generic §3-5-1
computation in rule 1 (e.g. OAK's 10-kt threshold and "SFO in SFOE ⇒ OAK 10s/12") — behind the
tailwind usability gate (10 kt dry / 5 kt wet, gust-inclusive; unusable departure runways are pruned and
only an empty set files `KnowledgeConflict` and hands the generic rule the decision; the decision then
holds until the wind moves 30° / 5 kt) — and the assignment policy picks each aircraft's runway within the configuration
(OAK: no jets on the 28s; shipped as K1-lite 2026-09-01, `docs/facility-ops-knowledge.md`); approved
multiple-runway-crossing routes enable the §3-7-2.a.3 ≤ 1,300 ft exception in rule 2; runway
assignment policy constrains rule 1's runway pick (e.g. OAK: no jets on 28L/R). Without a knowledge
file, the generic rules above stand alone.

## Non-goals v1 (explicit)

- Pushback conflict management beyond the existing `PUSH` behavior.
- Intersection-departure taxi optimization.
- Deicing/gate conflicts, metering or flow sequencing, multi-airport ground splits, vehicle ops.
- Mid-session runway-in-use changes (§3-5-1.a supervisor function).
- §3-1-4.a/b GC→LC notifications (off-designated-runway departures, intersection departures) —
  vacuous in v1 given a single facility runway-in-use and full-length-only departures; required the
  moment either non-goal is relaxed.

Rationale: none are needed to soak the taxi/runway/phase machinery, and each adds rule surface that
would obscure v1 signal.

## Verification

- TDD per rule with real airport layouts (KOAK first — richest existing test fixtures), including a
  multi-crossing route (sequential per-runway request/approve/CROSS/complete cycles) and the
  §3-10-9.b.2 Local-retains exit geometry.
- `aviation-sim-expert` re-review of the final rule *code* (this doc's rules were design-reviewed
  2026-09-01).
- Soak target for CA1: departures parking → hold-short and arrivals runway-exit → parking, N
  sim-hours, zero Hard/Progress findings on a known-good scenario.

## CA1 implementation notes (2026-09-01)

- **Rule 1** — `AnswerTaxiOutRule`: an open ready-to-taxi request (`PendingPilotRequest` Taxi with no `ParkingName`) from a parked or
  pushed-back aircraft ⇒ `TAXIAUTO <runway>`. The runway comes from `AiControllerService.RunwayInUse` (`RunwayInUseState`, one decision
  per airport, held until the weather profile changes): the `ControllerAiConfig.RunwayInUse` override for the primary airport, else
  `RunwayInUseResolver` — wind ≥ 5 kt ⇒ the runway end most nearly aligned with the magnetic surface wind (`WindLayers[0]`, runway
  true headings converted at the session's magnetic-model date), longer pavement then designator as tiebreaks; calm/variable ⇒ the
  longest pavement, its end toward any residual wind, else by designator (an arbitrary stand-in for the facility's calm-wind runway —
  at OAK that yields `12`; K1's knowledge overlay and `--runway` are the real answers).
- **Rule 2** — `RunwayCrossingRule`: `TaxiRouteProgress.NextUnclearedCrossing` finds the first uncleared runway-crossing bar ahead (an
  uncleared taxiway hold or the departure bar coming first means "not yet"); the rule acts when the aircraft is holding at it or within
  `PreClearDistanceFt` = 500 ft of it. **Nobody holds Local** (no active AI Local, no human on a Local position — `IAiStaffing.
  IsHumanHeld(AiPositionConfig)`) ⇒ combined-position semantics: `CROSS <end>` (the end nearest the aircraft, `RunwayCrossingEnd.Nearest`)
  once `RunwayCrossingGate.IsClear` — no other aircraft Departing / Landing / OnSurface / ShortFinal on the pavement, none inside the
  final gate (3 NM, or 90 s to the threshold within 10 NM, on the approach course and not climbing, below 2,500 ft AGL — an
  overflight is not an arrival), the crosser not under a hold; a merely crossing occupant elsewhere does not close it. **Local staffed**
  ⇒ one `[AI-COORD] OAK_GND requests cross runway 28R at B for N123AB` line on the aircraft's warning lane, then `CoordinationTimeout`
  after 120 s. One crossing at a time falls out of "first uncleared bar": the next bar is only pending once the previous one is cleared.
  `AutoCrossRunway` pre-clears every bar at taxi time, so it is standing approval with no special case.
- **Rule 3** stays reactive; the E2E surfaced the deadlock class it exists to catch (README findings).
- **Rule 4** — `AnswerTaxiInRule`: the taxi-in request names the pilot's parking (`ParkingName`); Ground answers `TAXIAUTO @<spot>`, or
  re-picks with the pilot's next choice when the spot has been taken meanwhile (`ArrivalParkingPicker.TakenSpots`: parked on, taxied to,
  or asked for by another open request).
- **Rule 5** — `HandToLocalRule`: `CT <Local callsign>` (from `AiPositionResolver.Catalog`) while still taxiing, no uncleared crossing
  ahead, within `HandoffDistanceFt` = 1,200 ft of the departure bar along the route — so the pilot's "holding short, ready" call
  (`HoldingShortPhase.OnStart`, roster-resolved) goes to Local, and the destination-runway hold stays Local's jurisdiction as CA0b left it.
- **The pilot side** (`Pilot/TaxiInRequest.cs`): `RunwayExitPhase.CompleteExit` sets `AircraftGroundOps.AwaitingTaxiInCall`; the idle
  phase the exit ends in (`HoldingAfterExitPhase`, or `HoldingInPositionPhase` after a pull-up crossing) makes the call three seconds
  later to whoever answers ground calls (`PilotContactRoster.ResolveFor(… "GND" …)` without the airborne transfer-SOP check — a landed
  aircraft is the cab's) — `Oakland Ground, clear of runway 28R at W, taxi to gate 29.` (AIM 4-3-21.c) — recording a Taxi request with
  the parking; any taxi clearance clears the flag. Solo Ground students get the same call.
- **Aviation review 2026-09-01 (folded in):** the gate also closes for an aircraft *over* the pavement — a go-around or
  low approach flown on the runway (phase-based) or anything inside the footprint below 1,500 ft AGL, whatever its vertical
  speed (§3-7-2.a.7.1: only a §3-10-10 altitude-restricted low approach authorizes a crossing under an aircraft, and the AI
  never asks for one); the taxi-in call waits for the tower's release (`AircraftGroundOps.ReleasedToGround`, set by a `CT`
  to a ground position or an `FCA`) whenever a *separately* staffed Local answers at the airport (AIM 4-3-14.c, 4-3-21.c) —
  a combined cab needs no release; rule 5 transfers only while someone holds Local (a combined cab transfers nothing,
  §2-1-17.a — the `--positions GC` soak issues no `CT`); the terminal request line is paced like any transmission, names
  the taxiway the route reaches the bar on (§3-1-3.a), and is repeated at every timeout instead of going silent.
