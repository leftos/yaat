# Class B hold in front of a pattern or approach

Design for YAAT-2668 and its four parts: an uncleared VFR aircraft flying a pattern entry or approach stays out of Class B (7110.65 §7-9-2.a; NOTE 1: the pilot advises ATC rather than comply). The owner rulings live in YAAT-2668; this file is the build design, from a read-only invariant pass over `Yaat.Sim` with YAAT-2670's RMOB on `main`.

## Parts and order

1. **Grant fix.** A pattern-entry or approach clearance implies a Class B clearance only for an airport inside that Bravo, never under RMOB. The waiting fallback in `ImplicitBravoClearance.GrantForLanding` that grants the waited-on Bravo for a satellite airport goes. An implied-clearance command answers an open AirspaceEntry request only when, after the grant, the aircraft is cleared into that Bravo (`PilotRequestTracker`).
2. **Vertical cap record.** Built (YAAT-2699): [`airspace-database.md`](../airspace-database.md) "In-phase Class B cap".
3. **In-phase lateral hold.** A new `PhaseList` operation inserts an Orbit-mode `AirspaceBoundaryHoldPhase` at the current index; the running phase stays behind it, Active, not ticked, with no `OnEnd`. Each phase declares how it resumes when the hold completes:
   - **in place** (route- or fix-index-driven: `PatternEntryPhase`, `MidfieldCrossingPhase`, `TeardropReentryPhase`, `PatternExitPhase`, `ApproachNavigationPhase`, `GoAroundPhase`): the hold's `OnEnd` restores the route, heading and turn direction;
   - **restart copy** (geometry- or timer-driven: `UpwindPhase`, `CrosswindPhase`, `DownwindPhase` with `RejoinTrack`, `BasePhase`, `FinalApproachPhase`, `InterceptCoursePhase`, `ProcedureTurnPhase`, `HoldingPatternPhase`): the original ends Completed once and a copy re-anchors from where the orbit left the aircraft, through one per-phase copy that `PatternCommandHandler.ClonePatternPhase` also calls;
   - **not suspendable** (`VfrFollowPhase`, `HelicopterApproachPhase` until read, the turn, S-turn, hold, low approach, touch-and-go, stop-and-go and landing phases): today's early return.

   The dispatcher **lifts** the hold for every controller command (at the top of `CommandDispatcher.DispatchCompoundCore` and inside `BuildApplyAction`, after `PreDispatchCapture`), so the command is judged against the held phase; the same hold goes back only when the held phase is still current, the same instance and Active, else it is discarded. A hold with a held phase follows the free-flight vector rule: a new heading or direct that keeps the aircraft outside the Bravo ends the hold and is flown, one into the Bravo is held through and flown once cleared, and a heading standing when the hold went in is not new. The held phase's lateral targets come back only when no new heading or direct arrived. Class C keeps today's behaviour.
4. **Path-based lookahead and self-release.** Step 1 built (YAAT-2701): the 60 s path test while a phase runs and the hold's self-release, [`airspace-database.md`](../airspace-database.md) "Path lookahead and self-release"; step 2, the dispatch advisory, is built (YAAT-2790, [`airspace-database.md`](../airspace-database.md) "Path lookahead and self-release"); the shelf cap on the predictor (step 3, YAAT-2791) remains. While a phase runs, the boundary test follows the path the phase will fly (pattern waypoints, or the navigation route then the final approach course), cut at 60 s of along-path distance, at the phase's planned altitude under the cap; any lateral clip into a Bravo volume whose band holds that altitude is an entry. The same test runs on the whole remaining path when a pattern-entry or approach clearance is issued (the advisory comes first), and ends a hold in front of a held phase once the held path clears the Bravo by one turn radius. The 120 s straight-ray request does not run for an aircraft in a suspendable phase.

## Invariants

- At most one boundary hold per aircraft, only as the current phase; a hold never holds a hold.
- At most one held phase, directly behind the hold, Active; nothing else is Active without being current.
- The held phase gets exactly one `OnEnd`: Skipped when the chain is cleared while it is held (`PhaseList.Clear` must call it on an Active phase that is not current), Completed when its restart copy replaces it, or its natural end after an in-place resume.
- Only Orbit-mode holds go in front of a running phase; a vertical entry never holds a phase.
- Every controller command for a held pair runs against the held phase; a command that replaces or clears the list takes the held phase with it.
- Snapshots need no new phase status or list field; the lift never spans a tick.
- Under RMOB, in a phase: no grant, no request of the pilot's own; the hold lasts until CLBRV or the path clears.
- Everything is decided in the once-per-second proactive step or in synchronous dispatch, so replays stay exact.

## Open for review

- The resume rule per phase kind is aviation behaviour: `aviation-sim-expert` reviews part 3's table, including whether an orbit on short final is acceptable.
