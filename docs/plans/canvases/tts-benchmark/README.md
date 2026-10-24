# YAAT-1136: small TTS models, listening page

The listening page for the small-TTS benchmark (Linear YAAT-1136). `index.html` puts the results table on top: download and disk size, cold load time, median and p90 first-audio latency and median real-time factor per model. Below it, each of the 20 pilot readback lines has one player per model × voice. A Dry / Radio FX switch at the top swaps every player between the raw voice and the same clip through the client's `RadioAudioFx` chain.

Play-all controls: each line's "Play line" button plays every model × voice saying that line, in grid order; the bar's voice select and "Play voice" play one voice across all 20 lines; "Stop", a single player's play button or a Dry / Radio FX switch ends the sequence. Clips play 400 ms apart in the current mode, the playing one outlined and scrolled into view.

The page and its table come from one `Yaat.SpeechSandbox --tts-bench` run; the full numbers and the rendered text of every line are in `docs/research/2026-10-24-tts-benchmark/` (`report.md`, `results.json`).

## Opening it

The clips are WAV files, well over 1 MB in total, so they are not committed: the committed `index.html` names `clips/<model>/<voice>/<nn>-<dry|fx>.wav` beside it, and those files exist only in a build. `RECIPE.md` rebuilds them and makes the review copy at `.tmp/mocks/tts-benchmark/index.html`, a folder holding the page and its clips that opens from the file system in any browser.

Clips are peak-normalised to -1 dBFS so the models compare at equal loudness. The timings in the table come from the raw synthesis, before normalising or FX.
