# ATPA volume TCP exclusion does nothing

## Problem

`AtpaProcessor.IsExcludedByTcp` (`src/Yaat.Sim/Data/Vnas/AtpaProcessor.cs`) **always returns `false`**, so a
controller's ATPA-volume TCP exclusions are silently ignored — every aircraft in the volume is considered for
in-trail pairing regardless of the volume's `ExcludedTcpIds`.

## Root cause

`AtpaVolumeConfig.ExcludedTcpIds` holds **ULIDs**, but `AircraftState.Track.Owner` (`TrackOwner`) carries only
`Subset` + `SectorId` — not the ULID stored on `Tcp.Id`. There is no way to match an owner against the excluded
ULID list, so the method short-circuits to `false`. The in-code comment documents this as a deliberate
conservative ("show more alerts") fallback, not an oversight.

## Fix (required work)

1. Propagate the ULID (`Tcp.Id`) onto `TrackOwner` wherever ownership is assigned (this is a track-ownership
   **data-model change** — touches `TrackOwner`, `TrackEngine`/`TrackResolver`, and the CRC/snapshot DTOs that
   carry owner identity).
2. Match `volume.ExcludedTcpIds` against the owner's ULID in `IsExcludedByTcp`.
3. TDD: add a test that an aircraft owned by an excluded TCP is dropped from the volume's pairing set.
4. Aviation-realism review (`aviation-sim-expert`) — ATPA behavior change. Reference the local FAA 7110.65 / AIM
   files at `.claude/reference/faa/` (do not web-search).

## Cross-repo note

`TrackOwner` and the snapshot/CRC DTOs span both repos; verify with `pwsh tools/test-all.ps1`.

> Surfaced by the doc-coverage gap-analysis workflow (see `docs/plans/doc-coverage-opportunities.md`,
> "Conflict, Alert & Visual-Acquisition Detection"). Behavior left conservative until this is scheduled.
