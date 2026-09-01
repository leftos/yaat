# 09 — Live-Room Attach Mode (dev-only)

Part of [Controller AI + Soak Harness](README.md). Enabling AI positions in a live dev room so the
user can watch the AI control a session in the client — primarily for debugging the AI itself.

## Gating (defense in depth — never on public servers)

- Config key **`Yaat:ControllerAi:Enabled`**, default false. When false, the AI service and
  `RoomSoakMonitor` registrations are simply **not added to DI** — there is no hub method that can
  enable the feature remotely when the flag is off; the gate is process-level, not request-level.
- Startup **refuses the flag when `ASPNETCORE_ENVIRONMENT == Production`** (log + ignore).
- No deployed env file ever sets it; `appsettings.Local.json` (highest precedence) is where a dev
  turns it on locally.

## Enabling in a room

- Mentor/instructor-only room command (working name `AIPOS <position> ON|OFF`; exact verb owned by
  the core plan and registered like any command) routed through `RoomEngine`, mutating
  `ControllerAiConfig.EnabledPositionIds` as a recorded setting change.
- When the first position turns on, `RoomSoakMonitor.Attach` hooks the shared detector set into the
  room's tick. Detectors are O(aircraft) per sim-second and fit the 800 ms budget — the solo
  evaluator already runs in the same slot.
- A human connecting to an AI position's TCP suspends that brain; disconnecting resumes it with a
  `Reset()` ([02](02-positions-and-handoffs.md) partial staffing).

## Surfacing findings

`SoakFindingBroadcastSink`: findings → `TerminalBroadcast` warning lines (amber in the client
terminal) + structured server log. The runner and attach mode share `RoomSoakMonitor` and all
detectors — only the sink differs (files vs broadcast).

## Recording

Nothing new: the room's normal `RecordingManager` path applies, AI commands are recorded like human
ones ([01](01-architecture.md) dispatch contract), and the user saves via the existing recording UI.
Appending soak findings as bookmarks on export is a nice-to-have follow-up.
