# 02 — Positions, Jurisdiction, and Handoffs

Part of [Controller AI + Soak Harness](README.md). Covers position identity, the jurisdiction query,
gate-to-gate ownership flows, precedence against the existing auto-services, partial staffing, and
GC↔LC coordination.

## Identity

Each AI position is a real `TrackOwner` (Callsign, FacilityId, Subset, SectorId, OwnerType) resolved
from ArtccConfig by `AiPositionResolver` ([01](01-architecture.md)). AI positions use the real
machinery — TRACK/HO/ACCEPT/DROP/PO for radar ownership, HFR/REL for departure releases, the RD
family for configured coordination items — so the track-sharing, consolidation, and release code
paths get exercised (that is soak coverage, not overhead), and a human can staff any one position
with everything else continuing to work.

## Jurisdiction — the missing scoping query

Nothing in the codebase answers "which position is responsible for this aircraft" today. New
`PositionJurisdiction.Resolve(aircraft, staffedPositions, layout) → AiPositionConfig?` in
`AiWorldView` — the single shared, unit-testable mapping:

- **Radar roles (Approach/Center):** trivially `Track.Owner` — the real machinery scopes for free;
  radar brains act only on tracks their identity owns.
- **Tower-cab roles** (no per-aircraft tuned-frequency field exists — `FrequencyState` is only a
  busy-meter): deterministic inference from phase family + location + `RunwayOccupancy.ClassifyBest`:
  - **Ground** owns: `AtParking` with an open taxi request, pushback phases, taxiing phases,
    holding-short **not** at the departure runway, **aircraft crossing a runway under a
    Local-approved crossing** (§3-1-3.a/c — the crosser stays on Ground's frequency; Local
    approves, Ground issues the `CROSS` and reports completion), arrivals past runway exit that
    received `CT`.
  - **Local** owns: holding short at the departure runway post-`CT`, LUAW/rolling departures,
    `ShortFinal`/`OnFinal`/landing/rollout, pattern phases, go-arounds, **and the runway itself** —
    approving/denying crossings and withholding clearances while one is in progress (§3-1-3
    opening: primary responsibility for operations on the active runway).
- The GC→LC transfer marker is the **`CT` command**, tracked in the brains' shared memo and
  re-derived after a snapshot restore as "holding short at departure runway with taxi complete ⇒
  Local's".
- Aircraft assigned to a specific human connection (`Room.AircraftAssignments`) are excluded from
  all AI jurisdiction.

Open verification (CA0): exact `CT` canonical semantics (what it does to pilot behavior and pending
requests) before relying on it as the transfer trigger.

## Gate-to-gate flows

### Departures

| Stage | Actor | Mechanism |
|---|---|---|
| Parked, ready-to-taxi check-in open | **AI Ground** | `TAXI <rwy>` (pathfinder routes; phases hold short of runways automatically) |
| Route crosses an active runway | AI Ground ↔ AI Local | Coordination bus request → `CROSS` on approval (below) |
| Holding short of departure runway | AI Ground | `CT` (contact tower); memo → handed-to-Local |
| Pre-takeoff departure instructions | AI Local (or omitted) | §3-9-3.a.1 — the departure frequency may be omitted when a SID with a published departure frequency is assigned (the normal soak case); the beacon code comes from the flight plan. A non-SID departure with no modeled frequency issuance files an anomaly |
| Held for release (`Ground.HeldForRelease`) | AI radar (or human) | Real HFR/REL machinery unchanged; AI Local never issues LUAW/CTO to a held aircraft (checks the flag first — a dispatcher rejection here would be a false anomaly) |
| Runway entry / takeoff | **AI Local** | `LUAW` / `CTO` per gates ([05](05-tower-brain.md)) |
| About 1/2 mile beyond the runway end (§3-9-3.b.1) | AI Local | `CT` to departure frequency |
| Radar acquisition | existing `ProcessDeferredAutoTrack` | **Kept** — it models NAS auto-acquire; the AI departure-radar position simply *is* the configured auto-track owner |
| Approach → Center | **AI Approach** | Real `HO`; **AI Center** ACCEPTs via its own rule after a deterministic delay |

### Arrivals (reverse)

Center ACCEPTs the inbound handoff, descends, HOs to Approach; Approach vectors and clears the
approach (CA5 — until then arrivals spawn established on final per scenario), `CT`s to tower;
**AI Local** issues `CLAND` per gates, monitors rollout (runway exit is already autonomous in
phases), `CT`s the rolled-out arrival to ground — except where clearing the landing runway requires
entering another runway, in which case Local retains the aircraft and issues the exit + crossing
itself before the `CT` (§3-10-9.b.2, both positions protecting the intersection per §3-10-9.c);
**AI Ground** then answers the taxi-in request and taxis to parking. Tower-cab positions never radar-track arrivals — the track drops per existing
surface rules; `ProcessTowerLists` and visual detection are unaffected.

## GC↔LC coordination (crossings; 7110.65 §3-1-3.a–c)

Real GC/LC crossing coordination is verbal interphone. §3-1-3 governs: Ground must obtain Local's
approval before authorizing a crossing, the coordination must name the point/intersection
(§3-1-3.a), Local's authorization must specify runway + point preceded by "cross" (§3-1-3.b), and
Ground must advise Local when the coordinated operation is complete (§3-1-3.c). YAAT's RD channels
model *departure* coordination items, not crossings, and there is no crossing-request command
today. Design:

- **AI↔AI:** a typed, deterministic `AiCoordinationBus` (in-sim, owned by `AiControllerService`) —
  `RequestCrossing(from, runway, holdPoint, callsign)` / `Approve(runway, holdPoint)` / `Deny` /
  `CrossingComplete(from, runway, holdPoint, callsign)` — with every transaction echoed as an
  `[AI-COORD]` terminal line for observability. Approval carries the runway **and** the
  point/intersection back (§3-1-3.b), and the completion report (§3-1-3.c) is what lets Local
  re-open the runway without inferring completion from occupancy; a missing completion report is
  the `CoordinationCompleteMissing` anomaly ([03](03-decision-framework.md)). The *resulting*
  `CROSS` command is what replays re-drive; the bus itself carries no replay obligation. Local
  approves/denies against **§3-7-2.a.7** ([05](05-tower-brain.md) crossing approvals), not its
  takeoff/landing gates.
- **AI Ground + human Local:** the AI cannot "call" a human. v1 rule: hold the crosser short and
  emit one paced terminal request line ("GC requests cross 28L at F for N123AB"); the human either
  issues `CROSS` themselves or the room's `AutoCrossRunway` setting handles it. Unanswered past a
  watchdog threshold → anomaly (in a solo-training context this is the student's job, so it surfaces
  as a reminder, not a bug).
- **Deliberately deferred:** promoting crossing requests to a first-class canonical command with a
  proper UI affordance. Do not block v1 on it.

The RD channels get exercised from CA5 on, when AI radar positions participate in configured
departure-coordination flows; AI positions honor `RDACK`/`RDAUTO` semantics via decision rules
rather than the server timers where they are the receiving party.

## Precedence vs the existing auto-services (the critical integration)

The server already "plays" unstaffed positions. An AI-staffed position must be treated like a
*human-staffed* one by those services, or they race the brain:

- `TickProcessor.ProcessAutoAccept`: already skips CRC-controlled TCPs
  (`PositionRegistry.IsTcpControlledByCrc`) and the solo `StudentPosition`. Add a third skip:
  `IAiStaffing.IsAiControlled(handoffPeer)`. The AI brain ACCEPTs on its own (deterministic 5–15 s
  delay from the AI RNG stream), exercising the real accept path.
- `ProcessPointoutAutoAck`: same skip; AI acknowledges pointouts by rule.
- Engine auto-CTO (`SimulationEngine.ProcessReleasedGroundDepartures`): suppressed for airports
  whose Local position is AI-staffed — the AI Local issues the CTO itself with real sequencing.
  This lives in Yaat.Sim, so `SimScenarioState` needs the staffing answer (an
  `IsLocalAiStaffed(airportId)` predicate kept current by the host).
- `ProcessDeferredAutoTrack` / `ProcessFlightPlanCreatorAutoTrack`: **unchanged** — auto-track *to*
  an AI position is exactly right (it is NAS behavior, and both already respect "don't overwrite an
  existing owner").

## Partial staffing (human takes over one position)

`IAiStaffing` (Yaat.Sim interface):

- Server implementation composes `PositionRegistry.IsTcpControlledByCrc` (with consolidation), the
  solo `StudentPosition`, and the `ControllerAiConfig` enable set. A position is AI-controlled iff
  enabled in config **and** not human-held. A human connecting to an AI position's TCP suspends that
  brain (memos retained for the session; `Reset()` on resume so intent re-derives).
- Headless implementation is pure config.

With a human neighbor, AI positions interact exactly as with any controller: `HO` and wait for a
manual ACCEPT (patience watchdog → reminder line; in soak → `HandoffUnaccepted` anomaly), ACCEPT the
human's handoffs after the deterministic delay, honor the human's HFR/REL, and never command
aircraft under the human's jurisdiction.
