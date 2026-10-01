# FOLLOW "lead ahead" gate: permutation study

Every combination of the follower's phase and the lead's state for an airborne `FOLLOW`, with what the code does today and what it should do, to decide where the "lead ahead" gate belongs beyond base. The behaviour studied is described in [`docs/approach-and-pattern-geometry.md`](../approach-and-pattern-geometry.md) (*Visual following*). Cells that could not be settled from the code say **unverified**.

## Terms and abbreviations

- **Lead-ahead gate**: `AirborneFollowHelper.IsLeadAheadOfTrack`, true when the bearing to the lead is within `LeadAheadOfTrackMaxDeg = 60` of the follower's ground track (AFH:186-198). Today it is applied in one place only: a follower on `BasePhase` whose lead has **no assigned runway** (CD:3976-3979, refusal "Unable, on base for runway {rwy}, {T} is not ahead of us, request vectors").
- **Sequence-ahead**: the lead will reach the follower's landing threshold first, judged by remaining path to the threshold (the proposal below). Today the code approximates it with the pattern-leg index and, on a shared leg, with phase `ElapsedSeconds` (AFH:324-390, 441-478).
- **Same runway**: follower and lead have the same `AssignedRunway` (CD:4138-4149 compares with the airport; the per-tick flow tests at AFH:361-370 and AFH:443-452 compare the designator only).
- **Retarget in place**: `RetargetFollowOnPatternLeg` sets `FollowingCallsign`, clears an `EXT` on upwind/crosswind/downwind, and leaves the phase list alone (CD:3812-3828). The leg phases then do the spacing each tick.
- **Downwind entry**: `TryFollowIntoLeadPattern` builds a `TryEnterPattern(Downwind)` to the lead's runway on the lead's circuit side (CD:3894-3932).
- **Free pursuit**: a fresh `VfrFollowPhase`, optionally with a `FollowPatternReturn` to the follower's own circuit (CD:4112-4127).
- File shorthand: **CD** `src/Yaat.Sim/Commands/CommandDispatcher.cs`; **AFH** `src/Yaat.Sim/Phases/AirborneFollowHelper.cs`; **VFP** `src/Yaat.Sim/Phases/Pattern/VfrFollowPhase.cs`; **BFS** `src/Yaat.Sim/Phases/Pattern/BaseFollowSpacing.cs`; other phases by class name.

## How FOLLOW is routed today

1. The phase gate runs first (CD:2099-2112). Phases that list `CanonicalCommandType.Follow` as `Allowed`: `PatternEntryPhase`:341, `MidfieldCrossingPhase`:223, `TeardropReentryPhase`:177, `UpwindPhase`:225, `CrosswindPhase`:206, `DownwindPhase`:589, `BasePhase`:404, `FinalApproachPhase`:1732, `VfrFollowPhase`:2178, `InterceptCoursePhase`:518, `ApproachNavigationPhase`:266; `AirspaceBoundaryHoldPhase`:260 allows every command. Landing (airborne), touch-and-go, stop-and-go and every ground phase reject it with their own message. Every other airborne phase returns `ClearsPhase` (the `Phase.cs`:88 default or an explicit default arm), so after the dry-run passes (CD:440-444) the chain is torn down by `ClearPhaseChain` (CD:455-460, 2726-2739), which drops the phase list, its runway and its landing clearance, and FOLLOW then routes with no phase.
2. **`FOLLOWF` is a different canonical type** (`FollowForce`, `CommandDescriber.cs`:113) and no phase lists it, so on every phase in step 1's allowed list it returns `ClearsPhase` and routes as a follower with no phase (see row Z).
3. `TryAirborneFollow` (CD:3686-3716): on the ground, "FOLLOW requires the aircraft to be airborne"; no traffic in sight, "Traffic not in sight — issue RTIS first" (FOLLOWF sets it, CD:3723-3744); no callsign, "Unable, say traffic callsign"; super lead, "Unable, visual separation not authorized behind super {T}" (CD:3750-3759).
4. `RouteFollow` (CD:3766-3805). Only when the follower is on a pattern leg (`IsPatternLeg`, CD:3995-4004: entry, crossing, teardrop, upwind, crosswind, downwind, base, final) with an assigned runway, `TryRouteRunwaylessLead` (CD:4017-4035) refuses a lead on the ground unless it is rolling out on the follower's runway, refuses a lead bound for another airport (CD:4049-4065), and sends a lead with no runway to `TryFollowFromPatternLeg` (CD:3945-3983). Then a lead on a different runway is refused from base or final (CD:3794-3797) and otherwise re-sequenced; a same-runway lead is retargeted in place (CD:3799-3802).
5. `InstallFollow` (CD:3835-3872) for everything else: an approach is torn down keeping the landing clearance (CD:3845-3849); a lead that is airborne and `IsEstablishedTowardRunway` (CD:4157-4169: entry, crossing, teardrop, upwind, crosswind, downwind, base, `FinalApproachPhase`, `LandingPhase`, `TouchAndGoPhase`, with a runway) gets a downwind entry, unless the lead is on final/landing and the follower is in its final-approach corridor (CD:3903-3907, 4195-4204), which gets free pursuit; a follower already in `VfrFollowPhase` is retargeted (CD:3863-3868); otherwise free pursuit without a pattern return (CD:3870).

A lead in `InterceptCoursePhase`/`ApproachNavigationPhase`, `GoAroundPhase`, a departure phase or a hold is **not** "established toward a runway" even though it usually has one (approach installers set `AssignedRunway`, `ApproachCommandHandler.cs`:178/371/465/661). Its `PatternLegIndex` is null (AFH:324-345), so both flow tests are false for it and no leg-hold sequences behind it; only the straight-line speed spacing runs (AFH:515-535).

## Lead classes

| Code | Lead state |
|---|---|
| ENT | `PatternEntryPhase` / `MidfieldCrossingPhase` / `TeardropReentryPhase`, same runway (leg index 0) |
| UW, XW | `UpwindPhase` (1), `CrosswindPhase` (2), same runway |
| DW | `DownwindPhase` (3), same runway |
| BASE | `BasePhase` (4), same runway |
| FIN | `FinalApproachPhase` (5, pattern or straight-in) or `LandingPhase` airborne (6), same runway |
| APP | `InterceptCoursePhase` / `ApproachNavigationPhase` with the same runway (leg index null) |
| GA | `GoAroundPhase` or `LowApproachPhase` (index 6 for low approach, null for go-around), same runway |
| DEP | airborne `TakeoffPhase` / `InitialClimbPhase` / `DepartureProcedurePhase` off the same runway (index null) |
| HOLD | `VfrHoldPhase` / `HoldingPatternPhase` / `STurnPhase` / `MakeTurnPhase`; whether it still has a runway depends on how the hold was issued (`PatternCommandHandler.cs`:3121-3126 keeps it when a phase list exists; a `ClearsPhase` gate before it drops it): **unverified** per scenario, so both FREE and "same runway, index null" apply |
| FREE | airborne, no assigned runway (no phase, DCT, `VfrFollowPhase`) at the follower's airport |
| XRWY | established toward a different runway at the same airport |
| ELSE | runway or filed destination at another airport |
| GND-R | rolling out (`LandingPhase`/`TouchAndGoPhase`/`StopAndGoPhase` on the ground) on the follower's runway |
| GND | any other ground state |
| SUP | a super (refused in every row by CD:3750-3759; kept, 7110.65 §7-2-1 "Visual separation is not authorized when the lead aircraft is a super"; AIM §4-4-14.b) |

"Ahead"/"behind" in a row below means sequence-ahead/behind. **Δ** marks a cell where today and proposed differ.

## Grounding used throughout

- 7110.65 §3-8-1: FOLLOW is the tower's sequencing instruction ("Establish the sequence of arriving and departing aircraft…"; FOLLOW (description and location of traffic)); traffic on another runway gets "TRAFFIC … LANDING RUNWAY (number)", not FOLLOW; the tower's own tools for a re-sequence are EXTEND DOWNWIND, a 360/270, CIRCLE THE AIRPORT and GO AROUND.
- 7110.65 §7-6-7.a: approach control tells a VFR arrival which aircraft to follow "when the integrity of the approach sequence is dependent on following a preceding aircraft". 7110.65 §7-4-3.c.2: a visual approach following a preceding aircraft to the same runway.
- AIM §5-5-12.a.1 / §4-4-14.b: accepting a follow commits the pilot to maneuver as necessary to maintain in-trail separation. AIM §5-5-12.a.2 and §4-4-14.b NOTE: the pilot promptly tells the controller when it cannot accept the responsibility "for any reason" (the basis for a spoken refusal).
- AIM §4-4-14.a.2 NOTE: traffic is no longer a factor once it is in the landing phase or executes a missed approach.
- AIM §4-3-5: shallow S-turns are anticipated; a 360 or other major maneuver is not, and the pilot advises the controller. Following traffic that is behind requires exactly such a maneuver.
- AIM §4-3-4.d (item 4): do not cut in front of or overtake an aircraft on final. 7110.65 §3-10-3: same-runway separation at the threshold.

## A. Follower on a pattern entry (`PatternEntryPhase`, `MidfieldCrossingPhase`, `TeardropReentryPhase`)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| ENT | Retarget in place; entry flies free-flight speed spacing, no leg hold; the shared leg index 0 is ordered by elapsed time (CD:3799-3802, AFH:383-388, 544-580) | Accept; order a shared leg by remaining path, not elapsed time (§3-8-1, AIM §5-5-12.a.1) | Δ (ordering only) |
| UW, XW, DW, BASE, FIN | Retarget in place; entry speed spacing; the downwind the entry feeds runs the holds (CD:3799-3802, `DownwindPhase`:353-356) | Same | |
| APP | Retarget in place; lead leg index null, so no hold anywhere downstream, speed spacing by straight line only (AFH:324-345, 515-535) | Accept; count a lead on final by geometry (`IsOnFinalByGeometry`) or on an approach as a finite remaining path so the downwind hold sequences behind it (§3-10-3, AIM §4-3-4.d) | Δ |
| GA, DEP | Retarget in place; nothing sequences behind it (index null) | Accept; treat a go-around that re-enters (`GoAroundPhase.ReenterPattern`, `GoAroundHelper.cs`:49-61) and a closed-traffic departure as upwind (index 1); a departure leaving the pattern: refuse "Unable, {T} is departing" (§3-8-1 sequences arrivals behind arrivals; AIM §4-4-14.a.2 NOTE) | Δ |
| FREE | Free pursuit with a pattern return to the follower's circuit, no ahead gate (CD:3971-3982) | Apply the lead-ahead cone: refuse when outside it (AIM §4-3-5, a reversal to chase traffic behind is a major maneuver) | Δ |
| XRWY | Downwind entry to the lead's runway (CD:3785, 3804, 3894-3932) | Same (trainer affordance, see the doc's *Pattern-aware FOLLOW install*) | |
| ELSE | "Unable, {T} is inbound to {APT}, request vectors" (CD:4029-4032) | Same | |
| GND-R | Accepted, then the next tick's lifecycle ends the follow with "target landed" (CD:4026, AFH:237-250) | Same (AIM §4-4-14.a.2 NOTE) | |
| GND | "Unable, {T} is on the ground" (CD:4024-4027) | Same | |

## B. Follower on upwind or crosswind (`UpwindPhase`, `CrosswindPhase`)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| ENT | Retarget in place; lead is flow-behind, so no hold and baseline speed; the follower flies its own circuit and lands first (AFH:359-390, 1072; `UpwindPhase`:184, `CrosswindPhase`:165) | Refuse: "Unable, on {leg} for runway {rwy}, {T} is not ahead of us, request vectors" (AIM §5-5-12.a.2, §4-3-5) | Δ |
| Same leg, lead ahead | Retarget; leg held until remaining path ≥ lead's + desired (AFH:1039-1100); "ahead" decided by elapsed time (AFH:383-388) | Accept; decide by remaining path | Δ (ordering only) |
| Same leg, lead behind | Retarget; flow-behind, no effect, lands first | Refuse as for ENT | Δ |
| Later leg (XW for an upwind follower, DW, BASE, FIN) | Retarget; the leg is held by remaining path (AFH:1039-1100) | Same (AIM §4-3-2.a.3.2 upwind is a sequencing leg; §3-8-1) | |
| APP | Retarget; lead index null, so no leg hold (AFH:1062) | Accept; hold by remaining path against the lead's distance to the threshold | Δ |
| GA | Retarget; index null, no hold | Go-around lead as upwind (index 1): ahead of a crosswind follower (accept, hold), and ordered by remaining path against an upwind follower | Δ |
| DEP | Retarget; index null | Closed traffic as upwind; departing lead refused as in A | Δ |
| HOLD | FREE or index-null row depending on the runway (**unverified** per issue path) | As FREE / as APP | |
| FREE | Free pursuit with a pattern return (CD:3971-3982); no ahead gate | Lead-ahead cone; refuse outside it | Δ |
| XRWY | Downwind entry to the lead's runway | Same | |
| ELSE, GND-R, GND | As in A | Same | |

## C. Follower on downwind (`DownwindPhase`)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| ENT, UW, XW | Retarget; lead flow-behind, no hold, baseline speed; the follower turns base at its normal point and lands first (AFH:515-523, 728-731, 895-910) | Refuse, "Unable, on downwind for runway {rwy}, {T} is not ahead of us, request vectors" (AIM §4-3-5: following it takes a 360; the controller's tool for that is §3-8-1 MAKE LEFT/RIGHT THREE-SIXTY) | Δ |
| DW ahead, holding out (EXT or past its own base point) | Retarget; flow-ahead, base turn held by the 3-9 line and projected ETAs (AFH:413-421, 441-478, 809-890) | Same | |
| DW ahead, not holding out | Retarget; neither flow test true; proximity extension only (AFH:711-759) | Same; decide "ahead" by remaining path, not elapsed time | Δ (ordering only) |
| DW behind | Retarget; flow-behind, no effect, lands first | Refuse as for ENT | Δ |
| BASE, FIN | Retarget; base turn held until the lead is aft of the 3-9 line and the projections clear (`DownwindPhase`:353-356, AFH:809-890) | Same (§3-10-3, AIM §4-3-4.d, 14 CFR §91.113(g)) | |
| APP (on final by geometry) | Retarget; index null, so no 3-9 hold and no ETA hold; only the straight-line proximity hold (AFH:711-759), which a lead on the reciprocal final (about one circuit width abeam) does not normally trip, so the follower can turn base ahead of an ILS lead | Accept; treat the lead as leg 5 so the base-turn hold applies (AIM §4-3-4.d, §3-10-3) | Δ |
| APP (not yet near final) | As above | Accept when sequence-ahead (lead's distance to the threshold shorter than the follower's remaining path); refuse otherwise | Δ |
| GA | Retarget; index null, no hold | Go-around lead as upwind: behind, refuse | Δ |
| DEP | Retarget; index null | Closed traffic as upwind (behind, refuse); departing lead refused | Δ |
| HOLD | FREE or index-null row (**unverified** per issue path) | As FREE / as APP | |
| FREE | Free pursuit with a pattern return; no ahead gate (CD:3971-3982) | Lead-ahead cone; refuse outside it | Δ |
| XRWY | Downwind entry to the lead's runway | Same | |
| ELSE, GND-R, GND | As in A | Same | |

## D. Follower on base (`BasePhase`)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| FIN (phase or by geometry) or BASE ahead | Retarget; `BaseFollowSpacing` keeps, widens, breaks off to the turn-out, or goes around (`BasePhase`:223, 319, 377-386; BFS; VFP:1724-1788) | Same (AIM §4-3-5, §5-5-12.a.1) | |
| APP on final by geometry | Retarget; handled by `BaseFollowSpacing` through `IsOnFinalByGeometry` | Same | |
| APP not yet on final | Retarget; `BaseFollowSpacing` idle (it needs final, landing or base-ahead), no other hold; follower lands first | Refuse when not sequence-ahead ("…{T} is not ahead of us, request vectors") | Δ |
| ENT, UW, XW, DW, BASE behind | Retarget; `BaseFollowSpacing` idle and speed at baseline (AFH:515-523); follower lands first and the follow sits unused until sight is lost | Refuse with the existing base text (AIM §5-5-12.a.2; following it needs a 360, AIM §4-3-5) | Δ |
| GA, DEP | Retarget; nothing acts | Refuse (a go-around is no longer a factor, AIM §4-4-14.a.2 NOTE; a departure is not in the landing sequence) | Δ |
| HOLD | FREE row or index-null row (**unverified** per issue path) | As FREE / refuse when not sequence-ahead | |
| FREE | Lead-ahead gate: inside ±60°, free pursuit with a pattern return (from base: pattern altitude, no glideslope descent, VFP `OnStart`); outside, refused (CD:3976-3981) | Same | |
| FREE with a pattern entry queued for another runway | "Unable, on base for runway {rwy}, request vectors to follow {T}" (CD:3956-3964) | Same | |
| XRWY | Same refusal (CD:3794-3797) | Same (AIM FIG 4-3-3 key 7, §4-3-5) | |
| ELSE, GND-R, GND | As in A | Same | |

## E. Follower on final (`FinalApproachPhase`, pattern or straight-in)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| FIN ahead, landing airborne | Retarget; speed spacing capped at ±10 kt, S-turn outside 5 nm, structural go-around (`FinalApproachPhase`:727, 783, 1407-1439; AFH:1116-1162) | Same (7110.65 §5-7-1.b.4 inside 5 nm; AIM §4-3-5) | |
| FIN ahead but joined final later (e.g. a straight-in spawned closer in) | Retarget; the elapsed-time tiebreak can call it flow-behind (AFH:383-388), so the speed spacing is skipped; the S-turn uses along-track geometry and still works outside 5 nm | Accept; ordering by distance to the threshold | Δ |
| FIN behind, BASE, DW, XW, UW, ENT | Retarget; flow-behind, no effect, lands first | Refuse: "Unable, on final for runway {rwy}, {T} is not ahead of us, request vectors" (AIM §5-5-12.a.2). The ±60° cone is not a safe test here: from a 3 nm final a lead on downwind abeam the threshold bears about 14° off the track | Δ |
| APP ahead on the same final | Retarget; index null, speed spacing by straight line only | Accept; count it as leg 5 | Δ |
| APP behind, GA, DEP | Retarget; nothing acts | Refuse | Δ |
| FREE | "Unable, on final for runway {rwy}, request vectors to follow {T}" (CD:3971-3974) | Same | |
| XRWY | Same refusal (CD:3794-3797) | Same | |
| ELSE, GND-R, GND | As in A | Same | |

## F. Follower on an instrument approach (`InterceptCoursePhase`, `ApproachNavigationPhase`)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| FIN, APP ahead, same runway | Approach torn down keeping the landing clearance (CD:3845-3849); a FIN lead with the follower in the corridor gets free pursuit and `TryJoinLeadFinal` (VFP:1235-1290); otherwise a downwind entry to the runway it was already approaching; an APP lead gets free pursuit without a pattern return (CD:3870) | Keep the approach and retarget in place with the final-approach speed spacing (7110.65 §7-4-3.c.2; AIM §5-5-12.a.1) | Δ |
| Same runway, lead behind (pattern legs, APP farther out, GA, DEP) | Downwind entry or free pursuit as above; no ahead test | Refuse "Unable, {T} is not ahead of us, request vectors" | Δ |
| XRWY | Downwind entry to the lead's runway, no refusal (the cross-runway refusal only covers base/final, CD:3794) | Refuse inside the final approach fix as from final; outside it, re-sequence as the pattern legs do (**unverified** which the user wants) | Δ |
| ELSE, GND | No refusal: downwind entry or free pursuit; a ground lead ends the pursuit next tick (AFH:237-250) | Refuse as in A | Δ |
| FREE | Free pursuit without a pattern return | Lead-ahead cone | Δ |

## G. Follower already following (`VfrFollowPhase`, including a turn-out)

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| Same lead, established toward a runway | A downwind entry replaces the pursuit, discarding a turn-out and its pattern return (CD:3851-3858) | Acknowledge and keep the pursuit unchanged | Δ |
| Same lead, FREE | Retarget in place (CD:3863-3868) | Same | |
| New lead | Established: downwind entry; FREE: retarget (the old path and base are dropped) | Apply the row of the leg the pursuit came from (`FollowPatternReturn.FromBase` → table D) or, with no pattern return, the lead-ahead cone | Δ |
| ELSE, GND | No refusal (`IsPatternLeg` is false for `VfrFollowPhase`) | Refuse as in A | Δ |

## H. Follower not on a pattern leg (no phase, VFR inbound, go-around, departure climb, holds, S-turn, 360/270, low approach, pattern exit, helicopter airborne)

All of these return `ClearsPhase` except `AirspaceBoundaryHoldPhase` (Allowed); the chain and any landing clearance are dropped before routing (CD:455-460, 2726-2739).

| Lead | Today (evidence) | Proposed (grounding) | Δ |
|---|---|---|---|
| ENT…FIN (established) | Downwind entry to the lead's runway, or free pursuit in the final corridor (CD:3851-3858, 3894-3932) | Same for an inbound or holding follower (7110.65 §7-6-7.a); no command-time ahead gate, since falling in behind is the entry's job | |
| Any, follower in `GoAroundPhase` re-entering the pattern | The go-around climb is cancelled and a downwind entry is built from over the runway; the shape of that entry is **unverified**; a pre-issued landing clearance is dropped | Accept FOLLOW additively in `GoAroundPhase` (keep the climb, treat as upwind, table B) | Δ |
| Any, follower departing in closed traffic (airborne `TakeoffPhase`) | The circuit is cleared and a downwind entry built (`TakeoffPhase`:236-241) | Treat as upwind (table B). Whether closed traffic always skips `InitialClimbPhase` (`DepartureClearanceHandler.cs`:736-740 `removeInitialClimb`) is **unverified** | Δ |
| APP, GA, DEP, HOLD, FREE | Free pursuit without a pattern return, no ahead gate (CD:3870) | Same for a follower outside the pattern (AIM §5-5-12.a.1 lets it maneuver to fall in trail) | |
| GND-R, GND | Free pursuit installed, then the next tick ends it with "target landed" (AFH:237-250), leaving the follower with no phase | Refuse "Unable, {T} is on the ground" | Δ |
| ELSE | Re-sequenced onto the other airport's runway or pursued | Refuse when the follower's own destination or airport is known and differs (**unverified** whether the user wants a VFR to be able to follow traffic to another field) | Δ |
| Helicopter-specific phases | `HelicopterApproachPhase` and `HelicopterTakeoffPhase` clear; `HelicopterLandingPhase` rejects; what a helicopter pursuit then does is **unverified** | No proposal | |

## J. Follower landing or on the runway (`LandingPhase` airborne, `TouchAndGoPhase`, `StopAndGoPhase`)

Rejected by the phase: "aircraft is committed to landing on stabilized approach / flare; only GA, EL/ER/EXIT, or DEL apply" (`LandingPhase`:1622-1634), and the touch-and-go/stop-and-go texts (`TouchAndGoPhase`:179, `StopAndGoPhase`:189). Proposed: same (AIM §4-4-14.a.2 NOTE: the landing phase ends the sequencing question).

## K. Follower on the ground

Rejected by the ground phase's own text (e.g. `TaxiingPhase`:299, `HoldingShortPhase`:269, `AtParkingPhase`:108, `RunwayExitPhase`:1213, `LandingPhase` rollout :1647), or "FOLLOW requires the aircraft to be airborne" with no phase (CD:3688-3691). Proposed: same; the ground verb is `FOLLOWG`.

## Z. FOLLOWF on any phase in tables A–G

| Today (evidence) | Proposed | Δ |
|---|---|---|
| `FollowForce` is not listed by any phase, so the pattern legs, final, the approaches and `VfrFollowPhase` return `ClearsPhase` (`BasePhase`:408 and peers). The chain, runway and landing clearance are dropped and FOLLOWF routes as table H: the base/final refusals, the lead-ahead gate, the cross-runway and elsewhere/ground refusals are all bypassed, and a follower on final is pulled off it into a downwind entry | FOLLOWF behaves exactly as FOLLOW except for the RTIS bypass (`COMMANDS.md` documents only that difference) | Δ |

No test covers FOLLOWF on a pattern leg (`AirborneFollowTests.cs`:331-356 cover the RTIS bypass and the solo block only).

## Recommendation

**Replace the per-leg ahead tests with one sequence test, used at command time and per tick.** For a same-runway lead, "ahead" is "less remaining path to the threshold" (`RemainingPatternPathNm`, with a lead on an approach or on final by geometry measured by its distance to the threshold, a go-around that re-enters counted as upwind, and a closed-traffic departure as upwind). That removes the elapsed-time tiebreak (AFH:383-388) and gives APP/GA/DEP leads a place in the order. The ±60° bearing cone stays for leads with **no runway**, where there is no path to compare. The cone must not be used for same-runway leads: on final or downwind it misreads a lead on another leg (a downwind lead abeam the threshold is ~14° off a 3 nm final track).

**Where the gate applies, beyond base:**

- Same-runway lead not sequence-ahead: refuse from upwind, crosswind, downwind, base, final and an approach (an entry follower is leg 0, so only a shared-entry lead can be behind it, and that case is ordering), with the existing text shape "Unable, on {leg} for runway {rwy}, {T} is not ahead of us, request vectors". Today these are silent accepts with no effect.
- Runwayless lead outside the ±60° cone: refuse from every pattern leg (today base only) and from an approach; keep the final refusal.
- No command-time gate for a follower outside the pattern (table H): 7110.65 §7-6-7.a expects it to maneuver into the sequence.

**Cells where today and proposed differ (implementation work), by weight:**

1. Z: FOLLOWF on every FOLLOW-accepting phase clears the phase and bypasses every refusal.
2. C/D/E, same-runway lead behind in sequence (DW, BASE, FIN followers): silent accept → refusal.
3. B, same-runway lead behind (ENT, same leg behind): silent accept → refusal.
4. A/B/C/F/G, runwayless lead outside ±60°: accept → refusal (extends today's base gate).
5. A/B/C/D/E, APP lead (and on-final-by-geometry leads in any phase): no leg hold or ordering → counted in the sequence (a downwind follower can turn base in front of an ILS lead today).
6. Shared-leg ordering on every leg: elapsed time → remaining path (E's later-joined straight-in lead is the visible failure).
7. F, follower on an approach: teardown into a downwind entry → keep the approach and retarget for a same-runway lead ahead; refuse a lead behind; add the elsewhere/ground refusals.
8. G, re-FOLLOW of the same lead during a pursuit or turn-out: rebuilt as a downwind entry (before B5a) → acknowledged, unchanged (shipped in B5a).
9. A/B/C/D/E, GA and DEP leads: nothing acts → go-around/closed traffic as upwind; a departing lead refused.
10. H, follower in a go-around or a closed-traffic takeoff: phase cleared → FOLLOW additive, treated as upwind.
11. H, ground lead: accept then immediate end → refusal; ELSE lead from outside the pattern: re-sequence to another airport → refusal (pending the question below).

**Decisions** (user 2026-09-29, each the recommended option):

1. A same-runway lead that is behind in sequence, from upwind/crosswind/downwind: refuse with "…{T} is not ahead of us, request vectors" (AIM §4-3-5, AIM §5-5-12.a.2).
2. Cone width for runwayless leads on the legs other than base: ±60°, the base value.
3. A follower on an approach told to follow traffic landing a different runway (table F, XRWY): refuse inside the final approach fix, re-sequence outside it.
4. A follower outside the pattern told to follow traffic bound for another airport (table H, ELSE): refuse when its own destination differs.

With these, every differing cell in the list above is decided and becomes implementation work, the FOLLOWF defect (1) first.

**Implementation rulings** (aviation consult 2026-09-30; [J] marks a judgement figure):

- **Behind in sequence** on a shared leg is position, not time: a closer, slower lead is still ahead (7110.65 §3-8-1; AIM §4-3-4.d). Across different legs it is leg order (B3 rulings below replace the remaining-path measure and its 0.5 NM tolerance).
- **Inside the FAF** (a different-runway lead, follower on an approach) is along-final distance to the threshold at or below the smaller of the published FAF distance and 5 NM, 5 NM when there is no FAF (7110.65 §5-7-1.b.4's "whichever is closer to the runway") [J]. An `InterceptCoursePhase` follower not yet on final is outside: re-sequenced, not refused. Pattern and straight-in finals keep today's refusal.
- **FOLLOW in a go-around that re-enters the pattern, or a closed-traffic takeoff climb**: the climb is kept (and today's `NoTurnAgl` 400 ft limit), FOLLOW only sets the lead, and the follow helper counts the go-around as leg 1 (upwind, AIM §4-3-2.a.3.2). A go-around flying a published missed approach (`ReenterPattern` false) accepts FOLLOW as a replacement of the missed approach, as today, keeping the climb's `TargetAltitude` as the altitude to fly [J]; it is not counted as upwind and takes no command-time behind check.

**Brief split** (exploration 2026-09-30; `CommandDispatcher.cs` is a hotspot, so B3–B6 run in order):

- B1: FOLLOWF normalised to FOLLOW at the phase gate (cell 1).
- B2: per-tick ordering in `AirborneFollowHelper` (cells 5, 6, 9): APP and on-final-by-geometry leads as leg 5, shared-leg ordering by remaining path, GA/DEP leads, and a public `IsLeadAheadInSequence(follower, lead)` predicate with the 0.5 NM tolerance. Independent of B1.
- B3: the command-time sequence refusal (cells 2, 3, the departing-lead refusal), consuming B2's predicate. Two things B2 left that B3 must settle: `IsLeadAheadInSequence` and the per-tick leg order can disagree across legs (a lead extended 3 NM on downwind against a crosswind follower is ahead per tick, leg 3 > 2, but about 0.7 NM behind by the predicate), so the refusal and the running follow must use one answer; and `LeadRemainingPathNm` measures every lead not on base by along-final distance, which misreads a lead on upwind or downwind (its `VfrFollowPhase.IsLevelOrAhead` caller, ~:1802, can pass such leads). B2 orders a shared outbound leg (upwind, crosswind, downwind) by progress along the leg and the other legs by remaining path to the threshold.
- B3 rulings (user 2026-09-30, from the B3 exploration and implementation): across different legs the refusal and the running follow both use leg order (the later leg is ahead; an approach or final-by-geometry lead is leg 5, a re-entering go-around or closed-traffic climb leg 1), so the refusal is `IsLeadPatternFlowBehind` plus "the same-runway lead has no sequence leg"; shared legs keep position order (`SharedLegOrderNm`) and the held-lead rule stays. Remaining-path order across legs was tried and rejected: a lead's extension adds path, so the order flips the moment it turns onto the next leg (a follower overtook a lead extended on crosswind, turned base inside a lead extended on downwind, and never turned base behind a re-entering go-around). `IsLeadAheadInSequence` and the 0.5 NM cross-leg tolerance go with it. A lead still on a pattern entry (leg 0) is accepted, since it may join downwind ahead (recorded controller sessions do exactly this); per tick it stays flow-behind until it joins. A follower on base, final, final by geometry or an instrument approach is ordered by remaining path to the threshold instead, at command time and per tick (the lead is ahead when its path through its circuit or along its approach is no longer than the follower's; no tolerance): base, final and approach converge on one final, so an extension cannot flip the order there, while leg order would refuse a 6 NM straight-in following a Cessna on close base and accept a close base behind a 10 NM final (aviation review, user 2026-09-30). From base or final an entry lead is refused (getting behind a 45° entrant would need a 360, AIM §4-3-5); from the outbound legs it is accepted. The departing-lead refusal is checked first, ahead of the runwayless and cross-runway refusals. Wordings: pattern legs "Unable, on {leg} for runway {rwy}, {T} is not ahead of us, request vectors"; a follower on an approach "Unable, on approach for runway {rwy}, {T} is not ahead of us, request vectors", checked before `InstallFollow` tears the approach down; a departing lead (airborne in a departure phase, not a closed-traffic climb) "Unable, {T} is departing, request vectors". The study's premise that `VfrFollowPhase.IsLevelOrAhead` can pass an upwind or downwind lead to `LeadRemainingPathNm` is wrong: `TurnOutCircuitFor` gates it to base/final leads, so that misread is latent (a Backlog line).
- B4 (shipped): the ±60° cone on every leg for a runwayless lead (cell 4). B4 rulings (user 2026-09-30): B4 covers the pattern legs only (entry, upwind, crosswind, downwind; base already had the cone, final refuses); the approach and pursuit followers of cell 4 get the cone in B5, which rewrites `InstallFollow`. A follower on a pattern entry is worded "Unable, on pattern entry for runway {rwy}, {T} is not ahead of us, request vectors". The queued-other-runway entry rebuild keeps no cone (an explicit re-sequence). Upwind and crosswind followers also accept a lead in the downwind box (aviation review 2026-09-30; `docs/approach-and-pattern-geometry.md`): the ±60° track cone alone refused traffic on the downwind line the follower joins behind by flying its circuit, and a cone on the downwind heading accepted a lead directly astern (upwind and downwind are antiparallel), so it was replaced by the box.
- B5: the runwayless-lead ±60° cone for approach and pursuit followers (cell 4's F and G rows, moved from B4); approach followers, re-FOLLOW of the same lead, ground and elsewhere leads from outside the pattern (cells 7, 8, 11). B3 shipped the approach refusal (by remaining path); B5 keeps the approach instead of the downwind-entry teardown. Also give a pattern-entry lead a real remaining path: `RemainingPatternPathNm`'s leg-0 case falls into the final formula (a straight line to the threshold), understating a 45° entrant's path, so an approach follower can accept it as ahead (aviation review of B3, 2026-09-30).
- B5 split (2026-09-30, from the B5 exploration): **B5a** the cone for approach and pursuit followers (approach wording "Unable, on approach for runway {rwy}, {T} is not ahead of us, request vectors", pursuit "Unable, {T} is not ahead of us, request vectors"), the ground refusal "Unable, {T} is on the ground" from outside the pattern (GND and GND-R) from a pursuit (GND only; a lead rolling out on the pursuit's runway is accepted and ends by lifecycle) and from an approach (every ground lead: accepting would tear the approach down; aviation review 2026-09-30, revisit once B5b keeps the approach); B5a shipped with two review additions — a same-lead re-FOLLOW is acknowledged before the cone, and an approach follower's cone also accepts a lead nearer its threshold than its own remaining path (course reversals), the elsewhere refusal (existing text) from outside the pattern, a pursuit or an approach when the follower's own airport (assigned runway's airport, else destination) is known and differs, a same-lead re-FOLLOW during a pursuit or turn-out acknowledged with the ordinary response and nothing changed, and a refused `FOLLOWF` leaving traffic-in-sight as it was (the B3 leftover (c)). **B5b** (after B5a; `CommandDispatcher.cs` is a hotspot): keep the approach (phase list and landing clearance kept, only `FollowingCallsign` set), the XRWY rule, a new lead chosen during a pursuit applying table D when the pursuit's constructor `PatternReturn.FromBase` is true (a turn-out's copy loses it) and the cone otherwise, the pattern-entry lead's path, and pre-final speed spacing. B5b rulings (aviation 2026-09-30; XRWY by the user 2026-09-30):
  - Entry lead's remaining path: lead → its join point along the entry route, plus the existing leg formula at the join. `PatternEntryPhase`: `LeadInLat/Lon` if not passed, then `EntryLat/Lon`, then by `Kind` — `Direct`/`FortyFive`/`Crosswind`/`Midfield` join downwind (leg 3 with ρP = width), `Base` leg 4, `Final` the final formula, `Upwind` leg 1. `MidfieldCrossingPhase`: the midfield target (midpoint of `DownwindStart` and `DownwindAbeam`), then with a `TeardropReentryPhase` queued TDROP-OUT → TDROP-LI → `DownwindAbeam` and leg 3 there, otherwise leg 3 at the midfield target. `TeardropReentryPhase`: the fixes left in its `NavigationRoute` to `DownwindAbeam`, then leg 3. Straight lines between points, no corner-cut credit [J]. Measured, not refused: an approach follower 8–10 NM out can fall in trail (AIM §5-5-12.a.1; 7110.65 §3-8-1).
  - Pre-final speed spacing on a kept approach: `InterceptCoursePhase` (once its approach speed is set or the course captured) and `ApproachNavigationPhase` call `GetAdjustedSpeed` with the phase's scheduled speed as the ceiling, only when there is no explicit ATC speed (`HasExplicitSpeedCommand`) and not `LateralInterceptOnly`; decrease only [J], floored at the category approach speed plus the wind additive [J] (7110.65 §5-7-1.d; AIM §5-4-23.e, §5-5-11.b.4, §5-5-9.a.2–3).
  - XRWY from an approach: an IFR follower is refused inside and outside the FAF, "Unable, on approach for runway {rwy}, {T} is landing runway {rwy2}, request vectors" (7110.65 §7-4-3.c.2: a follow only to the same runway); a VFR follower (`FlightPlan.IsVfr`, a practice approach) is refused inside the FAF (along-final distance ≤ min(published FAF distance, 5 NM), 5 NM with no FAF; a new `ApproachGateDatabase` accessor for the raw FAF distance, displacement added) and re-sequenced with a downwind entry outside it. This replaces decision 3 for IFR followers.
- B5b brief split (exploration 2026-09-30): **B5b-1** the XRWY refusal from an approach plus `ApproachGateDatabase.GetFafDistanceNm` (placed after `RouteOffPatternFollow`, before `CrossRunwayFromLegRefusal`; a follower or lead with no assigned runway is not refused; no flight rules counts as IFR; the VFR re-sequence outside the FAF unchanged); **B5b-2** keep the approach; **B5b-3** a new lead during a pursuit; **B5b-4** the entry lead's path (lead-in passed when the lead's `NavigationRoute` no longer holds `PTN-LEADIN`); **B5b-5** pre-final spacing. B5b-1, -2, -3 in that order (`CommandDispatcher.cs`); -4 beside them; -5 after -2.
- B5b-2/3/4/5 rulings (aviation 2026-09-30):
  - **Keep the approach** (B5b-2): only `Approach.FollowingCallsign` changes; the response is `Follow {T}` with no warning; the turn-rate override, `HasExplicitTurnRate`, `PreferredTurnDirection`, altitude and speed targets, `HasExplicitSpeedCommand`, `HasReportedTrafficInSight` and `LastReportedTrafficCallsign` are kept. It is not a visual approach (7110.65 §7-2-1.a.2; AIM §5-5-12.a.1). When an IFR follower on a kept non-visual approach loses sight, the instructor is also told `{Callsign} visual separation terminated — radar separation required` and the approach continues. No command-time gap refusal for a lead already on final; the running checks (pre-final spacing, `FinalApproachPhase`'s S-turn, break-off and unable-to-maintain cancel) handle it. `TryJoinLeadFinal`/`CanJoinLeadFinalDirectly` stay for followers outside the pattern.
  - **VFR re-sequence to another runway** (B5b-2): the landing clearance is dropped (7110.65 §3-10-5.a/c; `TryEnterPattern` already voids it) and the warning says so, verbatim `{Callsign} {summary} cancelled by FOLLOW, landing clearance RWY {oldRwy} cancelled` (one runway token throughout; the runway is kept even when the summary names it, since a re-sequence from a pattern leg has a summary with no runway); the pilot says `Follow {T}`. A `LandingClearance` with no `ClearedRunwayId` counts as a clearance for the follower's previous `AssignedRunway`.
  - **B5b-2 scope rulings** (aviation consult 2026-09-30, from the B5b-2 exploration):
    - A **runwayless lead** for an approach follower (inside the cone) also keeps the approach, IFR and VFR, setting only `FollowingCallsign` [J]: a lead with no runway gives no landing sequence, an IFR follow is pilot-applied visual separation and never leaves the cleared approach (7110.65 §7-2-1.a.2, §7-4-3); today's teardown into a pursuit goes.
    - **Approach phases kept**: `InterceptCoursePhase`, `ApproachNavigationPhase` and `ProcedureTurnPhase`, and `HoldingPatternPhase` only when it is the approach's hold-in-lieu of a procedure turn — all segments of the cleared approach (7110.65 §4-8-11.a); a plain en-route or published hold keeps today's behaviour [J]. `FinalApproachPhase` is already kept.
    - **Loses-sight warning** `{Callsign} visual separation terminated — radar separation required`: keyed on an IFR follower (no flight rules counts as IFR) with an active follow that is not on a visual approach, whatever the phase — pattern legs included (7110.65 §7-2-1, §4-8-11.c); a VFR follower gets no warning (§4-8-11.d.2/d.4 [J]), only its own "lost sight" call (AIM §5-5-12.a.2). A visual approach keeps its existing path.
    - **Lead goes around or flies the missed** while the follower is before final on a kept approach: the follow ends, the approach continues, no pilot transmission (the lead is still in sight); the instructor sees `{Callsign} follow of {T} ended — {T} went around` [J], and an IFR follower also gets the visual-separation-terminated line (AIM §5-5-12.a.1, 7110.65 §7-2-1). Not `FinalApproachPhase`'s baseline-spacing rule: a re-sequencing lead can end up behind the follower [J].
    - **Repeat FOLLOW of the same lead** on a kept approach: `Follow {T}`, nothing changed, traffic-in-sight, speed and turn state kept [J] — only while the lead still lands the follower's runway or has none; a lead since given another runway goes through the cross-runway rules (7110.65 §7-4-3.c.2).
  - **B5b-2 review rulings** (aviation 2026-09-30, after the implementation):
    - The **published missed approach is not the approach**: a phase built from the missed-approach fixes is flagged (`ApproachNavigationPhase.IsMissedApproach`) and has no sequence leg. FOLLOW to an IFR follower on its own missed is refused `Unable, on the missed approach, request vectors` (7110.65 §4-8-11.e.1, §4-8-9); a VFR follower on its missed is re-sequenced into the pattern as before, with no landing clearance (§4-8-11.e.2, §3-10-5).
    - **IFR follower** everywhere in the follow code = `HasFlightPlan && !IsVfr`: no flight plan counts as VFR (14 CFR §91.173; §4-8-11.d). This replaces "no flight rules counts as IFR" above.
    - A **lead going around** (`GoAroundPhase`, either way) or on its missed, followed from an approach, is refused at command time: `Unable, {T} is going around, request vectors` [J] (AIM §5-5-12.a.1). Go-around leads for pattern followers stay with B6.
    - A **procedure-turn or hold-in-lieu follower's** remaining path is the straight line to the turn or hold fix plus the path along the approach from that fix to the threshold, with no credit for the outbound leg or the turn [J] (§4-8-11.a); a lead on one keeps no leg. The hold-in-lieu is an explicit flag (`HoldingPatternPhase.IsHoldInLieu`), not inferred from a circuit limit.
  - **New lead during a pursuit** (B5b-3): with `PatternReturn.FromBase` and no turn-out, the pursuit's path is `ShortestPathToThresholdNm(FinalFrameOf(position, heading, PatternReturn.Runway, PatternReturn.Direction), BasePhase.TurnRadiusNm(GS, cat))` (as `IsLevelOrAhead`), the lead ahead when `SequenceRemainingPathNm(lead, runway)` is no longer, no tolerance; accepted in place by `UpdateTarget`, `PatternReturn` kept, no downwind entry. Refusals: behind or an entry lead `Unable, {T} is not ahead of us, request vectors`; another runway `Unable, {T} is landing runway {rwy2}, request vectors`; a runwayless lead the cone. During a turn-out the same comparison runs on the turn-out's own circuit. With `FromBase` false and a runway-bearing lead, table H: no ahead gate, today's downwind entry; the cone stays for runwayless leads only. Shipped; review ruling (aviation 2026-09-30): a straight-in entrant (an entry whose joined leg is the final) is compared by path, not refused as an entry lead (AIM §4-3-3 NOTE 1); accepting a new lead ends a running turn-out; a ground lead keeps B5a's rule.
  - **Pre-final spacing** (B5b-5): `GetAdjustedSpeed` (not the free-flight variant) with `MaxSpeedAdjustKts` [J]; a null result leaves `TargetSpeed` alone; `TargetSpeed = min(adjusted, ceiling)`, floored at `ApproachSpeed + WindApproachAdditive(weather, AssignedRunway.TrueHeading)`. Ceiling: `InterceptCoursePhase`'s 1.3 × FAS; `ApproachNavigationPhase` latches `Targets.TargetSpeed ?? IndicatedAirspeed` the first time spacing runs, replaced by each fix `SpeedKts`, snapshotted, never last tick's output. The same decrease-only rule runs in `FinalApproachPhase` before `_fasSet`.
  - **Entry leads for an approach follower** (B5b-4): measured by path, not refused, at command time (`IsLeadBehindInSequence`'s entry arm) and per tick for any leg-5 follower (`IsLeadAheadAcrossLegs`); base and final followers keep refusing an entry lead.
- **B5b-5 brief-ready** (exploration and aviation consult 2026-09-30; the consult corrects the clamp above): `TargetSpeed = max(min(adjusted, ceiling), min(floor, ceiling))`, so spacing never writes above the ceiling and is a no-op when the floor reaches it. Rulings:
  - `InterceptCoursePhase` (`OnTick` after `CheckLeadLifecycle`, ~:135; the speed step ~:167-185): spacing runs once `_approachSpeedSet` (same or next tick), never while cross-track ≥ 2 NM; ceiling 1.3 × bare `ApproachSpeed` recomputed each tick (no wind additive, no snapshot field) [J]; floor `ApproachSpeed + WindApproachAdditive(ctx.Weather, AssignedRunway.TrueHeading)`, none beyond bare FAS with no assigned runway (AIM §5-4-23.e; the assigned runway's heading, not the final course, so the floor does not jump at the hand-off on an LDA/SDF final).
  - `ApproachNavigationPhase` (`OnTick` after ~:91; fix speeds in `NavigateToCurrentFix` ~:181): latched ceiling `Targets.TargetSpeed ?? IndicatedAirspeed` at the first spacing tick, replaced by each fix `SpeedKts`, re-latched the same way at the first spacing tick after an explicit speed clears (7110.65 §5-7-4.a NOTE), snapshotted as an optional `double? SpacingCeilingKts` on `ApproachNavigationPhaseDto` (null = not latched; no schema bump, the `IsMissedApproach` precedent).
  - `FinalApproachPhase` before `_fasSet` (near ~:689-718, the same `!lateralOnly`/no-explicit-speed gate): ceiling the latest speed the phase's own schedule wrote (the approach-flap step ~:1221 or the config step fas × `ConfigSpeedMultiplier` ~:1183), else a latch of `Targets.TargetSpeed ?? IndicatedAirspeed` taken at phase entry (snapshotted); floor the phase's `fas` (wind included). Spacing writes never set `_configSet`/`_flapSet`.
  - `GetAdjustedSpeed(ctx, ceiling, floor, MaxSpeedAdjustKts)` (`AirborneFollowHelper` ~:846; not the free-flight variant); null leaves `TargetSpeed` alone; only with no explicit ATC speed and not `LateralInterceptOnly`.
  - Its cancel ("unable to maintain separation", ~:1018-1035) stays (AIM §5-5-12.a.2); on a kept approach it clears only `FollowingCallsign` (phases, landing clearance, traffic-in-sight kept) and an IFR follower also gets `{Callsign} visual separation terminated — radar separation required` — verify the cancel path does not tear the approach down.
  - Lead on the ground: stop spacing and restore the ceiling. Follower inside the 60 s stabilization window: stop spacing and leave `TargetSpeed` as it is [J].
  - Files: the three phases, `PhaseSnapshotDto.cs`, possibly a shared decrease-only helper in `AirborneFollowHelper.cs` (land before B6, which edits it). Tests beside `FollowKeepApproachTests` (KOAK ILS fixture); `FollowRunawayIasTests` (S2-OAK-3 recording) and other FOLLOW-on-approach recordings may desync.
- B6: go-around and closed-traffic climb followers (cell 10), last since it touches `AirborneFollowHelper` too. Also settle a closed-traffic climb to another pattern runway (`DepartureRunway` set): `IsClosedTrafficClimb` excludes it, so B3 refuses such a lead as departing.
