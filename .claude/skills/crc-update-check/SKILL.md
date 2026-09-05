---
name: crc-update-check
description: "Use when CRC (the vNAS client) has updated, when the user asks whether yaat / yaat-server need to change to keep up with a CRC version, when comparing the decompiled CRC reference at ..\\crc-decompiled against the installed client, or when a CRC version number (2.x.y) appears alongside 'update', 'changelog', 'decompile', or 'wire contract'."
---

# CRC update check

Answers "does yaat / yaat-server need to change for CRC x.y.z?" with evidence, and lands the
two artefacts that keep the next check cheap: the wire-contract snapshot and the decompiled
reference tree. Reference behind every step: `yaat-server/docs/crc-update.md`.

Run from the yaat-server repo root unless a step says otherwise. Tee every tool run into `.tmp/`.

## Step 1: Measure the gap

```bash
curl -s https://crc.virtualnas.net/LatestVersion.json
pwsh -NoProfile -Command '(Get-Item "$env:LOCALAPPDATA\CRC\Application\CRC.exe").VersionInfo.FileVersion'
git -C X:/dev/crc-decompiled/CRC log -1 --format=%s
```

The reference repo path is absolute on purpose: it is a sibling of `X:\dev\yaat`, not of a worktree
checkout, so `../crc-decompiled` fails from `X:\temp\rt-worktrees\...`. The commit subject carries the
three-part version, so `2.18.2.0` matches `decompiled CRC 2.18.2`.

All three equal → report "baseline current" and stop. Installed behind published → the user
must launch CRC to self-update before the rest is meaningful.

## Step 2: Changelog from the bundle, not the page

The vNAS page is an SPA; crawled and curl'd copies lag releases. Pull the hashed bundle and
extract the CRC array with a regex (JSON parsers choke on its escape sequences):

```bash
curl -sL https://vnas.vatsim.net/crc | rg -o '/assets/index-[A-Za-z0-9_-]+\.js'
curl -sL "https://vnas.vatsim.net$(curl -sL https://vnas.vatsim.net/crc | rg -o '/assets/index-[A-Za-z0-9_-]+\.js')" -o .tmp/vnas-index.js
rg -o '\{"version":"2\.[0-9]+\.[0-9]+","date":"[^"]+","notes":\[[^\]]*\]\}' .tmp/vnas-index.js | head -20
```

A hotfix can be absent from the list; `LatestVersion.json` is the authority.

## Step 3: Triage by assembly

```bash
pwsh -NoProfile -Command 'Get-ChildItem "$env:LOCALAPPDATA\CRC\Application\*.dll" | Where-Object Name -match "^(Vatsim\.Nas|CRC)" | ForEach-Object { "{0,-36} {1,-10} {2:yyyy-MM-dd}" -f $_.Name, $_.VersionInfo.FileVersion, $_.LastWriteTime }'
```

| `Vatsim.Nas.Messaging.dll` unchanged (version + date) | Wire contract unchanged. Step 4 is confirmation only. |
| `Vatsim.Nas.Data.dll` changed | Diff `Vatsim.Nas.Data.Facilities/*` in step 5; compare to `Yaat.Sim/Data/Vnas/ArtccConfig*.cs`. |
| `CRC.dll` changed (always) | Step 5 decides. |

## Step 4: Wire snapshot

```bash
dotnet run --project tools/CrcWireDump 2>&1 | tee .tmp/crcwiredump.log
git diff --stat docs/crc-wire/
```

Empty diff → `git checkout -- docs/crc-wire/messaging-contract.json` (the tool writes CRLF; the
flagged file is a line-ending no-op). Non-empty → run
`timeout 30 dotnet test -- --filter-class "*CrcWireContractTests" 2>&1 | tee .tmp/test.log`;
the failure names the `Yaat.Server.Dtos` type to fix. Commit snapshot + fix together.

## Step 5: Decompile into the reference repo and diff

Hold the decompiler invocation constant or the diff is churn:

- ilspycmd **9.1.0.7988** exactly (four-part NuGet version; `9.1.0` is "not found"). Not the
  global tool. `dotnet tool install ilspycmd --version 9.1.0.7988 --tool-path .tmp/ilspy9`.
- **Absolute** DLL paths — the generated csproj HintPaths echo the input path.
- One run per assembly into `X:\dev\crc-decompiled\CRC`: `CRC.dll`, `Vatsim.Nas.Common.dll`,
  `Vatsim.Nas.Data.dll`, `Vatsim.Nas.Render.Engine.dll`. Never `Vatsim.Nas.Messaging.dll`.
- Drive it from a `.ps1` in the scratchpad (a bash loop mangles `\$a`).

```powershell
$ilspy = '<repo>\.tmp\ilspy9\ilspycmd.exe'
$crc = Join-Path $env:LOCALAPPDATA 'CRC\Application'
foreach ($a in 'CRC.dll','Vatsim.Nas.Common.dll','Vatsim.Nas.Data.dll','Vatsim.Nas.Render.Engine.dll') {
  & $ilspy -p -o 'X:\dev\crc-decompiled\CRC' (Join-Path $crc $a)
}
```

Then in `X:\dev\crc-decompiled\CRC`:

```bash
git status --short | awk '{print $1}' | sort | uniq -c        # M / ?? counts; ilspycmd never deletes
git diff --stat -- '*.csproj' | tail -1                       # must be empty (HintPath churn = wrong path shape)
git diff | rg -c 'GeneratedCode\("(System.Text.RegularExpressions.Generator|PresentationBuildTasks)"'
git diff --numstat | sort -rn | awk '{print $1+$2, $3}' | head -30
```

Read the top of the ranked list, skipping regex-generator / XAML-compiler stamps, `.baml`, and
`AssemblyInfo` version bumps. Map each substantive file to a server surface with the table in
`docs/crc-update.md` §6 — `Networking` → `CrcClientState*`, `Commands`/`*.Input` → command
handlers, `Ui.Displays.*` → `DtoConverter`/room state, `Data.Facilities` → `ArtccConfigService`.

## Step 6: Report, then close out

Report: per-assembly table, the changelog lines for the gap, each substantive decompile change
with its yaat-server consequence (or "client-side only"), and the verdict. Then, asking before
each commit:

1. `crc-decompiled`: `decompiled CRC x.y.z` + bullets of substantive changes + the ilspycmd version.
2. yaat-server: regenerated snapshot with any DTO fix.
3. Doc pins: `rg -n 'CRC 2\.' docs ../yaat/docs`; bump "Last verified against" in `docs/crc-update.md`.

No CHANGELOG entry unless a DTO or behaviour actually changed.

## Common mistakes

| Mistake | Consequence |
|---|---|
| Reading the changelog from the rendered page or a search result | Stops several releases early; looks complete |
| Decompiling with the installed (newer) ilspycmd | Hundreds of reformatted files hide the real diff |
| Relative DLL paths | 78-line csproj HintPath diff per assembly |
| Treating "CRC does not enforce X" as licence to drop a server guard | The decompile is the client; the vNAS server may still enforce it |
| Modelling a new `Vatsim.Nas.Data` field nobody reads | `ArtccConfigRoot` ignores unknown JSON; wait until CRC or the server consumes it |
