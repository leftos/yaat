# FOLLOW video montage

A montage of FOLLOW situations recorded in the engine, in two cuts: a 60–90 s **release cut** that accompanies the release, and a **review reel** the user watches to review the FOLLOW work (B1–B6b). Index line: [MAIN.md](./MAIN.md), under **Bug reports and feature requests**.

## Decisions (user 2026-10-01)

- **Route.** Each clip is a scripted headless engine run that writes a YAAT recording; the recording is replayed in the desktop client's radar view and window-captured.
- **Setup.** Natural: a scenario spawns the aircraft airborne and a timed script of real commands (pattern entries, `CLAND`, `RTIS`, `FOLLOW`, …) brings them into the situation, as an instructor would. The lead-in is sped up in the edit. A situation commands cannot reach is reworked or dropped, never placed by hand.
- **Cuts.** The release cut (10 clips, accepted follows) and the review reel, one clip per shipped rule branch (accept and refusal once each, ~30 clips, variants dropped).
- **On screen.** Command captions, the solo pilot's TTS, a rule + citation card opening each review clip (the rule, its AIM / 7110.65 grounding, what to watch for), and a live spacing readout.
- **Spacing readout.** The client's own range/bearing line, `.rbl <FOLLOWER> <LEAD>` typed during the take.
- **Timing.** Made after FOLLOW B6b-1 ships and before the release is cut; the release gate waits on it.

## Pipeline (mapped 2026-10-01)

What exists, with the code it rests on:

- **Headless recording.** yaat-server `tools/Yaat.SoakRunner` runs one scenario headless through the server room pipeline (`HeadlessRoom`) with real navdata and writes a v4 archive (`SoakEpisodeRunner`, `SoakRecordingSink`): scenario, snapshot 0, RNG seed, action log, terminal log. `--snapshot-interval 1` gives a snapshot per second. Its only command source today is the controller AI; `HeadlessRoom.Engine.SendCommandAsync(conn, callsign, text, initials)` is the path a person's command takes, and `HeadlessRoomTests` pins that the same scenario and seed reproduce the action log and final snapshot.
- **Replay in the client.** Scenario → "Load Recording..." uploads the archive to the server, which loads it paused in the room; play, the sim-rate dropdown (1–16×) and the timeline drive it. Playback re-simulates from the action log on the live tick path, so the radar draws it as live. A server must run (`dotnet run --project src/Yaat.Server`, `:5130`); the picker filters `*.yaat-recording.zip`.
- **TTS in replay.** `TickProcessor.BroadcastPilotTransmissions` broadcasts during forward playback (a seek or rewind is a reconstruction and stays silent). The engine drains pilot transmissions only with solo training mode on and a student position (`PilotContacts.AnyAnswering`), and the client speaks them with `PilotVoiceEnabled` and solo mode on. So every clip's run sets solo mode and a student position.
- **Captions.** The archive's terminal log carries command echoes, responses, refusals and pilot lines with sim times; refusal text exists only there. `tools/bug_bundle.py terminal-log` reads it (`--callsign`, `--kind`, `--from`/`--to`, `--json`), and `--srt --out <file>` writes SubRip captions with LF endings (stdout on Windows translates to CRLF, so captions go through `--out`).
- **Spacing line.** `.rbl A B` latches to both callsigns and redraws each frame, in NM. It is client-side and survives playback ticks; whether a seek drops it is unmeasured, so takes play forward from the start.
- **Framing.** Radar centre, range and pan-zoom lock are saved per scenario id in `preferences.json` (`SavedRadarSettings`), so replays of one recording reuse one framing; a seeded `preferences.json` under the client-driver's `YAAT_APPDATA_DIR` frames a take without hand zooming.
- **Determinism.** Replay re-runs today's code over the recorded actions, and the client shows no desync. Record and capture with one build, and check the archive manifest's `ServerVersion` against the capturing build.
- **Capture and edit.** `docs/client-driver-mcp.md` "Recording a demo" (the #462 push demo): window sizing, `WgcCapture` video and client audio as two captures muxed afterwards, ffmpeg editing.

## Steps

- [ ] 1. **Scripted headless recordings.** yaat-server `Yaat.SoakRunner` gains `--script <file>`: one command per line, `t=<sec> <CALLSIGN> <command text>`, sent through `Engine.SendCommandAsync` before the second it names; plus solo training mode and a student position (recorded as setting changes) so pilot transmissions drain. The output archive is named `*.yaat-recording.zip`. Test beside `HeadlessRoomTests`: a two-line script lands as two recorded commands with their terminal entries at the scripted seconds, and the run is deterministic. Add the Task Index row "produce a recording headless (demo, video)" to `docs/architecture.md` (SoakRunner → `SoakEpisodeRunner` → `SoakRecordingSink` → `HeadlessRoom` → `RecordingArchiveWriter` → `docs/client-driver-mcp.md`).
- [ ] 3. **Clip scripts.** One folder per clip under `tools/montage/follow/<id>/`: `scenario.json` (KOAK unless the row names another airport, the spawns), `script.txt`, `card.md` (rule, citation, what to watch). Callsigns are realistic N-numbers and airline callsigns, consistent across clips. Each is run headless and checked with `bug_bundle.py history` / `track --pair` until the situation forms and the expected text appears in the terminal log; a script that cannot reach its situation is reported, not forced.
- [ ] 4. **Capture.** One pinned build; local server; client through the client-driver MCP at a 1920×1080 client area, framing seeded per scenario, solo mode and pilot voice on; Load Recording, type `.rbl`, play at 1×; `WgcCapture` video and client audio separately.
- [ ] 5. **Edit** (`ffmpeg-skill`): cards, burned captions from step 2, sped-up lead-ins, the release cut and the review reel with chapters; render, then review with the user before the release is cut.

## Clips

From the clip catalogue (exploration 2026-10-01). Seed tests are where the situation and its asserted outcome live; the natural setup reaches the same situation by commands. KOAK 28R right traffic, C172 VFR, unless noted. Text in quotes is the engine's, verbatim.

### Release cut (accepted follows)

| Id | Situation | Watch for | Seed |
|---|---|---|---|
| A1 | Downwind follower behind a straight-in on a 2 NM final | holds the downwind, turns base behind, lands second | `FollowPairTrajectoryTests.Follow_FromDownwind_LeadOnStraightInFinal_SequencesBehind` |
| A4 | Lead extends then turns base (`EXT`, `TB`) | follower holds, then turns base at spacing | `FollowPatternSequencingAuditTests.Follower_SequencesBehind_WhenLeadExtendsThenTurnsBase` |
| A8 | 28L downwind follower told to follow a 28R straight-in | re-sequences onto 28R, no left base across 28L's final | `FollowPairTrajectoryTests.Follow_CrossRunway_FromDownwind_ResequencesOntoLeadRunway_NoAboutFace` |
| A11 | `FOLLOW` with no traffic in sight, then `FOLLOWF` | "Traffic not in sight — issue RTIS first", then "Follow …" | `FollowRunwaylessLeadFromPatternTests.FollowForce_NoTrafficInSight_IsAccepted` |
| C1 | Base follower pursues a free-flight lead with a queued `ERB 28R` | joins the lead's base, lands in trail | `FollowRunwaylessLeadFromPatternTests.FollowFromBase_RecordedCase_JoinsLeadBaseAndLandsInTrail` |
| C2 | Pursuit too close behind a pattern-bound lead | "S-turning for spacing behind the traffic.", then nose-on in trail | `…FreePursuit_TooCloseBehindPatternBoundLead_STurnsOutsideThenFollowsNoseOnInTrail` |
| C3 | C172 pursuing a B738 past the jet's base turn | extends at its speed floor, turns base once 3 NM behind | `…FreePursuit_ExtendingPastTheLeadsBaseTurn_KeepsTheLegAtTheFloorAndTurnsBaseOnceSpaced` |
| E1 | Base follower slightly too close | widens 30° away, rolls out ≥ 1 NM behind | `BaseFollowSpacingTests.RightBaseFollower_RolloutSlightlyTooClose_WidensAndRollsOutBehind` |
| E2 | Base follower that would roll out ahead | "turning downwind for spacing behind the traffic, request base turn.", trails | `BaseFollowSpacingTests.RightBaseFollower_RolloutAheadOfLeadOnFinal_BreaksOffTurnsDownwindAndTrails` |
| G6 | Closed-traffic climb follows a runwayless lead | holds the upwind past the departure end and TPA − 300, then pursues | `FollowClimbFollowerTests.PendingPursuit_FromClosedClimb_HoldsUpwindUntilPastDepartureEndAndTpaMinus300` |

### Review reel (one per rule branch; the release clips above are in it too)

| Id | Rule branch | Outcome | Grounding | Seed |
|---|---|---|---|---|
| A2 | Downwind follower, lead on base | turns base once the lead is aft of the 3-9 line | 14 CFR 91.113(g), AIM 4-3-4.d | `FollowPairTrajectoryTests.Follow_FromDownwind_LeadOnBase_SequencesBehind` |
| A3 | Same downwind, lead ahead | trails, no overtake | 7110.65 §3-8-1 | `FollowPairTrajectoryTests.Follow_SameLegDownwind_TrailsWithoutOvertake` |
| A5 | Fast jet follower behind a slow C172 | never cuts in | 7110.65 §3-10-3.a.1 | `FollowPairTrajectoryTests.Follow_FastFollowerSlowLead_NeverCutsInFront` |
| A9 | FOLLOW during a pattern entry | entry kept, then sequenced | AIM 4-3-3 | `FollowPairTrajectoryTests.Follow_DuringPatternEntry_KeepsEntry_AndLands` |
| A12 | Crosswind follower, lead extended 3.5 NM on downwind | accepted: a later leg is ahead | AIM 4-3-2.a.3.2 | `FollowSequenceRefusalTests.FollowFromCrosswind_LeadExtendedOnDownwind_IsAccepted` |
| A13 | Downwind follower, lead on its pattern entry | accepted, flow-behind until it joins | AIM FIG 4-3-3 | `FollowSequenceRefusalTests.FollowFromDownwind_LeadOnPatternEntry_IsAccepted` |
| A15 | Lead lands mid-follow | "… is on the ground, breaking off the follow." | AIM 4-4-14.a.2 NOTE | `FollowPairTrajectoryTests.Follow_LeadLands_FollowerContinuesOwnApproach` |
| B1 | Base follower, lead behind on downwind | "Unable, on base for runway 28R, … is not ahead of us, request vectors" | AIM 4-3-5 | `FollowSequenceRefusalTests.FollowFromBase_SameRunwayLeadOnDownwind_IsRefused` |
| B3 / B4 | Final follower, lead on base: refused from a close final, accepted from a 6 NM straight-in | refusal, then "Follow …" | distance, not leg (study B3) | `FollowSequenceRefusalTests.FollowFromFinal_SameRunwayLeadOnBase_IsRefused`, `…FollowFromFinal_SixMileStraightIn_LeadOnCloseBase_IsAccepted` |
| B5 | Base follower, lead on a pattern entry | refused | AIM 4-3-5 | `FollowSequenceRefusalTests.FollowFromBase_LeadOnPatternEntry_IsRefused` |
| B6 | Downwind follower, lead on upwind | "Unable, on downwind for runway 28R, …" | AIM 4-3-2.a.3.2 | `FollowSequenceRefusalTests.FollowFromDownwind_SameRunwayLeadOnUpwind_IsRefused` |
| B7 | Departing lead | "Unable, … is departing, request vectors" | 7110.65 §3-8-1 | `FollowSequenceRefusalTests.Follow_DepartingLead_IsRefused` |
| B8 | Lead going around (not re-entering) | "Unable, … is going around, request vectors" | AIM 4-4-14.a.2 NOTE | `FollowSequenceRefusalTests.FollowFromDownwind_SameRunwayLeadOnMissedApproach_IsRefused` |
| B10 | Super lead refused, B738 accepted | "Unable, visual separation not authorized behind super …" | 7110.65 §7-2-1 | `VfrFollowSequencesToFinalTests.Follow_RejectedBehindSuper_AllowedBehindOrdinaryTraffic` |
| B12 | FOLLOWF from final | phase and landing clearance kept | study B1 | `FollowRunwaylessLeadFromPatternTests.FollowForceFromFinal_KeepsPhaseAndLandingClearance` |
| C4 | Pursuit past the extension limit | "unable to follow the traffic, extending downwind, request base turn." | AIM 5-5-12.a.2 | `…FreePursuit_ExtendedPastTheLimitWithoutTheGap_KeepsTheLegAndRequestsABaseTurn` |
| C5 | Runwayless lead outside / inside the ±60° cone | refused, then accepted | AIM 4-3-5 | `FollowRunwaylessLeadFromPatternTests.FollowFromDownwind_RunwaylessLeadJustOutsideTheCone_IsRefused` |
| C6 | Upwind follower, runwayless lead in the downwind box | accepted outside the cone | AIM 4-3-5 | `…FollowFromUpwind_RunwaylessLeadOnDownwindLineAbeamTurnPoint_IsAccepted` |
| C8 | Lead queued for another runway | refused from base, re-sequenced from downwind | 7110.65 §3-8-1 | `…FollowFromBase_LeadQueuedOtherRunway_IsRefused`, `…FollowFromDownwind_LeadQueuedOtherRunway_BuildsEntryToQueuedRunway` |
| C9 | Outside the pattern: lead on the ground, lead bound for KHWD | "Unable, … is on the ground"; "Unable, … is inbound to HWD, request vectors" | AIM 4-4-14.a.2 NOTE | `FollowOutsidePatternRefusalTests` |
| D1 | Intercept follower behind a lead on final | approach and landing clearance kept, spaces behind | 7110.65 §7-4-3.c.2 | `FollowKeepApproachTests.FollowFromApproach_SameRunwayLeadAhead_ThenTick_FinalApproachSpacesBehindLead` |
| D3 | Approach follower, entering lead measured by path | refused when the lead's path is longer, accepted when shorter | study B5b-4 | `FollowEntryLeadPathTests` |
| D5 / D6 | Lead landing 28L: IFR refused; VFR outside the FAF re-sequenced | "… is landing runway 28L, request vectors"; "… cancelled by FOLLOW, landing clearance RWY 28R cancelled" | 7110.65 §7-4-3.c.2, §3-10-5 | `FollowApproachCrossRunwayTests` |
| D7 | IFR approach follower loses sight (1 SM) | "… visual separation terminated — radar separation required" | AIM 5-5-12.a.2 | `FollowKeepApproachTests.FollowFromApproach_IfrFollowerLosesSight_WarnsAndContinuesApproach` |
| D8 | Lead goes around during a kept-approach follow | "follow of … ended — … went around" | AIM 4-4-14.a.2 NOTE | `FollowKeepApproachTests.FollowFromApproach_LeadGoesAround_EndsFollowAndWarns` |
| D10 | B738 closing on a C172 before final | slows below its ceiling, not below its floor | 7110.65 §5-7-1.d | `FollowPreFinalSpacingTests.InterceptCourse_FollowerCloserThanSpacing_SlowsBelowCeilingNotBelowFloor` |
| E3 | Structural overtake | goes around: "unable to maintain separation, breaking off the follow." | 7110.65 §3-10-3 | `BaseFollowSpacingTests.StructuralOvertake_DoesNotBreakOff_GoesAroundAsBefore` |
| E5 | Turn-out distance limit | holds the downwind, instructor warning "awaiting a base turn" | AIM 4-3-5 | `FollowTurnOutTests.DistanceLimit_HoldsTheDownwindHeadingWithoutASecondCall` |
| F3 | New lead ahead during a turn-out | turn-out ends, follows the new lead | study B5b-3 | `FollowPursuitNewLeadTests.TurnOut_NewLeadAhead_RetargetsAndEndsTurnOut` |
| G1 / G2 | Go-around follower: lead ahead kept, lead behind refused | climb kept; "Unable, on the go-around for runway 28R, …" | AIM 4-3-2.a.3.2 | `FollowClimbFollowerTests.GoAroundFollower_LeadAhead_KeepsClimbAndSetsLead`, `…GoAroundFollower_LeadBehind_RefusedWithGoAroundText` |
| G4 | Follower on its own published missed: IFR refused, VFR re-sequenced | "Unable, on the missed approach, request vectors" | 7110.65 §4-8-9.a | `FollowClimbFollowerTests.MissedApproachGoAround_*` |
| G7 | Closed-traffic climb onto another runway (B6b-1): close parallel leg 1, crossing pair leg 0 | after B6b-1 ships | study rule 6 | B6b-1's tests |

Refusals are shown as the command, the caption, and 10–15 s of the aircraft carrying on unchanged. Approach clips (D) need a real intercept heading: the tests' `FollowSequenceRefusalTests.AddApproachFollower` flies parallel to the final and never captures (MAIN.md backlog line), so their scripts issue a real approach clearance.
