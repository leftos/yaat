# Deferred: STARS keyboard `<TRK RPOS>` (Track Reposition, F2)

## Context

Came out of an audit of CRC STARS **keyboard** (no-slew, typed-FLID) command paths, prompted by
the keyboard handoff-accept bug (`<HND OFF><ENTER>` returned "TRACK NOT FOUND" because the server
resolved the track only from the clicked track id). The audit found and fixed the same class of bug
for several commands; this file records the one item that was deferred and the items that turned out
to be non-issues.

## Implemented (server-side, shipped)

All resolve the track by typed FLID when CRC sends no clicked track (`ResolveKeyboardFlid` +
per-command handlers in `CrcClientState.Stars.cs`):

- `<HND OFF><ENTER>` / `<HND OFF><FLID>` / `<HND OFF>(ID) <FLID>` — accept / recall / initiate handoff.
- `<TERM CNTL><FLID>` — drop track; `<TERM CNTL>ALL` — drop all owned tracks.
- `<CA>K <FLID>` — toggle CA inhibit.
- `<MULTI FUNC>Y…` — scratchpad 1/2 (set/clear), pilot-reported altitude.
- `<MULTI FUNC>M…` — Mode-C inhibit toggle, scratchpad 1/2, temporary altitude (Δ), filed/cruise
  altitude, assigned beacon code. Both keyboard-FLID and slew forms.

## Not implementable server-side (CRC handles locally — no-op for us)

Confirmed in the decompiled CRC `InputManager.ProcessMultiFuncCommand` / `ProcessMinimumSeparationCommand`:
these never send a `ProcessStarsCommand` to the server, so there is nothing to implement.

- `<MULTI FUNC>D<FLID>` — display flight-plan digest in the preview area (`ProcessMultiFuncD`, local readout).
- Force quicklook `<MULTI FUNC>Q…` / `**…` — mutates `CurrentPrefSet.QuickLookedTcps`, a client display pref.
- Minimum separation (`<MIN>`) — `mTrackManager.SetMinimumSeparationTracks`, client-only.

## Deferred: `<TRK RPOS>` (F2) — datablock correlation

Reaches the server (`SendCommandToServer` → `StarsCommandType.TrackReposition`) but currently falls to
`CrcUnhandledCommand` (no-op). It is **not** a keyboard-FLID variant of an existing command — it is a
standalone datablock-correlation feature requiring 1–2 clicked items (`CurrentCommandRequiresTwoClickedItems`
returns true when the param is empty). stars.md Table 22:

| Form | Meaning |
|------|---------|
| `<TRK RPOS><FLID><SLEW>` | Move data block from an uncorrelated (coasting/unsupported) track to an unassociated track (AIDs must match). |
| `<TRK RPOS><SLEW><LOCATION>` | Move data block from an associated track to a map location; original becomes unassociated, moved block becomes unsupported. |
| `<TRK RPOS><SLEW><SLEW>` | Move data block from an unsupported block to an unassociated track; track becomes associated, unsupported block removed. |

Building it needs a correlation model layered over the existing `AircraftGhostTrack` fields
(`IsUnsupported`, `IsOverlay`, `Latitude`/`Longitude`) and the two-clicked-item `ProcessStarsCommand`
path (which the server does not parse today — it only reads `ClickedItem1`). The closest existing
infrastructure is `TrackCommandHandler.HandleGhostTrack` (overlay ghost on an FP'd aircraft) and
`CrcVisibilityTracker`'s airborne auto-merge. No reuse exists for the pure reposition gesture.

Deferred by user decision — revisit if a training scenario needs it.

## Known acceptable omission: pilot-reported-altitude precondition

stars.md (lines 1171–1175) says pilot-reported altitude (`Y###` / the implied `###`-slew path) requires
"Must not be receiving Mode-C altitude or Mode-C altitude must be suppressed." Neither the new
`CrcMultiFuncY` path nor the pre-existing implied-slew PRA path enforces this. Left unenforced by
conscious decision (per aviation-sim-expert review): `PilotReportedAltitude` is a pure display field
with no downstream coupling, and the guard is a STARS input nicety, not a separation/clearance rule —
an unguarded entry produces at most a cosmetic oddity. If full fidelity is wanted later, gate on
`ac.Stars.IsModeCInhibited || ac.Transponder.Mode != "C"` and return `"FORMAT"` otherwise.
