# Issue #177 — Helicopter air-taxi / relocation issues

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** bug, local-cmds. **Source:** Discord thread, filed 2026-06-03.
> **Bundle:** `S1-OAK-6 (1) | Misc - No SID/Heli` (224 s, ZOA). Aircraft: **N436MS** (EC35),
> **N101H** (R22). Install with `python tools/bug_bundle.py install --issue 177 --desc oak-heli`.
> **Decision:** airborne commands (`FH`, `CM`/`DM`, turns, `SPD`, `DCT`) **clear the AirTaxi phase**
> and hand control back to the command queue.

## Symptom

Various helicopter air-taxi / relocation problems. Explicit, actionable request: *an airborne
helicopter mid-`ATXI` should be pullable out of the air-taxi behavior with an airborne-appropriate
command such as `FH`.* Commands exercised in the recording: `CTOPP`, `LAND @RON1`/`@RON2`,
`ATXI @RON2`, `HPP`, `HOLD`, plus `FH`/`CM`. Phases seen: `HelicopterTakeoff`, `VfrHold`, `AirTaxi`,
`HelicopterLanding`, `AtParking`.

## Root cause

**Primary (confirmed, actionable):** `AirTaxiPhase.CanAcceptCommand`
(`src/Yaat.Sim/Phases/Ground/AirTaxiPhase.cs:148`) accepts only `AirTaxi` / `Land` / `Delete`
(`ClearsPhase`) and `HoldPosition` / `Resume` (`Allowed`), and **rejects everything else** with
"helicopter is air-taxiing; only HOLD/RES, a new ATXI/LAND, or DEL apply". So an airborne heli
mid-ATXI cannot be redirected with `FH` / `CM` / `DM` / `SPD` / `DCT`.

**Secondary (needs replay/visual confirmation):** the plural "various … relocation issues" implies
more than the break-out. Inspect `ATXI @RON` / `LAND @RON` destination resolution
(`GroundCommandHandler.TryAirTaxi` / `TryResolveAirTaxiDestination`): does it resolve named
helipads / RON spots correctly vs parking, and preserve the `@` name for display? Decode `CTOPP` and
`HPP` canonical types and confirm the `AirTaxi` → `HelicopterLanding` → `AtParking` chain behaves on
relocation.

## Key files

- `src/Yaat.Sim/Phases/Ground/AirTaxiPhase.cs:148` — `CanAcceptCommand` (the primary fix site).
- `src/Yaat.Sim/Commands/GroundCommandHandler.cs` — `TryAirTaxi`, `TryResolveAirTaxiDestination`,
  `TryLand` (heli destination resolution / `@RON` naming).
- `src/Yaat.Sim/Phases/Ground/` — `HelicopterTakeoff`, `HelicopterLanding`, `VfrHold` phases (cross-
  check their acceptance lists for consistency). See `docs/phases.md`.
- `src/Yaat.Sim/Commands/CommandRegistry.cs` / `CanonicalCommandType` — confirm the exact airborne
  command set.

## Approach

Make airborne command types (`FlyHeading`, `ClimbMaintain`, `DescendMaintain`, turns, `Speed`,
`DirectTo`, …) return **`ClearsPhase`** from `AirTaxiPhase.CanAcceptCommand`, so the heli drops out
of air-taxi and the normal command queue flies the new clearance. Confirm the precise set against
`CanonicalCommandType` and align with `HelicopterTakeoff` / `VfrHold` acceptance for consistency.
Then scope the secondary relocation defects from the recording before fixing.

**Mandatory `aviation-sim-expert` review** — helicopter air-taxi altitude/speed (7110.65 §3-11) and
redirect handling (cite the local FAA refs; do not web-search).

## Verification

- Unit test: `AirTaxiPhase.CanAcceptCommand(FlyHeading)` (and CM/DM/SPD/DCT) returns `ClearsPhase`.
- Replay-driven test: an airborne heli given `FH` mid-ATXI leaves the phase and flies the heading.
- `pwsh tools/test-all.ps1`.

## Open questions

- Decode `CTOPP` and `HPP` semantics and confirm they are correct for the recorded behavior.
- Precisely scope the secondary `ATXI`/`LAND @RON` relocation bugs from the recording before
  committing to fixes (what visibly went wrong, per aircraft/time).
