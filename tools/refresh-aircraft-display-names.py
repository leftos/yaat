#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""Generate src/Yaat.Sim/Data/aircraft-display-names.json from cached FAA ACD data.

Reads the most recent `faa-acd-NNNN.json` from the YAAT cache directory (or a
path supplied via --input) and emits a curated `{ICAO: "Display Name"}` JSON
for the Aircraft List "Name" column.

Cleanup applied:
  1. Title-case the first word if it's all-lowercase (e.g. "canadair" -> "Canadair").
  2. Strip bare parentheticals like " (Douglas)" -> "".
  3. Collapse runs of whitespace to a single space and trim.

The generated JSON is the source of truth from here on. Re-running this script
re-seeds it from FAA ACD and overwrites hand edits. Re-run only when intentionally
refreshing from a new ACD cycle.

Usage:
    uv run tools/refresh-aircraft-display-names.py                     # uses newest cache
    uv run tools/refresh-aircraft-display-names.py --input path.json   # explicit input
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = REPO_ROOT / "src" / "Yaat.Sim" / "Data"
OUT_JSON = OUT_DIR / "aircraft-display-names.json"
OUT_META = OUT_DIR / "aircraft-display-names-source.meta"

# Default cache locations on Windows / macOS / Linux.
DEFAULT_CACHE_DIRS = [
    Path(os.environ.get("LOCALAPPDATA", "")) / "yaat" / "cache" / "faa-acd",
    Path.home() / "Library" / "Application Support" / "yaat" / "cache" / "faa-acd",
    Path.home() / ".local" / "share" / "yaat" / "cache" / "faa-acd",
]


_PARENS_RE = re.compile(r"\s*\([^)]*\)\s*")
_WS_RE = re.compile(r"\s+")


def find_latest_cache() -> Path | None:
    """Return the newest faa-acd-*.json across known cache dirs, or None."""
    candidates: list[tuple[float, Path]] = []
    for d in DEFAULT_CACHE_DIRS:
        if not d.is_dir():
            continue
        for f in d.glob("faa-acd-*.json"):
            candidates.append((f.stat().st_mtime, f))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    return candidates[0][1]


def clean(name: str) -> str:
    """Apply the cleanup pipeline to one modelFaa string."""
    # Strip parentheticals before any other work (collapses surrounding spaces too).
    s = _PARENS_RE.sub(" ", name)
    s = _WS_RE.sub(" ", s).strip()
    if not s:
        return ""

    # Title-case the first word only if it's entirely lowercase letters.
    parts = s.split(" ", 1)
    head = parts[0]
    if head and head.isalpha() and head.islower():
        parts[0] = head.capitalize()
        s = " ".join(parts)

    return s


def extract(records: dict[str, dict]) -> dict[str, str]:
    """Pull modelFaa from each FAA ACD record, apply cleanup, drop empties."""
    out: dict[str, str] = {}
    for icao, rec in records.items():
        if not isinstance(rec, dict):
            continue
        raw = (rec.get("modelFaa") or "").strip()
        if not raw:
            continue
        cleaned = clean(raw)
        if cleaned:
            out[icao.upper()] = cleaned
    return dict(sorted(out.items()))


def write_outputs(rows: dict[str, str], source_path: Path) -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # Pretty-printed for legibility + diff-friendliness when hand-editing.
    OUT_JSON.write_text(
        json.dumps(rows, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    fetched = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    meta = (
        f"source: {source_path}\n"
        f"source_mtime: {dt.datetime.fromtimestamp(source_path.stat().st_mtime, dt.timezone.utc).isoformat()}\n"
        f"generated_utc: {fetched}\n"
        f"row_count: {len(rows)}\n"
        f"\n"
        f"# This JSON is the source of truth for the Aircraft List 'Name' column.\n"
        f"# Hand-edit entries directly. Re-running tools/refresh-aircraft-display-names.py\n"
        f"# re-seeds from FAA ACD and overwrites hand edits.\n"
    )
    OUT_META.write_text(meta, encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--input", type=Path, help="Path to a specific faa-acd-NNNN.json (otherwise uses newest in cache)")
    args = ap.parse_args()

    source = args.input or find_latest_cache()
    if source is None or not source.is_file():
        print("ERROR: no FAA ACD cache found. Run YAAT client/server once to populate, or pass --input.", file=sys.stderr)
        for d in DEFAULT_CACHE_DIRS:
            print(f"  Searched: {d}", file=sys.stderr)
        return 1

    print(f"Reading FAA ACD cache: {source}")
    records = json.loads(source.read_text(encoding="utf-8"))
    if not isinstance(records, dict):
        print(f"ERROR: expected JSON object at top level, got {type(records).__name__}", file=sys.stderr)
        return 1

    rows = extract(records)
    print(f"Extracted {len(rows)} display names from {len(records)} FAA ACD records")

    write_outputs(rows, source)
    print(f"Wrote {OUT_JSON} ({len(rows)} entries)")
    print(f"Wrote {OUT_META}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
