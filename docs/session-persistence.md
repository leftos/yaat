# Session persistence across planned restarts

YAAT can preserve active training rooms across a **planned** server process restart. Crash recovery and background checkpointing are not supported — only the admin-driven prepare/shutdown flow.

## Operator flow

1. Authenticate as admin on the training hub (`AdminAuthenticate`).
2. Call `AdminPrepareRestart(drainSeconds)` — broadcasts `ServerRestarting` to all lobby clients, pauses every loaded scenario, waits for the drain window, then writes one ZIP checkpoint per room with an active scenario into `<SessionCheckpointPath>/current/` (default root: `%LOCALAPPDATA%/yaat/session-checkpoints/`). The staging and backup directories of the swap are siblings of `current/` inside the same root.

   On Windows a swap folder cannot be renamed while another process (an antivirus scan of a just-written ZIP) holds a file inside it, so each of the swap's renames, and the recovery's, retries up to 8 times over about 0.7 s before the prepare-restart fails.
3. Wait for `ServerRestartReady` on clients (or server log: "Prepared restart").
4. `POST /shutdown` with header `X-Yaat-Admin-Password: <password>` (or stop the process). Without a prior prepare, `/shutdown` returns 400 unless `?force=true`. Without the password header, `/shutdown` returns 401.
5. Start the server. `SessionRestoreHostedService` reloads checkpoints (default max age 24h, `Yaat:SessionCheckpointMaxAgeHours`), recreates rooms with the **same `RoomId`**, and broadcasts `ServerRestartComplete`.
6. Clients reconnect (SignalR auto-reconnect), call `FindRoomForMyCid` / `JoinRoom`, and receive full `RoomStateDto` plus strip initial state.

## Checkpoint contents

Each `{roomId}.checkpoint.zip` contains:

| Entry | Purpose |
|-------|---------|
| `manifest.json` | Room id, creator, members (CID), elapsed time, schema version (4), `SessionStartUtc` (the session clock's anchor; a checkpoint written before it was captured restores with `SavedAtUtc.Date` — a midnight-anchored clock, so PDC/strip times on that one restored room read from midnight until it reloads), `CarriedActiveRunways` (the room's active-runways answer for the scenario: airport → token list, an empty list for `NONE`, null or absent when not answered) and `InitialActiveRunways` (what the session started on, same shape; absent in an older checkpoint, where the restore works the start value out again), `InitialWeatherJson` (the weather the session's tape started on, which the restored room's rewinds, restarts and exports start from rather than the room's weather at save time; absent when the tape started on none and in an older checkpoint), and the layout fields: `LayoutAirportIds` and `AirportGeoJsonIds` (the layout's own `AirportId` spelling, which the entry names are built from; null when none), `LayoutFormatVersion`, and `MissingLayoutAirportIds` (the FAA codes the room had pinned with no map; null when none), and `WindGridHashes` (the GFS wind grids stored under `wind-grids/`; null when the room pinned none, absent before version 4) |
| `scenario.json.br` | Original scenario JSON |
| `actions.json.br` | Full `ActionLog` (rewind/export) |
| `terminal-log.json.br` | `TerminalLog` (omitted when empty) |
| `bookmarks.json` | Shared timeline bookmarks, in the same `RecordingBookmarks` payload a recording export writes (omitted when the room has none). Ids are restored verbatim; the id counter is not archived, so the restore resumes it one past the highest restored id |
| `snapshot-final.json.br` | Live `StateSnapshotDto` at save time — including the strips and the vTDLS session (`ServerSnapshotDto.Strips` / `.Tdls`) and the ASDE-X safety-logic configuration and standing alerts (`ScenarioSnapshotDto.AsdexSafetyLogicConfig` / `.ActiveAsdexAlerts`) |
| `room-state.json.br` | ASDEX / SAID surface temp data, presets and seeded-facility markers, ERAM prefs, line numbers, assignments by CID, the room's position display config (`RoomStateSnapshotDto.PositionDisplayConfig`; a checkpoint written before its `ActivePosition` member reads it as null until the next scenario load), and the room's identity ledger (`RoomStateSnapshotDto.Identities`: every CID the room ever held with each set of initials and each name seen, departed people included, in first-seen order, restored by `RoomIdentityLedger.Restore` so a bundle exported after the restart keeps the same pseudonyms) (`RoomStateSnapshotDto`; strips, TDLS, the ASDE-X safety-logic configuration and the standing ASDE-X alerts are in the Sim snapshot instead) |
| `weather.json` / `artcc-configs.json.br` | Optional bundled weather, and every ARTCC config the room had pinned (its own and its neighbours', by ARTCC id; version 2 on). A version 1 checkpoint's `artcc-config.json.br` (the scenario's own ARTCC only) still restores, its neighbours filled from the live caches. Restore pins these configs before the reload, so the room keeps the configs it was running with; a checkpoint that saved no config and no layout restores on the live caches and pins those. See [server-rooms-and-hub.md](server-rooms-and-hub.md#the-resource-pin) |
| `layouts/{id}.json.br` / `airport-geojson/{id}.geojson.br` | Every airport layout the room had pinned, referenced by an aircraft or not, and the source GeoJSON of each that has one (version 3), under the entry names and serializer a recording archive uses (`RecordingLayoutBundler.WritePinnedLayouts`). A room with no resource pin saves none. A version 2 checkpoint saved no layouts: it restores on the live layouts, with an Information line saying so |
| `wind-grids/{hash}.grib2.br` | Every GFS wind grid the room's weather and tape name (`TrainingRoom.CommittedWindGridHashes`: the pinned grids without a pending, uncommitted upload): the raw GRIB2, Brotli-compressed, under the recording archive's entry name (version 4). The weather text's `windGridHash` then resolves after the restart emptied the process-wide `WindGridRegistry`. A pinned hash the registry no longer holds is left out with a warning. A restore registers at most the first 17 distinct grids and skips the rest, and any entry past 512 KB decompressed, with a warning; a checkpoint is never refused for its grids. A version 3 or older checkpoint has none and restores as before |

Restore applies the final snapshot directly (no replay-from-zero). Coordination channel in-flight items are included in the scenario snapshot DTO. So are the standing ASDE-X safety alerts (`SimScenarioState.ActiveAsdexAlerts`): they ride the Sim snapshot through a prepared restart, so the first tick after it diffs against the alerts the displays were showing instead of announcing them again (`SessionPersistenceTests.PreparedRestart_RestoresTheActiveAlerts`). The restore runs with broadcasts suppressed, so once it settles `SessionPersistenceService.PublishRestoredAsdexStateAsync` re-sends each restored facility's safety-logic configuration and standing alerts to the displays.

The room is registered in `TrainingRoomManager` before any of its state exists, so `RestoreRoomFromArchiveAsync` runs the whole rebuild under the room's tick gate (`TrainingRoom.GuardAsync`, the same semaphore `RoomTickLoopService` takes per second).

The rebuild is engine creation, the wind-grid registration (`RoomCheckpointArchive.RegisterWindGrids`: each grid verified against its hash, registered and temporarily pinned before any of the checkpoint's weather is parsed), the resource pin (`ScenarioLifecycleService.PinArchivedResourcesAsync`: the archived configs, the saved layouts and missing-map airports, and the scenario's ARTCC configs loaded into the live caches too, which the position registry and the CRC broadcasts read), `ReloadOnRoomSettingsAsync`, the snapshot and room-state restores, `LeavePlayback`, the attendance sync and the returned summary. `RebuildWindGridPins` then gives the room its own pins on the grids its weather and snapshots name, and the temporary pins are released on every path, success or failure. A wind-grid entry that is missing, hashes to another name or does not decode is skipped with a warning naming the room and hash, and the weather naming it flies the FD wind layers.

The pin holds each saved layout with its saved GeoJSON, and each saved missing-map airport with no map, as the room had them; only a scenario airport the checkpoint lacks, or holds in another layout format, is fetched now, with one Information line for it.

The reload is `ReloadKind.CheckpointRestore`, started on the manifest's `InitialActiveRunways` and `InitialWeatherJson` (the fourth `ReloadStart` field) with the scenario's sidecar looked up afresh (`ScenarioLifecycleService.FindScenarioSidecar`). After the snapshot restore, the manifest's `CarriedActiveRunways` and `CarriedStudentLowestWorkedLevel` (a level code; absent in an older checkpoint) go back into the room's `ScenarioAnswers`, so a later load of the same scenario starts on the answers even after a rewind to before they were given; the snapshot's own list and level are the current values, not the answers, and a restart starts on the current list.

`RoomCheckpointArchiveWriter.Finish` rebuilds the manifest field by field, so a new manifest field must be copied there too.

Without it the tick loop could advance a half-restored room between the awaits. `TrainingRoomManager.RoomRegistered` (raised outside the manager's lock) is how `SessionPersistenceTests` takes the gate at registration and asserts the restore cannot finish while it is held.

Restored rooms report **zero members** until someone reconnects. A room member is a SignalR connection, and
`TrainingRoomManager.CreateRestoredRoom` deliberately does not repopulate `TrainingRoom.Members` — only the restored-member CIDs
(the rejoin whitelist, `TrainingRoom.AddRestoredMember`) and the CID→room mappings. A room's invited and kicked CIDs are saved (`RoomStateSnapshotDto.InvitedCids` / `KickedCids` in `room-state.json`) and restored with the room, so a restored room still knows who was pulled in as an RPO and who was kicked; an archive written before they existed reads none. It also arms **no** abandoned-room cleanup timer; a restored room nobody
reclaims is retired by the paused-retirement sweep instead. `GET /admin/status` cannot tell the two cases apart, so do not
assert that an occupied-looking room is "held open by its cleanup timer".

A Tower Cab 3D viewer listed in a room's members is not persisted (`RoomStateSnapshotMapper.Capture` skips it, and it never enters the restored-member set): its connection drops with the restart and reattaches on its own once its CID rejoins the restored room.

## Checkpoint lifetime

A checkpoint holds member CIDs, the identity ledger (every CID, initials and name the room has seen) and the room's chat, so it is kept only as long as a restart needs it ([data-handling.md](data-handling.md)):

- A checkpoint that restores is deleted right after its room is rebuilt, and one skipped as stale (older than `SessionCheckpointMaxAgeHours`) is deleted when skipped. `current/` is removed once a restore empties it. There is no post-restore archive. A restore that fails removes the half-built room (`ScenarioLifecycleService.RemoveFailedRestore`), keeps the checkpoint file and logs one line naming the room and the error.
- `SessionPersistenceService.SweepExpiredCheckpoints` deletes a `current/*.checkpoint.zip` whose last write is older than `SessionCheckpointRetentionDays`, any `restored-*` directory left by older builds, and `.staging-*` / `.old-*` swap directories created before the same cutoff. It touches nothing while a prepare-restart is in flight. It runs at the start of every restore (after `TryRecoverLiveCheckpointDirectory`, which gets first claim on the swap directories) and once a day from yaat-server's `DataRetentionHostedService`.
- What the sweep leaves to its 7-day cutoff is a checkpoint the restore never consumed: an unsupported version, a room that already existed, or a restore that threw.
- `DELETE /admin/data/{cid}` deletes every checkpoint under the root (live, staging or backup) whose manifest or room state names the CID as creator, member or vTDLS item, whole (room-state assignments are built only from members, so they are not searched). Sweep, restore, prepare-restart and erasure exclude each other under `_prepareLock`.

## Client behavior

- `ServerRestarting` — banner, commands disabled, `ActiveRoomId` persisted to preferences.
- Transport drop during restart does **not** clear room state on the client.
- After reconnect: `FindRoomForMyCid` then `JoinRoom`; `RoomAvailableForCid` handles late tabs.

## Configuration (`appsettings` / `Yaat` section)

| Key | Default | Meaning |
|-----|---------|---------|
| `SessionCheckpointPath` | `%LOCALAPPDATA%/yaat/session-checkpoints` | Checkpoint root. The runtime writes the live set to `<this>/current/`, with `.staging-*/` and `.old-*/` as siblings inside. Safe to point at a Docker bind volume mount root — the root itself is never renamed. |
| `SessionCheckpointMaxAgeHours` | `24` | Skip (and delete) stale checkpoints on restore |
| `SessionCheckpointRetentionDays` | `7` | Sweep any checkpoint file or swap directory older than this; at least 1, checked at startup |
| `PrepareRestartDrainSeconds` | `30` | Default drain when hub arg is `0` |

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

The step ordering matters: the `POST /admin/prepare-restart` call (step 1) runs against the **currently-running container**, before `git pull` and the rebuild (step 3). A fix to the prepare-restart code path therefore only ships with the deploy and is exercised for the first time on the **next** deploy — the deploy that delivers the fix still runs the old binary's prepare-restart.

To validate a prepare-restart fix in the same session, probe the new container directly once the deploy reports the server is back up: `Invoke-RestMethod -Uri "https://yaat1.leftos.dev/admin/prepare-restart?drainSeconds=5" -Headers @{'X-Yaat-Admin-Password'=$pw}`. Probing leaves `_prepareCompleted=true` on the server; clear it with `docker compose restart yaat-server`.

## Deploy notes

Restore checkpoints only when the **same** yaat + yaat-server build (snapshot schema) wrote them. Cross-version restore may fail migration or produce drift.
