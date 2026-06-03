# Issue #173 — Warn before "Take Control" of a recording

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** enhancement, scenarios. **Source:** Discord thread, filed 2026-06-03.

## Symptom

"Take Control" while viewing a replay is a destructive action — it ends playback, transitions to
live control, and discards the playback context — yet it fires with no confirmation. Add a warning
prompt before proceeding.

## Root cause (confirmed)

`MainViewModel.TakeControl()` (`src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs:65`) calls
`_connection.TakeControlAsync()` and unconditionally flips `IsPlaybackMode=false` /
`PlaybackTapeEnd=0`. Replay state is already exposed via `IsPlaybackMode`
(`MainViewModel.cs:229`), so the gate condition is in hand.

## Key files

- `src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs:65` — `TakeControl()` (the destructive path).
- `src/Yaat.Client/ViewModels/MainViewModel.cs:229` — `IsPlaybackMode` (gate).
- `src/Yaat.Client/Views/MainWindow.axaml.cs` (`OnClosing`, ~line 2800) — existing confirmation
  pattern to reuse: a plain `Window` + `await dialog.ShowDialog(this)` returning a confirmed flag.

## Approach

In `TakeControl()`, when `IsPlaybackMode` is true, show a confirmation dialog before calling
`TakeControlAsync()`; bail on cancel. Reuse the `Window` + `ShowDialog` pattern from the close
handler — **do not** add a new dialog framework. Scope the warning to the **timeline/playback**
Take-Control only; the context-menu "Take control" RPO path (`MainViewModel.Rooms.cs`) assigns
aircraft directly and is not the replay-destructive action.

## Verification

- Manual: load a recording, click Take Control → warning appears; Cancel preserves playback; Confirm
  proceeds as before.
- Add a small UI-logic test if a testable seam is reachable (the gate condition at minimum).

## Open questions

- Confirm the warning copy and button labels (e.g. "Taking control ends recording playback and
  cannot be undone." / [Cancel] [Take Control]).
