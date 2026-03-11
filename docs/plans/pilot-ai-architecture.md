# Pilot AI Architecture Design Document

## 1. Executive Summary

This document defines the behavioral architecture for YAAT's pilot AI — the system that drives simulated aircraft in response to ATC instructions and according to realistic pilot decision-making patterns. It covers the plan-based execution model, clearance gating, the three control axes, communication behavior, plan modification rules, and how the AI layer builds on top of the existing RPO-mode infrastructure.

The design is grounded in FAA Order JO 7110.65 (Air Traffic Control) and the Aeronautical Information Manual (AIM), with specific section references throughout.

**Relationship to existing code:** YAAT already has a mature RPO-mode simulation with 37 flight phases, a command dispatch system, control targets, flight physics, and clearance gating. This document describes the **AI layer that sits on top of that foundation** — adding autonomous plan generation, proactive communication, and ATC instruction interpretation. It targets Milestone 10 (Automated Pilot Logic).

---

## 2. Realism Validation of the Design Vision

### 2.1 What Is Accurate

The six-point vision is fundamentally sound and maps well to how real pilots think:

**Plan-based architecture.** Real pilots operate from a mental model that is essentially a plan: departure procedure, en route, arrival, approach. This is formalized in the IFR system through the CRAFT clearance format (Clearance limit, Route, Altitude, Frequency, Transponder) per AIM 5-2-5. The plan-based model is correct.

**Clearance-gated steps.** This is the core of ATC: pilots may not enter a movement area, cross a runway, take off, or land without explicit clearance. Per 7110.65 3-7-2 (taxi clearance), 3-9-4 (LUAW), 3-9-6 (takeoff clearance), 3-10-5 (landing clearance), this is regulatory, not convention. **Already implemented:** `ClearanceType` enum and `ClearanceRequirement` on phases gate takeoff, landing, runway crossing, and LUAW.

**Three control axes.** Heading, altitude, speed are indeed the three primary control dimensions ATC uses to manage traffic. Per 7110.65 5-6-2 (vectoring methods), 4-5-7 (altitude information), and 5-7-1 (speed adjustment), these are the three independent instruction categories controllers issue. **Already implemented:** `ControlTargets` with heading, altitude, speed, plus navigation route.

**Parallel actions within sequential steps.** ATC routinely issues compound clearances: "Fly heading 200, climb and maintain 5,000" — two simultaneous actions. Conditional sequential blocks ("upon reaching 4,000 proceed direct DARBY") are also a real-world pattern per 7110.65 5-6-2. **Already implemented:** Phases set multiple `ControlTargets` simultaneously; `CommandBlock` triggers handle conditional execution.

**Conditional/contingency blocks.** The DA/DH decision is precisely an if/else: if runway environment in sight, land; else, execute missed approach per AIM 5-5-5. This is correct and critical for training realism.

**Proactive communication.** Pilots do initiate contact and make requests. Per AIM 4-2-3, pilots make initial contact when entering a new frequency. Pilots holding short will request taxi or crossing clearance. The interval-based re-request model matches real pilot behavior, though the intervals and triggering conditions need refinement (see Section 4).

### 2.2 What Needs Correction or Refinement

**"Lost comms philosophy" framing.** The default plan should be modeled as "default plan with clearance gates," not as a lost-comms philosophy. Lost communications procedures per 14 CFR 91.185 and AIM 6-4-1 are specific regulatory procedures that only apply when two-way radio communication is actually lost. The pilot AI should model:

1. **Default intent** — the pilot has an intended sequence of actions (depart, fly route, land at destination). This drives the plan.
2. **Lost communications procedures** — a separate, distinct behavior mode that activates only when communication is truly lost (maintain assigned altitude or MEA, route: AVE-F, etc.).

**RPO mode vs AI mode.** The architecture must support both modes cleanly:

- **RPO mode** (current, Milestones 0–9): Plan steps are driven by explicit RPO commands. The phase system handles flight dynamics, clearance gating, and autonomous sequences (takeoff roll, landing rollout, pattern legs). Commands interact with phases via the `CommandAcceptance` pattern (Allowed, Rejected, ClearsPhase).
- **AI mode** (Milestone 10): The pilot AI interprets ATC instructions in natural language, maintains its own plan, and makes proactive decisions.

The existing phase system, control targets, and command dispatch form the execution substrate for both modes.

---

## 3. Gap Analysis — What M10 Must Add

The following gaps describe capabilities that the AI layer needs beyond what RPO mode already provides. Items that have already been implemented are noted.

### 3.1 Already Implemented (No Work Needed)

These were identified as gaps in the original design but have since been built:

- **Holding patterns:** `HoldingPatternPhase` with AIM 5-3-8 entry determination (direct, teardrop, parallel), configurable fix/course/turn direction/leg length.
- **Speed restrictions (14 CFR 91.117):** 250 KIAS below 10,000 MSL enforced in `FlightPhysics.UpdateSpeed()`. SID/STAR procedural speed restrictions enforced via `ApplyFixConstraints()` when via mode is active.
- **Taxi procedures:** `TaxiingPhase` with A* pathfinding, hold-short gates at runway intersections, `HoldingShortPhase` with clearance requirements, `CrossingRunwayPhase`, progressive taxi via edge-by-edge route following.
- **Wake turbulence categories:** Aircraft categories (Small, Large, Heavy, Helicopter) with per-category performance constants in `CategoryPerformance`.
- **Visual approaches:** `CVA` command with 3 approach paths, `RFIS`/`RTIS` traffic-in-sight reporting, `VisualDetection` with bank angle occlusion.

### 3.2 Gaps That M10 Must Address

#### 3.2.1 Autonomous Plan Generation

The current system has no `Intent` or `Plan` object. `PhaseList` serves as an implicit plan driven by RPO commands. M10 needs:

- An `Intent` model describing the pilot's high-level goal (depart IFR, arrive IFR, land VFR, etc.).
- A plan generator that produces a phase sequence from intent + scenario data.
- Plan modification logic for ATC instruction handling (replace, insert, temporary override).

#### 3.2.2 Missed Approach Procedures

Per AIM 5-5-5, at MAP or DH without sufficient visual reference:

1. Initiate climb on runway heading (or as published).
2. Follow the published missed approach procedure (climb to altitude, turn, proceed to holding fix).
3. Contact ATC: "Missed approach, [callsign], [intentions]."
4. ATC may: assign vectors, clear for another approach, clear to alternate.

Currently `GoAroundPhase` handles the immediate go-around maneuver but does not fly a published missed approach procedure. M10 needs a `MissedApproachPhase` that reads CIFP missed approach legs and builds a phase sequence from them.

#### 3.2.3 Contingency System

No contingency system exists. The AI needs condition-triggered plan modifications:

- At DA/DH: if no runway environment in sight → missed approach.
- On approach without landing clearance: at configurable distance → go around.
- Holding with EFC time: if EFC expires without further clearance → lost comms procedures.

#### 3.2.4 Expectations Layer

Only `ExpectedApproach` (a string on `AircraftState`) exists. The AI needs structured expectations for:

- Expected approach type and runway.
- Expected altitude (from "expect" instructions).
- Expected further clearance time (EFC in holding).

Per AIM 5-4-1.a.1, "expect" values are planning information only, NOT clearances. They become actionable only in lost-comms mode per 14 CFR 91.185(c)(2)(iii).

#### 3.2.5 ATIS and Frequency Management

Not implemented. Critical for AI realism:

- Pilots obtain ATIS before contacting approach/tower (AIM 4-1-13).
- ATIS information codes (Alpha through Zulu) must be referenced on initial contact.
- Frequency changes: pilot acknowledges, switches promptly (AIM 4-2-3.4).
- Monitor vs. active frequencies: "Monitor [frequency]" means listen only; "Contact [facility] [frequency]" means check in.

Each pilot needs a `FrequencyState` tracking: current frequency, whether communication is established, current ATIS code, and pending frequency changes.

#### 3.2.6 Proactive Communication

The current `PendingNotifications`/`PendingWarnings` system is display-only text. The AI needs:

- Phrase generation (natural language readbacks, requests, reports).
- Proactive request logic with timing intervals (see Section 4.5).
- "Unable" responses when instructions exceed aircraft capabilities.
- Frequency management (check-in, handoff acknowledgment).

#### 3.2.7 Speed Restriction Stack

The current implementation uses `SpeedFloor`/`SpeedCeiling` on `ControlTargets` plus hardcoded 250kt-below-10k. For AI mode, a proper stack is needed to manage competing restrictions from multiple sources (regulatory, procedural, ATC-assigned, holding). See Section 7.1.8.

#### 3.2.8 "Expect" Instructions

Per AIM 5-4-1.a.1, "expect" altitudes/speeds are planning information only, NOT clearances. The plan should have an expectations layer that stores anticipated clearances but does not execute them. In lost-comms mode, expects become the default plan.

#### 3.2.9 Emergency Procedures

Not covered but relevant for advanced training:

- Declaring emergency: "MAYDAY MAYDAY MAYDAY" or "PAN PAN PAN PAN PAN PAN" (AIM 6-3-1).
- Minimum fuel: "MINIMUM FUEL" advisory (not an emergency declaration) per AIM 5-5-15.
- Fuel emergency: "DECLARING FUEL EMERGENCY" per AIM 5-5-15.

Defer to post-M10. The architecture should allow an `EmergencyPhase` to preempt the entire plan.

---

## 4. Communication Realism

### 4.1 Initial Contact / Check-In by Phase of Flight

Per AIM 4-2-3, initial contact format is: `[Facility name], [Full callsign], [Position/altitude], [Request/info]`

Phase-specific phraseology:

| Phase | Example |
|-------|---------|
| Ground (IFR clearance) | "SFO Clearance, United 123, gate B22, IFR to Los Angeles, with information Alpha." |
| Ground (taxi) | "SFO Ground, United 123, gate B22, ready to taxi, information Alpha." |
| Tower (departure) | "SFO Tower, United 123, holding short runway 28 right, ready for departure." |
| Departure | "NorCal Departure, United 123, one thousand two hundred climbing three thousand." |
| Center (initial) | "Oakland Center, United 123, flight level three three zero." |
| Approach (arrival) | "NorCal Approach, United 123, descending via the SERFR One arrival, information Bravo." |
| Tower (inbound IFR) | "SFO Tower, United 123, ILS 28 right." |
| Tower (inbound VFR) | "SFO Tower, Cessna 12345, ten miles south, inbound for landing with Alpha." |

Per AIM 5-4-1.a.5.b, when cleared "descend via" a STAR, initial contact with each new frequency must include: altitude leaving, "descending via [STAR name]," runway transition or landing direction if assigned, and any assigned restrictions not published on the procedure.

### 4.2 When Pilots Request vs. Wait

**Pilots initiate requests for:**
- IFR clearance (at gate or ready to copy)
- Taxi clearance (ready to push/taxi)
- Takeoff clearance (holding short, ready for departure)
- Landing clearance (only if not received by ~1 nm final for VFR; IFR pilots expect it to be issued, and go around if not received by DA/DH)
- Altitude change (en route, when desiring different altitude)
- Direct routing (when operationally advantageous)
- Frequency change (when leaving a controller's area)
- Weather deviations

**Pilots wait for (do not request):**
- Approach clearance (controller issues when ready to sequence)
- Vectors (controller-initiated)
- Speed assignments (controller-initiated)
- Handoff/frequency change (controller-initiated)
- LUAW (controller-initiated per 7110.65 3-9-4)

**Special case — VFR pilots in the pattern:**
- Report pattern positions (downwind, base, final) per local procedures.
- Tower typically issues sequencing and landing clearance proactively.
- If no clearance by short final (~0.5 nm), VFR pilot should query or go around.

### 4.3 Readback Requirements

Per AIM 4-4-7, pilots must read back:

1. **All altitude assignments** including restrictions ("Climb and maintain flight level three three zero, United 123").
2. **All heading assignments** ("Turn left heading two seven zero, United 123").
3. **All runway assignments** including number and L/R/C.
4. **All hold short instructions** — mandatory per 7110.65 3-7-2 ("Hold short runway 28 right, United 123").
5. **Altimeter settings** (AIM 4-2-3).
6. **All frequency changes** ("One two four point six five, United 123" or longer form per AIM 4-2-3.4).

**Items that get "roger" or "wilco" (not full readback):**
- Traffic advisories ("Roger, looking" or "Traffic in sight").
- Weather information.
- "Resume own navigation" — "Wilco" or "Roger."
- "Radar contact" — no readback needed; acknowledge with callsign.
- "Radar service terminated" — "Roger."

### 4.4 "Unable" Responses

Per AIM 5-5-2.a.3, pilots may request amendment when a clearance is:
- Not fully understood ("Say again").
- Considered unsafe ("Unable, [reason]").
- Beyond aircraft capabilities ("Unable, aircraft performance").

Common unable scenarios for the AI:
- Speed assignment below minimum safe speed.
- Altitude assignment above service ceiling.
- Heading toward known terrain (terrain awareness).
- Approach type aircraft is not equipped for (e.g., ILS with no ILS receiver).

### 4.5 Proactive Communication Timing

| Situation | First request timing | Repeat interval | Escalation |
|-----------|---------------------|-----------------|------------|
| Holding short, ready for departure | Immediate when ready | 60 sec | 90 sec per 7110.65 3-9-4 note |
| Holding short, ready to cross | Immediate when reaching hold line | 60 sec | N/A |
| Inbound, need landing clearance (VFR) | Report position on downwind | Report base turn | Go around if none by 0.5 nm final |
| Inbound, need landing clearance (IFR) | N/A (ATC responsible) | N/A | Missed approach at DA/MDA |
| Waiting for pushback | Immediate when ready | 90 sec | N/A |
| On frequency, no initial contact made | Immediate on frequency change | 15 sec | 30 sec |

---

## 5. Plan Modification Rules

### 5.1 Instruction Types and Their Effect on the Plan

#### 5.1.1 Replacing Part of the Plan

**"Fly heading 270"** — Replaces the current heading target. If the pilot was navigating to a waypoint, this overrides it and puts the pilot on a vector. Per 7110.65 5-6-2, when vectored off a procedure, all published altitude/speed restrictions are canceled unless re-issued. **Already implemented:** RPO `FH`/`TL`/`TR` commands set `ControlTargets.TargetHeading` and clear `NavigationRoute`. `FlightPhysics.ClearProcedureState()` cancels via-mode restrictions.

**"Descend and maintain 5,000"** — Replaces the altitude target. Per AIM 4-4-10.4, pilot should begin descent promptly. **Already implemented:** RPO `DM`/`CM` commands set `ControlTargets.TargetAltitude`.

**"Cleared ILS runway 28 right approach"** — Replaces the entire approach/arrival portion of the plan with the published approach procedure. **Already implemented:** Approach commands (`JFAC`/`CAPP`/`JAPP`/`PTAC`) build `ApproachNavigationPhase` → `InterceptCoursePhase` → `FinalApproachPhase` → `LandingPhase` sequence from CIFP data.

**"Cleared direct SUNOL"** — Replaces the routing between current position and SUNOL. Subsequent route remains unless also amended. **Already implemented:** `DCT` command sets `NavigationRoute`.

#### 5.1.2 Inserting Into the Plan

**"Cross SUNOL at or above 5,000"** — Adds an altitude constraint at a specific fix without changing the route. The constraint inserts into the current plan's constraint list.

**"After SUNOL, turn left heading 180"** — Conditional instruction that inserts a heading change triggered by passing a fix. **Already implemented:** `CommandBlock` triggers with `BlockTriggerType.ReachFix`.

#### 5.1.3 Temporary Instructions

**"Turn right heading 090 for traffic, expect direct SUNOL in 2 miles"** — The heading vector is temporary. The pilot knows to expect a return to their route. Per 7110.65 5-6-2.4, the controller should inform the pilot when they intend to clear back onto the procedure.

**"Resume own navigation"** or **"Resume [procedure name]"** per 7110.65 5-6-2.5 — terminates the temporary vector and returns to the planned route. If the procedure had published restrictions, they must be re-issued or a "climb via"/"descend via" must be given per 7110.65 5-6-2.6.

#### 5.1.4 "Expect" Instructions (Planning Information)

**"Expect ILS runway 28 right approach"** — Not a clearance. Stored as an expectation. Per AIM 5-4-4, this is advance information to aid planning. The pilot may begin briefing the approach but must not descend on it or fly it until actually cleared.

**"Expect further clearance at 1530"** — Per 7110.65 4-6-1.c, this is the EFC time in a hold. Used only for lost-comms planning per 14 CFR 91.185.

#### 5.1.5 Amended Clearances

**"Climb and maintain flight level 350"** — The new instruction supersedes the previous one. The plan updates the assigned altitude. Per AIM 4-4-4, amended clearances may be issued at any time to avoid confliction.

**Full re-clearance (reroute):** "Cleared to SFO airport via direct SUNOL, V25 CEDES, SERFR1 arrival, maintain flight level 280." This replaces the entire route from current position to destination.

### 5.2 Plan Modification Algorithm

When an ATC instruction is received in AI mode:

1. **Classify the instruction** by affected axis (heading, altitude, speed) and scope (immediate, conditional, temporary, expect-only).

2. **For immediate instructions:** Update the relevant `ControlTargets`. If the instruction conflicts with the current phase (e.g., heading vector during waypoint navigation), the AI clears the phase system and takes direct control, same as RPO mode.

3. **For conditional instructions ("at [fix/altitude], [action]"):** Create a `CommandBlock` with the appropriate `BlockTrigger` and queue it. This reuses the existing command queue infrastructure.

4. **For clearances that replace plan segments:** Rebuild the affected portion of the `PhaseList`. For example, an approach clearance replaces remaining phases with the approach sequence, same as the existing approach command handlers do.

5. **For "resume" instructions:** Restore the navigation route from the plan's stored procedure. Re-apply via-mode restrictions per 7110.65 5-6-2.6.

6. **For "expect" instructions:** Store in the expectations layer. Do not modify the active plan. In lost-comms mode, promote expectations to plan actions.

---

## 6. Current State Model (Implemented)

### 6.1 Flight Phases (37 Phases)

The existing phase hierarchy, organized by category:

```
Phase (abstract base)
  │
  ├── Ground Phases (9)
  │     AtParkingPhase
  │     PushbackPhase
  │     PushbackToSpotPhase          (A* pathfinding to named spot)
  │     TaxiingPhase                 (edge-by-edge with hold-short insertion)
  │     HoldingShortPhase            (clearance-gated: cross/LUAW/takeoff)
  │     HoldingInPositionPhase
  │     HoldingAfterPushbackPhase
  │     CrossingRunwayPhase
  │     FollowingPhase               (ground follow traffic)
  │
  ├── Tower Phases (14)
  │     LineUpPhase
  │     LinedUpAndWaitingPhase       (clearance-gated: takeoff)
  │     TakeoffPhase
  │     InitialClimbPhase
  │     FinalApproachPhase           (glideslope intercept + descent)
  │     LandingPhase
  │     RunwayExitPhase
  │     HoldingAfterExitPhase
  │     GoAroundPhase
  │     TouchAndGoPhase
  │     StopAndGoPhase
  │     LowApproachPhase
  │     MakeTurnPhase                (360/270 degree turns for spacing)
  │     STurnPhase                   (S-turn spacing maneuver)
  │
  ├── Pattern Phases (7)
  │     PatternEntryPhase            (joining from outside with lead-in)
  │     UpwindPhase
  │     CrosswindPhase
  │     DownwindPhase
  │     BasePhase
  │     MidfieldCrossingPhase
  │     (PatternBuilder constructs circuit geometry)
  │
  ├── Approach Phases (3)
  │     ApproachNavigationPhase      (IAF→IF→FAF with CIFP fix sequences)
  │     InterceptCoursePhase         (establishes on localizer/final course)
  │     HoldingPatternPhase          (AIM 5-3-8 with entry determination)
  │
  └── Helicopter Phases (4)
        HelicopterTakeoffPhase
        HelicopterLandingPhase
        AirTaxiPhase
        (uses standard pattern phases with helicopter geometry)
```

**Phases M10 may add:**
- `MissedApproachPhase` — flies published missed approach legs from CIFP data
- `EnRouteCruisePhase` — for long-haul AI flights between facilities
- `VfrPatternExitPhase` — departure from pattern to VFR corridor

### 6.2 Command Acceptance Pattern

Each phase controls which commands it accepts via `CanAcceptCommand()`:

```
CommandAcceptance
  Allowed       — command accepted, phase continues
  Rejected      — command rejected (e.g., can't turn during takeoff roll)
  ClearsPhase   — command accepted and exits the phase system
```

This is how RPO commands interact with autonomous phase sequences. The AI layer must respect this same pattern — when the AI generates an action equivalent to a command, it should check acceptance before applying.

### 6.3 Clearance States (Implemented)

```
ClearanceType (enum)
  LineUpAndWait
  ClearedForTakeoff
  ClearedToLand
  ClearedForOption
  ClearedTouchAndGo
  ClearedStopAndGo
  ClearedLowApproach
  RunwayCrossing
```

Clearances are stored on `PhaseList`:
- `DepartureClearance` — pre-issued during taxi, consumed at runway hold-short
- `LandingClearance` — issued on downwind/base, consumed by `FinalApproachPhase`
- `ActiveApproach` — approach clearance with CIFP procedure data

### 6.4 What M10 Adds to State

The pilot's complete state in AI mode extends the existing state:

```
PilotState (AI mode additions)
  Intent:             (high-level goal driving plan generation)
  Expectations:       (expect instructions not yet cleared)
  FrequencyState:     (current frequency, comm established, ATIS code)
  CommunicationState: (check-in pending, awaiting response, etc.)
  PendingClearances:  (clearances being actively waited for, with timing)
  VerbalQueue:        (radio transmissions waiting to be sent)
```

The existing `PhaseList`, `ControlTargets`, `AircraftState`, and `ClearanceRequirement` infrastructure remains unchanged.

---

## 7. Data Model for M10 Additions

### 7.1 New Types

The following types must be added for AI mode. They build on top of existing infrastructure — no changes to the Phase base class, `ControlTargets`, or `FlightPhysics` are required.

#### 7.1.1 Intent

```csharp
/// <summary>
/// The pilot's high-level goal. Drives plan generation.
/// </summary>
public class Intent
{
    public required IntentType Type { get; init; }
    public string? Runway { get; init; }
    public string? DestinationAirport { get; init; }
    public string? DepartureAirport { get; init; }
    public string? Route { get; init; }
    public int? CruiseAltitude { get; init; }
    public string? AssignedSid { get; init; }
    public string? AssignedStar { get; init; }
    public string? ExpectedApproach { get; init; }
    public PatternDirection PatternDirection { get; init; } = PatternDirection.Left;
    public string? ParkingPosition { get; init; }
}

public enum IntentType
{
    TaxiToRunway,
    TaxiToParking,
    LandVfr,
    TouchAndGo,
    LowApproach,
    StopAndGo,
    DepartVfr,
    TransitionVfr,
    DepartIfr,
    ArriveIfr,
    PracticeApproach,
    EnRouteCruise,
    MissedApproach,
    GoAround,
    HoldAsPublished,
    LostCommunications,
}
```

#### 7.1.2 Contingency

```csharp
/// <summary>
/// A contingency that triggers when conditions are not met. Evaluated each tick when
/// applicable phases are active.
/// </summary>
public class Contingency
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<Type> ApplicablePhases { get; init; }
    public required Func<PhaseContext, bool> Condition { get; init; }
    public required Action<PhaseContext> OnTrigger { get; init; }
    public bool Triggered { get; set; }
}
```

#### 7.1.3 Expectations

```csharp
/// <summary>
/// An "expect" instruction — planning information only, not a clearance. Becomes actionable
/// only in lost-comms mode.
/// </summary>
public class Expectation
{
    public required string Name { get; init; }
    public required ExpectationType Type { get; init; }
    public string? ApproachType { get; init; }
    public string? Runway { get; init; }
    public int? Altitude { get; init; }
    public int? Speed { get; init; }
    public string? Route { get; init; }
    public TimeSpan? EfcTime { get; init; }
}

public enum ExpectationType
{
    Approach,
    Altitude,
    Speed,
    Route,
    FurtherClearance,
}
```

#### 7.1.4 Pending Clearances (AI Mode)

```csharp
/// <summary>
/// Clearance the pilot is actively waiting for. Extends the existing ClearanceRequirement
/// with timing and communication behavior.
/// </summary>
public abstract class PendingClearance
{
    public required string Name { get; init; }
    public required ClearanceType Type { get; init; }
    public long WaitStartTimeMs { get; set; }
    public bool Received { get; set; }
    public abstract bool IsStopped { get; }
}

/// <summary>
/// Clearance waited for while stopped (e.g., holding short). Sends periodic reminders.
/// </summary>
public class StoppedPendingClearance : PendingClearance
{
    public override bool IsStopped => true;
    public long ReminderIntervalMs { get; init; } = 60_000;
    public long? LastReminderTimeMs { get; set; }
    public string? ReminderMessage { get; init; }
}

/// <summary>
/// Clearance waited for while flying (e.g., landing clearance on approach). Has a contingency
/// if not received in time.
/// </summary>
public class FlyingPendingClearance : PendingClearance
{
    public override bool IsStopped => false;
    public required Contingency Contingency { get; init; }
    public required string RequestMessage { get; init; }
    public IReadOnlyList<long> RequestIntervalsMs { get; init; } = [60_000, 30_000];
    public int NextRequestIndex { get; set; }
    public long? LastRequestTimeMs { get; set; }
}
```

#### 7.1.5 Communication State

```csharp
public enum CommunicationState
{
    NotOnFrequency,
    Monitoring,
    CheckInPending,
    Established,
    AwaitingResponse,
    LostComms,
}
```

#### 7.1.6 Frequency State

```csharp
public class FrequencyState
{
    public string? CurrentFrequency { get; set; }
    public CommunicationState CommState { get; set; } = CommunicationState.NotOnFrequency;
    public string? CurrentAtisCode { get; set; }
    public string? PendingFrequencyChange { get; set; }
    public long? LastTransmissionTimeMs { get; set; }
}
```

#### 7.1.7 Verbal Actions

```csharp
/// <summary>
/// A radio transmission the pilot needs to make. Queued by the AI and transmitted when the
/// frequency is clear.
/// </summary>
public class VerbalAction
{
    public required VerbalActionType Type { get; init; }
    public required string Message { get; init; }
    public required int Priority { get; init; }
    public long QueuedAtMs { get; set; }
}

public enum VerbalActionType
{
    CheckIn,
    Readback,
    Request,
    Report,
    Unable,
    Acknowledge,
    Emergency,
}
```

#### 7.1.8 Speed Restriction Stack

Replaces the current `SpeedFloor`/`SpeedCeiling` + hardcoded 250kt logic with a layered system:

```csharp
/// <summary>
/// Manages the stack of speed restrictions. Lower layers (regulatory) always apply. Higher
/// layers (ATC-assigned) override procedural restrictions.
/// </summary>
public class SpeedRestrictionStack
{
    private readonly List<SpeedRestriction> _restrictions = [];

    public SpeedLimit GetEffectiveLimit(double altitudeMsl, bool inClassB, bool inClassD, double? distanceFromPrimaryAirportNm)
    {
        double maxSpeed = double.MaxValue;
        double minSpeed = 0;

        foreach (var r in _restrictions.Where(r => r.IsActive))
        {
            if (r.MaxSpeed.HasValue)
                maxSpeed = Math.Min(maxSpeed, r.MaxSpeed.Value);
            if (r.MinSpeed.HasValue)
                minSpeed = Math.Max(minSpeed, r.MinSpeed.Value);
        }

        if (altitudeMsl < 10_000)
            maxSpeed = Math.Min(maxSpeed, 250);
        if (inClassB && altitudeMsl < 10_000)
            maxSpeed = Math.Min(maxSpeed, 200);
        if (inClassD && distanceFromPrimaryAirportNm is <= 4.0)
            maxSpeed = Math.Min(maxSpeed, 200);

        return new SpeedLimit(minSpeed, maxSpeed);
    }

    public void Push(SpeedRestriction restriction) => _restrictions.Add(restriction);
    public void Remove(SpeedRestriction restriction) => _restrictions.Remove(restriction);
    public void ClearAtcRestrictions() => _restrictions.RemoveAll(r => r.Source == SpeedRestrictionSource.AtcAssigned);
}

public class SpeedRestriction
{
    public required string Name { get; init; }
    public required SpeedRestrictionSource Source { get; init; }
    public double? MaxSpeed { get; init; }
    public double? MinSpeed { get; init; }
    public bool IsActive { get; set; } = true;
}

public enum SpeedRestrictionSource
{
    Regulatory,
    Procedural,
    AtcAssigned,
    Holding,
}

public record SpeedLimit(double MinSpeed, double MaxSpeed);
```

---

## 8. Decision Altitude / Minimums

### 8.1 Approach Types and Their Decision Points

| Approach Type | Decision Ref | Typical Value | Action if no visual |
|---------------|-------------|---------------|---------------------|
| ILS CAT I | DA (Decision Altitude) | 200 ft AGL | Missed approach |
| ILS CAT II | DH (Decision Height) | 100 ft AGL | Missed approach |
| ILS CAT III-A | DH | 50 ft AGL or no DH | Alert height check |
| ILS CAT III-B/C | DH | No DH (0 ft) | Rollout guidance |
| RNAV (GPS) LPV | DA | 200-250 ft AGL | Missed approach |
| RNAV LNAV/VNAV | DA | 250-350 ft AGL | Missed approach |
| RNAV LNAV only | MDA (Min Descent Alt) | 350-600 ft AGL | Missed approach at MAP |
| VOR/NDB | MDA | 400-800 ft AGL | Missed approach at MAP |
| Visual | N/A | N/A | Go around if unstable |

### 8.2 DA/DH Decision Logic

Per AIM 5-4-20 and 5-5-5:

**For precision approaches (DA/DH):**
```
At Decision Altitude:
  IF runway environment in sight
    AND aircraft in position to make normal descent to landing
    AND landing clearance received (for sim purposes)
  THEN continue to land
  ELSE execute missed approach immediately
```

"Runway environment" per 14 CFR 91.175(c)(3) includes: approach light system, threshold, threshold markings, threshold lights, REIL, VASI, touchdown zone, touchdown zone markings, touchdown zone lights, runway or runway markings, runway lights.

**For non-precision approaches (MDA):**
```
Prior to MAP:
  IF at or above MDA
    AND runway environment in sight
    AND can make normal descent
    AND landing clearance received
  THEN descend from MDA to land
  ELSE at MAP, execute missed approach
```

Key difference: at MDA the pilot levels off and continues to the MAP. At DA, the pilot must decide AT that altitude (no leveling off).

### 8.3 Implementation for the Sim

For training purposes, the sim should model:

1. **Weather visibility:** A configurable parameter (e.g., RVR, ceiling). The pilot AI checks if the runway environment would be visible at DA/DH given the weather.
2. **Landing clearance check:** At DA/DH or MAP, the contingency evaluates whether landing clearance has been received. If not, execute missed approach.
3. **Configurable DA/DH:** Each approach procedure stores its minimums (from CIFP data). The pilot AI uses these to set the contingency trigger altitude.

---

## 9. Execution Loop (AI Mode)

### 9.1 Per-Tick Processing

The AI mode extends the existing tick loop (PhaseRunner + FlightPhysics) with additional steps. New steps are marked with **[NEW]**.

```
For each pilot in AI mode:
  1. PROCESS INSTRUCTION QUEUE                              [NEW]
     - Dequeue any ATC instructions received via natural language.
     - Classify and apply plan modifications per Section 5.2 rules.
     - Update clearance states.

  2. EVALUATE CONTINGENCIES                                 [NEW]
     - For each contingency where applicable phase matches current:
       if condition is met and not already triggered:
         trigger contingency (may replace plan phases).

  3. TICK CURRENT PHASE                                     [EXISTING]
     - PhaseRunner calls CurrentPhase.OnTick(context).
     - Phase updates ControlTargets (heading, altitude, speed).
     - If phase completes, advance to next phase.

  4. UPDATE FLIGHT PHYSICS                                  [EXISTING]
     - FlightPhysics.Update() interpolates toward ControlTargets.
     - Navigation sequencing, turn anticipation, wind correction.

  5. APPLY SPEED RESTRICTION STACK                          [NEW - replaces current inline logic]
     - Clamp TargetSpeed to effective limit from SpeedRestrictionStack.

  6. CHECK COMMUNICATION CHECKPOINTS                        [NEW]
     - For each communication trigger on current phase:
       if condition met and not yet triggered:
         queue verbal action.

  7. PROCESS PENDING CLEARANCES                             [NEW]
     - For each pending clearance:
       if reminder/request interval elapsed:
         queue verbal action.

  8. PROCESS VERBAL QUEUE                                   [NEW]
     - If frequency is clear and queue is non-empty:
       transmit highest-priority item.
```

### 9.2 Physics Engine

No changes needed. The existing `FlightPhysics` takes `ControlTargets` and interpolates heading, altitude, speed, and position. The AI layer only sets targets — it does not change the physics.

---

## 10. Relationship to Milestones

| Milestone | Pilot AI Components | Status |
|-----------|-------------------|--------|
| M0 | None (hardcoded aircraft) | Complete |
| M1 | ControlTargets + physics interpolation. RPO commands set targets directly. | Complete |
| M2 | Phase structure for tower operations: TakeoffPhase, LandingPhase, GoAroundPhase, pattern phases. ClearanceRequirement for CTL/CTO/LUAW. | Complete |
| M3 | Ground phases: TaxiingPhase, HoldingShortPhase, PushbackPhase, etc. TaxiPlan with A* pathfinding. | Complete |
| M4 | STARS track operations. Not directly pilot AI, but provides the ATC-side infrastructure. | Complete |
| M5 | Approach phases: ApproachNavigationPhase, InterceptCoursePhase, HoldingPatternPhase. NavigationTarget for waypoint tracking. CIFP SID/STAR/approach parsing. | Complete |
| M5.5 | STARS ARTCC config expansion. Not pilot AI, but provides coordination channel and ATPA infrastructure. | Complete |
| M9 | Helicopter phases: HelicopterTakeoffPhase, HelicopterLandingPhase, AirTaxiPhase. | Complete |
| M10 | Full AI layer: Intent/Plan generation, ATC instruction interpretation, proactive communication, contingencies, expectations, frequency management, verbal actions, speed restriction stack. | **Not started** |

### Key Principle: Incremental Adoption

The architecture is designed so that M10 adds a layer on top of existing infrastructure rather than replacing it. The AI layer:

- Uses the same `Phase` base class and `PhaseList` for execution.
- Sets the same `ControlTargets` that `FlightPhysics` already interpolates.
- Uses the same `ClearanceRequirement` and `ClearanceType` system.
- Leverages the same `CommandBlock`/`BlockTrigger` infrastructure for conditional actions.
- Adds `Intent`, `Contingency`, `Expectation`, `PendingClearance`, `FrequencyState`, and `VerbalAction` as new types.

RPO mode continues to work unchanged — the AI mode is an alternative input source, not a replacement.

---

## 11. Key Design Decisions and Tradeoffs

### 11.1 Single Active Phase Model

Parallelism is handled within each Phase by setting multiple ControlTargets simultaneously. This is simpler, proven, and matches how real pilots think ("I am on vectors" is one mental state, not two parallel states of "turning" and "descending").

Conditional blocks ("upon reaching 4,000, proceed direct DARBY") are handled by `CommandBlock` triggers checking the altitude condition and updating the heading target when reached. No separate "conditional block" data structure is needed.

### 11.2 Server-Side Execution

The pilot AI executes on yaat-server, not on the YAAT client. This is important because:

- Multiple YAAT clients may be connected simultaneously.
- CRC needs consistent aircraft state regardless of which client is connected.
- The simulation tick rate must be deterministic.
- Rewind/replay must produce identical results.

### 11.3 RPO Mode vs. AI Mode

In RPO mode (current), the pilot AI is a thin layer: RPO commands directly set ControlTargets, and the phase system handles autonomous sequences. The `CommandAcceptance` pattern mediates between commands and phases.

In AI mode (M10), the pilot AI becomes the full system described in this document, with plan generation, clearance gating, and proactive communication. The key architectural choice is that AI mode reuses all existing execution infrastructure — it only adds the "brain" that decides what to do, not a new execution path.

### 11.4 Decision Tick Rate

The current server tick rate drives both phase decisions and physics. For AI mode, the communication and contingency evaluation can run at the same rate — real pilot reaction times are 2-10 seconds for typical instructions, so even a 1Hz tick is more than sufficient.

---

## 12. Reference Summary

| Topic | Source | Section |
|-------|--------|---------|
| Clearance format (CRAFT) | AIM | 4-2-3, 5-2-5 |
| Contact procedures | AIM | 4-2-3 |
| Readback requirements | AIM | 4-4-7 |
| Adherence to clearance | AIM | 4-4-10 |
| Speed adjustments | AIM | 5-5-9 |
| Missed approach | AIM | 5-5-5 |
| Visual approach | AIM | 5-5-11 |
| STAR procedures | AIM | 5-4-1 |
| Approach clearance | AIM | 5-4-3, 5-4-5 |
| Holding | AIM | 5-3-8 |
| Descent expectations | AIM | 4-4-10.4 |
| 250 below 10,000 | 14 CFR | 91.117(a) |
| 200 in Class B/D | 14 CFR | 91.117(b)(c) |
| Taxi clearance | 7110.65 | 3-7-2 |
| LUAW | 7110.65 | 3-9-4 |
| Takeoff clearance | 7110.65 | 3-9-6 |
| Landing clearance | 7110.65 | 3-10-5 |
| Vectoring methods | 7110.65 | 5-6-2 |
| Resume own navigation | 7110.65 | 5-6-2.5 |
| Approach clearance | 7110.65 | 4-8-1 |
| Holding instructions | 7110.65 | 4-6-1 through 4-6-4 |
| Arrival procedures | 7110.65 | 4-7-1 |
| Radar separation | 7110.65 | 5-5-4 |
| Wake turbulence | 7110.65 | 5-5-4.6, 5-5-4.7 |
| Lost comms | 14 CFR | 91.185 |

---

## 13. Open Questions for Discussion

1. **Pattern size / noise abatement:** Should the sim model different pattern sizes (close, normal, extended) and noise-abatement departure procedures? This affects the fidelity of tower training scenarios.

2. **ATIS simulation:** Should ATIS be a static configuration per scenario, or should it update dynamically based on scenario weather/runway changes? For training realism, dynamic ATIS adds significant value for approach/tower training.

3. **Pilot skill levels:** Configurable response times and error rates. Should YAAT adopt this from M10 start, or defer? Recommended: defer, but include the config structure in the architecture so it can be populated later.

4. **Voice vs. text communication:** The current architecture models communication as text strings. If voice synthesis is ever desired, the `VerbalAction` structure is already compatible (text-to-speech on the string).

5. **Multi-airport scenarios:** The current model assumes one primary airport. The Phase/Plan architecture supports this naturally (different phases reference different airports), but the airport data abstractions may need to accept an airport identifier parameter.

6. **ATC instruction parsing:** What level of natural language understanding is needed? Options range from a keyword/pattern matcher (similar to how RPO commands work) to full NLP. A pattern matcher may be sufficient for the controlled vocabulary of ATC phraseology.
