---
name: guide-capture
description: Use when adding, changing or debugging a tools/Yaat.GuideCapture screenshot scene (a USER_GUIDE.md image, a release feature-showcase shot under docs/releases/img, a whats-new-* scene), when a capture shows the wrong state, an empty popup, a menu that never opened or no aircraft, or when a capture run is slow to iterate on.
---

# GuideCapture scenes

The facts (CLI, scene contract, determinism, `ADD` forms, menu builders) are in `docs/guide-capture.md`. Read it before writing or changing a scene. This skill is the decisions.

## Decide before writing the scene

| Question | Choose |
|---|---|
| Where does the traffic come from? | `ADD` through `SceneActions.SendCommandAsync`, placed where the shot needs it. Never advance hundreds of seconds of a loaded scenario hoping traffic arrives |
| How much sim time? | Only the seconds the state needs (a landing roll, a taxi a few hundred feet), via `RoomTicks.AdvancePausedAsync` |
| How does a context menu open? | The view's `BuildAircraftRightClickMenu` / `BuildNodeContextMenu`, opened at the target's screen point. Never a simulated pointer press on `MainWindow` |
| Which output folder? | User guide: default `--out`. Release showcase: scene `whats-new-<topic>`, `--out docs/releases/img` |
| What does the scene change? | Everything it changes (preferences, view settings, prompts) is put back in `AfterCapture` |

## Iterating

- Every run re-pays the server boot and the scenario load: change one thing per run, and debug the scene's state with log lines or assertions in the scene, not by eye across many runs.
- The active-runways prompt covers the views after a scenario load in an RPO room: handle it first when a shot comes out with a dialog over it.
- Done means you opened the PNG and it shows the state the scene names. Exit code 0 is not evidence.

## When the tool is the obstacle

A scene that needs a helper GuideCapture lacks (an empty scenario, a popup host, a way to keep one booted room across scenes) gets the helper in `Capture/SceneActions.cs` or a scene base class, and a line in `docs/guide-capture.md`; it is not worked around inside one scene.
