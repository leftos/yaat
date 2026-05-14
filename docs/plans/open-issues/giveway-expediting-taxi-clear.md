# GIVEWAY: clear IsExpeditingTaxi (latent inconsistency)

## Why this exists

`AircraftGroundOps.IsExpeditingTaxi` is a sticky flag: once `EXPEDITE`-on-the-ground bumps the taxi speed cap, the flag stays set until something clears it. The historical sites that clear it are inconsistent:

| Site | Clears `IsExpeditingTaxi`? |
|---|---|
| `TryHoldPosition` | ✅ yes |
| `TryResumeTaxi` (RES) | ✅ yes |
| `TryHoldShort` | ✅ yes |
| `TryGiveWay` | ❌ **no** |

The Plan agent that reviewed the GIVEWAY redesign flagged this asymmetry. `TryGiveWay` was preserved as-is during the structural refactor (commit `a8b514f9`) to keep the behaviour change isolated.

## What's proposed

Make `TryGiveWay` clear `IsExpeditingTaxi` to match every other hold-class command. The rationale is the same as for `TryHoldPosition`: once the aircraft is told to wait for another aircraft, it has implicitly cancelled the controller's earlier "expedite" — when it resumes, it should resume at normal taxi speed, not at the previously-set expedite multiplier.

## What works today

- The bare command flow runs cleanly — no scenario explicitly relies on the stuck-expedite behaviour.
- The Plan agent could not find a path where the asymmetry produces a visible bug, but the symmetry argument is strong: every other hold clears it.

## Risks

- A regression test or scenario somewhere may implicitly depend on `EXPEDITE; GW SWA123; RES` preserving the expedite. Search before changing.

## Implementation checklist

- [ ] Grep tests for `EXPEDITE` + `GIVEWAY` / `GW` co-occurrence. If any test depends on the stuck behaviour, decide whether to preserve or update the test.
- [ ] In `src/Yaat.Sim/Commands/GroundCommandHandler.cs:911-925` (`TryGiveWay`), add `aircraft.Ground.IsExpeditingTaxi = false;` alongside the `Hold = HoldDirective.GiveWay(target)` write.
- [ ] Add a test: aircraft expediting → `GW SWA123` → assert `IsExpeditingTaxi == false`.
- [ ] Run `pwsh tools/test-all.ps1` — no regressions expected.
- [ ] Delete this plan after merging.

## Verification

1. Build clean + tests green.
2. Manual: in `Yaat.Client`, issue `EXPEDITE` then `GW SWA123` on a taxiing aircraft. After RES (or auto-release), the aircraft should resume at normal taxi speed, not the expedited multiplier. Compare with a fresh `EXPEDITE` followed by `HOLDPOSITION` then `RES` — they should behave identically.
