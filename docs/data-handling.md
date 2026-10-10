# Data handling: what YAAT collects, keeps and deletes

The developer-side inventory of personal data in the desktop client, the browser front-ends and the public server YAAT1 (`yaat1.leftos.dev`, yaat-server in Docker on a DigitalOcean droplet). The public privacy policy on leftos.dev is written from this page; a change to anything below changes this page in the same commit, and the policy follows.

Server paths are in the sibling repo `yaat-server` (`src/Yaat.Server/...`).

## Identity

- Sign-in is VATSIM Connect (OAuth2 + PKCE) run by the server, which is the only VATSIM client; the desktop never holds a VATSIM token ([`vatsim-auth.md`](vatsim-auth.md)).
- From VATSIM the server receives CID, full name, rating and subdivision (scopes `full_name vatsim_details`); from VATUSA (`api.vatusa.net/v2/user/{cid}`, at login and each refresh) the mentor flag and home facility.
- The server mints its own session JWTs (`sub` = CID, plus name, rating, subdivision, ARTCC, mentor flag): access about 1 h, refresh 30 days (24 h for vEDST). The revocation list is in memory and cleared on restart.
- The desktop keeps the tokens per server URL in `%LOCALAPPDATA%/yaat/auth-sessions.json`: DPAPI-encrypted on Windows, plain JSON on macOS and Linux. Sign-out revokes the refresh token on the server.
- Browser front-ends (`/vstrips/`, `/vtdls/`) use first-party HttpOnly cookies `yaat_refresh` and `yaat_session`, cleared on logout, and keep `{initials, artcc}` in `localStorage` to pre-fill the form. Initials and ARTCC also ride in the page URL query.
- CRC clients connect to the server with the CID from the unverified VATSIM fsd-jwt `sub` and CRC's real-name field.

## What the desktop client sends

| To | What | When |
|----|------|------|
| The configured server (`/hubs/training`, `/auth/*`) | Session token, commands, room and scenario actions, initials, chat | While connected |
| YAAT1 `POST /telemetry/speech` | Opt-in speech samples: push-to-talk WAV, the transcript at each recognition stage, scenario context; tagged with the CID from the token | Only after the user accepts the opt-in; only to YAAT1 ([`speech-recognition-pipeline.md`](speech-recognition-pipeline.md#speech-telemetry-opt-in-upload)) |
| vNAS data and configuration APIs, FAA (CIFP, aircraft database), aviationweather.gov (live weather only), GitHub (updates), Hugging Face / LM-Kit catalogue (model downloads), GitHub (Piper voices), nuget.org (optional CUDA backend) | Plain GETs with no identifier beyond what HTTP carries | On use, cached |
| Discord desktop app, local IPC only | Rich Presence: scenario name, ARTCC and airport, start time | While a scenario runs, unless turned off in Settings ([`discord-integration.md`](discord-integration.md)) |

There is no analytics, usage telemetry or crash reporting, and speech recognition, the command LLM and pilot TTS all run locally. Bug reports are written to disk and published on GitHub by the user by hand; the room-scoped server-log slice in a bundle is pseudonymised by yaat-server's `SessionLogAnonymizer` (CIDs, names and initials become `A0`, `A1`, …), recordings and callsigns are not.

## What the server keeps, and for how long

| Data | Where | Holds | Lifetime |
|------|-------|-------|----------|
| Live rooms | Memory | Members (CID, initials), scenario, aircraft, action log including chat, terminal log, bookmarks | Until the room closes |
| Room checkpoints | Volume `yaat-session-checkpoints` | One zip per room saved for a planned restart: creator and member CIDs, assignments by CID, scenario, action log including chat, terminal log, snapshot | Deleted once restored, deleted when skipped as stale (`SessionCheckpointMaxAgeHours`, 24), and swept after 7 days (`SessionCheckpointRetentionDays`) at startup and daily ([`session-persistence.md`](session-persistence.md)) |
| Speech uploads | Volume `yaat-telemetry`, `speech/{yyyy-MM-dd}/{cid}-{HHmmss}-{guid}.zip` | The opt-in samples, verbatim | Date folders older than 12 months (`SpeechTelemetryRetentionMonths`) deleted daily |
| Server log file | Volume `yaat-logs`, `yaat-server.log` and rolled `.N` generations | CIDs, room ids, initials; the remote IP of a refused CRC socket | Rolled at each start, past 50 MB and at each UTC day change; a rolled file is deleted once its last write is 88 days old and the daily pass prunes, so no line outlives 90 days (`Yaat:LogRetentionDays`), and the rolled set never exceeds 450 MB ([`logging.md`](logging.md)) |
| Container stdout | Docker json-file driver | Critical lines only when `Yaat:LogPath` is set, as in the container | 50 MB x 5, cleared on each container recreate |
| ARTCC grants | `artcc-grants.json` (operator file) | CIDs and notes of hand-vetted visiting mentors | Until the operator edits it |
| Facility temp data | Volume `yaat-facility-data` | Controller-drawn ASDE-X/SAID geometry, not tied to a CID | Until changed |

Caddy's access log is off. The live-traffic (SWIM) raw log holds real-world aircraft data, not user data ([`live-traffic.md`](live-traffic.md)).

## Deleting one person's data

`DELETE /admin/data/{cid}` with the `X-Yaat-Admin-Password` header deletes that CID's speech uploads and every room checkpoint that names it (as creator, member or vTDLS item), and returns a JSON report of what it removed. It answers 409, deleting nothing, while a prepare-restart or checkpoint restore is running, and 500 with the same report when a checkpoint could not be read or a delete was refused (`CheckpointsUnreadable`, `DeleteFailures`): retry, or remove those files by hand. Rooms still running in memory are not touched; one that names the CID writes it into a new checkpoint at the next planned restart, so run the erasure again after it. A checkpoint's action and terminal logs are not searched: someone who left the room before it was saved appears there only by initials, and goes when the checkpoint does, within 7 days. A checkpoint is deleted whole, so the other members' copy of that room goes too; with a 7-day lifetime that costs at most one planned-restart restore.

```bash
curl -X DELETE -H "X-Yaat-Admin-Password: $YAAT_ADMIN_PASSWORD" https://yaat1.leftos.dev/admin/data/1234567
```

Log lines are not rewritten: they age out under the 90-day cap. Tokens expire on their own (30 days at most). Data on the user's own machine is theirs to delete (`%LOCALAPPDATA%/yaat/`; Settings → "Delete all saved samples" clears local speech samples).
