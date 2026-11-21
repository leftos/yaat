# Repo files and tools

Per-file map of the repo root's scripts and agent files, the scripts under `tools/`, and the standalone CLI tools (`tools/Yaat.LayoutInspector`, `tools/stash-procedure.py`, `tools/build-surface-temp-data.py`, `tools/yaat-crc-config`).

The speech sandbox is in [speech-recognition-pipeline.md](speech-recognition-pipeline.md), the Discord bot in [discord-integration.md](discord-integration.md), and the browser front-ends in [flight-strips.md](flight-strips.md) and [vtdls.md](vtdls.md). The Task Index and the layers are in [architecture.md](architecture.md).

## File map

One entry per file, grouped by project.

### Root Scripts

```
AGENTS.md                         # Codex project wrapper; points Codex back to CLAUDE.md and maps Claude agents/commands/hooks to Codex behavior.
.claude/rules/cifp-parser.md      # Path-scoped Claude guidance (loads only for src/Yaat.Sim/Data/Vnas, reference/cifp, tools/Yaat.CifpInspector): the cifparse/parseCifp column-offset authority + re-clone recipe.
tests/CLAUDE.md                   # Test-project Claude guidance: docs/test-harness.md pointer + the singleton-race, YAAT_APPDATA_DIR and xunit.runner.json gotchas (moved out of the root CLAUDE.md).
Setup-CrcEnvironment.ps1          # Adds YAAT1 to CRC's DevEnvironments.json (-Servers overrides for self-hosted)
deploy-targets.ps1                # Per-deployment map (DropletIp, ServerPath, ServerUrl, RemoteEnvFile) + Resolve-DeployTarget; dot-sourced by the deploy scripts
deploy-to-droplet.ps1             # Deploys yaat-server to a droplet (CI-built ghcr image by default; -BuildOnDroplet / -BuildImageOnly variants; -BuildImageLocally builds it here with tools/server-image/; -ServerRef/-ClientRef pin a hotfix build to `hotfix/v<version>` and `v<version>` instead of both mains, -ReleaseTag names the tag the publish check compares; the procedure is `prepare-release`'s hotfix path)
deploy-release-tag.ps1            # Names the client release tag deploy-to-droplet.ps1's publish check compares against (Resolve-ReleaseTag from -ReleaseTag, -ClientRef or Directory.Build.props); dot-sourced by deploy-to-droplet.ps1, defines functions only
deploy-secrets.ps1                # Merges yaat-server/.env + .env.<target> over the droplet's env file (key names only printed, timestamped backup, -DryRun)
deploy-ladd.ps1                   # Ships yaat-server/ladd/ladd.json (FAA LADD block list, restricted) to <ServerPath>/ladd/; -Restart recreates the container
update-ladd.ps1                   # Monthly LADD routine in one command: fetch-ladd.py (ADX portal document sync) -> refresh-ladd.py -> deploy-ladd.ps1 ->
                                  # deploy-to-droplet.ps1 -WaitForEmptyRooms -> deploy-ladd.ps1 -Restart; -Zip uses an already-downloaded zip
swim-slice.ps1                    # Live-traffic repro: copies the SWIM raw-log hours covering -From/-To off the droplet's yaat-swim-raw volume (or -Local) and
                                  # runs yaat-server tools/Yaat.SwimSlice cut [-Artcc [-Facility]]; output under yaat-server/.tmp/swim-slices (FAA data, never shared)
tools/release-local/              # Builds a client release on the local builders (the default path; the hosted workflows are the manual fallback) and creates its draft: release.ps1 (orchestrator, worktree at the tag), pack-win-linux.ps1 (Windows builder), pack-macos.ps1 (mac-mini, signed + notarized), release-notes.sh (body, shared with release.yml); README.md
tools/server-image/               # Builds the yaat-server image with the .NET SDK container publish (no Docker) from clean worktrees and pushes it to ghcr; build.ps1, container.targets (mirrors the Dockerfile's runtime stage), web-apps.proj; README.md
tools/codex-yaat.ps1              # Launches Codex from the YAAT repo root and adds ..\yaat-server as an extra writable/readable directory.
tools/setup-codex.ps1             # Creates user-local Codex skill junctions and registers MCP servers without committing local state or token values.
tools/build-artcc-boundaries.py   # Downloads the NASR 28-day ARB CSV (or --zip) and writes src/Yaat.Sim/Data/Artcc/ArtccBoundaries.geojson (--strata LOW,HIGH; re-run per cycle)
tools/bug_bundle.py               # Inspects, extracts, installs, and validates v4 bug bundles (*.yaat-bug-report-bundle.zip, *-recording.zip): info (bundle contents, incl. the `Server:` line), history (per-callsign triage), snapshot (at a tick), actions (filtered log), terminal (all broadcasts). Subcommands bridge RecordingArchive queries and the bundle export manifest.
tools/erase-user-data.ps1         # Privacy deletion request: calls the server's DELETE /admin/data/{cid} (ADMIN_PASSWORD from .env.<target> or .env) and prints what was removed; exits 0 erased, 1 retry (409), 2 incomplete, 3 failure
tools/refresh-faa-airspace.ps1    # Reads vNAS training scenario primary airports by ARTCC, then downloads matching FAA AIS Class Airspace GeoJSON/Brotli.
tools/refresh-airport-airlines.ps1 # Builds Data/airport-airlines.json.br from BTS T-100 segment ZIPs, OurAirports, and OpenFlights carrier/route crosswalks.
tools/refresh-test-layouts.py     # Re-fetches each tests/Yaat.Sim.Tests/TestData/<ID>.geojson (plain 3-4 letter stems only; `-`/prefixed snapshots are skipped) from the vNAS training-airport map API, normalized to LF; an airport with no map is kept as committed. Also re-fetches each TestData/TowerCabMaps/<id>.geojson tower-cab videomap (the ARTCC config's tower-cab `videoMapId`, ARTCC from the script's `TOWER_CAB_ARTCC`); a response that is not a FeatureCollection keeps the fixture. `--all` adds a fixture for each airport in `TestData/layout-airports.txt` that has none (`--force` also refreshes the listed airports' committed fixtures); `--check` reports what would change and each listed airport missing from TestData, and exits 1 if anything would.
tools/refresh-navdata.py          # One-command repin of the test pin: downloads vNAS NavData and the FAA CIFP zip for the NavData's own AIRAC cycle, writes NavData.dat, FAACIFP18.gz and both manifests under tests/Yaat.Sim.Tests/TestData together (nothing on a failed download); then commit. See docs/test-harness.md
tools/airac-pin-status.py         # Whether the FAA has published a CIFP newer than the committed pin: one line, exit 0 in sync or declined, 10 newer cycle published, 2 check failed; `--state <json>` remembers a declined cycle, `--decline <YYNN> --state <json>` records one. Run by the yaat-nextup pre-loop hook
tools/airac.py                    # AIRAC cycle arithmetic shared by airac-pin-status.py and refresh-navdata.py (the grid of AiracCycle.cs: YYNN ids, cycle_for_date, serial_cycle); reads no clock
tools/refresh-airline-fleets.py   # Parses Airfleets PDFs into Data/airline-fleets.json + .meta provenance sidecar.
tools/refresh-aircraft-display-names.py # One-shot tool that queries OpenAI for human-readable names of every ICAO type in AircraftSpecs.json and writes Data/aircraft-display-names.json + .meta sidecar. Re-run only when AircraftSpecs.json gains new types.
tools/refresh-crc-docs.py         # Mirrors the vNAS docs site (docs.virtualnas.net, Material for MkDocs) into docs/crc/ + docs/vnas-data-admin/: fetches each page, converts HTML->Markdown, downloads images, rewrites links. See docs/crc/README.md.
tools/parse_airfleets.py          # pdfplumber parser used by refresh-airline-fleets.py; maps fleet variants to ICAO Doc 8643 types.
tools/build-mtr-data.py           # Parses the DoD AP/1B PDF by word bounding box into Data/MilitaryRoutes/ap1b-mtr.json.br + ap1b-ar.json.br (+ .meta sidecars). Gated by an FRD oracle (navaid-to-point distance must match the published Fac/Rad/Dist) and an FAA AIS MTRSegment cross-check. See military-training-routes.md.
tools/mcp/context7-stdio.ps1      # Context7 stdio adapter that reads CONTEXT7_API_KEY from the environment when Codex cannot express the custom header.
tools/mcp/exa-stdio.ps1           # Exa stdio adapter that reads EXA_API_KEY from the environment when a local authenticated Exa MCP is preferred.
tools/Yaat.PrecomputeCache/       # Maintainer CLI for the committed precompute cache (`dotnet run -c Release --project tools/Yaat.PrecomputeCache -- [flags]`): CliOptions.cs (flags, usage, exit codes), PrecomputeCacheCli.cs (compute, --check), RefreshAirportsCommand.cs (--refresh-airports from every ARTCC's vNAS scenarios), PrecomputeCacheServices.cs (the network, navdata and output a run reaches, so tests stub them), Program.cs. See src/Yaat.Sim/Data/PrecomputeCache/README.md.
tools/hooks/whitespace-fix-autostage.ps1 # prek hook (pwsh): trailing-whitespace + EOF-newline fixes on raw bytes, auto-staged so the commit proceeds; "changed" is judged by `git hash-object`, so a CRLF-only difference is left alone.
tools/hooks/csharpier-wrapper.ps1        # prek hook (pwsh): `dotnet csharpier format` on the staged C#/XAML files, then re-stages them.
tools/hooks/test-hook-ports.ps1          # Characterization harness for the whitespace hook: fixture bytes and staging (`pwsh -NoProfile -File tools/hooks/test-hook-ports.ps1` prints PASS).
tools/hooks/claude-guard-bash.sh   # Claude Code PreToolUse(Bash) guard: denies uncaptured/untimed dotnet test|build|run (must run through tools/gate.ps1 or redirect to a .tmp log; `| tee` is denied), bare `dotnet format`, dotnet -q/--nologo, and `prek run --all-files`. Matches only in command position (heredoc bodies and quoted spans that hold a shell separator stripped) so grepping a doc that mentions a guarded command is not blocked; each list or pipeline segment is judged by its own gate or timeout wrapper, never a neighbour's. Registered from .claude/settings.json (tracked, so contributors + the CI Claude workflows get it too).
tools/hooks/claude-guard-read.sh   # Claude Code PreToolUse(Read) guard: denies reading secret-bearing files (.env*, *.pem/key/pfx/p12, credentials, secrets.*, appsettings.Local.json). Narrow by design — `Secrets.cs` and docs about credentials stay readable.
tools/hooks/claude-guard-cases.jsonl    # Expected allow/deny table for the Bash guard.
tools/hooks/test-claude-guards.sh       # Runs the table above; run by hand after changing a guard file.
tools/gate.ps1                    # `pwsh tools/gate.ps1 -Log <log> -TimeoutSeconds <seconds> -Slot heavy|light -- <command...>`, all four required (the gate wrapper: it hands every call to the installed gate.exe, and on a machine without one runs the command directly with no log, slot or watchdog; runs from Bash too). Full output to a log, only the tail (plus the failure lines on red) on screen, exits with the command's own status, also failing when the log holds `Build FAILED` or `error CS`; kills a stalled run, one past its ceiling on the load-adjusted clock, or one at the 5x wall backstop (exit 124); runs the command at below-normal priority with its own MSBuild nodes, holding one heavy slot ((threads - 1) / 4 machine-wide) or one light slot ((threads - 1) / 2). Use for every dotnet build/test/run, always `-Slot heavy` here; `cmd | tee` floods the context with the whole log and reports tee's status.
Directory.Build.targets           # Imports build/sherpa/SherpaNativeOverlay.targets into every yaat project (guarded by Exists, for yaat-server's extern/yaat copy).
build/sherpa/SherpaNativeOverlay.targets # After ResolvePackageAssets, swaps NuGet's org.k2fsa.sherpa.onnx.runtime.<rid> natives for our fork's CPU native per RID listed in src/Yaat.Client/Speech/stt-downloads.json, plus runtimes/<rid>/native/sherpa-native-MANIFEST.txt. When no source verifies, `dotnet publish` (or -p:YaatSherpaRequireOverlay=true) fails; any other build warns YAATSHERPA001 (not an error under /warnaserror), keeps NuGet's stock native and ships sherpa-native-FALLBACK.txt beside it. docs/speech-recognition-pipeline.md
build/sherpa/EnsureSherpaNatives.cs # RoslynCodeTaskFactory task behind it: downloads each RID's zip from dl.leftos.dev, then each natives.mirrors entry (the leftos/sherpa-onnx GitHub release), into the cache under YAAT_APPDATA_DIR or %LOCALAPPDATA%/yaat, checks SHA-256 against the manifest (a mismatch tries the next source; none verifying warns YAATSHERPA001 and falls back to NuGet's native, or fails a publish), and holds a per-RID .lock beside the cached zip while it prepares and extracts, so parallel builds wait and keep one extraction. EnsureSherpaNativesTests compiles this file into Yaat.Client.Tests and drives it against a localhost server.
src/Yaat.Client/Speech/stt-downloads.json # The STT download manifest written by tools/publish_stt_downloads.py: baseUrl (dl.leftos.dev), sherpa natives per RID and variant (cpu/cuda) and rasr-parakeet-v2 model files per precision, each with key, size, SHA-256. Copied to the client output; read by the build overlay above.
src/Yaat.Client/Speech/stt-reference.json # The reference owner WER and callsign recall per engine and mode the benchmark shows beside this machine's figures (schema 1), measured on the CI natives the file's `sttNative` names. Copied to the client output.
src/Yaat.Client/Speech/BenchClips/ # The benchmark's committed clip set: clips.json (id, expected) and one 16 kHz mono PCM16 WAV per clip, synthesized with the sandbox's --synth-corpus. Copied to the client output.
.mcp.json                         # Claude Code project MCP registration: `yaat-client-driver` → `pwsh ../yaat-server/tools/Yaat.ClientDriver.Mcp/launch.ps1` (the private yaat-server repo; yaat-server `docs/client-driver-mcp.md`)
tools/test-all.ps1                # Builds and tests both repos (`-Full` adds Nightly + PathfinderGrid). Self-gating: called bare it re-runs itself once under one heavy `tools/gate.ps1` run (900 s, 2700 s with `-Full`, log `.tmp/test-all.log`), a run already inside a gate runs directly. Restores the local dotnet tools first; `Wait-TestJobs` follows both suites' logs and runs the stall watchdog; on the Mac builder it collects crash dumps, and a failed run returns `.tmp/dumps/**`. docs/test-harness.md
tools/TestAllSelfGate.ps1         # Dot-sourced by test-all.ps1: the self-wrap's decision (`Test-InsideGate`: `YAAT_TEST_ALL_GATED`, `GATE_SLOT_HELD`, `GATE_BOX_JOB`) and the `gate.ps1 run` argument list (`Get-SelfGateArguments`). docs/test-harness.md
tools/test-TestAllSelfGate.ps1    # Case script for TestAllSelfGate.ps1 (run bare): the marker check and the argument list
tools/TestAllWatchdog.ps1         # Dot-sourced by test-all.ps1: finds the test hosts under the run's process tree, flags one whose tree (host plus descendants) shows no CPU growth or descendant change, and holds no thread the machine is holding in the host, its descendants or its ancestors below the root that made no CPU progress since the previous poll, for 90 s (`Test-StalledHost`), and on a stall (`Invoke-StallResponse`) prints the tree and the suite log tails, dumps the host with dotnet-dump into `.tmp/dumps/`, prints `clrstack -all`, kills the hosts. docs/test-harness.md
tools/test-TestAllWatchdog.ps1    # Case script for TestAllWatchdog.ps1 (run bare, Windows): Find-TestHost finds a live host and never a process-snapshot clone, live or killed
tools/test-TestAllWatchdogStall.ps1  # Case script for the stall verdict (run bare, Windows): a host waiting on a working child, with held threads, or waiting on a starved parent or a starved child, is not STALLED; one blocked in its own wait or in Thread.Sleep with no working child is; a held-thread ancestor pauses the idle clock only while its CPU stands still, so a busy ancestor lets a hung host still be cut and a starved one does not
tools/measure-test-loop.ps1       # Measures the developer test loop (discovery, one-class, whole-project wall+CPU, incremental/cold build, full gate) as medians over N runs. Detects xunit.v3 vs TUnit from the csproj and picks the matching filter syntax, so a framework-migration branch can be diffed against main. Baseline numbers: docs/plans/tunit-migration.md.
tools/analyze-test-schedule.py    # Reads a TRX of per-test durations and LPT-packs it onto N workers under both scheduling models (xunit's per-collection vs per-test), reporting the makespan gap. Answers "would a different scheduler help?" without changing frameworks — on this suite the gap measures 0.0s. Import-safe: `tools/refresh-sim-collection-weights.py` imports its TRX parsing by path.
tools/refresh-sim-collection-weights.py # Rebuilds tests/Yaat.Sim.Tests/collection-weights.json (collection key → whole seconds, read by `CollectionWeightOrderer`) from a Sim run's TRX: `uv run --no-project python tools/refresh-sim-collection-weights.py <trx> [--machine <name>] [--lanes <n>]`. The docstring carries the command that produces the TRX.
tools/gen-synthetic-suite.py      # Emits a throwaway test project matching Yaat.Sim.Tests' shape (class/test/InlineData counts) in xunit.v3 or TUnit, with trivial bodies. Measures a framework's *compile* cost at our scale without converting real files — how the TUnit source generator was shown to add ~5s per incremental build at 9,306 cases.
tools/speech_telemetry.py         # Server-side speech telemetry triage (stdlib Python, admin password from YAAT_ADMIN_PASSWORD): pull (download + unpack bundles into .tmp/speech-telemetry/cases/), summary (worst-first triage table), eval (score cases through Yaat.SpeechSandbox --eval), reviewed (mark cases seen), promote (append a reviewed case's text-only labels to the telemetry regression corpus).
tools/tests/test_bug_bundle_trim.py # pytest for bug_bundle.py trim: --keep-at keeps the latest snapshot at or before each time plus the first, renumbered from 000 with the manifest matching
tools/tests/test_airac.py          # pytest for airac.py: cycle ids, indexes and dates on the 28-day grid, serial_cycle
tools/tests/test_airac_pin_status.py # pytest for airac-pin-status.py: exit codes, the declined-cycle state file
tools/tests/test_refresh_navdata.py # pytest for refresh-navdata.py: the cycle stamp and the all-or-nothing write
tools/generate-speech-lexicon.py  # Spoken-form lexicon generator (PEP 723, ANTHROPIC_API_KEY from the environment only): generate (claude-opus-5-5 proposes spoken forms for a Yaat.SpeechSandbox --lexicon-inputs dump: fixes, procedures, gates, spots, named places, VFR landmarks), review (local page on 127.0.0.1, owner accepts/edits/rejects into decisions.json), apply (writes only accepted items into Data/ARTCCs/<ARTCC>/{FixPronunciations,ProcedurePronunciations,GatePronunciations,CustomFixes}).
tools/tests/test_generate_speech_lexicon.py # pytest for generate-speech-lexicon.py with a fake model client: the request's model/beta/fallback/schema, a refusal failing its batch while earlier batches stay written, the missing-key exit, apply's merges (existing landmarks and procedure forms kept, a fix already in another FixPronunciations file skipped, reads_as_spelled never written), missing decisions.json, and the key absent from every written file
tools/tests/test_speech_telemetry.py # pytest for speech_telemetry.py against a fake admin server: pull watermark and errors, summary ordering/JSON/width, reviewed bookkeeping, promote (text-only, sorted, duplicate/unreviewed refusal), case staging, gate-run eval command
```

The sibling yaat-server repo registers the same two guards via its own `.claude/settings.json` → `tools/hooks/claude-guard.sh`, a shim that resolves these scripts through the sibling checkout first and `extern/yaat/` second (the same order `Directory.Build.props` uses for Yaat.Sim) and fails open if neither is present.

See [installer-release.md](installer-release.md) for the Velopack packaging, auto-update, CRC install prompt, and the local `tools/release-local/release.ps1` release (the default; a `v*` tag push starts no hosted build) with the manual `workflow_dispatch` fallback `release.yml` (win+linux) / `release-macos.yml` (Apple Silicon + Intel, one Velopack channel per architecture).

macOS code signing/notarization (Developer ID certs, entitlements + `Info.plist` template in `build/macos/`, the eight `MACOS_*` secrets) is set up via [macos-code-signing.md](macos-code-signing.md).

### Yaat.LayoutInspector — CLI tool (`tools/Yaat.LayoutInspector/`)

Loads airport GeoJSON and queries the ground graph (nodes, taxiways, runways, exits, BFS path traces, pathfinder route forensics, parking/spots), renders interactive HTML maps with optional tick overlays, and prints text tick-tables from `TickRecorder` JSON. Output modes are mutually exclusive: `--html` → HtmlRenderCommand, `--dump` → DumpCommand, `--tick-table`/`--tick-summary` → TickTableCommand, `--arcs` → ArcsCommand, otherwise → QueryCommand (text or `--json`).

```
Program.cs                     # Thin entry: parse args → bootstrap → dispatch ICommand
CliOptions.cs                  # Options record + TryParse (all arg parsing lives here, including comma-separated --node id lists, batch query flags, --html-route, --pathfinder, repeatable --ticks [LABEL=]<path> sources)
UsageText.cs                   # --help text
Bootstrap.cs                   # NavData auto-discovery (walks up to yaat.slnx) + debug logger wiring

Commands/
  ICommand.cs                  # int Execute(LayoutAnalyzer, CliOptions)
  QueryCommand.cs              # Default: text/json query dispatch (--taxiway, --runway, --node, --exits, --bfs, --pathfinder, --parking, --spots, --intersection, --validate)
  HtmlRenderCommand.cs         # --html <path>: interactive HTML render; honors all --html-* highlights and overlays --ticks animation when present
  DumpCommand.cs               # --dump: full airport JSON to stdout
  TickTableCommand.cs          # --tick-table / --tick-summary: TickRecorder JSON → fixed-width text table; optional --tick-ref + --tick-hold-shorts add cross-track / along-track columns
  ArcsCommand.cs               # --arcs / --arcs-max-radius <ft>: every fillet arc, tightest effective radius first (effective and tightest Bezier radius, turn, length, nodes, the taxiways at each end); --json too

Tick/
  TickRecording.cs             # Top-level TickRecorder JSON schema as sealed records (mirrors Yaat.Sim.Tests.Helpers.TickRecording); rejects unknown major versions
  TickJsonReader.cs            # JSON file → TickRecording
  TickRecordingLoader.cs       # Reads every --ticks source; a missing/empty/malformed file becomes a TickSourceProblem instead of hiding the others
  TickRecordingMerger.cs       # Merges --ticks sources into one recording: a LABEL renames a source's aircraft (one aircraft → LABEL, several → LABEL:CALLSIGN), palette-colours multi-source merges, orders ticks by time; throws TickMergeException on a shared unlabelled callsign or mixed airports
  TickDataRow.cs               # One per-tick aircraft state row used by both HTML overlay and text formatter
  RunwayReference.cs           # Runway centerline (lat/lon + true heading) for signed xteFt / hdgErr columns
  HoldShortResolver.cs         # Resolve --tick-hold-shorts taxiway letters → GroundNode list + along-track distance math

LayoutAnalyzer.cs              # Core query engine over AirportGroundLayout
QueryResults.cs                # Result record DTOs for all queries
IFormatter.cs                  # Output formatter interface (text vs JSON)
TextFormatter.cs               # Human-readable stdout formatter for query results
JsonFormatter.cs               # JSON stdout formatter (--json flag)
TickTableFormatter.cs          # Fixed-width text formatter for --tick-table / --tick-summary (not an IFormatter — operates on a row list, not single results)
HtmlRenderer.cs                # Interactive HTML+Canvas renderer; embeds layout JSON in inspector-template.html, all rendering happens client-side
inspector-template.html        # Page shell — pan/zoom, search, toggle highlights, tick-overlay player, URL-hash persisted view
inspector.css / inspector.js   # Extracted styles + client logic (layout polish, forensic restyle); kept out of the C# string template
```

### stash-procedure.py — CLI tool (`tools/stash-procedure.py`)

Captures a published procedure as an ARTCC CIFP fragment before it ages out of reach. The FAA sometimes drops a still-charted procedure from the CIFP dataset (KOAK NIMITZ); the prior-cycle chain recovers it for only ~12 months and only on a machine that cached the right cycle, so anything a facility depends on has to be pinned into `Data/ARTCCs/{ARTCC}/Procedures/*.cifp`.

```bash
python tools/stash-procedure.py NIMI --airport KOAK              # find + write the fragment
python tools/stash-procedure.py NIMI --airport KOAK --dry-run    # report which cycles carry it, write nothing
python tools/stash-procedure.py BDEGA4 --airport KSFO --kind star --artcc ZOA
```

Stdlib-only Python. A bare name (`NIMI`) matches every version (`NIMI5`/`NIMI6`) using the same base-name rule as `NavigationDatabase.StripTrailingDigits`; an exact id matches only itself. Searches newest-first across the local CIFP cache (`%LOCALAPPDATA%/yaat/cache/cifp/`), the repo's bundled `TestData/FAACIFP18.gz`, any `--search-path`, and optionally the FAA server (`--fetch`, usually 404s for past cycles).

Line selection mirrors `CifpParser`'s own gate exactly, and terminal-waypoint (`PC`) records are emitted alongside legs that reference an arc center. The owning ARTCC is inferred from existing `Data/ARTCCs/*/` content naming the airport; `--artcc` overrides. `--verify` round-trips the result through `Yaat.CifpInspector`.

### build-surface-temp-data.py — CLI tool (`tools/build-surface-temp-data.py`)

Generates the ASDE-X / SAAB SAID final-approach overlay vZOA controllers otherwise hand-draw at SFO: each runway's extended centerline outbound from the landing threshold, with a short mark across it at each whole mile.

The mark shape is copied from the reporter's screenshot — centred on the runway's own centerline, crossing it on both sides, and stopping short of the parallel so the two never merge; no text. Writes an ARTCC sidecar at `Data/ARTCCs/{ARTCC}/SurfaceTempData/{FACILITY}.json`, which yaat-server seeds every room from.

```bash
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --runways 28L 28R --set 1 --set-name FINALS
python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --runways 28L 28R --dry-run
```

Stdlib-only Python. Runway ends come from the vNAS airport map GeoJSON (the same surface map the controller sees) — fetched from the training API, or read from the bundled `TestData/{airport}.geojson` when present. Bearings are TRUE, because the output is lat/lon; the centerline is drawn on the reciprocal of the landing course.

CRC closes an area's ring itself and draws it as an outline plus a hatched fill, so lines and ticks are emitted as quads `LINE_WIDTH_NM` (10 ft) across — wide enough to tessellate (a zero-area ring throws in the client) and narrow enough to stay sub-pixel, so the outline collapses to the 1-px line in the issue #312 reference instead of a hatched band.

`--append` merges a second flow's group into the same facility file, each group in its own SET; `--length`, `--tick-interval`, and `--tick-length` tune the geometry; `--set` files everything into one CRC SET so a controller can toggle the whole overlay off.

Committing generated output is one way to author a sidecar; the other is drawing it in CRC and using the client's **Tools → Export ASDE-X / SAID Temp Data...**, which emits the same schema from a live room.

### yaat-crc-config — Standalone Rust binary (`tools/yaat-crc-config/`)

Tiny (~200 KB) standalone tool that ports the YAAT client's `Tools → Configure CRC Environments` flow into a single small binary. Lets students who only want to point CRC at YAAT skip installing the full client.

Released independently from the `crc-config-v*` tag via `.github/workflows/yaat-crc-config.yml`; the macOS build wraps the binary in a minimal `.app` bundle (the tool is dialog-driven, so it is double-clickable), signs it with the client's Developer ID Application certificate, and ships it notarized and stapled inside a likewise-notarized `.dmg`.

A bare Mach-O cannot carry a stapled ticket, so the `.app` wrapper is what lets Gatekeeper clear it offline. Windows and Linux ship the bare executable.

```
Cargo.toml                     # Size-optimized release profile (opt-level=z, lto=fat, panic=abort, strip)
build.rs                       # Validates ../../docs/crc-environments.json schema at build time
src/main.rs                    # Flow: detect dir → already configured? → confirm → upsert → success. windows_subsystem="windows" so double-click doesn't flash a console.
src/config.rs                  # Mirrors CrcConfigService.cs: find_crc_config_dir (Win registry + LOCALAPPDATA, Mac/Linux home paths), are_entries_present, upsert_entries (preserves unrelated keys via serde_json::Value)
src/dialog.rs                  # Cross-platform native dialogs: MessageBoxW (Win), osascript (Mac), zenity/kdialog/console (Linux)
```

The canonical YAAT environment list (`YAAT1`) lives in `docs/crc-environments.json` — a single source of truth shared by this tool, `Yaat.Client.Core/Services/CrcConfigService.cs` (embedded resource), and `Setup-CrcEnvironment.ps1` (read at script execution time when run from the repo). Self-hosted servers are not defaults; they are added by hand or via `Setup-CrcEnvironment.ps1 -Servers`.
