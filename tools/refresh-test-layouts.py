"""Refresh the airport GeoJSON test fixtures from the vNAS data API.

Each ``tests/Yaat.Sim.Tests/TestData/<ID>.geojson`` whose stem is a plain 3-4 letter airport ID is re-fetched from
``https://data-api.vnas.vatsim.net/api/training/airports/{ID}/map`` (the endpoint ``AirportLayoutDownloader`` uses)
and rewritten with LF line endings, no trailing spaces and one trailing newline. Stems with a ``-`` or a prefix (``issue172-sfo``,
``sfo-b1short``) are deliberate snapshots and are never touched. An airport the API has no map for is kept as committed.

Usage:
    python tools/refresh-test-layouts.py           # rewrite fixtures that changed
    python tools/refresh-test-layouts.py --check   # report what would change; exit 1 if anything would
"""

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TEST_DATA = REPO_ROOT / "tests" / "Yaat.Sim.Tests" / "TestData"
MAP_URL = "https://data-api.vnas.vatsim.net/api/training/airports/{airport}/map"
USER_AGENT = "yaat-refresh-test-layouts/1.0"
AIRPORT_STEM = re.compile(r"^[A-Za-z]{3,4}$")
LINE_COMMENT = re.compile(r"^[ \t]*//.*$", re.MULTILINE)


def strip_line_comments(body: bytes) -> str:
    """Drop whole-line ``//`` comments so the map validates as JSON.

    CMH's map carries JavaScript-style ``///Parking///`` header lines inside its feature array. Only the
    validation copy is stripped; the raw body is what gets written.
    """
    return LINE_COMMENT.sub("", body.decode("utf-8"))


def fetch_map(airport: str) -> bytes | None:
    """Fetch one airport's ground map, or None when the API has none or the request fails."""
    url = MAP_URL.format(airport=airport.upper())
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = resp.read()
    except (urllib.error.URLError, TimeoutError) as exc:
        print(f"  {airport}: fetch failed: {exc}", file=sys.stderr)
        return None
    try:
        doc = json.loads(strip_line_comments(body))
    except json.JSONDecodeError as exc:
        print(f"  {airport}: response is not JSON: {exc}", file=sys.stderr)
        return None
    if not isinstance(doc, dict) or "features" not in doc:
        print(f"  {airport}: response is not a GeoJSON FeatureCollection", file=sys.stderr)
        return None
    return body


def normalize(body: bytes) -> bytes:
    """Match what the whitespace pre-commit hook commits: LF, no trailing spaces, exactly one trailing newline.

    Without this a fixture would report a change on every run for whitespace the hook strips again on commit.
    Trimming line ends is safe for JSON: a string literal cannot span a raw newline.
    """
    lines = body.replace(b"\r\n", b"\n").split(b"\n")
    return b"\n".join(line.rstrip() for line in lines).rstrip(b"\n") + b"\n"


def refresh(path: Path, check: bool) -> tuple[str, bool]:
    """Refresh one fixture; return its status line and whether it changed (or would change)."""
    if not AIRPORT_STEM.match(path.stem):
        return "snapshot, skipped", False
    body = fetch_map(path.stem)
    if body is None:
        return "no vNAS map, kept", False
    fresh = normalize(body)
    if fresh == path.read_bytes():
        return "same", False
    if not check:
        path.write_bytes(fresh)
    return ("would update" if check else "updated"), True


def main() -> int:
    parser = argparse.ArgumentParser(description="Refresh TestData airport GeoJSONs from the vNAS data API.")
    parser.add_argument("--check", action="store_true", help="report what would change and exit 1 if anything would")
    args = parser.parse_args()

    changed = 0
    for path in sorted(TEST_DATA.glob("*.geojson"), key=lambda p: p.name.lower()):
        status, did_change = refresh(path, args.check)
        print(f"{path.name}: {status}")
        changed += int(did_change)

    if args.check and changed:
        print(f"{changed} fixture(s) would change")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
