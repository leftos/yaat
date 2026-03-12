# Systemic review: optional parameters

## Context

Optional parameters (`= null`, `= false`, etc.) caused a silent bug in `DepartureClearanceHandler`: `InsertTowerPhasesAfterCurrent` didn't pass `IProcedureLookup` to `ResolveDepartureRoute` because the parameter had a `= null` default. The compiler was happy, CIFP SID resolution silently failed for CTO-from-hold-short, and aircraft skipped runway transition waypoints.

## Goal

Audit all optional parameters across Yaat.Sim and Yaat.Client. For each, decide:
1. **Make required** — if callers should always pass the value explicitly
2. **Keep optional** — only if the default is genuinely correct in all cases (e.g., cancellation tokens)
3. **Remove** — if the parameter is unused or redundant

## Checklist

- [ ] Scan `src/Yaat.Sim/` for `= null` and `= false` and `= true` and `= 0` in method signatures
- [ ] Scan `src/Yaat.Client/` for the same
- [ ] For each, check all call sites — are any relying on the default when they shouldn't be?
- [ ] Make parameters required where the default hides missing wiring
- [ ] Fix all call sites
- [ ] Build + test

## Priority

Medium — the critical instance (IProcedureLookup) is already fixed. The rest are latent risks.
