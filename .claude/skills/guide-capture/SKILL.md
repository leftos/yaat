---
name: guide-capture
description: Use when adding, changing or debugging a tools/Yaat.GuideCapture screenshot scene (a USER_GUIDE.md image, a release feature-showcase shot under docs/releases/img, a whats-new-* scene), when a capture shows the wrong state, an empty popup, a menu that never opened or no aircraft, or when a capture run is slow to iterate on.
---

# GuideCapture scenes

The facts (CLI, scene contract, determinism, `ADD` forms, menu builders) are in `docs/guide-capture.md`. Read it before writing or changing a scene. This skill is the decisions.

## Decide before writing the scene

| Question | Choose |
|---|---|
| Where does the traffic come from? | `ADD` through `SceneActions.SpawnAsync(vm, "ADD …", timeout)` (returns the aircraft), placed where the shot needs it. Never advance hundreds of seconds of a loaded scenario hoping traffic arrives |
| How much sim time? | Only the seconds the state needs (a landing roll, a taxi a few hundred feet), via `RoomTicks.AdvanceUntilAsync` with a `RoomTicks.Stage` to run until a state, `RoomTicks.AdvancePausedAsync` for a fixed number of seconds |
| How does a context menu open? | The view's `BuildAircraftRightClickMenu` / `BuildNodeContextMenu`, opened at the target's screen point. Never a simulated pointer press on `MainWindow` |
| What state must the aircraft be in, and how is it reached? | Decided from the code before the first run, by `docs/guide-capture.md` "Staging an aircraft for a menu entry": the entry's situation, what the spawn files, what not to send, where it sits on the scope. Use a listed recipe when one fits. The scene prints and asserts `aircraft.Situation` before opening the menu |
| Who stages it, when the shot is briefed to an implementer? | The orchestrator: the brief gives the exact commands, the state each must reach and the check that proves it, from a recipe or from the code. A brief that says "use an ADD form that…" or "if X fails, try Y" is not ready; the implementer reports a failed check instead of trying other staging |
| Which output folder? | User guide: default `--out`. Release showcase: scene `whats-new-<topic>`, no `--out` needed (`Scene.DefaultOutDir`) |
| What does the scene change? | Everything it changes (preferences, view settings, prompts) is put back in `AfterCapture` |

## Iterating

- Every run re-pays the server boot and the scenario load: change one thing per run, and debug the scene's state with log lines or assertions in the scene, not by eye across many runs.
- A run that shows the wrong state is a staging error: go back to the state the entry needs (the doc's five steps), never try another position, runway or command to see what sticks. Run captures one at a time; two GuideCapture builds at once collide on file locks.
- The active-runways prompt covers the views after a scenario load in an RPO room: handle it first when a shot comes out with a dialog over it.
- Done means you opened the PNG and it shows the state the scene names. Exit code 0 is not evidence.

## When the tool is the obstacle

A scene that needs a helper GuideCapture lacks (an empty scenario, a popup host, a way to keep one booted room across scenes) gets the helper in `Capture/SceneActions.cs` or a scene base class, and a line in `docs/guide-capture.md`; it is not worked around inside one scene.
