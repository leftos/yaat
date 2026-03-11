# Issue #49: App Cannot Be Closed While a Scenario Is Loaded

## Root Cause

`MainWindow.OnClosing` (line 1264) checks `vm.HasScenario`, cancels the close, and shows a
confirmation dialog. When the user clicks "Exit", it calls `Close()` — which re-enters
`OnClosing`. Because `vm.HasScenario` is still true, `e.Cancel = true` is set again and a
second dialog is spawned. This loops indefinitely — the window never closes.

## Fix

`src/Yaat.Client/Views/MainWindow.axaml.cs` only.

Add `private bool _isConfirmedClose;` field. Set it to `true` before the recursive `Close()`
call. Guard the dialog block with `!_isConfirmedClose` so the second pass through `OnClosing`
skips the guard and the window closes.

## Implementation Status

Fixed. Build: 0W 0E.
