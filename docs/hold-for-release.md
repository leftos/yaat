# Hold for Release (HFR / REL)

**Read before touching** `HeldReleaseService`, `DepartureSpawnClassifier`, the hold-for-release gates in `CommandDispatcher` / `TaxiingPhase` / `SimulationEngine`, the `RundownDto` broadcast, `RunwaySpawnCall`'s release request, or any `HeldForRelease` / `ReleasedForDeparture` / `ReleasedAtSpawnGate` field.

Companion to [`scenario-loading-and-generation.md`](scenario-loading-and-generation.md) (spawn pipelines), [`phases.md`](phases.md) (HoldingShortPhase), [`command-pipeline.md`](command-pipeline.md) (group-command routing), [`training-hub-contract.md`](training-hub-contract.md) (the broadcast), and [`solo-training-pilot-speech.md`](solo-training-pilot-speech.md) (the runway spawns' release request).

GitHub issue: https://github.com/leftos/yaat/issues/168

## What & why

A TRACON provides **departure release** services to the satellite *towered* airports under its
airspace (the issue's example: NorCal TRACON / NCT releasing San Jose SJC, Palo Alto PAO). The
satellite tower must obtain a release from the TRACON before it can clear an IFR departure for
takeoff; the TRACON may say "hold for release" when it has conflicting traffic, then release when
ready. This feature lets the student/RPO (playing the radar position) control when satellite
departures become airborne.

**The unifying concept: _released = this departure is now authorized to enter the runway and become
airborne._** Everything below is bookkeeping around that one idea.

**Spawn-state-aware hold.** HFR gates the *moment of becoming airborne*, never ground movement:

- A departure that spawns **airborne or lined up on the runway** has its **spawn** gated — it appears
  on the scope only when released (then climbs out / rolls).
- A departure that spawns at **parking / taxiway** spawns and taxis normally to the runway, then
  **holds short** of it (the existing `HoldingShortPhase`) — it never takes the runway while held, so
  it doesn't block arrivals/crossings. Only runway *entry* (LUAW / takeoff) is withheld.

**IFR only.** A release gates an **IFR departure clearance** (7110.65 §4-3-4.b.1); a VFR aircraft isn't
on an IFR clearance, so there's nothing to hold — it departs on the tower's own authority (cf. §4-3-9,
which covers the only VFR↔release interaction: an IFR-filed aircraft electing to depart VFR). VFR
departures therefore depart normally even when their airport is armed.

## State model

All hold-for-release state is **per-room and lives in `Yaat.Sim`** (the server only routes commands
and broadcasts). `HeldReleaseService` is the only writer.

| State | Where | Meaning |
|---|---|---|
| `SimScenarioState.HeldDepartureAirports : HashSet<string>` | `Yaat.Sim/Simulation/SimScenarioState.cs` | Airports armed for hold-for-release. Single source of truth for "airport X is armed." |
| `DelayedSpawn.HeldForRelease : bool` | `Yaat.Sim/Simulation/ScenarioQueues.cs` | Marks a delayed spawn as a held-spawn *candidate* (runway/airborne departure). Set at load via `DepartureSpawnClassifier`; the runtime armed check decides if it's actually held. |
| `AircraftGroundOps.HeldForRelease : bool` | `Yaat.Sim/AircraftGroundOps.cs` | A spawned ground departure is held short until released. The runway-entry gate reads this; the rundown lists it. |
| `AircraftGroundOps.ReleasedForDeparture : bool` + `ReleasedAtSeconds : double` | `Yaat.Sim/AircraftGroundOps.cs` | Set by REL on a ground departure: authorized, awaiting the auto-issued takeoff clearance once it's holding short (or lined up, for a runway spawn that asked for its release). Solo rooms only act on it; an RPO room clears it on the next tick and the departure waits for a `CTO`. `ReleasedAtSeconds` anchors the readback jitter. |
| `AircraftGroundOps.ReleasedAtSpawnGate : bool` | `Yaat.Sim/AircraftGroundOps.cs` | Set when a held runway/airborne spawn is released through the spawn gate: it already has its release, so a runway spawn never asks for one and departs as one that asked and got REL. |
| `SimScenarioState.ReleaseQueue : List<ScheduledRelease>` | `Yaat.Sim/Simulation/ScenarioQueues.cs` | Pending auto-spaced releases (one per departure when a whole field's queue is released with an interval). Fired by `ProcessReleaseQueue` against `ElapsedSeconds`. |

`DepartureSpawnClassifier.IsHeldSpawnCandidate(loaded)`
(`Yaat.Sim/Scenarios/DepartureSpawnClassifier.cs`) returns true for an IFR departure whose
`CurrentPhase` is `LinedUpAndWaitingPhase` (on the runway) or `InitialClimbPhase` while airborne
(climbing out) — those are the spawn-gated cases. Parking/taxiway departures return false and are
handled by the per-aircraft ground flag instead.

## The two gates

### Spawn gate — `SimulationEngine.ProcessDelayedSpawns`
A held runway/airborne departure is skipped while its airport is armed
(`HeldReleaseService.IsSpawnHeld(scenario, entry)`), so it never enters the world. When a ground
departure spawns under an armed airport, `HeldReleaseService.MarkHeldOnSpawnIfArmed` sets its
`Ground.HeldForRelease` so it will hold short. (The same load-time `HeldForRelease` marking is applied
on the server in `ScenarioLifecycleService` and on the manual `SPAWN` path via `HandleSpawnNow`.)

### Runway-entry gate — block LUAW + CTO for held ground departures
A held ground departure must hold **short** of the runway, so the gate blocks anything that would put
it *on* the runway — both `LineUpAndWait` (→ `HoldingInPositionPhase`/LUAW, on the runway) and
`ClearedForTakeoff`/`ClearedTakeoffPresent`. The gate reads `aircraft.Ground.HeldForRelease` **directly**
(no `DispatchContext` plumbing needed) at two enforcement points:

1. **Command issuance** — `CommandDispatcher.TryApplyTowerCommand` rejects CTO/CTOPP/LUAW in the first block of a dispatch with `"{cs} is held for release at {dep} — REL {cs} first"`, so a CTO the RPO types while the departure is held is refused. A held departure's CTO/CTOPP/LUAW is never refused in two other cases; it waits instead and fires on the first tick after `REL` or `HFROFF` lifts the hold, in every room:
   - a **timed** preset whose first block carries CTO, CTOPP or LUAW and falls due while the departure is held is not dispatched: `SimulationEngine.ProcessTimedPresets` keeps it queued (`IsHeldUntilRelease`), with no warning, and every later-due preset for that aircraft waits behind it. On the release the held presets dispatch in fire-time order (a `LUAW` at +30 s before a `CTO` at +60 s; presets due the same second keep the queue's reverse order);
   - a **chained or triggered** CTO/CTOPP/LUAW block (a timed preset `TAXIAUTO 28R; CTO`, the same chain typed, or `AT B CTO`) stays unapplied in the aircraft's command queue when its turn comes (`FlightPhysics.WaitsForRelease`, checked at the top of `FlightPhysics.ApplyBlock`), with its chain intact, and the terminal shows `SWA1234 CTO waits for the release` once: the aircraft taxis, stops at the bar, and takes off on that CTO once released.

     Triggered blocks queued behind it still fire ([command-chaining.md](command-chaining.md), "Abort on fire-time failure"). A new non-conditional command for the aircraft runs the usual dimension-aware queue clear, which can drop the waiting block with the queue-clear warning.

   The three gates share one set of runway-entry commands, `HeldReleaseService.IsRunwayEntryCommand`.

   CROSS is left alone — a departure holding short of its *own* departure runway isn't crossing it.
2. **Stored-clearance consume** — `TaxiingPhase.ApplyDepartureClearanceIfPending` skips applying a
   stored departure clearance while `Ground.HeldForRelease`, catching the one path that isn't a fresh
   command issuance (a clearance issued before the airport was armed).

**Why `HoldingShortPhase` and not LUAW:** a departure with no LUAW/CTO clearance already sits in the existing `HoldingShortPhase` indefinitely (it is gated on a `RunwayCrossing` requirement satisfied only by CROSS/LUAW/CTO). `HoldingInPositionPhase` / `LinedUpAndWaitingPhase` are the *on-runway* states.

So withholding runway-entry clearance keeps a held departure off the runway with **no new hold phase**. The release auto-CTO (below) runs only once the hold is lifted, so it needs no gate of its own — those two reads are the whole gate.

## Release flow

`HeldReleaseService.Release(scenario, world, rng, target, intervalSeconds?)` is the entry point
(called from `RoomEngine` and from `ProcessReleaseQueue`):

- **`target` is a callsign** → release that specific held departure.
- **`target` is an airport, no interval** → release the next-pending there (rundown order).
- **`target` is an airport + interval** → enqueue one `ScheduledRelease` per held entry, spaced
  `interval` seconds apart; `SimulationEngine.ProcessReleaseQueue` fires each in order.

`ReleaseOne` acts by spawn state:

- **Held runway/airborne spawn** — clear `DelayedSpawn.HeldForRelease`, set `Ground.ReleasedAtSpawnGate`, and set `SpawnAtSeconds = Elapsed + Rng(20..60)` so `ProcessDelayedSpawns` spawns it shortly after (it appears climbing, or lined up on its runway — never on the release tick).

  A runway spawn with no scripted takeoff then leaves as described under [Release requests from untowered runway spawns](#release-requests-from-untowered-runway-spawns): on its own in a solo room, on the RPO's `CTO` in an RPO room.
- **Held ground departure** — `ReleaseHeldGroundDeparture`: clear `Ground.HeldForRelease`, set `ReleasedForDeparture = true` and `ReleasedAtSeconds`, and answer the aircraft's open `Release` pending request (`PilotRequestTracker.SatisfyOpenRequest`), if it made one, which also clears the frequency's awaiting-controller-response gate (`SimulationWorld.AcknowledgeControllerResponse`).

  A CTO waiting in the aircraft's queue or as a held timed preset fires on the next tick. What follows depends on the room. In an **RPO room** nothing departs on its own: `SimulationEngine.ProcessReleasedGroundDepartures` clears `ReleasedForDeparture` on the next tick, and the released departure waits for the RPO's `CTO` or its timed preset (which fires now that the hold is lifted).

  In a **solo room** `ProcessReleasedGroundDepartures` waits until the aircraft is at its **departure** runway — holding short of it, or lined up on it as a runway spawn with no scripted takeoff that asked for its release or was released through the spawn gate (`IsAtItsDepartureRunway`) — and a deterministic 5–20 s readback jitter has elapsed, then auto-issues `CTO` (`AutoIssueTakeoffClearance`, terminal note "[HFR] Released — cleared for takeoff").

  A released departure that is already cleared for takeoff (its own waiting CTO fired) or airborne gets no auto-`CTO`: its `ReleasedForDeparture` is cleared instead. `HoldingShortPhase` accepts CTO as `ClearsPhase` → the normal line-up → takeoff → climb sequence runs.

  Any other released departure the controller lines up waits for the controller's own `CTO`. The jitter is FNV-1a over the callsign (`DeterministicHash`, no salt, no RNG state) so replays reproduce; the spawn jitter uses `World.Rng` (the deterministic `SerializableRandom`).

In a solo room the two paths are uniform from the controller's view: *released → airborne shortly*. In an RPO room *released* means only that the hold is lifted; the RPO (or a preset) still launches the departure.

## Release requests from untowered runway spawns

A departure from a field without an operating tower must be issued a release or a hold for release (7110.65 §4-3-4; AIM 5-2-7.a), so a runway spawn with no scripted takeoff and no `SAY` preset asks the student for one in solo training when the student works a radar position (`APP` or `CTR`), the field has no tower cab in the ARTCC config, and it flies IFR (`RunwaySpawnCall`, [solo-training-pilot-speech.md](solo-training-pilot-speech.md) "Runway spawns"). No `HFR` arming is involved:

- 5–10 s after lining up it says "{approach}, runway 25 at {airport}, ready for departure, request release." and `RunwaySpawnCall.TryRequestRelease` sets `Ground.HeldForRelease` and records a `Release` pending request. The aircraft is now a held ground departure: it shows in the rundown with the status "Lined up (held)", and `REL <callsign>`, `REL <airport>` or the Releases flyout finds it.
- `REL` releases it through the held-ground-departure path above, and `HFROFF` at an armed field does the same for every aircraft it releases; both answer the open `Release` request (`ReleaseHeldGroundDeparture` → `PilotRequestTracker.SatisfyOpenRequest`), so its 120 s follow-up stops. Lined up and released, it gets the solo-room auto-`CTO` after the 5–20 s jitter, and makes its airborne check-in after takeoff (the request is not its initial contact).
- A runway spawn released through the **spawn gate** (`Ground.ReleasedAtSpawnGate`) never asks. In a solo room, at an untowered field an IFR one starts the released departure's clock as soon as it is lined up (`SimulationEngine.ProcessRunwaySpawnAutoTakeoffs` sets `ReleasedForDeparture` and `ReleasedAtSeconds`) and departs after the jitter; at a towered field the solo towered auto-takeoff applies.
- In solo training with the same radar (APP/CTR) student, a VFR runway spawn at an untowered field asks for nothing and departs on its own (a release gates only the IFR clearance, as above), and a runway spawn at a towered field is cleared by the simulated tower. Under a TWR (or any non-radar) student none of this applies: the spawn makes the usual lined-up call.
- In an **RPO room** none of these spawns departs on its own: under an APP/CTR student position a towered or VFR runway spawn holds lined up with no lined-up call and no "[Auto]" line, a spawn released through the spawn gate holds lined up too (its release clock is never started in an RPO room), and each leaves on the RPO's `CTO`.

## Commands

All command types live in `Yaat.Sim/Commands/` (shared by client + server). They are **airport-scoped
group commands** (`isGlobal=true`, like `TAXIALL`/`SQALL`) — the airport/callsign rides in the arg.

| Verb | Canonical type | Effect |
|---|---|---|
| `HFR <airport>` | `HoldForRelease` | Arm hold-for-release; sweep current on-ground IFR departures at the field to held. |
| `HFROFF <airport>` | `DisarmHoldForRelease` | Disarm; auto-release anything still held there, answering any release request it made. A released ground departure then takes off by itself in a solo room; in an RPO room it waits for the RPO's `CTO` or its timed preset. |
| `REL <airport>` / `CTOA <airport>` | `ReleaseDeparture` | Release the next-pending at the field (then as for `HFROFF`: solo rooms launch it, RPO rooms wait for `CTO` or a preset). |
| `REL <callsign>` | `ReleaseDeparture` | Release a specific held departure. |
| `REL <airport> <minutes>` | `ReleaseDeparture` (interval) | Release the whole field's queue auto-spaced. |

Parsed by `CommandParser.ParseRelease`; the interval arg is **minutes**, stored as seconds. Routed in
`RoomEngine.SendCommandAsync` (`HandleHfrArmCmd` / `HandleHfrDisarmCmd` / `HandleReleaseCmd`), which
delegate to `HeldReleaseService` and then `BroadcastHeldDeparturesChanged`. `REL <callsign>` vs
`REL <airport>` is disambiguated inside `HeldReleaseService.Release` against the held set.

## Rundown broadcast & client

The armed-airports set + held departures are **dynamic per-room state**, broadcast like
`ArrivalGeneratorsChanged` (not folded into `SessionSettingsDto`):

- `HeldReleaseService.BuildRundown(scenario, world)` unions held runway/airborne spawns (from
  `DelayedQueue`) with held ground departures (from the world), grouped by airport and ordered so the
  first entry per field is the next-pending release.
- `TrainingBroadcastService.BuildRundown(room)` projects that into the wire `RundownDto`
  (`ArmedAirports` + `HeldDepartureDto[]`); `BroadcastHeldDeparturesChanged` sends it on
  `HeldDeparturesChanged`. Command-driven changes broadcast eagerly; `TickProcessor.BroadcastRundownIfChanged`
  re-broadcasts on a change-detected per-tick basis for mid-tick changes a command didn't drive (a held
  departure spawning, or a status transition taxiing → holding-short).
- `RoomStateDto.Rundown` seeds the rundown on join/reconnect (`MainViewModel.ApplyRoomState` → `ApplyRundown`).
- Per-aircraft, `AircraftStateDto.HeldForRelease` rides the normal delta engine — mapped in `DtoConverter.ToTrainingDto` and **added to `AircraftChangeTracker`'s `TrainingDtoFingerprint` + `CaptureTrainingDto`** (or it would appear on join but never update live). It drives `AircraftModel.IsHeldForRelease` and the "Release (HFR)" item (`coordination.release-held`, gated by `AircraftCommandApplicability.CanReleaseHeld`) under the title of the aircraft context menu on every view.
- Client: `MainViewModel` (the `HoldForRelease` partial) mirrors the rundown grouped by airport and
  exposes `ReleaseDepartureCommand` / `ReleaseNextAtAirportCommand`; the **Releases** flyout in
  `CommandInputView.axaml` renders it.

## Snapshot / replay

All hold-for-release state survives `GetSnapshot`/recording so a rewind reproduces the exact held set: `HeldDepartureAirports` and `ReleaseQueue` → `ScenarioSnapshotDto`; `DelayedSpawn.HeldForRelease` → `DelayedSpawnDto`; `Ground.HeldForRelease` / `ReleasedForDeparture` / `ReleasedAtSeconds` / `ReleasedAtSpawnGate` → `AircraftGroundOpsDto`; a runway spawn's open or answered `Release` request → `AircraftSnapshotDto.PendingPilotRequest`.

All optional-with-defaults so older snapshots deserialize. Determinism holds because the spawn jitter uses `World.Rng` and the auto-CTO and release-request delays are pure callsign hashes.

## Aviation basis

| Behavior | Reference |
|---|---|
| "Hold for release" / "Released" phraseology, release/void times | 7110.65 §4-3-4; AIM §5-2-7 |
| Successive-departure spacing (interval defaults) | §5-8-3 (radar/1 NM, diverging), §3-9-6 (wake time intervals) |
| Release → airborne delay 20–60 s; ground readback jitter 5–20 s | §3-9-5 (anticipating separation) + roll latency |
| Auto-interval spacing is controller-driven, in **minutes** | §4-3-4.c.2 ("RELEASED FOR DEPARTURE IN (number) MINUTES") |
| VFR not gated (a release gates only the IFR clearance) | §4-3-4.b.1; §4-3-9 |
| An IFR runway spawn at an untowered field asks the radar controller for its release | §4-3-4; AIM 5-2-7.a |

v1 applies a **single uniform controller-supplied interval** (default 1 min) to a whole-queue
release — it does **not** auto-derive a wake/category-aware interval. The FAA same-runway wake-time
spacing (§3-9-6.f: 2 min behind a heavy/B757, 3 min behind a super; §3-9-7: +1 min for intersection
departures) is a **tower (local controller)** duty — out of scope here, since the student is the radar
controller (see the scope boundary below). A 1-minute default is conservative versus the §5-8-3.a
radar minimum (~1 NM for diverging courses), which is appropriate for a trainer.

**v1 scope boundary:** runtime enforcement of §3-9-6 wake/successive-departure separation *at the
runway* (the tower can't roll the next until the prior cleared) is a tower-ops (M2) concern and is
**not** modeled. v1 spacing is controller-driven — manual sequencing, or the auto-interval whose
defaults are wake-informed. Void times are not modeled (they're non-towered only, §4-3-4.f).

## CFR — release-time compliance window

`CFR` is a **separate feature from HFR/REL above** — keep them distinct. HFR/REL is a hard gate (LUAW/CTO are rejected until
released); `CFR` is **alert-only and never gates anything** — it never blocks takeoff and never affects the simulation (issue #230).
It marks an on-ground IFR departure with a release-time compliance window so the student practices release-time discipline without
the sim enforcing it mechanically.

`CfrDepartureCommand(int? Hhmm, CfrAction Action)`, `CfrAction { Set, Clear, Check }`. Aircraft-scoped (`isGlobal:false`) — **no
`APREQ` alias** (APREQ is broader than a departure release).

- `CFR <HHMM>` → window `[HHMM-2min, HHMM+1min]`.
- Bare `CFR` → immediate release: assigned time = now+2min, so the window opens now (`[now, now+3min]`).
- `CFR OFF` / `CFR CANCEL` → clears the window.
- `CFR CHECK` / `CFR STATUS` → read-only status readout (opens-in / closes-in / expired) via `CfrDepartureService.DescribeStatus` — no mutation, still broadcasts a terminal line. "Check release window" under the title of the radar/ground/list aircraft menu (`coordination.check-release-window`, on the ground with a window) sends `CFR CHECK`.

**The −2/+1 window is FAA-fixed** (7110.65 §4-3-4.e.5), not a user/session preference — hardcoded constants in `Commands/CfrWindow.cs`
(`WindowBeforeSeconds=120`, `WindowAfterSeconds=60`). `CfrWindowResolver.Resolve(hhmm, nowUtc)` (pure, nearest-instant HHMM, handles
midnight rollover) computes the assigned time and brackets it uniformly.

**Wall-clock UTC, client-evaluated** — deliberately un-anchored from sim time (cosmetic, so it's fine that it's inconsistent under
pause/rewind/FF). The server (`RoomEngine.HandleCfrCmd` → `CfrDepartureService.Apply`, one `DateTime.UtcNow` read) only resolves and
stores the absolute-UTC window on `AircraftGroundOps.ReleaseWindowStartUtc/EndUtc` and broadcasts it (the same 5-point `HeldForRelease`
broadcast path, both `AircraftChangeTracker` sites). The **client** owns all alerting: `CfrAlertMonitor` (per-callsign latch) +
`CfrAlertEvaluator` fire early/late/expired-grounded via `MainViewModel.OnAircraftUpdated` (on wheels-up, capturing was-on-ground) plus
a 1s `DispatcherTimer` sweep (`SweepCfrExpiry`, for the expired-while-stationary case that stops broadcasting). Alerts surface as a
warning terminal line + amber bubble (`AddAircraftWarning`) — no datablock flash, no flyout countdown, no audio ding. A live `CFR M:SS`
countdown badge (amber, red when expired) renders in the Aircraft List Info column (`AircraftModel.UpdateCfrBadge` via shared
`CfrCountdown.Evaluate`, on-ground only).

**Excluded from the recorded action log** (`RoomEngine` negative filter next to Pause/SimRate) — the window itself survives snapshot
via `AircraftGroundOps`, so a rewind reproduces it, but replaying a recording never re-issues the `CFR` command itself.

## Footguns

- **Disarm auto-releases the field.** `HFROFF` (and the spawn gate's `Contains` check going false)
  releases everything still held there — call it out, it's intentional.
- **Arming after a clearance is already in hand doesn't retroactively yank it.** The arm sweep only
  holds pre-runway ground departures (`AtParking`/`Pushback`/`Taxiing`/`HoldingShort`); one already
  rolling is gone.
- **IFR only.** A VFR departure at an armed airport is never held — check `FlightPlan.IsVfr` before
  assuming the gate applies.
- **Two storage locations feed one rundown.** Held runway/airborne spawns live in `DelayedQueue`
  (not in the world); held ground departures are live `AircraftState`s. `BuildRundown` unions both —
  don't assume a held departure is always a live aircraft (only ground ones are).
