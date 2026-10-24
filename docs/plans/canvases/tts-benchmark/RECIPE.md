# Rebuilding the TTS benchmark clips and review copy

Run from the repo root (main checkout or worktree), in PowerShell. Synthesis runs alone in the gate's critical slot so other work cannot skew the timings; a local Release build comes first so nothing compiles inside that slot, and the synthesis runs call the built `Yaat.SpeechSandbox.exe` directly (never `dotnet run --no-build`, which the gate rewrites into a heavy build).

1. Build the sandbox in Release, on this machine (a routed build brings no binaries back):

   `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/tts-build.log --timeout 900 --slot heavy --local --reason "TTS benchmark binaries must exist on this machine for the alone timing run" -- dotnet build tools/Yaat.SpeechSandbox -c Release -p:TreatWarningsAsErrors=true`
2. Download and extract the models (about 1 GB, cached under `%LOCALAPPDATA%/yaat/tts-bench/models/`, so later runs skip it):

   `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/tts-download.log --timeout 1800 --slot light --local --reason "TTS model download into this machine's cache" -- tools/Yaat.SpeechSandbox/bin/Release/net10.0/Yaat.SpeechSandbox.exe --tts-bench --download-only`
3. Run the benchmark. It rewrites `docs/research/2026-10-24-tts-benchmark/report.md`, `results.json` and this folder's `index.html` after each model, and writes the clips to `.tmp/tts-bench/clips/`:

   `pwsh -NoProfile -File tools/gate.ps1 run --log .tmp/tts-bench.log --timeout 5400 --slot critical --alone --local --reason "TTS synthesis timing (YAAT-1136): measured on this machine, nothing else running" -- tools/Yaat.SpeechSandbox/bin/Release/net10.0/Yaat.SpeechSandbox.exe --tts-bench`

   Check that the log's header shows the alone critical slot. The report cannot see the slot, so it says "Gate slot: not recorded". Kitten-TTS 2 is not re-run: its rows come from `docs/research/2026-10-24-tts-benchmark/recorded-screens.json`.
4. Assemble the page with its clips under `.tmp/mocks/tts-benchmark-src/`:

   `New-Item -ItemType Directory -Force .tmp/mocks/tts-benchmark-src | Out-Null; Copy-Item docs/plans/canvases/tts-benchmark/index.html .tmp/mocks/tts-benchmark-src/; Copy-Item -Recurse -Force .tmp/tts-bench/clips .tmp/mocks/tts-benchmark-src/`
5. Make the review copy at `.tmp/mocks/tts-benchmark/` (the tool copies the folder, clips included; WAV files stay files beside the page):

   `uv run --no-project python ~/.claude/tools/mock-embed/embed_media.py .tmp/mocks/tts-benchmark-src/index.html .tmp/mocks/tts-benchmark`

Open `.tmp/mocks/tts-benchmark/index.html`. Piper output differs between processes, so a rebuild reproduces the clips' content and the timings' scale, not the same bytes or the same milliseconds.
