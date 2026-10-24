"""Kitten-TTS 2 half of the sandbox's --tts-bench mode.

Kitten-TTS 2 runs on KittenML's llama.cpp fork (the ``kitten_tts_cpp`` package), not on
sherpa-onnx, so the C# runner hands it the rendered readback lines and reads back the WAVs and a
timings JSON. Run by the runner as
``uv run --no-project --with kitten_tts_cpp python -I kitten2_bench.py --lines <json> --out-dir <dir>
--timings <json> --voices Bruno,Jasper,Hugo --threads 2 --cache-dir <dir>``.

The lines file is a JSON array of strings. Each clip lands at ``<out-dir>/<voice>/<nn>.wav``
(mono 16-bit PCM at the model's rate, ``nn`` 1-based and two digits). The timings JSON holds the
load time, the warm-up time and, per clip, the first-audio latency (first streamed chunk, null
when no chunk carried samples), the total synthesis time and the audio duration. The model's
weights are cached under ``--cache-dir``.
"""

import argparse
import json
import pathlib
import sys
import time
import wave

WARMUP_TEXT = "Radio check, how do you read."


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lines", required=True, type=pathlib.Path)
    parser.add_argument("--out-dir", required=True, type=pathlib.Path)
    parser.add_argument("--timings", required=True, type=pathlib.Path)
    parser.add_argument("--voices", required=True)
    parser.add_argument("--threads", required=True, type=int)
    parser.add_argument("--cache-dir", required=True, type=pathlib.Path)
    parser.add_argument("--decoder", default="student_w4")
    return parser.parse_args(argv)


def load_model(threads, decoder, cache_dir):
    """Returns the kitten_tts_cpp model, its weights cached under ``cache_dir``."""
    from kitten_tts_cpp import KittenTTS  # noqa: PLC0415 -- imported inside the timed load, so loadSeconds counts the package import

    return KittenTTS("KittenML/kitten-tts-2", decoder=decoder, threads=threads, cache_dir=str(cache_dir))


def synthesize(model, text, voice):
    """Streams one line; returns (samples as a list of float arrays, first-chunk seconds or None, total seconds)."""
    chunks = []
    first = None
    start = time.perf_counter()
    for chunk in model.generate_stream(text, voice=voice):
        if first is None and len(chunk) > 0:
            first = time.perf_counter() - start
        chunks.append(chunk)
    total = time.perf_counter() - start
    return chunks, first, total


def write_wav(path, chunks, sample_rate):
    path.parent.mkdir(parents=True, exist_ok=True)
    frames = bytearray()
    for chunk in chunks:
        for value in chunk:
            clipped = max(-1.0, min(1.0, float(value)))
            frames += int(clipped * 32767).to_bytes(2, "little", signed=True)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(sample_rate)
        out.writeframes(bytes(frames))
    return len(frames) // 2


def main(argv):
    args = parse_args(argv)
    lines = json.loads(args.lines.read_text(encoding="utf-8"))
    voices = [v for v in args.voices.split(",") if v]

    load_start = time.perf_counter()
    model = load_model(args.threads, args.decoder, args.cache_dir)
    load_seconds = time.perf_counter() - load_start

    missing = [v for v in voices if v not in model.available_voices]
    if missing:
        print(f"unknown voices {missing}; available: {model.available_voices}", file=sys.stderr)
        return 2

    _, _, warmup_seconds = synthesize(model, WARMUP_TEXT, voices[0])
    clips = []
    for voice in voices:
        for index, text in enumerate(lines, start=1):
            chunks, first, total = synthesize(model, text, voice)
            samples = write_wav(args.out_dir / voice / f"{index:02d}.wav", chunks, model.sample_rate)
            clips.append(
                {
                    "voice": voice,
                    "line": index,
                    "firstAudioSeconds": first,
                    "synthSeconds": total,
                    "audioSeconds": samples / model.sample_rate,
                }
            )
            print(f"{voice} {index:02d}: {total:.2f}s synth, {samples / model.sample_rate:.2f}s audio", flush=True)

    timings = {
        "package": "kitten_tts_cpp",
        "decoder": args.decoder,
        "threads": args.threads,
        "sampleRate": model.sample_rate,
        "loadSeconds": load_seconds,
        "warmupSeconds": warmup_seconds,
        "clips": clips,
    }
    args.timings.parent.mkdir(parents=True, exist_ok=True)
    with args.timings.open("w", encoding="utf-8", newline="\n") as out:
        json.dump(timings, out, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
