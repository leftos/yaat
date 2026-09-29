# FOLLOW sequencing from pattern legs + `WAIT <n>NM`

Status: A (routing), B (`TryJoinLeadBase`, with the free-pursuit spacing rework in "Rulings during B"), C (pattern return) and D (`WAIT <n>NM`) have shipped, aviation- and code-reviewed; E (spacing on base) is next. Shipped differently from the text below: pattern altitude is flown from every leg but base (base keeps the lower of present and pattern altitude); any lead on the ground not rolling out on the follower's runway is refused; the other-airport refusal needs the lead's runway or filed destination; FOLLOW from `InterceptCoursePhase`/`ApproachNavigationPhase` keeps the landing clearance.

## Context

The bug bundle is `S2-OAK-3 (1) _ VFR Sequencing.yaat-bug-report-bundle.zip`. N123AB was flying a long right base to 28R when it was told RTIS N314GT and then FOLLOW, at t=285 and again at t=349. Both commands were accepted, and neither changed anything.

N314GT was on `DCT VPCBT` with `ERB 28R` queued behind it, so at that moment it had no current runway. `CommandDispatcher.TryAirborneFollow` (~3716-3848) takes an unknown lead runway to mean "same runway" and retargets the follow in place. On `BasePhase` the only follow effect is a speed cap that can only slow the aircraft, and N123AB was already slower and far enough back, so nothing moved.

Mid-session the user also hit a `WAIT 1NM ERB 28R` problem. `CommandSchemeParser.ExpandWaitBlock` rewrites any WAIT argument that is not an integer to `AT <arg>`, so this ended in "AT ground entity not found: taxi 1NM".

## Decisions (user, informed by the aviation-sim-expert review and the mock at https://claude.ai/artifact/MMnL5uZETqExzUSh3WUDSG)

1. **Lead with no current runway, follower on a pattern leg.** The follower trails the lead in free pursuit. This applies even when the lead has an entry for the same runway queued.
   - From Base, pursuit starts only if the lead is ahead, within ±60° of the follower's track. Otherwise FOLLOW is refused.
   - From Final, FOLLOW is refused.
2. **Joining the lead's base.** Once the lead starts its base, the follower flies to the point where the lead's base began. From there it flies Base → Final → Landing on the same runway, in the same pattern direction and at the same final-turn distance. The normal pattern spacing then keeps a safe distance, by speed only. This applies to every pursuit, including a FOLLOW from a clean start.
3. **Pursuit that starts from a pattern leg:**
   - Target altitude is the follower's pattern altitude, set once. From Base it is the lower of the current altitude and pattern altitude. A later CM overrides it.
   - If the follow ends, the follower re-enters the pattern for its own runway.
   - Any widen excursion stays on the pattern side while within 5 nm of the threshold.
4. **Clearances carry over.** Every clearance type (land, touch-and-go, option, stop-and-go, low approach) is kept on the pursuit phase list and reapplied whenever the follower joins the same runway, by any join path.
5. **Queued entry for a different runway.**
   - Follower on downwind, upwind or an entry: FOLLOW builds a pattern entry to the queued runway, the same as today's cross-runway re-sequence. The direction comes from the lead's `TrafficDirection`, otherwise from the queued entry (ERx is right, ELx is left).
   - Follower on Base or Final: FOLLOW is refused.
6. **Follower already on base, lead on final or on base ahead of it.** This is checked every tick. Project the follower's rollout against where the lead will be at that time. The required spacing is the larger of the category pattern spacing and the wake minimum.
   - Spacing is enough: keep the base; speed spacing handles the rest.
   - Follower would roll out behind the lead but too close, and the deficit is small: widen. Turn 30° away from the field and hold that heading until the gap is met or the follower reaches the widen floor, max(1.0 nm, 3 × turn radius) from the centerline. Then fly the base heading again. The widen never turns toward the parallel.
   - Otherwise (the follower would roll out ahead of or level with the lead, or the widen is not enough): break off to pursuit with the pattern return, send a one-shot pilot transmission, and let `TryJoinLeadFinal` sequence the follower in trail.
7. **`WAIT <n>NM` / `DELAY <n>NM`** (decimal allowed, any case) is treated as `WAITD <n>`: `WAIT 1NM ERB 28R` becomes `WAITD 1; ERB 28R`.

Rulings after the review of A (user 2026-09-28):
- **Decision 5's direction order is reversed**: the queued entry's side (ERx right, ELx left) comes first; the lead's phase `TrafficDirection` is used only for an entry that names no side (EF), then the runway default. A runwayless lead's phase direction can only be left over from an earlier circuit.
- **A lead on the ground is refused**: "Unable, {target} is on the ground".
- **A lead bound for another airport is refused**: "Unable, {target} is inbound to {airport}, request vectors". Queued-entry runways are compared with the airport, not the designator alone (OAK and HWD both have 28L/28R).
- **Refusal texts** are spoken in solo training, so they follow `docs/pilot-phraseology.md` (reason, then request; no interior dash): "Unable, on final for runway {rwy}, request vectors to follow {target}"; "Unable, on base for runway {rwy}, {target} is not ahead of us, request vectors"; "Unable, on {leg} for runway {rwy}, request vectors to follow {target}" (lead queued for another runway, also the older cross-runway refusal).
- **Where the "lead ahead" gate applies** (base only today) waits on a permutation study of every lead × follower state the user asked for; its result may add a step. The study is drafted once E lands (user 2026-09-29).
- Step C makes `VfrFollowPhase.PatternReturn` a required constructor parameter (today an init property) together with its DTO field.

Rulings during B (user 2026-09-29, from an `aviation-sim-expert` consult; the recorded case holds the follower 0.86–0.91 nm behind at its speed floor, so no gap gate below the lead's base could be met by speed alone):
- **The base join waits for pattern spacing** (`AirborneFollowHelper.DesiredDistanceForLeader`, 1.0 nm behind a piston). Its base track is judged against the lead's base line (within 0.3 nm), not the start point, because a pattern-entry waypoint completes 0.5 nm out (`FlightPhysics.NavArrivalNm`).
- **A too-close pursuit builds spacing laterally**: user steer, "free fly in a way where they create lateral trailing spacing until they can point their nose directly at the lead and follow in a chain". It starts as soon as the along-path gap is short by more than ~0.1 nm (not waiting for the speed floor) and slows at the same time. It is a shallow S-turn (AIM 4-3-5): 30° off the lead's track, 45° when short by more than 0.3 nm, always to the pattern's outside, never toward the final or a parallel final. The offset is capped at max(1.0 nm, 3 turn radii); no 360 and no 90° turn-out.
- **No room**: at the cap without the gap, hold it parallel at the speed floor and extend past the lead's base turn point, turning base only once the gap is met. At the extension limit (2 nm past the lead's base start), or nearing the final, the follower keeps flying the extended leg and calls "unable to follow the traffic, extending downwind, request base turn" (AIM 5-5-12.a.2), then waits for the controller, as the speed-cancel path does; it never re-enters by a downwind entry, which would reverse it against the downwind flow (AIM 4-3-5; user 2026-09-29 after the aviation review).
- **Gap target**: one target, the join's 1.0 nm plus ~0.1 nm hysteresis, measured along the lead's recorded path; 1.5 nm stays for a lead not in the pattern.
- **Chain once spaced**: nose on the lead while the lead is on a straight leg; through the lead's turns, fly its ground track and turn where it turned (pure pursuit cuts the corner and loses up to ~0.3 nm). This replaces the parallel-trail steady state; AIM 5-5-12.a.1 says in-trail.

Defaults taken without asking:
- The extension limit is 2 nm past the lead's base start point, measured along the extended leg.
- The base join enters at the lead's pattern altitude.
- Elapsed-time ordering on the same leg is kept. A lead's resumed final after an S-turn restarts its elapsed time; see the follow-ups at the end.
- Phraseology of the new refusals and transmissions goes to the aviation review.

## Implementation: five implementer briefs, in the order A → B → C → E, with D independent

Every step is test-first. The proving command is `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test tests/Yaat.Sim.Tests -c Release -- --filter-class "*.<Class>"`. After each brief, build with `-p:TreatWarningsAsErrors=true` and run the `*Follow*` and `S2Oak3*` classes. yaat-server only calls `HasQueuedPatternEntry`, and its signature stays the same.

### A. FOLLOW routing

Files: `PatternCommandHandler.cs`, `CommandDispatcher.cs`, `AirborneFollowHelper.cs`, and the tests `FollowRunwaylessLeadFromPatternTests.cs` and `FollowJoinGateTests.cs`.

- Add `PatternCommandHandler.QueuedPatternEntry(ac) → (RunwayId, PatternDirection?)?`. It reuses `FindQueuedPatternEntry`, `BlockParsedCommands` and `PatternEntryRunwayOf`, and normalizes the runway designator.
- Add `AirborneFollowHelper.IsLeadAheadOfTrack`, true within ±60° of the follower's track.
- Extract `TryFollowFromPatternLeg` and an `internal static InstallVfrFollowPhase(aircraft, target, FollowPatternReturn?)`. Together they carry the routing table from decisions 1, 4 and 5. The new branch only applies when the lead is found, the follower has an assigned runway, and the lead has no current runway. That keeps six existing retarget tests valid: `AirborneFollowTests.Follow_ClearsExtended*` and `VfrFollowPhaseTests.Follow_From*Phase_OnlySetsFollowingCallsign`.
- Add a `ChooseFollowJoinDirection(follower, PatternDirection? leadDirection, runway)` overload, so the dispatcher no longer dereferences `lead.Phases!`.
- Tests:
  - `FollowFromBase_LeadNoCurrentRunwayQueuedSame_AheadInstallsPursuit`, in a new `tests/Yaat.Sim.Tests/Simulation/FollowRunwaylessLeadFromPatternTests.cs`. It replays the committed fixture `tests/Yaat.Sim.Tests/TestData/oak-follow-base-freeflight-lead-recording.yaat-bug-report-bundle.zip` (357 s; it still holds the recorded FOLLOWs at t=285 and t=349, so the test stops at t≈284), then sends FOLLOW to N123AB. The same class also holds a same-runway guard: a follower on a pattern leg with a lead on the same runway's pattern keeps its phase instance and gets `FollowingCallsign` set.
  - `FollowFromBase_LeadQueuedOtherRunway_IsRefused`
  - `FollowFromBase_RunwaylessLeadBehind_IsRefused`
  - `FollowFromFinal_LeadNoCurrentRunway_IsRefused`
  - `FollowFromDownwind_LeadQueuedOtherRunway_BuildsEntryToQueuedRunway`
  - `FollowFromBase_RunwaylessLead_CarriesLandingClearance`
  - `QueuedPatternEntry_*` (unfired, bare, applied, restored from a snapshot and re-parsed)
  - `IsLeadAheadOfTrack_*`

### B. `TryJoinLeadBase`

Files: `BasePhase.cs`, `PhaseSnapshotDto.cs`, `VfrFollowPhase.cs`, `VfrFollowPhaseTests.cs`, `FollowRunwaylessLeadFromPatternTests.cs`.

- `BasePhase.OnStart` records `StartLat`/`StartLon`. They are serialized on `BasePhaseDto` as nullable fields; an old snapshot falls back to the lead's current position.
- `VfrFollowPhase.TryJoinLeadBase` runs before `TryJoinLeadPattern`, and only while the lead's current phase is `BasePhase`. The follower joins only when all of these gates pass:
  - it is on the pattern side;
  - its path does not cross the parallel's final;
  - it is not past the lead's base line (along-final ≥ entry − 0.2 nm);
  - the turn at the entry is sane (≤120° off the base heading);
  - it is within 5 nm of the entry;
  - the gap to the lead is at least the desired spacing.
- It builds a `PatternEntryPhase(Kind=Base)` to the start point, then `PatternBuilder.BuildCircuit(Base, lead's FinalDistanceNm, lead's pattern altitude)`, then reapplies the carried clearances. A follower already past the entry point keeps pursuing and joins on final.
- The clearance reapply is widened to all clearance types (decision 4).
- Tests:
  - `BasePhase_StartPoint_RecordedAndSurvivesSnapshot`
  - `TryJoinLeadBase_*`: flies to the start point and then the base; past the base line → keeps pursuing; inboard → keeps pursuing; on the parallel side → keeps pursuing; lead already on base when FOLLOW is issued → uses the recorded start point.
  - A replay of the fixture to t≈500. The follower's base starts within 0.3 nm of the lead's start point. The gap along final stays ≥1.0 nm from the moment both are on final. The follower never goes more than 0.1 nm south of the 28R centerline before its own final turn.

### C. Pattern return

Files: `VfrFollowPhase.cs`, `AirborneFollowHelper.cs`, `PhaseSnapshotDto.cs`, `CommandDispatcher.cs`, `VfrFollowPhaseTests.cs`.

- `record FollowPatternReturn(RunwayInfo Runway, PatternDirection Direction, double PatternAltitudeFt)` is serialized on `VfrFollowPhaseDto`.
- `OnStart` sets the target altitude (decision 3).
- When the follow ends (lead lost or despawned, lead landed with no captured runway, speed returned null), the follower re-enters the pattern for the return runway with `TryEnterPattern`, and an RPO note is added.
- `FollowWidenState.PreferredSide` together with `PatternSideWidenSide` clamps the widen within 5 nm.
- Tests: snapshot round-trip, the altitude target from above and from below pattern altitude, the despawn and landed re-entry cases, and the widen side for right and left traffic, never toward the parallel.

### E. Spacing on base (decision 6)

Files: new `Phases/Pattern/BaseFollowSpacing.cs`, `BasePhase.cs`, `PhaseSnapshotDto.cs` (`FollowWidenActive`), `PilotResponder.cs`, new `BaseFollowSpacingTests.cs`.

- The projection reuses `BasePhase.TurnRadiusNm` and the lead-speed projection from `ShouldHoldForLeadSequencing` (the slower of present and approach speed, floored at 40 kt). It also applies the `RunwayClearanceSeconds` check.
- The widen starts when the gap is below required − 0.3 nm and stops when the gap is met or at the floor. The break-off goes through `InstallVfrFollowPhase`.
- Tests:
  - `RightBaseFollower_RolloutAheadOfLeadOnFinal_BreaksOffAndTrails`
  - `..._RolloutSlightlyTooClose_WidensAndRollsOutBehind` (heading is base − 30°; same phase instance; gap at rollout ≥1.0 nm)
  - `..._RolloutWellBehind_KeepsBaseUnchanged`
  - `..._LeadSlowsMidBase_StartsWidenLater`
  - `BaseWiden_ReachesFloor_RestoresBaseHeading`
  - Snapshot round-trip.

### D. `WAIT <n>NM`

Files: `CommandSchemeParser.cs`, `tests/Yaat.Sim.Tests/ZoaParseFixTests.cs`, `COMMANDS.md`, and possibly `docs/command-cheatsheet.json`.

- In `ExpandWaitBlock`, before the rewrite to `AT`: when the argument matches `^(\d+(?:\.\d+)?)NM$` (ignoring case), emit `WAITD {n}` and recurse on the rest of the block.
- Test cases: `WAIT 1NM ERB 28R` → `WAITD 1; ERB 28R`; `DELAY 1.5nm FH 270`; `wait 2Nm`; `WAIT 5 WAIT 1NM FH 270`. Also `ParseCompound_WaitNmThenErb_*`, where block 0 is `WaitDistanceCommand(1.0)` and block 1 is `EnterRightBaseCommand("28R")`.

## Existing tests at risk (review each red one; never edit a test just to make it pass)

`FollowJoinGateTests`, `AirborneFollowTests` (Follow_CrossRunway / SameRunway / break-off), `S2Oak3FollowSequencingTests`, `FollowBreaksOnLeaderPatternEntryTests`, `VfrFollowSequencesToFinalTests`, `FollowArmedPatternRunwayTests`, `FollowPairTrajectoryTests`, `FollowStraightInJetBaseTurnTests`, `N342TFollowStraightInDownwindTests`, `FollowRunawayIasTests`.

## Orchestrator work

- `aviation-sim-expert` review after B and after E, covering the phraseology of the refusals and transmissions.
- `csharp-reviewer` before commit.
- Docs:
  - `docs/approach-and-pattern-geometry.md`: the FOLLOW install section and the VfrFollowPhase section.
  - `COMMANDS.md`: the FOLLOW bullets and the Wait table.
  - `docs/architecture.md` (`BaseFollowSpacing.cs`).
  - `USER_GUIDE.md`, if FOLLOW is covered there.
- CHANGELOG bullets.
- `docs/plans/MAIN.md`: one line for this item, plus a backlog line for the elapsed-time reset after an S-turn.
- Commits: D on its own; A–E together, or one per brief as each lands. Ask before committing.

## Verification

- Every new test is red before its step and green after it.
- `pwsh tools/test-all.ps1` passes at the end.
- Replay the fixture with `layout-inspect --ticks --html` and check that N123AB trails N314GT, joins its base at VPCBT, and lands behind it.

## Follow-ups (not in these briefs)

- [ ] A lead that S-turns on final resumes on a new `FinalApproachPhase` whose `ElapsedSeconds` restarts, so the same-leg ordering in `AirborneFollowHelper.IsLeadPatternFlowBehind` can then judge it behind its own follower. Order same-leg Base/Final pairs by remaining path to the threshold instead.
