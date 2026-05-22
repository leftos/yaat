# Session persistence across planned restarts

YAAT can preserve active training rooms across a **planned** server process restart. Crash recovery and background checkpointing are not supported — only the admin-driven prepare/shutdown flow.

## Operator flow

1. Authenticate as admin on the training hub (`AdminAuthenticate`).
2. Call `AdminPrepareRestart(drainSeconds)` — broadcasts `ServerRestarting` to all lobby clients, pauses every loaded scenario, waits for the drain window, then writes one ZIP checkpoint per room with an active scenario under `%LOCALAPPDATA%/yaat/session-checkpoints/` (override via `Yaat:SessionCheckpointPath`).
3. Wait for `ServerRestartReady` on clients (or server log: "Prepared restart").
4. `POST /shutdown` (or stop the process). Without a prior prepare, `/shutdown` returns 400 unless `?force=true`.
5. Start the server. `SessionRestoreHostedService` reloads checkpoints (default max age 24h, `Yaat:SessionCheckpointMaxAgeHours`), recreates rooms with the **same `RoomId`**, and broadcasts `ServerRestartComplete`.
6. Clients reconnect (SignalR auto-reconnect), call `FindRoomForMyCid` / `JoinRoom`, and receive full `RoomStateDto` plus strip initial state.

## Checkpoint contents

Each `{roomId}.checkpoint.zip` contains:

| Entry | Purpose |
|-------|---------|
| `manifest.json` | Room id, creator, members (CID), elapsed time, schema version |
| `scenario.json.br` | Original scenario JSON |
| `actions.json.br` | Full `ActionLog` (rewind/export) |
| `snapshot-final.json.br` | Live `StateSnapshotDto` at save time |
| `room-state.json.br` | Strips, ASDEX, ERAM prefs, line numbers, assignments by CID |
| `weather.json` / `artcc-config.json.br` | Optional bundled weather and ARTCC config |

Restore applies the final snapshot directly (no replay-from-zero). Coordination channel in-flight items are included in the scenario snapshot DTO.

## Client behavior

- `ServerRestarting` — banner, commands disabled, `ActiveRoomId` persisted to preferences.
- Transport drop during restart does **not** clear room state on the client.
- After reconnect: `FindRoomForMyCid` then `JoinRoom`; `RoomAvailableForCid` handles late tabs.

## Configuration (`appsettings` / `Yaat` section)

| Key | Default | Meaning |
|-----|---------|---------|
| `SessionCheckpointPath` | `%LOCALAPPDATA%/yaat/session-checkpoints` | Checkpoint directory |
| `SessionCheckpointMaxAgeHours` | `24` | Skip stale checkpoints on restore |
| `PrepareRestartDrainSeconds` | `30` | Default drain when hub arg is `0` |
| `SessionCheckpointArchiveKeepCount` | `3` | Retained `session-checkpoints-restored-*` dirs after restore |

## CRC

CRC clients reconnect via JWT/CID like a normal reconnect. There is no separate CRC session blob; tracks rebroadcast on the next tick after clients rejoin.

## Droplet deploy (`deploy-to-droplet.ps1`)

Production uses a named Docker volume (`yaat-session-checkpoints` → `/data/session-checkpoints`) so checkpoints survive `docker compose up --force-recreate`.

Default deploy flow (from the yaat repo):

1. `POST https://yaat1.leftos.dev/admin/prepare-restart?drainSeconds=30` with header `X-Yaat-Admin-Password` (from repo-root `.env` as `ADMIN_PASSWORD`, same value as the droplet’s `ADMIN_PASSWORD` in `yaat-server/.env`).
2. Wait for drain + checkpoint write (the HTTP call blocks until done).
3. `git pull` on the droplet, `docker compose build`, `docker compose up -d --force-recreate`.
4. New container starts; `SessionRestoreHostedService` reloads checkpoints from the volume.

Flags:

- `-SkipSessionSave` — old behavior (no prepare; active rooms lost).
- `-DrainSeconds <n>` — override default 30s drain (default matches `Yaat:PrepareRestartDrainSeconds`).

Requires `ADMIN_PASSWORD` in the yaat repo `.env` for session save. If missing or the server is unreachable, deploy continues with a warning.

## Deploy notes

Restore checkpoints only when the **same** yaat + yaat-server build (snapshot schema) wrote them. Cross-version restore may fail migration or produce drift.
