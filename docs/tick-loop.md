# Tick Loop Reference

> Read this before changing anything that runs every sim-second: `SimulationEngine`, `RoomEngine` (yaat-server), `FlightPhysics`, `PhaseRunner`, `TickProcessor`, conflict detectors, or broadcast services. The order of operations matters.

## The engine's partial files

`SimulationEngine` is split across partial files by cluster, following the `MainViewModel` precedent in [client-mainviewmodel.md](client-mainviewmodel.md). The split is purely organizational — they compile into one class, so a private field declared in `SimulationEngine.cs` is visible from `SimulationEngine.Tick.cs`. Use this to find the right file; nothing about the split changes behaviour or the public surface.

| File | Owns |
|---|---|
| `SimulationEngine.cs` | Engine state (`World`, `Scenario`, the evaluators and pools, `ControllerAi`, `Actions` — the `ActionRouter` every controller action goes through — and `LocalConnectionId`, what a bare `SendCommand` issues under), the lifecycle events (`TickCompleted`, `WarningEmitted`, `PilotSpeechEmitted` as `Action<string, PilotSpeechText>`, `TerminalEntryEmitted`, which fires for every terminal entry — command echoes, preset outcomes, warnings — so subscribers react as entries happen instead of polling `DrainTerminalEntries`) and their `Fire*` helpers, the constructor, `FindAircraft`, `DeleteAircraft` (the sim-side half of DEL: stamps `CompletionReason.Dropped`, clears a queued delayed spawn, removes; the live server and replay both route through it), and the terminal-entry sink every other partial writes to. `RehydrateRestoredQueueBlocks` rebuilds restored blocks' `ParsedCommands` / `ApplyAction` from `SourceCommandText` under the block's `IsScenarioScripted` each `TickPhysics` (shared by both hosts), so queued commands survive rewind, replay and restore. |
| `SimulationEngine.Snapshots.cs` | `CaptureSnapshot` / `RestoreFromSnapshot` and the server's slice (`CaptureServerSnapshot` / `RestoreServerSnapshot`). Restore re-binds each live aircraft, and each delayed-spawn aircraft (rebuilt from its `AircraftSnapshotDto`, phases included; `RestoreDelayedAircraft` also reads a pre-54 `AircraftJson`), to its own airport's layout from the persisted `Ground.LayoutAirportId` (primary layout as the live-aircraft fallback). |
| `SimulationEngine.Scenario.cs` | `LoadScenario`, `ResolveGroundLayout` (per aircraft) and `ResolveAirportLayout(airportId)`: the layout of a runway's own field — `World.GroundLayout` for the primary airport, the ground data's otherwise, null with no map — which every threshold measurement about a runway resolves through. |
| `SimulationEngine.Spine.cs` | **The segment entry points and the runner** every run kind advances a sim-second through — `RunSecond`, `BeginSecond`, `OpenSecond`, `RunPrePhysics`, `RunPhysicsSubTick`, `RunPostPhysics`, `RunEndOfSecond`; iterates the lists in `Simulation/Spine/SpineOrder.cs`, records every step into `StepTrace`, and times each into `TickTimings` when a sink is attached. `BareHost` (the Test-run host) lives on the engine. |
| `SimulationEngine.Tick.cs` | **The engine's own step bodies** — `TickPrePhysics`, `TickPhysics`, the detectors (`TickVisualDetection`, `TickConflictAlerts`, `TickEramConflictAlerts`, `TickTransponders`, `TickAutoDelete`, `TickSoloTrainingEvaluation`), `TickPilotProactive`, `TickControllerAi`, and the bare-host wrappers `TickOneSecond` / `TickPostPhysics`. The order they run in is `SpineOrder`'s, not this file's. `TickEramConflictAlerts` returns an `EramConflictAlertChanges` (in `SimulationEngine.cs`): New, Cleared, Suppressed, Restored and Reclassified — the standing alerts whose Mode C Intruder status flipped, so a host that filters by it re-evaluates them. `BuildOccupiedHoldShortNodes` also counts each exit capacity segment's occupants; `OccupancyForExitChoice` adds a full segment's exit bar to an arrival's `OccupiedHoldShortNodes`. |
| `SimulationEngine.Replay.cs` | Replay entry points — `ReplayFromStartTo`, `FastForwardTo`, `ReplayRange`, `Replay`, `ArmReplay` (arms the driver against a scenario loaded by other means: the tick oracle), `ReplayOneSecond`, `ReplayOneSubTick`. These are thin delegators: the stepping logic lives in `Simulation/Replay/ReplayDriver.cs`, which runs the spine under a `ReplayHost` over a `RecordedActionPump`. Also `RunProfile` and `EnterReplay()`: the engine's run profile (`Simulation/RunProfile.cs` — kind + the three allowances `RecordsActions`, `RunsGenerators`, `RunsControllerAi`) (defaults to Test; hosts set it) stays on the engine because the recorders, the generators and `TickControllerAi` read it to decide what they may do; each driver stepping call runs under an `EnterReplay()` scope and hands the previous profile back. Steps read the allowances, never the kind (ADR 0005). `TickTimings` (the opt-in per-step timing sink, null in production; the soak runner and the reconstruction benchmark attach one) lives here too. |
| `SimulationEngine.Commands.cs` | `SendCommand` and `DispatchAiCommand` (one-line callers of `Actions.Issue` — the `ActionRouter` in `Simulation/Actions/` routes every controller action, fresh or recorded), `DeferForReaction`, `BuildDispatchContext`, `TaxiAll` (every aircraft at parking through the TAXI arm), `ApplyPostDispatch` (the aviation arm's post-dispatch body on every run kind), and the aircraft mutations commands drive (`WarpAircraft`, `AmendFlightPlan`, `RequestNewBeaconCode`). |
| `SimulationEngine.DeferredCommands.cs` | Commands held until a trigger fires: `ProcessDeferredDispatches` and the triggered track blocks. A scenario-scripted deferral that would end an active pushback (`EndsPushback`), and any scripted one the pushback would reject behind it, is held until the phase ends; ready deferrals are then released in expiry order. |
| `SimulationEngine.Generators.cs` | Arrival, VFR and overflight generators — spawning, in-trail spacing, weight and engine selection; they stand down in every replay and playback. `AutoTrackGeneratedSpawn`: a spawn whose generator carries an autotrack configuration is owned, then recorded, then its `[AutoTrack]` lines emitted. `TickPrePhysicsResult` is one `SpawnedAircraft` list. `ApplyArrivalSpacing`'s `CorridorAircraft` only counts aircraft actually inbound to land on the runway (`IsArrivalToRunway`, via `ApproachCommandHandler.IsOnFinal` / `IsInboundToLand`), so a departure sharing the runway isn't mistaken for an arrival, and measures along-final distance from the landing threshold (`LandingThreshold.Resolve`, 7110.65 §5-5-4.h). Once a ceiling is no longer needed it also restores the stream lead's scheduled speed, or a follower's current ceiling (`RestoreManagedSpeed`, gated by `ArrivalSpacingManager.SpeedRestoreGateNm` / `SpeedRestoreDeadbandKts`). Both passes take every on-final verdict and threshold distance from the runway's own field's layout (`ResolveAirportLayout(runway.AirportId)`, secondary airports included — never the aircraft's `Ground.Layout`), and the protection pass's release path carries runway + layout + distance as one `ArrivalThresholdDatum`. The same-runway protection pass announces its own release (resume normal / published speed) through `SameRunwayArrivalProtection`'s line builders, held off while a handoff to the student is in progress (`HandoffToStudentInProgress`) so the receiving controller inherits the standing ceiling. |
| `SimulationEngine.Presets.cs` | Scenario-authored timing: the release queue, timers, timed presets, triggers, global commands; the solo-room auto takeoffs (`ProcessReleasedGroundDepartures`, `ProcessRunwaySpawnAutoTakeoffs`, and the silent simulated tower's `ProcessSimulatedTowerDepartures` with its 5–20 s `SimulatedTowerCtoDelaySeconds`). |
| `SimulationEngine.ScriptedArms.cs` | The scripted route: a preset or `WAIT` payload of flight-plan or global verbs (`DA`, `FP`, `VP`, `REMARKS`, `SQALL`, `SNALL`, `SSALL`) runs through its `ArmTable` row (`TryApplyScriptedPreset`, `ApplyScriptedArm`), recording nothing; `PAUSE`, `UNPAUSE`, `SIMRATE`, `SPAWN` and a bare `AS` are refused (`IsRefusedInPreset`). |
| `SimulationEngine.LiveTraffic.cs` | Shadow aircraft from the live feed — samples, beacon tracking, runway-use latching. |
| `SimulationEngine.Recording.cs` | `RecordAction` (the one append site, gated by `RunProfile.RecordsActions`), `ApplyRecordedAircraftSpawn` and the pre-tick action pass (the pre-tick half of the router's `ApplyRecorded`). |
| `SimulationEngine.RecordedAppliers.cs` | The bodies the router's spawn arms and derived-record appliers call: `SpawnNow` / `SpawnDelay`, `AddAircraft` (derived from the shared RNG + beacon pool on every run kind; the recorded snapshot wins on disagreement, with a replay-fidelity warning), and the setting / weather / generator appliers (`ApplyRecordedWeatherChange`, `ApplySettingChange` — which mirrors the server's `SimControlService` recorders — `ApplyGeneratorsJson`, `ApplyWeatherJson`). |
| `SimulationEngine.Consolidation.cs` | Consolidate (CON / CON+ over ConsolidationState: records the override; a full consolidation moves the sender's block — GetConsolidatedDescendants reading Attendance — transferring owned tracks and redirecting handoffs) + Deconsolidate (DECON). One body on every run kind; the server's HandleConsolidationCmd and reconstruction wrap it |
| `SimulationEngine.TrackAutomation.cs` | Track automation on every run kind. ApplyAutoTrackConditions(loaded): a scenario aircraft's autoTrackConditions or a generator's; AutoTrackConfiguration → owner (the roster position, else the autoTrackAirportIds position for an airborne departure), scratchpad rules, ERAM interim/cleared altitudes, a delayed handoff to the student on DelayedHandoffQueue (called by the server's PopulateRoom and by AutoTrackGeneratedSpawn; the engine's own LoadScenario holds no ARTCC config and does not). ApplyAutoTrackChange + helpers apply a recorded RecordedAutoTrackChange on every run kind (the server routes CRC .AUTOTRACK deltas as recorded inputs). Spine steps: TickDelayedHandoffs (pre-physics; a target folded under an attended TCP waits), TickAutoAccept (post-physics; after AutoAcceptDelay — floored at SimScenarioState.SoloAutoAcceptFloorSeconds in solo mode — unless the target TCP is CRC-controlled per Attendance, the student's own in solo mode, or a live-feed owner), TickSoloPointoutApproval (post-physics; solo mode only: a pending point-out to an unattended recipient is acknowledged on its behalf after the effective auto-accept delay; the student's own and an attended recipient's wait; outside solo a point-out stays pending with no timeout, announced once at filing by AfterTrackDispatch / AnnounceUnansweredPointout on every track-dispatch path), TickFlightPlanCreatorAutoTrack (an untracked aircraft squawking its assigned code → the plan's CreatedByOwner) before TickDeferredAutoTrack (an untracked departure above the display floor → the autoTrackAirportIds position). The [AutoTrack]/[AutoAccept]/[Pointout] lines go through EmitTerminal |
| `SimulationEngine.Tdls.cs` | The vTDLS spine steps (TickAutoTdlsQueue, TickTdlsAutoWilco, TickTdlsExpiry, TickTdlsTrackRemoval), the AfterAircraftSpawned spawn hook (QueueSpawnTdlsPdc auto-queues a departure's PDC inline at spawn, then PrintSpawnStrip — SimulationEngine.Strips.cs — prints its departure strip, the order the live room ran them in; TickAutoTdlsQueue stays the catch-up path for a plan edited after spawn; the push plan of a push-back stand is made there too, only while AI Ground is active; AfterLoadedAircraftPlaced runs the same hooks for a load's placed aircraft and a replay's load-time aircraft without planning), SetTdlsOpConfig/ApplyTdlsOpConfig (TDLSOPS), IsDepartureAircraft, InitializeFromArtcc (loads the coordination channels — SimulationEngine.Coordination.cs — pre-creates strip bay slots + registers a TdlsConfig for every ARTCC facility that has one; run by scenario load and ReplayDriver), and DrainStateChangesInto (hands the host what FlightStripState.Changes and TdlsState.Changes accumulated since the last drain, strips first, then the coordination, bookmark and session-clock dirty flags as OnCoordinationChanged / OnBookmarksChanged / OnSimStateChanged). Decides from engine state alone (the session clock, the ARTCC's TDLS configuration, the world), so every run kind builds the same DCL/PDC lists. |
| `SimulationEngine.Bookmarks.cs` | The bookmark bodies: AddBookmark(timeSeconds, name, initials) / RenameBookmark / DeleteBookmark / DeleteAllBookmarks over SimScenarioState.Bookmarks + NextBookmarkId (MaxBookmarks 500, the id in the result Message), each marking the bookmark dirty flag that DrainStateChangesInto hands the host as OnBookmarksChanged. Bookmarks stay out of the snapshot on purpose (a rewind carries them over) and BM is RecordingPolicy.Never. The server's RoomEngine.AddBookmark/RenameBookmark/DeleteBookmark wrappers (the desktop client's bookmark RPCs, explicit timeSeconds) call these then drain, so a paused room still broadcasts |
| `SimulationEngine.Transport.cs` | The session-clock bodies: Pause() / Resume() / SetSimRate(int) over SimScenarioState.IsPaused / SimRate (clamped 1..16; refused while LiveTrafficEnabled; (false, "No active scenario") without one), each marking the sim-state dirty flag (OnSimStateChanged → the host's BroadcastSimState). The unattended-pause and rewind paths still write IsPaused directly and broadcast themselves. PAUSE/UNPAUSE/SIMRATE stay RecordingPolicy.Never — a rewind must never pause itself |
| `SimulationEngine.EramConformance.cs` | TickEramVerticalConformance (spine step EramVerticalConformance, after AltitudeFixPassage): latches AircraftEramState.ReachedAssignedAltitude inside the assigned band (±200 ft; block floor−200..ceiling+200; ABV ≥ −200), clears it when EramConformanceKey (EramAltitudeFeet, block floor, ABV) changes; skipped while QT-coasted, frozen or no Mode C. Also TickEramCoverageCoast (spine step EramCoverageCoast, just before AutoAccept): runs Coast/EramCoverageCoast.Evaluate on every aircraft |
| `SimulationEngine.Asdex.cs` | The recorded CRC ASDE-X / SAID display mutations: ApplyAsdexMutation / ApplySaidMutation (tag / terminate / suspend / inhibit / edit onto AircraftStarsState through TrackEngine.SetAsdexField + the SetSaidField / HandleSaidVerb twins; a Terminate tells the host once — OnAsdexTrackTerminated / OnSaidTrackTerminated — so the room's one-shot delete marker fires) and EnableAllAsdexAlerts (ASDXALERTS, a per-aircraft sweep, a Sim arm). A CRC EditDbFields echo stores "" as written; only the typed ASDXSP1-family maps an empty argument to null (clear). ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange) replaces the facility's entry in Scenario.AsdexSafetyLogicConfigs (merged onto the stored one: a different, non-superseded configuration id is a choice and clears the inhibits, a superseded id keeps the stored id and applies only the closures, inhibit starts are stamped from the record's second; the facility is marked for the host) — the body for a CRC safety-logic push on every run kind; dropped with a Debug line when no scenario is loaded. RepickAsdexRunwayConfigurations(before) re-picks the unchosen facilities' runway configuration after an ARWY (called from its ActionArms body), superseding the one moved away from. LapseAsdexInhibits (head of the alert step) drops inhibits 180 sim seconds old; DrainAsdexSafetyLogicConfigChanges() / DrainAsdexSafetyLogicConfigChangesInto hand the marked facilities to IStateChangeConsumer.OnAsdexSafetyLogicConfigsChanged. TickAsdexAlerts(IHostConsumers) is the post-physics alert step (a Sim spine step on every run kind), run once per facility in ordinal order: the facility's configured runways as navdata resolves them, from the per-facility runway cache CurrentAsdexRunways (axes and current footprints; the hold-bar step reads the same cache) + the ground layout's true taxiways (at the layout's own airport, limited to the airport sidecar's asdexLandingTaxiways by AsdexConfiguredTaxiways.Filter) go to AsdexSafetyLogicDetector.Detect (closedRunwayAlertsOnly = the facility's configuration id names none of its runwayConfigurations, read each tick), and ApplyAsdexDetection folds the findings into that facility's; Scenario.ActiveAsdexAlerts entry and hands the host only the diff (IStateChangeConsumer.OnAsdexAlertsChanged: facility, new alerts, cleared ids; never both empty). A facility with no runways or no configuration clears its own alerts only. A tick that moves nothing leaves the set's reference alone; TickAsdexHoldBars(IHostConsumers) is the hold-bar step right after it (StepId.AsdexHoldBars, every run kind): at the facility whose airport is the loaded ground layout it builds the fixed set once per layout and runway resolution (AsdexHoldBarBuilder; a configuration whose runway list changes rebuilds it in the same tick the alerts switch), asks AsdexHoldBarStatusEvaluator which bars are Active, and hands the host only the bars whose status changed (IStateChangeConsumer.OnAsdexHoldBarsChanged: facility, the whole bar with its new status). Transient, never snapshotted: AsdexHoldBarStates(facilityId) gives a subscriber the whole set with the last reported statuses, AsdexHoldBarFacilityIds and ReportedActiveAsdexHoldBars let the server resync or unload what the displays show lit |
| `SimulationEngine.DisconnectCoast.cs` | The disconnect-coast lifecycle as Sim state (Scenario.DisconnectCoasts, callsign → AircraftDisconnectCoast): RegisterDisconnectCoast (called by DeleteAircraft, RemoveLiveTraffic, its replay twin ApplyRecordedLiveTrafficRemoval and TickAutoDelete before World.RemoveAircraft) builds the facets — ERAM when DisconnectCoastRules.IsVisibleOnEram and not frozen or QT-coasted (24 s; a coverage-coasting aircraft's facet carries EramAnchor and expires 24 s after the coast's start), one ASDE-X / SAID facet per AircraftStarsState membership (45 s, IsDrop at the destination) — and replaces any standing entry; TickDisconnectCoastExpiry (post-physics Sim step after the host's auto-delete) expires facets on sim time and hands the host OnDisconnectCoastExpired, which ends those coast entries (also while broadcasts are suppressed) and queues their CRC deletes on TrainingRoom.PendingCoastDeletes; AfterAircraftSpawned (public; SpawnShadow for a live-feed shadow) clears a re-spawned callsign's entry, drained as OnDisconnectCoastsCleared, which ends the server's coast caches and queues a delete for each entry it ends; the queue is sent, awaited and in order, at the start of the room's next unsuppressed CRC broadcast pass, before any live track |
| `SimulationEngine.Eram.cs` | The ERAM CRR-group definitions: CrrGroups (label → EramCrrGroup, case-insensitive) and ApplyCrrGroup(RecordedEramCrrGroup) — create/replace/recolor, null latitude = delete — marking the dirty flag DrainStateChangesInto hands the host as OnEramCrrGroupsChanged (the room re-pushes the whole EramCrrGroups topic; a delete is still the CRC handler's own additive-topic removal). Applied from ActionRouter.ApplyStateRecordCore on every run kind, so a Sim replay has the groups; snapshotted as ServerSnapshotDto.CrrGroups; cleared by ReplayDriver's t=0 block. Also the room's conflict-alert settings: EramRoomSettings (the CA entry; detection never reads it, only what a host shows each sector) with ApplyEramRoomEntry(RecordedEramRoomEntry) — a malformed entry is logged and refused — and its own dirty flag drained as OnEramConflictSettingsChanged (payload-less); snapshotted as ServerSnapshotDto.EramConflictSettings; a snapshot restore also drops ERAM conflicts absent from the snapshot. ApplyEramRoomEntry dispatches on the entry's verb (CA to EramRoomSettings, SM / SMDE to EramSectorMessages, which notifies no host); EramSectorMessages is snapshotted as ServerSnapshotDto.EramSectorMessages (CaptureEramSectorMessages) and cleared by ReplayDriver's t=0 block |
| `SimulationEngine.Coordination.cs` | The coordination bodies: TickCoordinationTimers (post-physics: an acknowledged release voids CoordinationAckExpirySeconds after the ack, flags DepartureExpirationWarning at CoordinationExpiryWarningSeconds remaining, a recalled item reverts to Unsent after CoordinationRecallLingerSeconds — SimScenarioState constants), RemoveCoordinationOnRadarAcquisition (the Track arm's tail: a TRACK voids the aircraft's items), InitializeCoordinationChannelsFromArtcc (Scenario.CoordinationChannels from the ARTCC's STARS lists), and the CoordinationChanged dirty flag (MarkCoordinationChanged / DrainCoordinationChanged) every mutation sets — payload-less because the StarsCoordination topic is always pushed whole. Also the tower-list half of that topic: TickTowerLists (post-physics: TowerListTracker.Update over the world snapshot, marks the flag on change) and InitializeTowerListsFromArtcc (the P-list airports from the ARTCC, run by InitializeFromArtcc after the coordination channels) |
| `SimulationEngine.Strips.cs` | The flight-strip spine steps (TickAutoArrivalStrips, TickAutoApproachDepartureStrips, TickStripDispatches — the queued preset/deferred/triggered strip verbs the command queue could not apply when they were issued, after the departure-strip reprints a preset's flight-plan edit queued (`PendingDepartureStripReprint`)), the spawn hook's strip half (PrintSpawnStrip/TryPlaceConfiguredStrip — position-type default routing vs. a scenario-configured bay/rack), and ReprintDepartureStripAfterAmendment (a flight-plan amendment prints a NEW departure strip carrying the bumped revision; the id it printed under, baked from the record, keeps a replay from minting a second copy). Decides from engine state alone (the session clock, the scenario's student position, the ARTCC's bay configuration, the world), so every run kind builds the same racks and printer queues. |

## Where the tick comes from

The **server** drives the simulation. `RoomTickLoopService` runs a `PeriodicTimer` at 1 Hz wall-clock; for each non-paused room it fires one tick per second. The client does **not** run physics — it receives broadcast snapshots and animates between them ([tick-animator.md](tick-animator.md)).

When a room is paused (`scenario.IsPaused == true`), the host service skips it entirely. `ElapsedSeconds` does not advance, no physics runs, no broadcasts.

## The spine — one sim-second, one definition

Every run kind advances a sim-second by iterating **the spine**: the ordered lists in `src/Yaat.Sim/Simulation/Spine/SpineOrder.cs` (ADR 0001). There is no other list. The live room, the bare test engine, the replay driver and recording reconstruction all call `SimulationEngine.RunSecond(host)` (or, for the sub-tick replay step, its segment entry points), and what differs between them is the **host** each supplies — see below.

A sim-second has five **segments** (CONTEXT.md; *phase* is reserved for an aircraft's flight phase):

```
BeginSecond    ElapsedSeconds += 1                                   (fixed code; the sub-tick replay step keeps its own clock)
OpenSecond     trace reset; host.ApplyPreTickRecordedActions(t)     (recorded spawns / live-traffic samples land before physics)
PrePhysics     SpineOrder.PrePhysics
               ├─ sim TickPrePhysics → host.OnPrePhysics             delayed spawns, generators (a spawn with an autotrack configuration is owned, then recorded), triggers, presets, release queue, timers
               ├─ sim DrainTerminalEntries → host.OnTerminalEntries
               ├─ sim TickDelayedHandoffs                             fires the queued autotrack handoffs; a target folded under an attended TCP waits
               └─ sim TickLiveTrafficSync(host.LiveTrafficFeed)      last, so a sample placed at this second records at this second; the host's feed port answers, the engine spawns/refreshes/removes
Physics ×4     sim TickPhysics(0.25)                                 (fixed code, traced with its sub-tick index)
               └─ SimulationWorld.Tick(0.25, PreTick)
                   ├─ GroundConflictDetector.ApplySpeedLimits
                   ├─ [IsShadow] LiveTrafficKinematics.Advance + airborne latch, then continue (no PreTick, no physics — live-traffic.md)
                   ├─ PreTick → PhaseRunner.Tick   (per aircraft)
                   └─ FlightPhysics.Update         (per aircraft, 8 steps)
PostPhysics    SpineOrder.PostPhysics — the live server's 40-step order
               ├─ sim TickTaxiEdgeTrails                              each moving ground aircraft's TaxiEdgeTrail: the taxi edges it drove, oldest dropped past ~3,000 ft; nothing recorded on a runway roll (line-up, takeoff, rejected takeoff, landing, touch-and-go, stop-and-go); emptied when airborne or warped
               ├─ sim TickLiveTrafficRunwayUse, TickTransponders
               ├─ sim TickAltitudeFixPassage                          an ERAM fix-qualified altitude (170/SJC/110) latches to its second altitude once the aircraft has closed within 10 nm of the fix (≤90° off track) and then has it >90° off; data only (Field B, QF), never flown
               ├─ sim TickEramVerticalConformance                     the ERAM vertical-conformance latch: AircraftEramState.ReachedAssignedAltitude sets inside the assigned band and clears only when the assignment changes, so CRC draws - or + instead of the climb/descent arrow once reached; paused while QT-coasted, frozen or without Mode C
               ├─ sim TickEramCoverageCoast                           the ERAM coverage-loss coast (Coast/EramCoverageCoast): per aircraft, hysteresis 200 ft over the 1,500 ft AGL floor and a 24 s coast on AircraftEramState, plus the 12 s ERAM sweep sample (LastEramSweep); before TickAutoAccept, which withholds a handoff while the track is coverage-coasting or below coverage
               ├─ sim TickAutoAccept, TickSoloPointoutApproval             a pending handoff to an unattended position auto-accepts after AutoAcceptDelay (not while the track is withheld: coasting or below coverage; the controller-AI HandoffUnaccepted rule still flags such a handoff when its receiver is CRC-attended or the student); in solo mode a pending point-out to one is approved after the same delay (Attendance); outside solo it stays pending
               ├─ sim TickFlightPlanCreatorAutoTrack, TickDeferredAutoTrack  an untracked aircraft squawking its assigned code → the FP creator; one crossing the display floor → the autoTrackAirportIds position
               ├─ sim TickCoordinationTimers                           an acknowledged release warns at CoordinationExpiryWarningSeconds remaining and voids at CoordinationAckExpirySeconds; a recalled item reverts to Unsent after CoordinationRecallLingerSeconds (the sender keeps it with its text; CRC hides an Unsent item from every receiver)
               ├─ sim TickTowerLists                                  the P-list dwell entries (snapshotted); a change marks the same coordination flag
               ├─ sim DrainTerminalEntries → host.OnTerminalEntries     the track-automation lines, the same second they were emitted
               ├─ sim TickVisualDetection, TickConflictAlerts → host, TickEramConflictAlerts → host
               ├─ sim TickSurfaceMembership                          ASDE-X / SAID hysteresis membership per aircraft (`AircraftStarsState.VisibleAsdexAirports` / `VisibleSaidAirports`, snapshotted), from the scenario's ARTCC config; an airport no longer configured drops out; the only place hysteresis lives (the server's `CrcVisibilityTracker` diffs these sets)
               ├─ sim TickAsdexAlerts → host                          the ASDE-X Safety Logic detector over the scenario's standing alert set; hands over only the diff (new alerts, cleared ids), and only when something changed
               ├─ sim TickAsdexHoldBars → host                        the ASDE-X hold bars (`StepId.AsdexHoldBars`): the statuses derived afresh from the aircraft, handing over only the bars whose Active / Suppressed status changed
               ├─ sim TickSoloTrainingEvaluation → host                empty outside solo mode
               ├─ sim TickPilotProactive                              after the detectors, before the drains
               ├─ sim TickSituation                                   stamps each aircraft's liftoff time, then classifies it into `AircraftSituationState.Current` with the stored value as the previous one (the hysteresis bands); snapshotted, so it runs on every run kind
               ├─ sim drains → host: warnings, notifications, pilot speech, readbacks, transmissions, approach scores
               ├─ sim TickAutoArrivalStrips, TickAutoApproachDepartureStrips
               ├─ sim TickAutoTdlsQueue, TickTdlsAutoWilco, TickTdlsExpiry, TickTdlsTrackRemoval
               ├─ sim TickStripDispatches                            the queued preset/deferred strip verbs, immediately before the only step that removes aircraft
               ├─ sim drain strip + TDLS changes → host              what this second's steps touched, before AutoDelete so an item's aircraft still resolves
               ├─ sim TickAutoDelete → host                          removes on every run kind; the host tears down room state and broadcasts
               ├─ sim TickDisconnectCoastExpiry → host               expires Scenario.DisconnectCoasts facets on sim time (ERAM 24 s, ASDE-X / SAID 45 s); the host's OnDisconnectCoastExpired ends exactly those facets' coast entries and queues their CRC deletes, sent first in the room's next unsuppressed CRC broadcast pass
               └─ host RundownBroadcast, LiveTrafficStatusBroadcast, PerSecondBroadcasts   (PerSecondBroadcasts sends TimersChanged and ActiveRunwaysChanged when their value changed)
EndOfSecond    SpineOrder.EndOfSecond
               ├─ sim SamplePositionHistory                          every 5 s, 10 deep — the history trails
               ├─ sim AdvanceWeatherTimeline → host                  ungated; the live host mirrors Room.Weather
               ├─ host IssueMetars, ApplyRecordedActions
               ├─ sim TickControllerAi                                gated by RunProfile.RunsControllerAi
               └─ TickCompleted event
```

`sim` is an engine body (`SimulationEngine.Tick*` and the world drains); `host.X` is a **host step**, a body the host supplies; `→ host.OnX` is a **consumer**, where a sim step hands its result over. The 4 sub-ticks give physics 0.25-second resolution while keeping all broadcasts on a 1-second cadence; `SimulationEngine.PhysicsSubTickRate = 4` is the one constant.

### Hosts

`ISimulationHost` (`Simulation/Spine/`) is two views: `IHostSteps` — the bodies — and `IHostConsumers` — the results. A sim step is handed only the consumer view, so it can deliver but never invoke a slot, and no view says which run kind this is; that is `RunProfile`'s (ADR 0005). Neither interface has default implementations: **adding a member fails the build in every host** until each has answered, which is how "a step added to one path and not the other" became unrepresentable.

| Host | Where | Slots | Consumers |
|---|---|---|---|
| `BareHost` | `Yaat.Sim` — `TickOneSecond`, the `RunKind.Test` run | all empty (delayed handoffs, auto-accept and the solo point-out approval are sim steps, so a bare run performs them) | the engine's events (`WarningEmitted`, `TerminalEntryEmitted`, `PilotSpeechEmitted`, which carries the `PilotSpeechText`; a `PilotSpeech` terminal entry gets its `TerminalForRpo` line); the strip and TDLS change sets, the ASDE-X alert diff, auto-deleted aircraft and solo findings are discarded (the removal, the standing alert set and the evaluator's record already happened in the sim) |
| `ReplayHost` | `Yaat.Sim` — the replay driver | pre-tick recorded actions, recorded actions after the second (a `RecordedActionPump`); the rest as bare | as bare |
| `LiveRoomHost` | yaat-server — `RoomEngine.AdvanceLiveSecond` (the hosted loop, the harness, the headless soak room) | every `TickProcessor` body; tape-playback actions when in playback (a pump over the scenario's log at its `PlaybackCursor`); METAR issuance | the broadcasts; `Room.Weather` follows the world's profile |
| `ReconstructionHost` | yaat-server — `RecordingManager.ReconstructViaServerTick` | the same `TickProcessor` bodies; the recorded log around the second (its own pump); no METAR | the broadcasts (suppressed) |

The action path has the same shape, and `ISimulationHost` includes it: `IActionHost` (`Simulation/Actions/`) is the host's view for a controller *action*. No `Apply*` slot is left (every recorded state change has an engine body; the last, the ASDE-X safety-logic configuration, is `SimulationEngine.ApplyRecordedAsdexSafetyLogic` writing the facility's entry in `SimScenarioState.AsdexSafetyLogicConfigs`).

Only the consumers a Sim arm or applier notifies remain: a spawned or deleted aircraft, a hidden live-traffic shadow, a selected position, a lifted ghost overlay, a terminated surface track, changed timers, held departures or consolidation, a weather change, a queued-commands listing, and the strip and TDLS change sets plus the coordination, bookmark, session-clock and CRR-group dirty flags and the ASDE-X alert diff.

Those last are `OnStripsChanged` / `OnTdlsChanged` / `OnCoordinationChanged` / `OnBookmarksChanged` / `OnSimStateChanged` / `OnEramCrrGroupsChanged` / `OnAsdexAlertsChanged`, declared on `IStateChangeConsumer` so the router's action view and the spine's consumer view share one implementation.

Bookmarks and the session clock are engine bodies too (`BookmarkCommandHandler` / `SimulationEngine.Bookmarks.cs`, `TransportCommandHandler` / `SimulationEngine.Transport.cs`; `RecordingPolicy.Never` on both verbs is unchanged — a recorded `BM` or `PAUSE` stays inert, the body's home has nothing to do with whether it is replayed — and the server's bookmark RPC wrappers call the engine bodies then drain, so a bookmark added on a paused room still broadcasts).

Strips, TDLS and coordination are no slots at all: their bodies are the engine's (`SimulationEngine.Strips.cs` / `.Tdls.cs`, `StripCommandHandler`, `StripRequests`, `TdlsCommandHandler`), the engine's spawn hook (`AfterAircraftSpawned`: the push plan while AI Ground is active, the PDC auto-queue, then the strip auto-print; `AfterLoadedAircraftPlaced` skips the plan) runs before the host's spawn consumer, and the amendment reprint is the engine's.

The coordination verbs (`CoordinationCommandHandler` in `Simulation/Coordination/`), the coordination timers step and the removal of an aircraft's items on `TRACK` are the engine's (`SimulationEngine.Coordination.cs`; an item's id is `{ListId}-{SequenceNumber}`, so every run kind mints the same one), and the host only broadcasts what the change trackers drained — for coordination the whole `StarsCoordination` topic, since that is what the wire carries.

`BareHost` discards every consumer; `ReplayHost` delegates to it; yaat-server's `RoomHost` owns no action body, and its consumers carry the room's tails for a fresh action (the spawn hooks and broadcasts, display config on a position change, the CRC and hub broadcasts) while a replaying room keeps its change tracker honest and nothing more.

**`IHostSteps` has no simulation-affecting member left** (ADR 0001's residue is gone as of tick-path 04e): the last one, `LiveTrafficSync`, became `SimulationEngine.TickLiveTrafficSync` over a feed port the host supplies (`IHostConsumers.LiveTrafficFeed`, `Yaat.Sim.LiveTraffic.ILiveTrafficFeedPort`).

The port answers "what does the feed say this second" (`BeginSecond` → `LiveTrafficFeedSecond`: sync or inert, a clear-all reason, the scoped tracks already on sim time; `ShadowStatus(callsign)` per standing shadow; `EndSecond`); the engine decides spawn vs refresh, the collision and filter gates and the removal tiers, and reports each outcome through the `OnLiveTraffic*` consumers (`OnLiveTrafficSpawned` is on `IStateChangeConsumer`, since a replayed sample hands a spawn over too; the rest are on `IHostConsumers`).

`BareHost` and `ReplayHost` answer with `EmptyLiveTrafficFeedPort`, which is always inert. The wall clock never enters the Sim: the port converts observation times to sim seconds.

### The step trace

`SimulationEngine.StepTrace` records every step of the current second — `(StepId, subTick)` in order, an FNV-1a 64 digest over the second and that sequence, per-second and total counts. It is on by default and allocation-free once warm.

It exists because the snapshot oracle cannot see ordering: every host iterates the same lists, so what the trace catches is a host that skips a segment, a second opened against the wrong time, a wrong sub-tick count, or the sub-tick replay split drifting from the whole-second path. What it cannot see is a slot body that does nothing — that stays the oracle's job.

- `SpineTraceTests` (Yaat.Sim.Tests) pins the literal step sequence of one bare second — the only pin on `TickPilotProactive` sitting after the detectors — and proves four `ReplayOneSubTick`s compose the same second as one `ReplayOneSecond` (equal digest *and* equal snapshot).
- The oracle sweep (below) asserts every leg's digest, counts and second equal live's **every second, hard fail, never baselined**.
- `PostPhysicsDrainOrderTests` still pins the drain order behaviourally, through the buffers themselves.

The trace has no switch and stays on in every run kind: the oracle asserts it every second, and it costs a list append, two counter increments and two digest mixes per step.

Per-step timing is separate and opt-in: `SimulationEngine.TickTimings` is null in production (two null checks per step); attach a dictionary and each spine step records under its `StepId` name, plus the segment rollups `PrePhysics` / `PostPhysics` / `EndOfSecond`, one `Physics` bucket per physics sub-tick, and the world's internal buckets (`Physics.WorldTick`, `World.Physics.Ground` / `.Air`, `World.GroundConflict`, …).

`Replay` clears it. The soak runner's `--timings` and `ReconstructionBenchmarkTests` attach one; an export's temp reconstruction room (`RoomEngine.CreateTempReplayEngine`) forwards the live engine's sink, which is how the benchmark times an export. `TickTimingsTests` pins the buckets.

### What a new per-second step looks like

Add a `StepId`, add the entry to the right `SpineOrder` list at its position, and — if the body is the engine's — write it as a `SimulationEngine.Tick*` method (`TickPrePhysics` is the model: compute, update engine-owned state, hand the host the result).

If the body is still the server's, add the member to `IHostSteps` and let the compiler walk you through the hosts. Never add a second list anywhere: the pilot-proactive request reminders once ran dark on the live server for months because the engine and the server each kept their own.

- **ASDE-X alert processing is a sim step; the host only broadcasts the diff.**

  `SimulationEngine.TickAsdexAlerts` (`SimulationEngine.Asdex.cs`) first lapses every arrival-alert inhibit set 180 or more sim seconds ago (`LapseAsdexInhibits`, from stored state, never recorded; the drain hands the changed facilities to `OnAsdexSafetyLogicConfigsChanged`), then reads each facility's safety-logic configuration (`SimScenarioState.AsdexSafetyLogicConfigs`), builds the runway surfaces and taxiway segments, calls `AsdexSafetyLogicDetector.Detect` once per facility (only closed-runway alerts while its runway configuration is LIMITED, empty or names none of its `runwayConfigurations`) and folds the findings into the standing set, `SimScenarioState.ActiveAsdexAlerts` (snapshotted as `ScenarioSnapshotDto.ActiveAsdexAlerts`); a facility with no configuration, or one with no runways, has its own standing alerts cleared instead.

  What leaves the engine is `IStateChangeConsumer.OnAsdexAlertsChanged(newAlerts, clearedAlertIds)` — a diff, because CRC's alert topic is additive with an explicit delete, never called with both sides empty. `RoomHost` sends it as `ReceiveAsdexAlerts` / `DeleteAsdexAlerts` unless `Room.IsBroadcastSuppressed`; `BareHost` discards it and `ReplayHost` delegates to `BareHost`.

  The standing set is an ordinal `ImmutableSortedDictionary` swapped by reference: it is written only on the tick thread, and the CRC initial-data build reads it from a connection's thread, off the tick gate — take the reference once per body.

- **ASDE-X hold bars are a sim step right after the alerts; the host only broadcasts the changed bars.**

  `SimulationEngine.TickAsdexHoldBars` (`SimulationEngine.Asdex.cs`, `StepId.AsdexHoldBars`) runs on every run kind. At the facility whose airport is the loaded ground layout it builds the fixed bar set once per layout (`AsdexHoldBarBuilder`) and asks `AsdexHoldBarStatusEvaluator.ActiveBarIds` which bars are Active this second; the evaluator is stateless, so a status is always a function of the aircraft now, never of an earlier tick.

  What leaves the engine is `IStateChangeConsumer.OnAsdexHoldBarsChanged(facilityId, changed)`: each changed bar whole with its new status, never called empty, because CRC's hold-bar topic upserts by id and never removes a bar. A facility that stops having bars (another layout, no scenario) reports the bars it held Active as Suppressed. `RoomHost` sends it as `ReceiveAsdexHoldBars` unless `Room.IsBroadcastSuppressed`; `BareHost` discards it and `ReplayHost` delegates to `BareHost`.

  The statuses last reported are transient engine state, not snapshotted, and survive a snapshot restore or a scenario load so the next tick diffs against what the displays show. A subscriber gets the whole set from `AsdexHoldBarStates(facilityId)`.

## `FlightPhysics.Update` — 8 steps in order

> This section gives the step ORDER. For the integration math inside each step, the airspeed-frame model (IAS/TAS/GS/Mach), and the validated per-category performance constants, see [flight-physics.md](flight-physics.md).

For each aircraft, `FlightPhysics.Update(ac, deltaSeconds, …)` runs:

1. **`UpdateNavigation`** — sequence to next waypoint; on arrival, fire `NotifyFixSequenced` (which feeds AT-fix triggers); compute course-to-target.
2. **`UpdateDescentPlanning`** — look ahead at altitude restrictions on the route; precompute descent rate to make them.
3. **`UpdateClimbPlanning`** — same for upcoming climb-to constraints.
4. **`UpdateSpeedPlanning`** — proactive speed look-ahead for procedure speed restrictions. Mirrors descent/climb planning.
5. **`UpdateHeading`** — turn toward target; bank angle from `atan(TAS × turnRate × coeff)`; snap at ±0.5°.
6. **`UpdateAltitude`** — climb/descend; expedite multiplies rate by 1.5×; snap at ±10 ft.
7. **`UpdateSpeed`** — accelerate/decelerate. Auto schedule **skipped** when `ActiveApproach` is set or current phase has `ManagesSpeed=true`. 14 CFR 91.117 caps 250 KIAS below 10,000 ft and 200 under a Class B shelf; a target above the cap stays standing and is taken up when the cap lifts. Mach hold recomputes equivalent IAS each tick.
8. **`UpdatePosition`** — TAS = `IasToTas(IAS, alt)`; ground track and groundspeed from TAS plus wind vector; lat/lon advances by groundspeed × delta.
9. **`UpdateCommandQueue`** — evaluate `CommandBlock` triggers (LV altitude, AT fix, intercept, give way, on handoff…). When met, fire the closure; advance the queue if `ReadyToAdvance`.
10. **`PilotObservationUpdater.Update`** — re-check pending visual acquisitions (RTIS/RFIS soft-fail watch state). On success, emit pilot readback. **Runs after `UpdateCommandQueue`** so observations see post-queue state.

The numbering is 1–10 even though the documented summary says "8-step" — the post-position queue and observation steps are sometimes counted with the queue step.

## Phases run **before** physics

Inside `SimulationWorld.Tick(delta, preTick, …)`, the `preTick` callback constructs a `PhaseContext` and calls `PhaseRunner.Tick(phases, ctx)`. So per sub-tick:

1. `PhaseRunner.Tick` — phases write to `ControlTargets`.
2. `GroundConflictDetector.ApplySpeedLimits` — caps target speed for proximity; ranks aircraft per runway first (runway user, then vacating, then the rest) so the lower rank yields.
3. `FlightPhysics.Update` — physics consumes the freshly-written targets.

This is why phases write directly to `ctx.Targets` and never enqueue commands: they own targets up until physics reads them.

A test that steps physics and then `PhaseRunner.Tick` (the order `PatternCompressionFlownTests` uses) samples the aircraft after the phase has written its targets and before physics applies them, the reverse of the engine's order above; its samples (and the tick a trigger fires on) are not the engine's, so assert on outcomes, not on a tick number.

## Conflict detection

Two detectors. Both are server-only (clients see results in broadcast snapshots).

- **Ground** — `GroundConflictDetector.ApplySpeedLimits` runs each sub-tick *before* physics. Pairwise check, skipping a pair beyond `SearchRangeNm` (0.3 nm) before `ResolvePair` runs, by a latitude-difference bound (`IsBeyondSearchRange`); classifies movement state (taxiing / pushing / stationary / following) and runway rank (runway user / vacating / rest), applies speed caps when proximity drops below thresholds.
- **Airborne** — `ConflictAlertDetector.Detect` runs in the `ConflictAlerts` spine step (`SimulationEngine.TickConflictAlerts`). Predicts position 5 s ahead; reports pairs where current or predicted separation crosses thresholds (3 nm / 1000 ft IFR; 0.25 nm / 500 ft VFR). Hysteresis on existing conflicts (must reach 3.3 nm / 1100 ft to clear). Mode-C-only; ignores aircraft on ground or with CA inhibited; suppressed during paired approaches; `CASUP` pairs and shadow↔shadow pairs never alert.

## Broadcast cadence

`RoomTickLoopService.RunTickLoop` fires on a one-second wall-clock `PeriodicTimer`. Each wall second `ProcessRoomSecond` advances every tickable room by `max(1, SimRate)` sim-seconds (a loop of `AdvanceLiveSecond`), then `DetectChangesAsync` and `BroadcastUpdates` run **once per wall second**, after those sim-seconds, not once per sim-second (at 4x, four sim-seconds of motion arrive in one delta). `BroadcastUpdates` calls `BroadcastTrainingUpdates`, `BroadcastAdminUpdates` and `CrcBroadcastService.BroadcastUpdatesAsync`; `BroadcastTrainingUpdates`:

1. `DetectChanges` walks each aircraft and computes a delta.
2. Changed aircraft → `AircraftSnapshot` DTOs go out via SignalR (JSON to YAAT clients).
3. `CrcBroadcastService.BroadcastUpdates` evaluates each subscribed CRC topic and emits MessagePack updates/deletes. See [crc-display-state.md](crc-display-state.md).
4. Drained warnings / notifications / pilot readbacks become terminal entries.

CRC visibility transitions (entering STARS coverage, ASDEX airport entry/exit, coast phase) are evaluated by `CrcVisibilityTracker` inside the broadcast pass — not in physics.

ASDE-X / SAID membership is the exception: the Sim step `TickSurfaceMembership` computes it on every run kind (hysteresis included), and the same per-aircraft evaluation also runs between ticks at every spawn and after every successful aircraft command, so a paused room's surface displays are current, and the tracker's ASDE-X/SAID evaluators only diff the aircraft's Sim sets against the set the previous evaluation saw (newly visible, removed), which drives the per-airport creates and deletes.

## Recording capture

Recording is **event-driven, not snapshot-per-tick**. `RecordingManager.Record(action)` appends `RecordedAction` entries (commands, setting changes, spawns) at their `ElapsedSeconds`. Snapshots (`StateSnapshotDto`) are not captured every second: `RoomEngine.AdvanceLiveSecond` keeps a compressed live checkpoint every 60 s for rewind and seek to start from, and an export regenerates a recording's snapshots. See [snapshots-and-replay.md](snapshots-and-replay.md).

## The tick oracle — what guards the two paths against each other

The per-feature parity tests above each guard one step. The **oracle** guards the whole captured state: it loads one scenario and seed into a headless room per run kind, advances them in lockstep, and diffs `CaptureSnapshot` every sim-second against the live room's. A step added to one path and not the other shows up whether or not anyone wrote a test for it. See ADR [0004](adr/0004-the-oracle-and-the-corpus.md).

- **The legs.** `live` is `RoomEngine.AdvanceLiveSecond()` — the spine under the `LiveRoomHost`. `test` is `SimulationEngine.TickOneSecond()` — the spine under the `BareHost`. `replay` is `SimulationEngine.ReplayOneSecond()` — the spine under the `ReplayHost`, which differs from bare only in the three recorded-action / weather steps and runs under the `Replay` profile; it earns its own leg because a regression in the run-profile allowances can surface on no other pair.

  `reconstruct` is `RecordingManager.ReconstructViaServerTick()` — the spine under the `ReconstructionHost`, which fills every room-owned slot the live host fills and re-applies the recorded log around each second.

  Every room is loaded through the server's own `LoadScenarioSeededAsync`, so the scenario load is not a variable and only the host differs; the replay room is armed afterwards with `SimulationEngine.ArmReplay(actions)`, using an action log a first live pass collected (it must be complete before arming — the replay leg applies it second by second).

  Alongside the snapshot diff, every leg's `StepTrace` digest, counts and second are asserted equal to live's each second — a hard failure, never baselined.
- **Findings.** *Physics is identical on all four run kinds* — zero position/altitude/speed/heading divergence over 600 s without weather; with weather it diverges on every leg (the three-semantics cascade ADR 0001 predicts) unless the run kinds share one advance semantic, which they do (step 5).

  The ATC/track state the host owned (the auto-accept, delayed-handoff and autotrack passes, position-history sampling, auto-delete, the strip and TDLS bodies) is engine state (steps 5 and 4), and **all twelve baselines are `Entries: []`**.

  *What they do not reach* (a green sweep is not full coverage): the deferred / FP-creator autotrack passes and a generator spawn carrying an autotrack configuration (pinned by `AutoTrackStepTests` in Yaat.Sim.Tests instead), solo-training evaluation, the auto-delete mode gates, and everything outside `CaptureSnapshot` (ASDE-X).

  Attendance is reached: the `actions` fixture signs a CRC client on to 4U from second 37 to 91 (`ScriptedAttendance`), so a leg that failed to apply the recorded attendance would auto-accept `HO 4U` at 43 and show on `Track.Owner`.

- **Comparator**: `src/Yaat.Sim/Simulation/Oracle/` — `SnapshotTreeDiff` walks two `JsonNode` trees serialized with `RecordingJsonOptions.Default` and emits one divergence per differing leaf, at a path that is literally the JSON pointer (`Aircraft[SWA123].Track.Owner.SectorId`). `DivergenceAccumulator` folds the per-second stream by *normalized* path (collection keys collapsed to `[*]`), which is what keeps the summary bounded when one divergence cascades.

  Each second the oracle first compares `SnapshotStateHash` (FNV-1a 64 over an ordinal-key-sorted canonical serialization of the same tree) of each leg against live, and runs `SnapshotTreeDiff` only for a second whose hashes differ; the diff stays the verdict, so a hash difference the diff normalizes away (aircraft order, virtual node ids, embedded JSON) records nothing. The hash only skips work on equal seconds.
- **Driver**: `tests/Yaat.Server.Tests/Oracle/TickOracleTests.cs` in yaat-server — only that repo can drive the live path.
- **The fixtures.** The sweep runs every pair over each `OracleFixture`, because a divergence set is a statement about the code *and* about what the input reaches. `s2oak4` is the long real-traffic run that reaches the ATC/track divergences.

  `weather` is the same scenario under a deliberately gentle wind ramp — 40° and 2 kt over two minutes, so each second's change stays under 1°/0.5 kt: a frozen advance, or one gated on a change threshold, would diverge from the continuous advance every run kind applies, so the fixture pins that semantic. `autodelete` is two parked aircraft, one carrying a preset `DEL`.

  It has to use the queued-delete branch: `ScenarioLoader` sets `AutoDeleteExempt` on every scenario-declared aircraft, so the `Parked`/`OnLanding`/departed-overflight gates are unreachable without generated traffic. The fixture asserts an aircraft actually disappeared, because one that silently stops reaching its gate produces a spotless baseline that reads as agreement.

  `actions` is S2-OAK-4 with a **scripted controller** (`OracleFixture.Script`, each step issued into the live room after the second it names, the same slot a recorded action is re-applied in; `ReactionDelaySeconds` installs a constant pilot-reaction delay on every room).

  The other fixtures only tick and so are blind by construction to one class of divergence — a recorded action that live routes through one arm and a replay applier through another, or none: both replay legs apply the same log through the same applier.

  The script covers every known member (global commands recorded with an empty callsign, position commands, a reaction-delayed manoeuvre, a typed flight-plan amendment, a ghost, a refused verb); the replay and reconstruct legs consume the log the live pass recorded, and the test leg issues the script fresh through the bare engine.

  Every live step asserts its expected verdict — a refused command is not recorded, so a step that silently failed would hollow out the fixture instead of failing it. A step must also reach *state*: the first cut's squawk globals left no trace because every aircraft already squawked its assigned code, so the script now sets a discrete code and standby first. Its baselines are the pin for tick step 3d and shrink as each arm is routed once.
- **Accepted divergences**: one baseline file per fixture and pair under `yaat-server/docs/tick-oracle/`, named `<fixture>.<pair>.baseline.json`, one entry per normalized path with the step it is attributed to. The pair itself is an `OraclePair` in the driver — key, the two side labels, and the long comparison string — so adding a leg is that record plus the loop driving its right-hand side; the csproj globs `*.baseline.json`, so adding a file does not mean editing it.

  The path set is asserted exactly, so both a new divergence and a silently-fixed one fail, and every leg is evaluated before the test fails so one red pair never hides another. Regenerate deliberately with `YAAT_ORACLE_REBASELINE=1` and review the diff before committing; a regeneration that finds nothing new rewrites every file byte-identically, so anything `git status` shows is a real change.
- **The two failure directions are not symmetric, and the message says so.** A *new* path is self-evidently something to investigate.

  A *vanished* one reads like good news and usually is not: during behaviour-preserving work the likeliest cause is that a path lost the step that produced the divergence, so `Describe` renders that branch as a regression to be attributed and deliberately **withholds** the re-baseline command there — offering it would hand you a one-liner that banks the lost step as the new expected state.

  Retiring an entry on purpose (tick step 5) still works; it just has to be something you can name first. Pinned by `TickOracleBaselineTests`.
- **Permanent exemptions**: `OracleExemptions` — for what no amount of unification would fix (a value that is not simulation state). Empty by design; if it grows, something is being accepted there that belongs in the baseline.

**What it still cannot see, worth knowing before trusting a green run.**

It sees exactly what `CaptureSnapshot` covers, so the approach evaluator — room-held, no snapshot DTO — is invisible until that state moves into `Yaat.Sim` (the ASDE-X safety-logic configuration and the standing alert set are covered — `ScenarioSnapshotDto.AsdexSafetyLogicConfigs` / `.ActiveAsdexAlerts` — the configs are derived at load, and the alert step is pinned by `AsdexAlertStepTests`).

Tower lists are covered (`ServerSnapshotDto.TowerLists`, the dwell entries, schema 23), and the step runs on every leg.

Strips and TDLS *are* covered (`ServerSnapshotDto.Strips` / `.Tdls`), and since their bodies are the engine's, the spawn hook (N342T's delayed spawn at t=271), the flight-plan-amendment reprint (the scripted `FP` at t=50), the TDLS arm and the `ADD` spawn hook all run on every leg — the `actions` fixture scripts `TDLSQ`/`TDLSS`/`TDLSW` on N172SP and an IFR `ADD` on 28R, so they are reached.

A verb that mints a Guid id (`SEP`, `HSC`, `SCAN`) cannot be scripted in it — the live leg is a second run compared against the first run's log, and two runs never draw the same Guid, so those stay pinned by `StripIdBakingTests`.

The fixture's `ADD V S P @NEW1` is a parking spawn, and a parking spawn files no departure airport (`AircraftGenerator.GenerateAtParking` builds an empty plan; only the IFR runway form files one), so `IsDepartureAircraft` is false and the spawn hook is not reached through the `ADD` arm on any leg, and no fixture departure acquires a track owner inside its window, so the `TdlsTrackRemoval` step never fires (pinned by `TdlsStepTests` instead).

Coordination is reached: the `actions` fixture scripts `RD` / `RDH` / `RDACK` / `RDR` on NCT's `POAK` list (the student OAK_TWR is its only sender, 4U a receiver), the acknowledgement at t=54 crosses the expiration warning at t=114 and the recall at t=64 its removal at t=74, and the deterministic item id (`POAK-1`) is what lets two live passes agree.

Solo-training evaluation is unreached by every fixture: the evaluator keeps its own record of what it scored and mutates no aircraft from `Evaluate`, so a replayed solo session rebuilds that record, and no fixture is a solo room.

And **the replay leg is weaker than it looks**: no fixture declares a runtime generator, so no action log carries a `RecordedAircraftSpawn` (asserted in the sweep, not assumed) and the generator stand-down in replay never has anything to stand down (it is pinned by `ReplayGeneratorStandDownTests` in Yaat.Sim.Tests instead), while recording suppression is invisible in principle because the action log is not part of `ScenarioSnapshotDto` — it is not simulation state.

The test's own doc comment carries the current blind-spot list.

## Rules for a change to the tick path

- **The server is a thin layer.** It holds as little state and logic as possible beyond relaying between the Sim and its clients (YAAT and CRC). When a change has to choose between keeping state or a decision on the server and moving it into the Sim, it moves it; the server keeps only wire projection, per-subscriber fan-out and connection bookkeeping (ADR [0003](adr/0003-simulation-logic-moves-into-yaat-sim.md)).
- **Predict, then re-baseline.** A commit that changes an oracle baseline names the entries it will retire before running `YAAT_ORACLE_REBASELINE=1`; an unpredicted `Removed` or `Added` stops the work until it is attributed.

  The trap this order exists for: a live-side regression makes a divergence disappear, which the oracle reports under `Removed`, so a regression presents as a congratulation with a suggested re-baseline. A behaviour-preserving commit is baseline-neutral in both `Added` and `Removed`; re-baselining is not an available response there.
- **Corpus triage** (ADR [0004](adr/0004-the-oracle-and-the-corpus.md)): an over-broad assertion → fix the test; a genuine desync → delete the recording; an unexpected cause → stop.
- **Green across both repos before every commit** (`pwsh tools/test-all.ps1`), red test first, and the commit records predicted-versus-got so a later reader can check the attribution.

**Live behaviour changes that land as named decisions, not silently:** ASDE-X/SAID coast runs on sim time (as the ERAM coast does) and the CRC surface displays follow the Sim's membership, so a room's ASDE-X/SAID airports come from its own ARTCC config; the three post-physics ordering moves (ADR [0002](adr/0002-ordering-defers-to-live-semantics-defer-to-merit.md)); `DrainAllApproachScores` consumed on every path.

## Client-side: animation only

The client receives `AircraftSnapshot` DTOs each sim-second. It does not run physics. `TickAnimator` (UI layer) interpolates position/heading between consecutive snapshots so movement looks smooth at the display refresh rate. Animation is non-authoritative; if a snapshot disagrees, the snapshot wins.

## Pitfalls

- **Order matters for new tick work.** Want to inject something? PrePhysics if it must affect this second's physics; PostPhysics if it consumes physics output (alerts, broadcasts); EndOfSecond for bookkeeping over the completed second.

  Put it in `SpineOrder` at its position (§ What a new per-second step looks like) — never in a host or a caller. Inside the physics sub-tick: pre-physics callback for things that must run before the 8 steps; otherwise add a step in `FlightPhysics.Update` and document where in the order.
- **`ManagesSpeed` and `ActiveApproach` skip auto speed schedule.** Pattern phases own speed; approach owns speed via the procedure. Don't add a feature that "fights" them — gate it on those flags.
- **Sub-tick rate is 4.** If you write a feature that fires "once per tick," be explicit: per **sub-tick** (4× per second) or per **sim-second** (after PostPhysics)? Conflict detection is post-physics → per second. Phase tick is pre-physics → 4× per second. Get this wrong and rates drift by 4×.
- **No client tick.** Don't try to run physics on the client to "smooth out" anything. Smoothing is `TickAnimator`'s job and the server is authoritative.
- **Broadcasts are deltas.** Adding a field to a DTO doesn't make it broadcast — `DetectChanges` / `ChangeTracker` must consider it, otherwise the field round-trips on initial subscribe but never updates.
- **Removing an aircraft must clear its tracker entry.** `DetectChanges` returns `DtoChangeFlags.All` only for a callsign it has never seen. Any path that removes an aircraft has to call `room.ChangeTracker.Remove(callsign)` (and `room.AircraftAssignments.Remove(callsign)`) alongside the delete broadcast — otherwise a same-callsign recurrence is diffed against the *dead* aircraft and every category that still matches is silently dropped from its initial CRC push.

  CRC has no out-of-band spawn message to paper over this; the training hub does (`AircraftSpawned`), which is why the client looks fine while CRC shows no track.
- **`AutoDelete` and `SoloTrainingEvaluation` run on every path.** A test that asserts on an arrival still parked after the second it parked sees a state only the bare path used to keep, since a live room removes it that second; send `NODEL` first (the instructor's own way to keep it) or step by segment.
- **Pause halts everything for that room.** Don't put background work in the tick path expecting it to keep ticking when paused.

## File map

The spine and oracle folders. The `SimulationEngine` partials are in [The engine's partial files](#the-engines-partial-files) above.

One entry per file; paths are relative to `src/Yaat.Sim/`.

```
# Simulation/Spine/ — the one ordered definition of a sim-second (§ the spine, ADR 0001)
SpineOrder.cs                  # The three step lists (PrePhysics / PostPhysics / EndOfSecond) in the live server's order — the adjudication record of ADR 0002
StepId.cs                      # One member per step, in spine order; the trace's and the timing buckets' key. PostPhysicsTerminalEntries drains the lines the
                               # track-automation steps emit so they reach the room the same second
SpineStep.cs                   # One list entry: a sim step (engine body, gets only IHostConsumers) or a host step (gets only IHostSteps)
IHostSteps.cs                  # The host's step view — every server-owned body as a named member, no defaults (a new member breaks every host); header lists the
                               # step-4 debt — none left: LiveTrafficSync became SimulationEngine.TickLiveTrafficSync over IHostConsumers.LiveTrafficFeed (04e). CoordinationTimers and TowerLists moved out (SimulationEngine.TickCoordinationTimers / TickTowerLists). The two strip auto-print passes (AutoArrivalStrips/
                               # AutoApproachDepartureStrips) and the four TDLS tick steps (AutoTdlsQueue/TdlsAutoWilco/TdlsExpiry/TdlsTrackRemoval) moved
                               # out — they're Sim steps now, engine bodies in SimulationEngine.Strips.cs / SimulationEngine.Tdls.cs. AsdexAlerts moved out
                               # (SimulationEngine.TickAsdexAlerts)
IHostConsumers.cs              # The host's consumer view — OnPrePhysics / OnTerminalEntries / OnConflictAlerts / the drains / OnStripsChanged / OnTdlsChanged / OnCoordinationChanged
                               # (IStateChangeConsumer, shared with IActionHost) for what the strip, TDLS and coordination mutations touched
IStateChangeConsumer.cs        # Where a drained StripChangeSet / TdlsChangeSet and the coordination dirty flag (OnCoordinationChanged, payload-less) go, and OnLiveTrafficSpawned (a shadow's spawn hand-over, from the feed's sync and from a replayed sample alike). Declared on its own because both halves of a host reach it: the action
                               # router holds the action view (IActionHost) and the post-physics drain step the consumer view (IHostConsumers), and one
                               # implementation on a host answers both. OnStripsChanged / OnTdlsChanged: the host broadcasts unless suppressed; a
                               # reconstruction drops them and the room re-syncs afterwards. OnAsdexAlertsChanged(facilityId, newAlerts, clearedAlertIds): one ASDE-X
                               # facility's alert-step diff (CRC's alert topic is additive with an explicit delete); the room broadcasts it on that facility's topic unless suppressed.
                               # OnAsdexHoldBarsChanged(facilityId, changed): one facility's hold bars whose status changed, each whole with Active / Suppressed (CRC upserts bars by id and never removes one); same suppression rule
ISimulationHost.cs             # IHostSteps + IHostConsumers + Actions.IActionHost; four implementations (BareHost, ReplayHost, yaat-server LiveRoomHost / ReconstructionHost)
StepTrace.cs                   # Per-second (StepId, subTick) sequence + FNV-1a digest + counts; on by default, allocation-free once warm
BareHost.cs                    # The Test-run host: every spine slot empty, consumers fire the engine's events; as an IActionHost
                               # IsPositionAttended is false and every consumer is discarded

# Simulation/Oracle/ — state-equivalence between run kinds (ADR 0004). Driver: yaat-server TickOracleTests.
SnapshotTreeDiff.cs            # Parallel JsonNode walk over two StateSnapshotDto captures -> one SnapshotDivergence per differing leaf, at the JSON-pointer path.
                               # Aircraft keyed by callsign (only list that reorders), everything else index-keyed; embedded-JSON strings (WeatherJson,
                               # ConfigJson, ...) re-parsed so paths reach inside them; negative VirtualNode ids normalized to -V.
SnapshotStateHash.cs           # FNV-1a 64 over an ordinal-key-sorted canonical serialization (CanonicalUtf8) of ToComparableNode's tree; the
                               # oracle's per-second gate: equal hashes skip the tree diff, different hashes run it.
DivergencePath.cs              # Normalize(): collapses every [key] to [*], so the baseline is about fields, not about which aircraft spawned.
DivergenceAccumulator.cs       # Folds a per-second divergence stream by normalized path: first second + a few concrete examples, plus FirstDivergentSecond.
TickOracleBaseline.cs          # The checked-in accepted-divergence set (Load/Render/CompareTo) + TickOracleComparison (Added/Removed/regression, Describe).
OracleExemptions.cs            # Permanently-accepted paths, with reasons. Empty by design — distinct from the baseline, which is meant to shrink to nothing.
```
