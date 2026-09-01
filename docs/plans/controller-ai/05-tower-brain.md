# 05 — Tower (Local) Brain v1

Part of [Controller AI + Soak Harness](README.md). Rule set for the AI Local position (7110.65
3-9-3/4/6/7/10/11, 3-10-1/3/5/9, 3-7-2, 3-8-1). Framework mechanics in
[03](03-decision-framework.md); jurisdiction and handoff flow in [02](02-positions-and-handoffs.md).
Reviewed against the local 7110.65 reference by `aviation-sim-expert` 2026-09-01; corrections folded
in.

## Rules

### Takeoff clearance (`CTO`) — all gates must pass

0. For a Super or Heavy: the aircraft is stopped at the hold short or established in position —
   `CTO` is never issued to a Super/Heavy still in motion toward the runway, which would imply a
   rolling takeoff (§3-9-6.c).
1. Not `Ground.HeldForRelease` (release is the radar side's call via the real HFR/REL machinery).
2. Runway surface clear per `RunwayOccupancy.ClassifyBest`: no Departing / Landing / OnSurface /
   Crossing occupant (`OccupiesSurface`).
3. Preceding departure airborne past the runway end **or** beyond the §3-9-6.a same-runway distance
   table (3,000 / 4,500 / 6,000 ft by **SRS Category I/II/III** per the §3-9-6 NOTE and FAA Order
   JO 7360.1), resolved via the existing `SameRunwaySeparation.ResolveSrsCategory`. SRS categories
   are *not* CWT classes — CWT drives the wake timers in gate 4 only.
4. Wake interval per §3-9-6.f/g, keyed on **CWT class**, timed from the preceding aircraft's
   **start of takeoff roll** (§3-9-6.e NOTE 2): 3 min behind Cat A (Super); 2 min behind Cat B or D
   (upper heavy, B757); 2 min for a Cat E–I follower behind Cat C (lower heavy); 2 min for a Cat I
   (light) follower behind Cat E (upper large — the most common mixed-GA/airline case, §3-9-6.g).
   v1 simplification: the 2-min interval behind Cat C applies to any follower (conservative vs
   §3-9-6.f.3, which exempts Cat B–D followers).
5. No arrival inside a final gate sized to the §3-10-3.a.2 obligation the arrival will face:
   **2 NM if the departure is already in position (LUAW), 5 NM if it is still at the hold short**;
   add 1 NM for an SRS Category III departure. §3-9-5 explicitly *permits* clearing before
   separation exists given reasonable assurance — v1 declines that latitude, which is conservative
   in the strict sense.
6. All runways along this aircraft's taxi route leading to the departure runway have been crossed
   (§3-9-10.d). v1 does not implement the §3-9-10.e combined cross-and-clear form; a geometry that
   requires it files an anomaly.

v1 simplification on intersection departures: full-length departures assumed — the §3-9-7
intersection-departure intervals (a **separate rule set**, not an increment on §3-9-6; e.g.
§3-9-7.a.1's 3-min Cat I behind a departing Cat F/G/H has no full-length analogue) are out of
scope, and §3-1-4.b's "notify Local of any aircraft taxied to an intersection for takeoff" is
likewise unmodeled since AI Ground never assigns one.

### Line up and wait (`LUAW`, §3-9-4) — conservative subset

Only when takeoff is blocked *solely* by the arrival gate, **and** all of:

- No aircraft cleared to land / touch-and-go / stop-and-go / low approach / option on that runway
  (§3-9-4.c.1(b) — v1 does not model the full-core-alert safety-logic relief under c.2).
- Nearest arrival ≥ 4 NM final; single-runway logic; never LUAW behind LUAW (§3-9-4.h); never with
  a crossing in progress.
- **LUAW is never used to absorb a wake interval** — §3-9-6.d prohibits it for a small aircraft
  behind a departing Super/Heavy, and v1 does not distinguish (blanket ban, conservative).
- Every LUAW carries traffic information on the closest same-runway arrival within 6 flying miles
  (§3-9-4.a, §3-9-4.d); no conditional phrasing such as "behind landing traffic" (§3-9-4.a,
  §3-7-1.a).

Non-goal: the §3-9-4.a NOTE 90-second LUAW-duration expectation.

### Landing clearance (`CLAND`)

Arrival OnFinal/ShortFinal → `CLAND` when the runway is clear, or the preceding occupant satisfies
the applicable §3-10-3.a landmark exception:

- Behind a **lander**: §3-10-3.a.1 — 3,000 / 4,500 ft, Category I/II only; **a Category III on
  either side requires the preceding lander clear of the runway**, and the exception is
  sunrise-to-sunset only.
- Behind a **departure**: §3-10-3.a.2 — 3,000 / 4,500 / 6,000 ft.

Both resolved via the existing `SameRunwaySeparation` helpers. v1 does **not** anticipate
separation (§3-10-6): it clears only on an actually-satisfied gate.

Never sit on a clearance the gates permit — §3-10-1.f makes the landing clearance part of the
landing information owed to every arrival, and the §3-10-1.a NOTE shows the correct alternative
when it is not yet issuable ("continue, expect landing clearance two mile final"), i.e. silence is
not an option. (Not §3-10-8, which prohibits withholding as a sanction for an apparent 14 CFR
violation — unmodeled and irrelevant to v1.)

### Go-around (`GA`) — top-priority safety rule, always active

Withheld clearance + arrival inside a floor (~1 NM / short final) while the §3-10-3.a gate is
unsatisfied → `GA` (§3-8-1 phraseology), **re-evaluated every tick until touchdown**, not tripped
once at the floor. This includes a LUAW occupant — v1 always sends the arrival around; §3-10-5.e.2
would permit landing over a LUAW aircraft at a full-core-alert facility in ≥ 800 ft / 2 SM weather,
which v1 does not model (the governing pair is §3-10-5.e.1/e.2 with the mirror at
§3-9-4.c.1(a)/c.2).

The go-around always goes to the arrival: per §3-9-11, once takeoff roll has begun a takeoff
clearance may be cancelled **only for safety**, never for sequencing (§3-9-11 NOTE). A rolling
departure is still cancelled for a genuine safety trigger (incursion on its runway) — the bar is
motive, not aircraft state.

### Crossing approvals

Approve/deny AI Ground's bus requests against **§3-7-2.a.7**, not the takeoff/landing gates:

- During departure operations: the departure must have passed the requested crossing point or be
  observed in a turn (§3-7-2.a.7.1).
- During arrival operations: the crossing must complete before the arrival crosses the landing
  threshold, or the arrival must have completed its landing roll and be established short of / past
  the crossing point (§3-7-2.a.7.2).

v1 estimates crossing time from the crosser's taxi speed and the runway width plus hold-bar
offsets, with a fixed safety margin; if the estimate is unavailable the request is denied. Approval
carries the runway **and** the point/intersection (§3-1-3.b); see
[02](02-positions-and-handoffs.md).

### Frequency changes (`CT`)

Departure **about 1/2 mile beyond the runway end** and no further Local communication required →
`CT` departure frequency (§3-9-3.b.1). Non-goal: the §3-9-3.b.2 2,500 ft AGL restriction for
military turboprop/turbojet types (no such types in v1 soak traffic). Arrival clear of the runway →
`CT` ground — except the §3-10-9.b.2 case where Local retains the aircraft
([04](04-ground-brain.md) rule 4).

### VFR departures

Same as IFR minus release gating.

## Facility-knowledge overlay

Where a `FacilityOps` file exists ([10](10-facility-knowledge.md)), it refines these rules: release
rules tighten CTO gate 1 (which departures are auto-released vs require verbal CFR, per
configuration — e.g. OAK's NIMI/NUEVO and heading-departure exceptions); missed-approach tables
replace the generic go-around instruction with the SOP heading/altitude per runway/configuration
(and name the radar sector to coordinate with); separation authorizations (e.g. OAK's 2.5 NM on
runway 30 final within 10 NM) and same-runway quirks (OAK 10/12 as one runway for jet departures)
adjust the gates; pattern preferences drive CA7 pattern management. Where knowledge and a 7110.65
gate disagree, the more conservative wins and the conflict files an anomaly. Without a knowledge
file, the generic rules above stand alone.

## Non-goals v1 (explicit)

- Intersecting/converging runway operations (3-9-8/9, 3-10-4) — v1 treats runways independently and
  files an anomaly (refusing to control) for scenarios whose geometry requires dependency logic.
- LAHSO; opposite-direction operations.
- Pattern management (TG, closed traffic, EF/ERB entries) — a large rule family of its own; v1.1.
- Helicopters (3-11); overhead maneuvers; runway change mid-session; ATIS management.
- Anticipated separation (§3-9-5 permission / §3-10-6) beyond the fixed conservative gates above.
- Intersection departures (§3-9-7) and the §3-9-10.e combined cross-and-clear form.
- Full-core-alert safety-logic relaxations (§3-9-4.c.2, §3-10-5.e.2).

Rationale: v1's job is running thousands of vanilla departure/arrival cycles through the phase
machinery. Each excluded item multiplies rule interactions without adding much *sim* code-path
coverage, and several (LAHSO, converging ops) need geometry the layout data may not model yet.

## Verification

- TDD per gate with real layouts: SRS distance table, occupancy gate, wake timers (incl. the
  Cat I-behind-Cat E §3-9-6.g case), CLAND a.1-vs-a.2 criteria, GA floor as a continuous predicate,
  LUAW guards incl. the cleared-arrival prohibition — each as a failing-test-first pair.
- `aviation-sim-expert` re-review of the final rule *code* (this doc's rules were design-reviewed
  2026-09-01).
- Soak target for CA2: full gate-to-gate departure cycles plus pre-positioned-final arrivals — the
  first many-sim-hour full-loop soak.
