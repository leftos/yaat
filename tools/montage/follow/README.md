# FOLLOW montage clips

One folder per clip id from [`docs/plans/follow-video-montage.md`](../../../docs/plans/follow-video-montage.md) — the release cut (A1, A4, A8, A11, C1, C2, C3, E1, E2, G6, then the ground clips H1, H2) and the review reel. Each folder holds:

| File | What it is |
|---|---|
| `scenario.json` | ATCTrainer-format scenario — the spawns only. In the airborne clips both aircraft are airborne, away from the pattern, with the KOAK tower position as the scenario's student position so the solo pilots answer that radio. The ground clips (H) spawn at KOAK north-field parking or on a taxiway, with Oakland Ground (GC1) as the student position |
| `script.txt` | The timed instructor commands that bring the scenario into the situation, `t=<sec> <CALLSIGN> <command>` with `#` comments |
| `card.md` | The rule, its AIM / 7110.65 grounding, and what to watch for |

The setup is **natural**: nothing is placed on a pattern leg by hand. The scenario spawns aircraft inbound and `script.txt` flies them in with the same commands an instructor would issue. A situation the commands cannot reach is reported, not forced.

## Record a clip

From the **yaat** repo root (works from the main checkout and from any worktree — `../yaat-server` is the sibling of either). The run is headless, writes a v4 archive, and never touches the client.

```bash
pwsh tools/gate.ps1 -Log .tmp/montage/<ID>-run.log -TimeoutSeconds 900 -Slot heavy -- \
  dotnet run --project ../yaat-server/tools/Yaat.SoakRunner -c Release -- \
  run --scenario tools/montage/follow/<ID>/scenario.json \
      --script   tools/montage/follow/<ID>/script.txt \
      --solo --snapshot-interval 1 --sim-hours 0.15 --seed 20261001 \
      --out .tmp/montage/<ID>
```

- **yaat-server's SoakRunner, never yaat's** — it is the only thing that runs a scenario through the full room pipeline and writes a client-loadable archive. Run the sibling worktree copy, never another checkout's.
- `--script` sends each line through the instructor's command path once sim-second `t` has ticked; every verdict prints to `stderr` (`script t=… -> accepted` / `refused`), and refused lines are counted in `report.json` as `ScriptRefused`.
- `--solo` turns solo training on so the pilots read back and call in, and the recording replays with their voices. It is what makes the terminal log carry pilot lines.
- `--snapshot-interval 1` writes one snapshot per sim-second, which is what `bug_bundle.py track` needs for a per-second trajectory.
- `--sim-hours` is the sim-time budget and must exceed the last `t=` in `script.txt`. `0.15` = 540 s; a clip whose follower lands later states its own budget in its card (C1 0.17, C2 0.23, C3 0.2, G6 0.17); the ground clips H1 and H2 record with 0.1 (360 s).
- **Choose each clip's runway by length before scripting it.** Check the runway against every type in the clip: a jet never lands KOAK 28R (5,458 ft); airliners use 30 at OAK.
- The run exits 0 when the budget completes; exit 2 is a tick exception or a script line that threw.

Output lands in `.tmp/montage/<ID>/episodes/ep00/` (gitignored — archives are never committed):

- `scenario.yaat-recording.zip` — the v4 archive the client's **Scenario ▸ Load Recording** accepts (soak names it after the scenario file's stem).
- `report.json` — `SimSecondsRun`, `ScriptedCommands`, `ScriptRefused`, `Completion`, `SimLogWarnings`/`Errors`.
- `anomalies.jsonl` — engine anomaly events, if any.

## Check a clip

```bash
B=.tmp/montage/<ID>/episodes/ep00/scenario.yaat-recording.zip

uv run --with brotli python tools/bug_bundle.py info "$B"                          # manifest + aircraft at t=0; prints ServerVersion
uv run --with brotli python tools/bug_bundle.py history --callsign N738SP "$B"     # per-callsign commands + phase/route changes
uv run --with brotli python tools/bug_bundle.py track --pair N738SP N52417 "$B"    # gap + runaway timer, second by second
uv run --with brotli python tools/bug_bundle.py terminal-log "$B" --callsign N738SP
uv run --with brotli python tools/bug_bundle.py terminal-log "$B" --srt --out .tmp/montage/<ID>/<ID>.srt
```

`--with brotli` is needed to read a bundle's Brotli-compressed entries; the repo has no `brotli` dependency declared. `--srt` must go through `--out`: stdout on Windows translates to CRLF, and the caption file has to stay LF.

Trim a long run down to the clip window with `--max-seconds`:

```bash
uv run --with brotli python tools/bug_bundle.py trim "$B" --max-seconds 240 --out .tmp/montage/<ID>/<ID>-trim.yaat-recording.zip
```

## Stop a capture

A capture of a clip replayed in the client ends on the client-driver's `wait_until` ([`docs/client-driver-mcp.md`](../../../docs/client-driver-mcp.md)), not on a guessed duration. The default stop is the follower on the ground plus a few seconds of roll-out:

```json
{"conditions": [{"kind": "landed", "callsign": "N738SP"}], "timeoutMs": 600000}
```

Stop the recorder a few seconds after it returns `met`. A clip whose story ends elsewhere (a go-around, a pattern re-entry) names its own condition in its `card.md`; with no such line, the default applies to the clip's follower.

## Callsign roster

Kept consistent across clips so the reel reads as one session.

| Callsign | Type | Role |
|---|---|---|
| `N738SP` | C172 | VFR pattern follower (the aircraft told to follow); on the ground, the aircraft told to follow (H1) or to give way (H2) |
| `N52417` | C172 | VFR lead, straight-in / pattern traffic ahead; on the ground, the taxiing lead (H1) or the crossing traffic (H2) |
| `SWA2471` | B738 | VFR jet lead (C3), landing runway 30 |
