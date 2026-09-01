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
computation in rule 1 (e.g. OAK's 10-kt threshold and "SFO in SFOE ⇒ OAK 10s/12"); approved
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
