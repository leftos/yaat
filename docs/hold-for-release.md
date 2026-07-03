# Hold for Release (HFR / REL)

**Read before touching** `HeldReleaseService`, `DepartureSpawnClassifier`, the hold-for-release gates
in `CommandDispatcher` / `TaxiingPhase` / `SimulationEngine`, the `RundownDto` broadcast, or any
`HeldForRelease` / `ReleasedForDeparture` field. Companion to
[`scenario-loading-and-generation.md`](scenario-loading-and-generation.md) (spawn pipelines),
[`phases.md`](phases.md) (HoldingShortPhase), [`command-pipeline.md`](command-pipeline.md) (group-command
routing), and [`training-hub-contract.md`](training-hub-contract.md) (the broadcast).

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
| `AircraftGroundOps.ReleasedForDeparture : bool` + `ReleasedAtSeconds : double` | `Yaat.Sim/AircraftGroundOps.cs` | Set by REL on a ground departure: authorized, awaiting the auto-issued takeoff clearance once it's holding short. `ReleasedAtSeconds` anchors the readback jitter. |
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

1. **Command issuance** — `CommandDispatcher.TryApplyTowerCommand` rejects CTO/CTOPP/LUAW with
   `"{cs} is held for release at {dep} — REL {cs} first"`. Covers manual commands and scenario presets
   (both flow through the same dispatch path). CROSS is left alone — a departure holding short of its
   *own* departure runway isn't crossing it.
2. **Stored-clearance consume** — `TaxiingPhase.ApplyDepartureClearanceIfPending` skips applying a
   stored departure clearance while `Ground.HeldForRelease`, catching the one path that isn't a fresh
   command issuance (a clearance issued before the airport was armed).

**Why `HoldingShortPhase` and not LUAW:** a departure with no LUAW/CTO clearance already sits in the
existing `HoldingShortPhase` indefinitely (it is gated on a `RunwayCrossing` requirement satisfied
only by CROSS/LUAW/CTO). `HoldingInPositionPhase` / `LinedUpAndWaitingPhase` are the *on-runway*
states. So withholding runway-entry clearance keeps a held departure off the runway with **no new
hold phase**. There is no AI/solo auto-CTO to suppress — those two reads are the whole gate.

## Release flow

`HeldReleaseService.Release(scenario, world, rng, target, intervalSeconds?)` is the entry point
(called from `RoomEngine` and from `ProcessReleaseQueue`):

- **`target` is a callsign** → release that specific held departure.
- **`target` is an airport, no interval** → release the next-pending there (rundown order).
- **`target` is an airport + interval** → enqueue one `ScheduledRelease` per held entry, spaced
  `interval` seconds apart; `SimulationEngine.ProcessReleaseQueue` fires each in order.

`ReleaseOne` acts by spawn state:

- **Held runway/airborne spawn** — clear `DelayedSpawn.HeldForRelease`, set
  `SpawnAtSeconds = Elapsed + Rng(20..60)` so `ProcessDelayedSpawns` spawns it shortly after (it
  appears climbing/rolling — never on the release tick).
- **Held ground departure** — clear `Ground.HeldForRelease`, set `ReleasedForDeparture = true` and
  `ReleasedAtSeconds`. `SimulationEngine.ProcessReleasedGroundDepartures` then waits until the aircraft
  is holding short of its **departure** runway and a deterministic 5–20 s readback jitter has elapsed,
  then auto-issues `CTO` (`AutoIssueTakeoffClearance`). `HoldingShortPhase` accepts CTO as `ClearsPhase`
  → the normal line-up → takeoff → climb sequence runs. The jitter is FNV-1a over the callsign (no RNG
  state) so replays reproduce; the spawn jitter uses `World.Rng` (the deterministic `SerializableRandom`).

The two paths are uniform from the controller's view: *released → airborne shortly*.

## Commands

All command types live in `Yaat.Sim/Commands/` (shared by client + server). They are **airport-scoped
group commands** (`isGlobal=true`, like `TAXIALL`/`SQALL`) — the airport/callsign rides in the arg.

| Verb | Canonical type | Effect |
|---|---|---|
| `HFR <airport>` | `HoldForRelease` | Arm hold-for-release; sweep current on-ground IFR departures at the field to held. |
| `HFROFF <airport>` | `DisarmHoldForRelease` | Disarm; auto-release anything still held there. |
| `REL <airport>` / `CTOA <airport>` | `ReleaseDeparture` | Release the next-pending at the field. |
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
- Per-aircraft, `AircraftStateDto.HeldForRelease` rides the normal delta engine — mapped in
  `DtoConverter.ToTrainingDto` and **added to `AircraftChangeTracker`'s `TrainingDtoFingerprint` +
  `CaptureTrainingDto`** (or it would appear on join but never update live). It drives the radar
  context-menu "Release (HFR)" item and `AircraftModel.IsHeldForRelease`.
- Client: `MainViewModel` (the `HoldForRelease` partial) mirrors the rundown grouped by airport and
  exposes `ReleaseDepartureCommand` / `ReleaseNextAtAirportCommand`; the **Releases** flyout in
  `CommandInputView.axaml` renders it.

## Snapshot / replay

All hold-for-release state survives `GetSnapshot`/recording so a rewind reproduces the exact held set:
`HeldDepartureAirports` and `ReleaseQueue` → `ScenarioSnapshotDto`; `DelayedSpawn.HeldForRelease` →
`DelayedSpawnDto`; `Ground.HeldForRelease` / `ReleasedForDeparture` / `ReleasedAtSeconds` →
`AircraftGroundOpsDto`. All optional-with-defaults so older snapshots deserialize. Determinism holds
because the spawn jitter uses `World.Rng` and the auto-CTO jitter is a pure callsign hash.

## Aviation basis

| Behavior | Reference |
|---|---|
| "Hold for release" / "Released" phraseology, release/void times | 7110.65 §4-3-4; AIM §5-2-7 |
| Successive-departure spacing (interval defaults) | §5-8-3 (radar/1 NM, diverging), §3-9-6 (wake time intervals) |
| Release → airborne delay 20–60 s; ground readback jitter 5–20 s | §3-9-5 (anticipating separation) + roll latency |
| Auto-interval spacing is controller-driven, in **minutes** | §4-3-4.c.2 ("RELEASED FOR DEPARTURE IN (number) MINUTES") |
| VFR not gated (a release gates only the IFR clearance) | §4-3-4.b.1; §4-3-9 |

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
- `CFR CHECK` / `CFR STATUS` → read-only status readout (opens-in / closes-in / expired) via `CfrDepartureService.DescribeStatus` —
  no mutation, still broadcasts a terminal line. Right-click "Check release window" in radar/ground/list menus sends `CFR CHECK`.

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
