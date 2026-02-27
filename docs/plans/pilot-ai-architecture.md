# Pilot AI Architecture Design Document

## 1. Executive Summary

This document defines the behavioral architecture for YAAT's pilot AI -- the
system that drives simulated aircraft in response to ATC instructions and
according to realistic pilot decision-making patterns. It covers the plan-based
execution model, clearance gating, the three control axes, communication
behavior, plan modification rules, and a concrete C# data model.

The design is grounded in FAA Order JO 7110.65 (Air Traffic Control) and the
Aeronautical Information Manual (AIM), with specific section references
throughout. Where the lc-trainer PilotBehaviorLib provides proven patterns,
those patterns are adopted and refined.

---

## 2. Realism Validation of the User's Design Vision

### 2.1 What Is Accurate

The user's six-point vision is fundamentally sound and maps well to how real
pilots think:

**Plan-based architecture.** Real pilots operate from a mental model that is
essentially a plan: departure procedure, en route, arrival, approach. This is
formalized in the IFR system through the CRAFT clearance format (Clearance
limit, Route, Altitude, Frequency, Transponder) per AIM 5-2-5. The plan-based
model is correct.

**Clearance-gated steps.** This is the core of ATC: pilots may not enter a
movement area, cross a runway, take off, or land without explicit clearance.
Per 7110.65 3-7-2 (taxi clearance), 3-9-4 (LUAW), 3-9-6 (takeoff clearance),
3-10-5 (landing clearance), this is regulatory, not convention.

**Three control axes.** Heading, altitude, speed are indeed the three primary
control dimensions ATC uses to manage traffic. Per 7110.65 5-6-2 (vectoring
methods), 4-5-7 (altitude information), and 5-7-1 (speed adjustment), these
are the three independent instruction categories controllers issue.

**Parallel actions within sequential steps.** This is accurate. ATC routinely
issues compound clearances: "Fly heading 200, climb and maintain 5,000" -- two
simultaneous actions. The user's example of conditional sequential blocks
("upon reaching 4,000 proceed direct DARBY") is also a real-world pattern per
7110.65 5-6-2 ("AT [altitude] PROCEED DIRECT TO [position]").

**Conditional/contingency blocks.** The DA/DH decision is precisely an
if/else: if runway environment in sight, land; else, execute missed approach
per AIM 5-5-5. This is correct and critical for training realism.

**Proactive communication.** Pilots do initiate contact and make requests. Per
AIM 4-2-3, pilots make initial contact when entering a new frequency. Pilots
holding short will request taxi or crossing clearance. The interval-based
re-request model matches real pilot behavior, though the intervals and
triggering conditions need refinement (see Section 5).

### 2.2 What Needs Correction or Refinement

**"Lost comms philosophy" framing.** The user describes the default plan as a
"lost comms" philosophy, but this conflates two distinct concepts:

1. **Default intent** -- the pilot has an intended sequence of actions (depart,
   fly route, land at destination). This is correct.
2. **Lost communications procedures** -- per 14 CFR 91.185 and AIM 6-4-1,
   these are specific regulatory procedures (maintain assigned altitude or MEA,
   route: AVE-F, etc.) that only apply when two-way radio communication is
   actually lost.

The pilot AI should model (1) as "default plan with clearance gates" and (2)
as a separate, distinct behavior mode that activates only when communication is
truly lost. The normal mode is: the pilot has a plan, but pauses at each
clearance gate until authorized to proceed.

**Missing: the distinction between RPO mode and AI mode.** Per the milestone
plan, Milestone 8 introduces automated pilot logic. Earlier milestones use RPO
(Remote Pilot Operator) commands. The architecture should support both modes
cleanly:

- **RPO mode**: Plan steps are driven by explicit RPO commands (FH, CM, SPD,
  CTO, etc.). The pilot AI only handles flight dynamics and basic compliance
  (speed restrictions, 250 below 10,000).
- **AI mode**: The pilot AI interprets ATC instructions in natural language,
  maintains its own plan, and makes proactive decisions.

The data model should support both modes from the start, even though AI mode
is Milestone 8.

---

## 3. Gap Analysis

### 3.1 Scenarios Not Covered

#### 3.1.1 Holding Patterns

The user's model does not address holding. Per 7110.65 4-6-1 through 4-6-4,
holding is a fundamental ATC tool:

- Pilot is cleared to a fix with holding instructions (direction, radial, leg
  length, turn direction, EFC time).
- Standard holding pattern: right turns, 1-minute legs (or 1.5 minutes above
  14,000 MSL), on the inbound course.
- Entry procedures (direct, teardrop, parallel) per AIM 5-3-8.2.
- Holding speeds: 200 KIAS up to 6,000 MSL, 230 KIAS 6,001-14,000, 265 KIAS
  above 14,000 per AIM 5-3-8.2.

**Implementation:** Holding must be a Phase that can be inserted anywhere in
the plan. It requires its own geometry calculation (racetrack pattern) and
entry logic. The Phase takes: fix position, inbound course, turn direction,
leg length, and EFC time as parameters.

#### 3.1.2 Missed Approach Procedures

The user mentions go-around but does not detail the missed approach sequence.
Per AIM 5-5-5:

1. At MAP or DH without sufficient visual reference: initiate climb on runway
   heading (or as published).
2. Follow the published missed approach procedure (climb to altitude, turn,
   proceed to holding fix).
3. Contact ATC: "Missed approach, [callsign], [intentions]."
4. ATC may: assign vectors, clear for another approach, clear to alternate.

**Implementation:** MissedApproachPhase needs the published procedure data
(headings, altitudes, holding fix). It triggers from the contingency on the
approach phase and replaces the remaining plan with the missed approach
sequence.

#### 3.1.3 Speed Restrictions

The user's vision does not address speed management beyond the three axes. Real
speed restrictions the pilot AI must enforce:

- **250 KIAS below 10,000 MSL** per 14 CFR 91.117(a). This is absolute.
- **200 KIAS below Class B surface area** per 14 CFR 91.117(c), unless
  otherwise authorized.
- **200 KIAS within 4 nm of the primary airport in Class D** per 14 CFR
  91.117(b).
- **ATC-assigned speed restrictions** per 7110.65 5-7-1. These are issued as
  specific IAS or "maintain present speed" and can be terminated with "resume
  normal speed" or "comply with speed restrictions."
- **Published STAR/SID speed restrictions** per AIM 5-4-1. These are mandatory
  unless modified by ATC.
- **Holding pattern airspeeds** per AIM 5-3-8.2 (200/230/265 KIAS by altitude
  band).

**Implementation:** Speed restrictions are a separate layer that clamps the
target speed set by the current phase. They form a stack: regulatory limits are
always at the bottom, ATC-assigned restrictions override procedural ones, and
procedural restrictions override the pilot's preferred speed.

#### 3.1.4 ATIS and Frequency Management

Not addressed in the user's vision but critical for realism:

- Pilots obtain ATIS before contacting approach/tower (AIM 4-1-13).
- ATIS information codes (Alpha through Zulu) must be referenced on initial
  contact: "Information Charlie" (AIM 4-1-13.a).
- Frequency changes: pilot acknowledges, switches promptly (AIM 4-2-3.4).
- Monitor vs. active frequencies: "Monitor [frequency]" means listen only
  until called; "Contact [facility] [frequency]" means check in.

**Implementation:** Each pilot needs a FrequencyState tracking: current
frequency, whether communication is established, current ATIS code, and a
pending frequency change. The CheckIn verbal action should include the ATIS
code.

#### 3.1.5 Wake Turbulence Separation

Not pilot-initiated but affects pilot behavior. Per 7110.65 5-5-4 (table 5-5-1),
wake turbulence categories (A through I under RECAT, or SUPER/HEAVY/LARGE/SMALL
under legacy) determine required separation. The pilot AI needs to know its
own wake category for:

- Readback confirmation when ATC issues wake turbulence caution.
- Accepting or refusing LAHSO clearances (AIM 4-3-11).
- Understanding sequencing delays.

**Implementation:** Wake category is an aircraft property derived from aircraft
type. Store it alongside the performance data.

#### 3.1.6 Taxi Procedures

The user mentions taxi but does not detail the state complexity:

- Taxi clearance includes specific route and hold short instructions per
  7110.65 3-7-2.
- Runway crossings require explicit clearance per 7110.65 3-7-2.
- Hold short readback is mandatory per AIM 4-4-7.
- Progressive taxi instructions may be issued per 7110.65 3-7-3.
- Pilots must read back: runway assignment, hold short instructions (AIM
  4-4-7.b.4).

**Implementation:** TaxiPlan is a sub-plan within the ground phase: a sequence
of taxiway segments with hold-short gates at each runway intersection.

#### 3.1.7 "Expect" Instructions

Per AIM 5-4-1.a.1, "expect" altitudes/speeds are planning information only,
NOT clearances. They must not be used unless ATC specifically states "expect
[value] as part of a further clearance." This is critical for lost-comms
procedures (14 CFR 91.185(c)(2)(iii)) but otherwise is informational.

**Implementation:** The plan should have an "expectations" layer that stores
anticipated clearances but does not execute them. In lost-comms mode, expects
become the default plan.

#### 3.1.8 Emergency Procedures

Not covered but relevant for advanced training:

- Declaring emergency: "MAYDAY MAYDAY MAYDAY" or "PAN PAN PAN PAN PAN PAN"
  (AIM 6-3-1).
- Minimum fuel: "MINIMUM FUEL" advisory (not an emergency declaration) per
  AIM 5-5-15.
- Fuel emergency: "DECLARING FUEL EMERGENCY" per AIM 5-5-15.
- Emergency descent, engine failure, etc.

**Implementation:** Defer to Milestone 8+. The architecture should allow an
EmergencyPhase to preempt the entire plan.

#### 3.1.9 Visual Approaches

Per AIM 5-5-11 and 7110.65 7-4-1:

- Controller or pilot may request a visual approach when ceiling >= 1,000 and
  visibility >= 3 SM.
- Pilot must have airport or preceding aircraft in sight.
- Pilot is responsible for wake turbulence separation when following traffic.
- Go-around responsibility: pilot maintains terrain/obstruction avoidance.

**Implementation:** VisualApproachPhase operates differently from instrument
approach: no published procedure to follow, pilot navigates visually to
runway. In the sim, this means flying a stabilized approach path computed from
current position to the runway threshold.

---

## 4. Communication Realism

### 4.1 Initial Contact / Check-In by Phase of Flight

Per AIM 4-2-3, initial contact format is:
`[Facility name], [Full callsign], [Position/altitude], [Request/info]`

**Phase-specific phraseology:**

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

Per AIM 5-4-1.a.5.b, when cleared "descend via" a STAR, initial contact with
each new frequency must include: altitude leaving, "descending via [STAR
name]," runway transition or landing direction if assigned, and any assigned
restrictions not published on the procedure.

### 4.2 When Pilots Request vs. Wait

**Pilots initiate requests for:**
- IFR clearance (at gate or ready to copy)
- Taxi clearance (ready to push/taxi)
- Takeoff clearance (holding short, ready for departure)
- Landing clearance (only if not received by ~1 nm final for VFR; IFR pilots
  expect it to be issued, and go around if not received by DA/DH)
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

**Special case -- VFR pilots in the pattern:**
- Report pattern positions (downwind, base, final) per local procedures.
- Tower typically issues sequencing and landing clearance proactively.
- If no clearance by short final (~0.5 nm), VFR pilot should query or go
  around.

### 4.3 Readback Requirements

Per AIM 4-4-7, pilots must read back:

1. **All altitude assignments** including restrictions ("Climb and maintain
   flight level three three zero, United 123").
2. **All heading assignments** ("Turn left heading two seven zero, United 123").
3. **All runway assignments** including number and L/R/C.
4. **All hold short instructions** -- mandatory per 7110.65 3-7-2 ("Hold short
   runway 28 right, United 123").
5. **Altimeter settings** (AIM 4-2-3).
6. **All frequency changes** ("One two four point six five, United 123" or
   longer form per AIM 4-2-3.4).

**Items that get "roger" or "wilco" (not full readback):**
- Traffic advisories ("Roger, looking" or "Traffic in sight").
- Weather information.
- "Resume own navigation" -- "Wilco" or "Roger."
- "Radar contact" -- no readback needed; acknowledge with callsign.
- "Radar service terminated" -- "Roger."

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

The user's model of "request, wait, repeat" is correct but needs specific
intervals:

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

**"Fly heading 270"** -- Replaces the current heading target. If the pilot was
navigating to a waypoint, this overrides it and puts the pilot on a vector.
Per 7110.65 5-6-2, when vectored off a procedure, all published
altitude/speed restrictions are canceled unless re-issued.

**"Descend and maintain 5,000"** -- Replaces the altitude target. Per AIM
4-4-10.4, pilot should begin descent promptly.

**"Cleared ILS runway 28 right approach"** -- Replaces the entire
approach/arrival portion of the plan with the published approach procedure.

**"Cleared direct SUNOL"** -- Replaces the routing between current position
and SUNOL. Subsequent route remains unless also amended.

#### 5.1.2 Inserting Into the Plan

**"Cross SUNOL at or above 5,000"** -- Adds an altitude constraint at a
specific fix without changing the route. The constraint inserts into the
current plan's constraint list.

**"After SUNOL, turn left heading 180"** -- Conditional instruction that
inserts a heading change triggered by passing a fix.

#### 5.1.3 Temporary Instructions

**"Turn right heading 090 for traffic, expect direct SUNOL in 2 miles"** --
The heading vector is temporary. The pilot knows to expect a return to their
route. Per 7110.65 5-6-2.4, the controller should inform the pilot when they
intend to clear back onto the procedure.

**"Reduce speed to 180, I'll have normal speed for you shortly"** -- Temporary
speed restriction with implicit expectation of release.

**"Resume own navigation"** or **"Resume [procedure name]"** per 7110.65
5-6-2.5 -- terminates the temporary vector and returns to the planned route.
If the procedure had published restrictions, they must be re-issued or a
"climb via"/"descend via" must be given per 7110.65 5-6-2.6.

#### 5.1.4 "Expect" Instructions (Planning Information)

**"Expect ILS runway 28 right approach"** -- Not a clearance. Stored as an
expectation. Per AIM 5-4-4, this is advance information to aid planning. The
pilot may begin briefing the approach but must not descend on it or fly it
until actually cleared.

**"Expect further clearance at 1530"** -- Per 7110.65 4-6-1.c, this is the
EFC time in a hold. Used only for lost-comms planning per 14 CFR 91.185.

#### 5.1.5 Amended Clearances

**"Amend altitude, maintain flight level 350"** or simply **"Climb and
maintain flight level 350"** -- The new instruction supersedes the previous
one. The plan updates the assigned altitude. Per AIM 4-4-4, amended clearances
may be issued at any time to avoid confliction.

**Full re-clearance (reroute):** "Cleared to SFO airport via direct SUNOL,
V25 CEDES, SERFR1 arrival, maintain flight level 280." This replaces the
entire route from current position to destination.

### 5.2 Plan Modification Algorithm

When an instruction is received:

1. **Classify the instruction** by affected axis (heading, altitude, speed)
   and scope (immediate, conditional, temporary, expect-only).

2. **For immediate instructions:**
   - Update the relevant target on the current Phase.
   - If the instruction conflicts with the current Phase type (e.g., heading
     vector during waypoint navigation), transition to an appropriate
     overriding Phase (e.g., AssignedHeadingPhase).

3. **For conditional instructions ("at [fix/altitude], [action]"):**
   - Insert a trigger into the current Phase or a new Phase that activates
     when the condition is met.

4. **For clearances that replace plan segments:**
   - Identify which future Phases are affected.
   - Use `Plan.ReplaceFromIndex()` to swap out the affected portion.
   - Preserve any constraints or contingencies that still apply.

5. **For "resume" instructions:**
   - Remove the overriding Phase (e.g., AssignedHeadingPhase).
   - Return to the underlying navigational Phase.
   - If returning to a STAR/SID with published restrictions, re-apply them per
     7110.65 5-6-2.6.

6. **For "expect" instructions:**
   - Store in the expectations layer. Do not modify the active plan.
   - In lost-comms mode, promote expectations to plan actions.

---

## 6. Refined State Model

### 6.1 Flight Phases

The Phase hierarchy from lc-trainer is well-proven. Here is the refined
taxonomy for YAAT, adding IFR enroute and approach phases:

```
Phase (abstract base)
  |
  +-- Ground Phases
  |     AtParkingPhase
  |     AwaitingIfrClearancePhase
  |     PushbackPhase
  |     TaxiingOutPhase
  |     HoldingShortPhase         (clearance-gated: cross/LUAW/takeoff)
  |     CrossingRunwayPhase
  |     LinedUpAndWaitingPhase    (clearance-gated: takeoff)
  |     TakeoffPhase
  |     RunwayExitPhase
  |     HoldingAfterRunwayExitPhase
  |     TaxiingInPhase
  |
  +-- Departure Phases
  |     InitialClimbPhase
  |     ClimbingViaSidPhase
  |     WithDeparturePhase        (on vectors from departure control)
  |     VfrPatternExitPhase
  |
  +-- Enroute Phases
  |     EnRoutePhase              (following airways/direct)
  |     DescendingViaStarPhase
  |
  +-- Approach Phases
  |     OnVectorsPhase            (being vectored to final)
  |     HoldingPhase              (in holding pattern)
  |     OnApproachPhase           (flying published approach procedure)
  |     MissedApproachPhase
  |
  +-- Pattern Phases
  |     PatternEntryPhase         (joining from outside)
  |     DownwindPhase
  |     BasePhase
  |     FinalPhase
  |     LandingPhase
  |     GoAroundPhase
  |
  +-- Maneuver Phases
        AssignedHeadingPhase      (flying ATC-assigned heading)
        DirectToFixPhase          (proceeding direct to a waypoint)
        OrbitPhase                (360-degree turn for spacing)
```

### 6.2 Clearance States

Each clearance-gated step tracks its state:

```
ClearanceState
  NotNeeded       -- This step does not require a clearance
  Pending         -- Clearance needed but not yet requested
  Requested       -- Pilot has requested the clearance
  StandBy         -- Controller acknowledged with "standby"
  Cleared         -- Clearance received
  Amended         -- Original clearance was amended
  Denied          -- "Unable" from ATC (rare)
  Expired         -- Clearance canceled or no longer valid
```

For the PendingClearance model (from lc-trainer), the key distinction is:

- **StoppedPendingClearance**: Aircraft is stopped (hold short, at parking).
  Can wait indefinitely with periodic reminders (default: 60 seconds).
- **FlyingPendingClearance**: Aircraft is in motion (on approach needing
  landing clearance). Has a contingency that triggers if not received by a
  deadline (e.g., go around at DA/DH).

### 6.3 Communication States

```
CommunicationState
  NotOnFrequency    -- Not tuned to any relevant facility
  Monitoring        -- Listening but not yet checked in
  CheckInPending    -- Needs to check in when frequency is clear
  Established       -- Two-way communication established
  AwaitingResponse  -- Made a request, waiting for reply
  LostComms         -- Two-way communication failure
```

### 6.4 State Composition

The pilot's complete state at any moment is:

```
PilotState
  FlightPhase:        (currently active Phase from the Plan)
  ClearanceStates:    (dictionary of clearance type -> state)
  CommunicationState: (current comm state)
  Plan:               (the full plan with all phases)
  Expectations:       (expect instructions not yet cleared)
  SpeedRestrictions:  (stack of active speed limits)
  Constraints:        (follow traffic, altitude restrictions)
```

---

## 7. Decision Altitude / Minimums

### 7.1 Approach Types and Their Decision Points

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

### 7.2 DA/DH Decision Logic

Per AIM 5-4-20 and 5-5-5, the decision process is:

**For precision approaches (DA/DH):**

```
At Decision Altitude:
  IF runway environment in sight
    AND aircraft in position to make normal descent to landing
    AND landing clearance received (for sim purposes)
  THEN continue to land
  ELSE execute missed approach immediately
```

"Runway environment" per 14 CFR 91.175(c)(3) includes: approach light system,
threshold, threshold markings, threshold lights, REIL, VASI, touchdown zone,
touchdown zone markings, touchdown zone lights, runway or runway markings,
runway lights.

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

Key difference: at MDA the pilot levels off and continues to the MAP. At DA,
the pilot must decide AT that altitude (no leveling off). Per AIM 5-4-5.a.7.c,
the pilot must not descend below DA/DH until visual.

### 7.3 Implementation for the Sim

For training purposes, the sim should model:

1. **Weather visibility**: A configurable parameter (e.g., RVR, ceiling).
   The pilot AI checks if the runway environment would be visible at DA/DH
   given the weather.
2. **Landing clearance check**: At a configurable distance from the runway
   threshold (default: at DA/DH or MAP), the contingency evaluates:
   - Has landing clearance been received? (ClearedToLand flag)
   - If not, execute missed approach.
3. **Configurable DA/DH**: Each approach procedure stores its minimums. The
   pilot AI uses these to set the contingency trigger altitude.

For CAT I ILS in training scenarios, a reasonable implementation:

- Approach phase descends on glideslope.
- At DA (200 AGL by default), contingency evaluates: cleared to land? Yes:
  continue. No: initiate missed approach.
- For VFR/visual approaches: the pilot goes around if no landing clearance
  by approximately 0.5 nm from threshold.

---

## 8. Concrete Data Model

### 8.1 Core Architecture (C#)

The following builds on lc-trainer's proven PilotBehaviorLib pattern of
Plan/Phase/Intent, refined for YAAT's server-side execution context.

#### 8.1.1 Phase Base Class

```csharp
/// <summary>
/// Base class for all phases in a pilot's plan.
/// A phase represents a discrete goal with completion criteria,
/// optional clearance requirements, and proactive communication
/// checkpoints.
/// </summary>
public abstract class Phase
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public PhaseStatus Status { get; set; } = PhaseStatus.Pending;
    public long? StartedAtMs { get; set; }
    public long? EndedAtMs { get; set; }

    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>
    /// Called when the phase transitions to Active.
    /// Set initial control targets here.
    /// </summary>
    public abstract void OnStart(PhaseContext ctx);

    /// <summary>
    /// Called every decision tick while Active.
    /// Returns true when phase is complete.
    /// </summary>
    public abstract bool OnTick(PhaseContext ctx, long deltaMs);

    /// <summary>
    /// Called when the phase ends (completed or skipped).
    /// </summary>
    public virtual void OnEnd(PhaseContext ctx, PhaseStatus endStatus) { }

    /// <summary>
    /// Create a deep copy for path prediction simulation.
    /// </summary>
    public abstract Phase CloneForPrediction();

    /// <summary>
    /// Clearance requirements for this phase. Cached on first access.
    /// </summary>
    public IReadOnlyList<ClearanceRequirement> Requirements
        => _requirements ??= CreateRequirements();

    protected virtual IReadOnlyList<ClearanceRequirement>
        CreateRequirements() => [];

    /// <summary>
    /// Communication checkpoints. Cached on first access.
    /// </summary>
    public IReadOnlyList<Checkpoint> Checkpoints
        => _checkpoints ??= CreateCheckpoints();

    protected virtual IReadOnlyList<Checkpoint>
        CreateCheckpoints() => [];

    private IReadOnlyList<ClearanceRequirement>? _requirements;
    private IReadOnlyList<Checkpoint>? _checkpoints;
}

public enum PhaseStatus
{
    Pending,
    Active,
    Completed,
    Skipped
}
```

#### 8.1.2 Plan

```csharp
/// <summary>
/// A sequence of phases that achieves the pilot's intent.
/// Supports insertion, replacement, and conditional branching.
/// </summary>
public class Plan
{
    public required Intent Intent { get; init; }

    private readonly List<Phase> _phases = [];
    private readonly List<Constraint> _constraints = [];
    private readonly List<Contingency> _contingencies = [];
    private readonly List<Expectation> _expectations = [];

    public IReadOnlyList<Phase> Phases => _phases;
    public IReadOnlyList<Constraint> Constraints => _constraints;
    public IReadOnlyList<Contingency> Contingencies => _contingencies;
    public IReadOnlyList<Expectation> Expectations => _expectations;
    public int CurrentPhaseIndex { get; private set; } = -1;

    public Phase? CurrentPhase =>
        CurrentPhaseIndex >= 0 && CurrentPhaseIndex < _phases.Count
            ? _phases[CurrentPhaseIndex]
            : null;

    public bool IsComplete => CurrentPhaseIndex >= _phases.Count;

    // --- Mutation methods ---

    public void AddPhase(Phase phase) => _phases.Add(phase);

    public void InsertPhase(int index, Phase phase)
    {
        _phases.Insert(index, phase);
        if (index <= CurrentPhaseIndex) CurrentPhaseIndex++;
    }

    public void InsertAfterCurrent(Phase phase)
        => InsertPhase(CurrentPhaseIndex + 1, phase);

    public void ReplaceFromIndex(int index, IEnumerable<Phase> phases)
    {
        while (_phases.Count > index) _phases.RemoveAt(_phases.Count - 1);
        _phases.AddRange(phases);
        if (CurrentPhaseIndex >= index && _phases.Count > index)
        {
            CurrentPhaseIndex = index;
            _phases[index].Status = PhaseStatus.Active;
        }
    }

    public void Start()
    {
        if (_phases.Count > 0)
        {
            CurrentPhaseIndex = 0;
            _phases[0].Status = PhaseStatus.Active;
        }
    }

    public Phase? AdvanceToNext()
    {
        if (CurrentPhase != null)
            CurrentPhase.Status = PhaseStatus.Completed;

        CurrentPhaseIndex++;

        if (CurrentPhaseIndex < _phases.Count)
        {
            _phases[CurrentPhaseIndex].Status = PhaseStatus.Active;
            return _phases[CurrentPhaseIndex];
        }
        return null;
    }

    public void SkipTo<T>() where T : Phase
    {
        var target = _phases
            .Skip(CurrentPhaseIndex + 1)
            .OfType<T>()
            .FirstOrDefault();
        if (target != null) SkipTo(target);
    }

    public void SkipTo(Phase target)
    {
        var idx = _phases.IndexOf(target);
        if (idx <= CurrentPhaseIndex) return;
        for (int i = CurrentPhaseIndex; i < idx; i++)
            _phases[i].Status = PhaseStatus.Skipped;
        CurrentPhaseIndex = idx;
        _phases[idx].Status = PhaseStatus.Active;
    }

    // --- Constraints, contingencies, expectations ---

    public void AddConstraint(Constraint c) => _constraints.Add(c);
    public void RemoveConstraint(Constraint c) => _constraints.Remove(c);
    public void AddContingency(Contingency c) => _contingencies.Add(c);
    public void RemoveContingency(Contingency c)
        => _contingencies.Remove(c);
    public void AddExpectation(Expectation e) => _expectations.Add(e);
    public void RemoveExpectation(Expectation e)
        => _expectations.Remove(e);
}
```

#### 8.1.3 Intent

```csharp
/// <summary>
/// The pilot's high-level goal. Drives plan generation.
/// </summary>
public class Intent
{
    public required IntentType Type { get; init; }

    // Common parameters
    public string? Runway { get; init; }
    public string? DestinationAirport { get; init; }
    public string? DepartureAirport { get; init; }

    // IFR parameters
    public string? Route { get; init; }
    public int? CruiseAltitude { get; init; }
    public string? AssignedSid { get; init; }
    public string? AssignedStar { get; init; }
    public string? ExpectedApproach { get; init; }

    // VFR parameters
    public PatternDirection PatternDirection { get; init; }
        = PatternDirection.Left;
    public CardinalDirection? DepartureDirection { get; init; }

    // Ground parameters
    public string? ParkingPosition { get; init; }
}

public enum IntentType
{
    // Ground intents
    TaxiToRunway,
    TaxiToParking,

    // VFR intents
    LandVfr,
    TouchAndGo,
    LowApproach,
    StopAndGo,
    DepartVfr,
    TransitionVfr,

    // IFR intents
    DepartIfr,
    ArriveIfr,
    PracticeApproach,

    // Enroute intents
    EnRouteCruise,

    // Contingency intents (generated, not assigned)
    MissedApproach,
    GoAround,
    HoldAsPublished,
    LostCommunications
}
```

#### 8.1.4 Clearance Requirements and Pending Clearances

```csharp
/// <summary>
/// A clearance the pilot needs before a phase can proceed or complete.
/// </summary>
public class ClearanceRequirement
{
    public required string Name { get; init; }
    public required ClearanceType Type { get; init; }
    public required string Description { get; init; }
    public bool IsSatisfied { get; set; }
}

public enum ClearanceType
{
    Pushback,
    TaxiClearance,
    RunwayCrossing,
    LineUpAndWait,
    ClearedForTakeoff,
    ClearedToLand,
    ClearedForOption,
    ClearedTouchAndGo,
    ApproachClearance,
    IfrClearance,
    FrequencyChange,
    ClearedIntoClassBravo,
    Lahso
}

/// <summary>
/// Clearance the pilot is actively waiting for.
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
/// Clearance waited for while stopped (e.g., holding short).
/// Sends periodic reminders.
/// </summary>
public class StoppedPendingClearance : PendingClearance
{
    public override bool IsStopped => true;
    public long ReminderIntervalMs { get; init; } = 60_000;
    public long? LastReminderTimeMs { get; set; }
    public string? ReminderMessage { get; init; }
}

/// <summary>
/// Clearance waited for while flying (e.g., landing clearance on
/// approach). Has a contingency if not received in time.
/// </summary>
public class FlyingPendingClearance : PendingClearance
{
    public override bool IsStopped => false;
    public required Contingency Contingency { get; init; }
    public required string RequestMessage { get; init; }
    public IReadOnlyList<long> RequestIntervalsMs { get; init; }
        = [60_000, 30_000];
    public int NextRequestIndex { get; set; }
    public long? LastRequestTimeMs { get; set; }
}
```

#### 8.1.5 Contingency

```csharp
/// <summary>
/// A contingency that triggers when conditions are not met.
/// Evaluated each tick when applicable phases are active.
/// </summary>
public class Contingency
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// Phase types this contingency applies to.
    /// Only evaluated when one of these is the active phase.
    /// </summary>
    public required IReadOnlyList<Type> ApplicablePhases { get; init; }

    /// <summary>
    /// Condition that triggers the contingency.
    /// </summary>
    public required Func<PhaseContext, bool> Condition { get; init; }

    /// <summary>
    /// Action to take: typically replaces plan phases.
    /// </summary>
    public required Action<PhaseContext> OnTrigger { get; init; }

    public bool Triggered { get; set; }
}
```

#### 8.1.6 Expectations (Planning Information)

```csharp
/// <summary>
/// An "expect" instruction -- planning information only, not a
/// clearance. Becomes actionable only in lost-comms mode.
/// </summary>
public class Expectation
{
    public required string Name { get; init; }
    public required ExpectationType Type { get; init; }

    // The expected values
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
    FurtherClearance
}
```

#### 8.1.7 The Three Control Axes

```csharp
/// <summary>
/// The three pilot control axes. Each has a current value (read from
/// aircraft state) and a target value (set by the active Phase).
/// The physics engine interpolates from current toward target.
/// </summary>
public class ControlTargets
{
    // --- Heading ---
    /// <summary>
    /// Target heading in degrees magnetic.
    /// Null means maintain current heading.
    /// </summary>
    public double? TargetHeading { get; set; }

    /// <summary>
    /// When navigating to a waypoint rather than a fixed heading,
    /// the Phase continuously updates TargetHeading to point at
    /// the waypoint. This field tracks the destination fix.
    /// </summary>
    public NavigationTarget? HeadingNavTarget { get; set; }

    /// <summary>
    /// Turn direction preference (null = shortest turn).
    /// </summary>
    public TurnDirection? PreferredTurnDirection { get; set; }

    // --- Altitude ---
    /// <summary>
    /// Target altitude in feet MSL.
    /// Null means maintain current altitude.
    /// </summary>
    public double? TargetAltitude { get; set; }

    /// <summary>
    /// Desired vertical rate in fpm (positive = climb).
    /// Null means use default rate from performance data.
    /// </summary>
    public double? DesiredVerticalRate { get; set; }

    /// <summary>
    /// Whether this is an "at pilot's discretion" descent/climb.
    /// If true, the pilot may level off at intermediate altitudes.
    /// </summary>
    public bool AtPilotsDiscretion { get; set; }

    // --- Speed ---
    /// <summary>
    /// Target indicated airspeed in knots.
    /// Null means maintain current speed or use phase default.
    /// </summary>
    public double? TargetSpeed { get; set; }

    /// <summary>
    /// Target Mach number (for high-altitude operations).
    /// Takes precedence over TargetSpeed when set.
    /// </summary>
    public double? TargetMach { get; set; }
}

/// <summary>
/// A waypoint the pilot is navigating toward.
/// </summary>
public class NavigationTarget
{
    public required string Name { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }

    /// <summary>
    /// Course to intercept (for localizer/radial intercepts).
    /// Null means proceed direct.
    /// </summary>
    public double? DesiredCourse { get; init; }
}
```

#### 8.1.8 Speed Restriction Stack

```csharp
/// <summary>
/// Manages the stack of speed restrictions.
/// Lower layers (regulatory) always apply. Higher layers
/// (ATC-assigned) override procedural restrictions.
/// </summary>
public class SpeedRestrictionStack
{
    private readonly List<SpeedRestriction> _restrictions = [];

    /// <summary>
    /// Compute the effective speed limit at a given altitude and
    /// distance from the airport.
    /// </summary>
    public SpeedLimit GetEffectiveLimit(
        double altitudeMsl,
        double? distanceFromPrimaryAirportNm,
        bool inClassB,
        bool inClassD)
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

        // Regulatory: 250 KIAS below 10,000 MSL
        if (altitudeMsl < 10_000)
            maxSpeed = Math.Min(maxSpeed, 250);

        // Regulatory: 200 KIAS in/below Class B surface area
        if (inClassB && altitudeMsl < 10_000)
            maxSpeed = Math.Min(maxSpeed, 200);

        // Regulatory: 200 KIAS within Class D
        if (inClassD
            && distanceFromPrimaryAirportNm.HasValue
            && distanceFromPrimaryAirportNm.Value <= 4.0)
            maxSpeed = Math.Min(maxSpeed, 200);

        return new SpeedLimit(minSpeed, maxSpeed);
    }

    public void Push(SpeedRestriction restriction)
        => _restrictions.Add(restriction);

    public void Remove(SpeedRestriction restriction)
        => _restrictions.Remove(restriction);

    public void ClearAtcRestrictions()
        => _restrictions.RemoveAll(
            r => r.Source == SpeedRestrictionSource.AtcAssigned);
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
    Regulatory,     // 14 CFR 91.117
    Procedural,     // Published on SID/STAR
    AtcAssigned,    // Controller-issued
    Holding         // Holding pattern speed limit
}

public record SpeedLimit(double MinSpeed, double MaxSpeed);
```

#### 8.1.9 Phase Context

```csharp
/// <summary>
/// Context passed to phases during execution.
/// Provides access to aircraft state and external services.
/// </summary>
public class PhaseContext
{
    public required IAircraftState Aircraft { get; init; }
    public required IAircraftPerformance Performance { get; init; }
    public required INavigationData NavigationData { get; init; }
    public required IAirportData AirportData { get; init; }
    public required ITaxiwayData TaxiwayData { get; init; }
    public required ITrafficQuery TrafficQuery { get; init; }
    public required IWeatherData WeatherData { get; init; }
    public required Plan Plan { get; init; }
    public required ControlTargets Targets { get; init; }
    public required SpeedRestrictionStack SpeedRestrictions { get; init; }
    public required CommunicationState CommState { get; init; }
    public required long SimTimeMs { get; init; }

    /// <summary>
    /// Queue a radio transmission for the pilot to make.
    /// </summary>
    public required Action<VerbalAction> QueueVerbalAction { get; init; }

    /// <summary>
    /// Queue a non-verbal action (squawk, ident, frequency change).
    /// </summary>
    public required Action<MiscAction> QueueMiscAction { get; init; }
}
```

#### 8.1.10 Constraint

```csharp
/// <summary>
/// An active constraint that modifies how the pilot executes
/// the current phase (e.g., follow traffic, crossing restriction).
/// </summary>
public abstract class Constraint
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual bool IsActive => true;

    /// <summary>
    /// Evaluate and return adjustments needed.
    /// </summary>
    public abstract ConstraintAdjustment? Evaluate(PhaseContext ctx);
}

/// <summary>
/// Speed/maneuver adjustments to satisfy a constraint.
/// </summary>
public class ConstraintAdjustment
{
    public double? TargetSpeed { get; init; }
    public bool PerformOrbit { get; init; }
    public bool ExtendCurrentLeg { get; init; }
    public string? Reason { get; init; }
}
```

---

## 9. Execution Loop

### 9.1 Per-Tick Processing (Server-Side, ~2 Hz Decision Rate)

```
For each pilot:
  1. PROCESS COMMAND QUEUE
     - Dequeue any RPO commands or ATC instructions.
     - Apply plan modifications per Section 5 rules.
     - Update clearance states.

  2. EVALUATE CONTINGENCIES
     - For each contingency where applicable phase matches current:
       if condition is met and not already triggered:
         trigger contingency (may replace plan).

  3. EVALUATE CONSTRAINTS
     - For each active constraint:
       evaluate -> if adjustment needed, apply to targets.

  4. TICK CURRENT PHASE
     - Call CurrentPhase.OnTick(context, deltaMs).
     - Phase updates ControlTargets (heading, altitude, speed).
     - If phase returns true (complete):
       advance to next phase.

  5. APPLY SPEED RESTRICTION STACK
     - Clamp TargetSpeed to effective limit from stack.

  6. CHECK COMMUNICATION CHECKPOINTS
     - For each checkpoint on current phase:
       if condition met and not yet triggered:
         queue verbal action.

  7. PROCESS PENDING CLEARANCES
     - For each pending clearance:
       if reminder/request interval elapsed:
         queue verbal action.

  8. PROCESS VERBAL QUEUE
     - If frequency is clear and queue is non-empty:
       transmit highest-priority item.
```

### 9.2 Physics Engine (1 Hz or Higher on Server)

The physics engine is separate from the pilot AI. It takes the ControlTargets
and interpolates:

- **Heading**: Standard rate turn (3 deg/sec) toward TargetHeading. Transport
  category aircraft limited to 25 degrees bank; GA limited to 30 degrees in
  pattern, standard rate elsewhere.
- **Altitude**: Climb/descend at the rate from performance data (or
  DesiredVerticalRate if set) toward TargetAltitude.
- **Speed**: Accelerate/decelerate at a realistic rate toward TargetSpeed,
  clamped by SpeedRestrictionStack limits.
- **Position**: Update lat/lon based on heading and groundspeed (TAS adjusted
  for wind).

---

## 10. Relationship to Milestones

| Milestone | Pilot AI Components |
|-----------|-------------------|
| M0 | None (hardcoded aircraft) |
| M1 | ControlTargets + physics interpolation. RPO commands set targets directly. No plan/phase structure yet. |
| M2 | Phase structure for tower operations: TakeoffPhase, LandingPhase, GoAroundPhase, pattern phases. ClearanceRequirement for CTL/CTO/LUAW. |
| M3 | Ground phases: TaxiingOutPhase, HoldingShortPhase, PushbackPhase, etc. TaxiPlan sub-system. |
| M4 | Approach phases: OnVectorsPhase, OnApproachPhase, MissedApproachPhase, HoldingPhase. NavigationTarget for waypoint tracking. SpeedRestrictionStack. |
| M5 | EnRoutePhase, DescendingViaStarPhase, ClimbingViaSidPhase. Mach speed. |
| M8 | Full Plan/Intent/PlanGenerator system. AI-driven communication. Proactive check-ins. Contingencies. Lost-comms behavior. |

### Key Principle: Incremental Adoption

The architecture is designed so that each milestone can adopt the pieces it
needs without requiring the full system. Milestone 1 only needs ControlTargets
and the physics interpolation. Milestone 2 introduces Phases but only for the
tower operations subset. Each subsequent milestone adds phases and the
supporting infrastructure (constraints, contingencies, expectations) as needed.

---

## 11. Key Design Decisions and Tradeoffs

### 11.1 Phase-per-Goal vs. Parallel Action Blocks

The user proposed "parallel actions within sequential blocks." The lc-trainer
architecture uses a simpler model: one Phase is active at a time, but a Phase
can internally manage multiple simultaneous control axis changes. For example,
`OnVectorsPhase` simultaneously adjusts heading and altitude.

**Recommendation: Keep the single-active-phase model.** Parallelism is handled
within each Phase by setting multiple ControlTargets simultaneously. This is
simpler, proven, and matches how real pilots think ("I am on vectors" is one
mental state, not two parallel states of "turning" and "descending").

The conditional/sequential blocks the user described ("upon reaching 4,000,
proceed direct DARBY") are handled by the Phase's OnTick logic checking the
altitude condition and updating the heading target when reached. No separate
"conditional block" data structure is needed.

### 11.2 Server-Side vs. Client-Side Execution

The pilot AI executes on yaat-server, not on the YAAT client. The client
displays state and sends RPO commands. This is important because:

- Multiple YAAT clients may be connected simultaneously (Milestone 6).
- CRC needs consistent aircraft state regardless of which client is connected.
- The simulation tick rate must be deterministic.

### 11.3 RPO Mode vs. AI Mode

In RPO mode (Milestones 1-7), the pilot AI is a thin layer: RPO commands
directly set ControlTargets, and the physics engine interpolates. The Plan/
Phase system is used only for autonomous sequences (takeoff roll, landing
rollout, traffic pattern legs).

In AI mode (Milestone 8), the pilot AI becomes the full system described in
this document, with plan generation, clearance gating, and proactive
communication.

### 11.4 Decision Tick Rate

2 Hz for pilot decisions (every 500 ms) is sufficient. Real pilot reaction
times are 2-10 seconds for typical instructions. The physics engine can run
at a higher rate (1 Hz minimum, ideally 4-10 Hz for smooth interpolation)
independently.

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

1. **Pattern size / noise abatement:** Should the sim model different pattern
   sizes (close, normal, extended) and noise-abatement departure procedures?
   This affects the fidelity of tower training scenarios.

2. **ATIS simulation:** Should ATIS be a static configuration per scenario, or
   should it update dynamically based on scenario weather/runway changes? For
   training realism, dynamic ATIS adds significant value for approach/tower
   training.

3. **Pilot skill levels:** The lc-trainer has PilotConfig with configurable
   response times and error rates. Should YAAT adopt this from the start, or
   defer to Milestone 8? Recommended: defer, but include the config structure
   in the architecture so it can be populated later.

4. **Voice vs. text communication:** The current architecture models
   communication as text strings. If voice synthesis is ever desired, the
   VerbalAction structure is already compatible (text-to-speech on the string).

5. **Multi-airport scenarios:** The current model assumes one primary airport.
   Milestone 7 mentions multi-airport. The Phase/Plan architecture supports
   this naturally (different phases reference different airports), but the
   AirportData and TaxiwayData abstractions may need to accept an airport
   identifier parameter.
