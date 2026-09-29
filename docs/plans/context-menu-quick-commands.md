# Context-menu quick commands

The aircraft right-click menus on the radar, ground and aircraft-list views offer nearly every command at once. This plan replaces that with a short flat list of **quick commands** chosen by the aircraft's **situation**, with the full tree one level down under **All Commands**, and lets the user edit each situation's list persistently. Both terms are in [`CONTEXT.md`](../../CONTEXT.md).

## Where it stands

- Radar: `RadarView.ContextMenus.cs` `OnAircraftRightClicked` (~L64). `ContextMenuProfileService.GetProfile(phase, isOnGround)` picks primary groups by phase, but every non-hidden group still shows as a secondary group, and Track, Data Block, Squawk, Ask pilot, Coordination, Display, Sim Control and RPO are always appended.
- Ground: `GroundView.axaml.cs` `OnAircraftRightClicked` (~L493), a separate builder with inline `phase == "Taxiing"` checks (`AddSimulatedAircraftItems` ~L658).
- Aircraft list: `DataGridView.ContextMenu.cs`, a third builder.
- Shared today: `AircraftCommandApplicability` predicates, `FavoritesContextMenu`, `LiveTrafficMenuItems`, `MainViewModel.BuildRpoMenuItems`.
- The phase reaches the client as the string `AircraftDto.CurrentPhase` (the phase's `Name`); some names are dynamic (`Holding Short {target}`, `Following {cs}`). The server also sends `AircraftDto.Situation` (step 1, shipped): `SituationClassifier` in `src/Yaat.Sim/Situation/`, described in `docs/training-hub-contract.md`; the menus do not read it yet.

## Decisions (user 2026-09-28)

- **Granularity: situations, not raw phases.** About 20 named situations, each mapping a set of phases plus DTO flags.
- **Flight rules are a runtime filter, not separate lists.** Each situation has one list; every entry carries *Both* / *IFR only* / *VFR only*, defaulted from the catalog item and overridable per entry in the editor, evaluated against `FlightRules` when the menu opens.
- **An entry is a catalog item or custom text.** Catalog items are stable IDs for today's menu items and submenus (keeping the pickers, smart runway defaults and applicability predicates); custom text is a command template with the same substitution as Favorites.
- **Inapplicable entries are hidden**, not disabled. An issued clearance hides itself and shows its cancel entry instead.
- **Top level:** header, `Command…`, the quick commands, then Track ▸, Data Block ▸, Squawk ▸, Display ▸, Favorites ▸, All Commands ▸, then Delete and the RPO items. Ask pilot, Coordination and Sim Control move under All Commands.
- **One shared builder** for radar, ground and the aircraft list, and one quick-command config shared by all three. It is view-agnostic so YAAT Scope ([yaat-scope/README.md](./yaat-scope/README.md)) reuses it for its aircraft menu; like YAAT Scope's field actions, every entry resolves to command text sent through `SendCommandAsync`.
- **Storage: global per user, exportable.** Only the situations the user changed are stored in `preferences.json`, so improved defaults reach users who haven't customised. Import/export to a file like `*.yaat-verbs.json`.
- **Editor:** a Quick Commands tab in Settings: situation list on the left, the ordered entry list on the right (add from catalog, add custom text, reorder, flight-rules filter per entry), with **Reset this situation** and **Reset all**.

## Default situations

Reviewed by `aviation-sim-expert` against 7110.65 / AIM (2026-09-28). Order is most frequent first. `▸` is a submenu or picker, `…` prompts for input.

| # | Situation | Matches | Default quick commands |
|---|---|---|---|
| 1 | At parking | AtParking | Push back ▸, Taxi preset ▸, Draw taxi route…, Push back to…, Check release window |
| 2 | Pushing back | Pushback | Hold position, Push route…, Follow… |
| 3 | Holding on ground | HoldingInPosition / AfterExit / AfterPushback | Resume taxi, Draw taxi route…, Follow…, Give way…, Cross runway (when holding short of a runway) |
| 4 | Taxiing | Taxiing, ground Following | Hold position, Hold short of…, Cross runway (next hold-short is a runway), Follow…, Give way…, Break conflict, Cleared for takeoff ▸ (only nearing the departure runway's hold line) |
| 5 | Holding short | `Holding Short *` | Departure runway: Cleared for takeoff ▸, Line up and wait, Resume taxi. Any other runway: Cross runway, Resume taxi |
| 6 | Lined up | LineUp, LinedUpAndWaiting | Cleared for takeoff ▸, Cancel takeoff clearance (once cleared), Exit left/right, Draw taxi route… |
| 7 | Departing | Takeoff (airborne), InitialClimb, DepartureProcedure | Fly heading ▸, Maintain ▸, Climb via SID (IFR, SID assigned), Direct to…, Speed ▸ |
| 8a | IFR enroute / climbing | airborne IFR, no special phase, not inbound | Fly heading ▸, Maintain ▸, Direct to…, Speed ▸, Hold ▸, Cross fix |
| 8b | IFR arrival | airborne IFR inbound to destination, not on an approach | Descend via STAR (IFR), Maintain ▸, Speed ▸, Fly heading ▸, Direct to…, Expect approach…, Hold ▸ |
| 8c | VFR flight following | airborne VFR, not in or near the pattern | Report traffic in sight, Fly heading ▸, Maintain ▸, Direct to…, Expect approach… |
| 9 | Approach | ApproachNavigation, InterceptCourse, ProcedureTurn | Cleared approach ▸ (hidden once cleared), Maintain ▸, Speed ▸ (hidden inside FAF / 5 NM), Report field in sight, Cleared visual ▸ (IFR, after field in sight), Cleared to land |
| 10 | Holding | HoldingPattern, VfrHold | Exit hold, Cleared approach ▸, Direct to…, Maintain ▸, Expect further clearance time |
| 11 | Pattern | pattern phases | Cleared to land, Option / Touch-and-go (after CTL for IFR), Follow… / sequence, Extend, Make short approach, 360 L/R, Turn base, Go around |
| 12 | Final | FinalApproach | Cleared to land (hidden once cleared), Go around, Cancel landing clearance, Reduce to final approach speed |
| 13 | Rollout / exit | Landing, RunwayExit, ClearRunway | Exit left/right (once decelerating), Cross runway, Hold short of…, Draw taxi route… |
| 14 | Go-around | GoAround | Fly heading ▸, Maintain ▸, Cleared approach ▸ (once it has an altitude to maintain until established), Enter downwind ▸ (VFR) |
| 15 | Live traffic | `IsLiveTraffic` | Assume control, Assume and track |
| 16 | VFR arrival, inbound | airborne VFR inbound to destination, not yet in the pattern | Enter L/R downwind ▸, Enter base ▸, Straight-in, Report N-mile / at fix, Cleared to land, Follow… |
| 17 | VFR departure, leaving the pattern | airborne VFR departing, not staying in the pattern | Fly heading ▸ / on course, Maintain ▸, Report at fix, Make L/R closed traffic |

Review notes that drive predicates:

- Final (12) offers no exit instruction: 7110.65 §3-10-9 note says exit instructions should not normally be issued before or immediately after touchdown. Rollout (13) shows exits only once decelerating.
- A visual approach clearance follows the pilot's report of the airport or runway in sight (§7-4-3.a).
- An approach clearance goes to an aircraft established on a published segment or given an altitude to maintain until established (§4-8-1).
- Runway crossing needs an explicit clearance (§3-7-2); a hold-short of a runway the aircraft only crosses offers no takeoff or LUAW.
- LUAW's predicate covers the §3-9-4 restrictions (e.g. intersection LUAW at night).
- Speed adjustments stop inside the FAF or 5 NM from the runway, whichever is closer (§5-7-1).
- Initiate handoff is not a quick command: the top-level Track ▸ covers it.
- Sim-only actions (Break conflict, Check release window) come after the instructions a controller would give.

## Open questions

- Several predicates need data the client may not have: nearing the departure runway's hold line (4), whether the hold-short runway is the departure runway (5), inbound vs enroute (8a/8b, 16/17), inside FAF / 5 NM (9), decelerating on rollout (13), field in sight reported (9). Each needs either a derivation from existing DTO fields or a new field (per `training-hub-contract.md`).
- Decided (user 2026-09-29): the server classifies. Yaat.Sim computes the situation from the phase object and aircraft state, and `AircraftUpdated` carries it as a `Situation` enum field, so a phase rename is a compile error and the predicates that need server data live beside the classifier. Step 1's classifier is therefore a Yaat.Sim class, and the field follows `docs/training-hub-contract.md`.

## Step 1 rulings

- **Landing** (user 2026-09-29): step 1 lands on `main`; steps 2–7 live on `feat/context-menu-quick-commands`, opened when step 2 starts.
- **Phase names**: `HPP-L`, `HPP-R`, `HPP`, `HoldingAtFix` and `ProceedToFix` are live (`VfrHoldPhase.Name`); only `Pushback to Spot` is dead (`PushbackPhase.Name` is always `Pushback`) — step 1 deletes it from `AircraftCommandApplicability`, `AircraftStatusDescriber` and the two tests that pin it. The table's "LineUp" is `LiningUp` and "ApproachNavigation" is `ApproachNav`. `HoldingShortPhase` and `RunwayHoldingPhase` both map to Holding short.
- **Shape**: `enum AircraftSituation` (`Unknown = 0`, the fallback, sent as a number) and a static `SituationClassifier.Classify(AircraftState)` in Yaat.Sim, switching on the current phase's type. A reflection test fails when a concrete `Phase` subclass maps to `Unknown`.
- **Precedence**: live-traffic shadow first; then the phase type; then, for an airborne aircraft with no mapped phase (or a turn, S-turn or other unmapped enroute phase), the flight-rules and inbound predicates. RPO state is not an input. A held taxi stays Taxiing (the Resume taxi entry's predicate handles the hold). One Holding short situation; takeoff vs cross is the entries' predicates.
- **Inbound** (aviation ruling; [J] is a judgement figure): `closing(x)` = track within 60° of the bearing to x [J] and GS > 40. IFR arrival = a STAR is active, or a destination runway is set, or the destination is within 40 NM [J] and closing, or closing while descending (VS ≤ −500 fpm, target ≥ 1,000 ft below) inside 3 NM per 1,000 ft above the field + 10 [J]; a local IFR flight (origin = destination) counts only the STAR, runway and descent tests. VFR inbound = destination within 20 NM (the Class C outer area, AIM 3-2-4) and closing, unless it departed that field under 3 min ago [J]. VFR departing = not inbound, within 10 NM of the origin [J] with track ≥ 120° off the bearing to it; no origin known: under 5 min since departure and below 3,000 ft AGL [J]. Otherwise enroute (IFR) or flight following (VFR). No destination: never an arrival except by STAR or runway. Approach, pattern and landing phases are classified before any of this.
- **Wire**: `Situation` is added to `AircraftStateDto` / `AircraftDto` in today's positional style (the `required` init conversion is a separate backlog line) and to `AircraftChangeTracker`'s fingerprint, since several inputs are not fingerprinted.
- The inbound rule's two time clauses (VFR inbound's "departed that field under 3 min ago" exclusion; VFR departing with no origin by "under 5 min since departure") need a liftoff time that `AircraftState` does not keep (only `HasBeenAirborne`, `SpawnedAtSeconds`), so step 1 leaves both out: no just-departed exclusion, and a VFR aircraft with no origin is flight following. Adding `AirborneAtSeconds` (set at liftoff, snapshotted) and sim time into `Classify` belongs to step 6.
- The step-6 predicates (nearing the departure hold line, inside FAF / 5 NM, decelerating on rollout) are not step 1's.

## Steps

- [ ] 2. Build the menu-item catalog (stable IDs, label, default flight-rules tag, applicability predicate, builder) in `Yaat.Client.Core`, and move the radar, ground and aircraft-list builders onto it; All Commands reproduces today's full tree
- [ ] 3. Quick-command resolution: situation → stored or default entry list → flight-rules and applicability filter → menu items; replace `ContextMenuProfileService`
- [ ] 4. Persistence in `UserPreferences` (overrides only) plus import/export
- [ ] 5. Settings → Quick Commands tab with per-situation and global reset
- [ ] 6. Add the missing predicates and DTO fields from the open questions, the two inbound time clauses (liftoff time), hysteresis at the 20 NM / 40 NM / 60° boundaries (today the situation can flip tick to tick near them), and for a standalone turn the classifier's "first mapped phase after the turn" should stop at an unrelated queued phase; `aviation-sim-expert` review of the final predicates and of step 1's phase mappings (in `SituationClassifierTests`' explicit map)
- [ ] 7. `USER_GUIDE.md`, `docs/radar-rendering.md` / `docs/ground-rendering.md` menu sections, `docs/architecture.md`
