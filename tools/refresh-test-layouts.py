"""Refresh the airport GeoJSON test fixtures from the vNAS data API.

Each ``tests/Yaat.Sim.Tests/TestData/<ID>.geojson`` whose stem is a plain 3-4 letter airport ID is re-fetched from
``https://data-api.vnas.vatsim.net/api/training/airports/{ID}/map`` (the endpoint ``AirportLayoutDownloader`` uses)
and rewritten with LF line endings, no trailing spaces and one trailing newline. Stems with a ``-`` or a prefix (``issue172-sfo``,
``sfo-b1short``) are deliberate snapshots and are never touched. An airport the API has no map for is kept as committed.

Each ``tests/Yaat.Sim.Tests/TestData/TowerCabMaps/<id>.geojson`` is the airport's tower-cab videomap: the ARTCC listed for it in
``TOWER_CAB_ARTCC`` is fetched from ``https://data-api.vnas.vatsim.net/api/artccs/{ARTCC}``, the facility whose id is the stem
upper-cased names its ``towerCabConfiguration.videoMapId``, and that map is re-fetched from the vNAS videomap files and written
normalised the same way. A fixture with no ARTCC listed, or whose map cannot be found or fetched, or whose response is not
a GeoJSON FeatureCollection, is kept as committed.

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
TOWER_CAB_DIR = TEST_DATA / "TowerCabMaps"
ARTCC_URL = "https://data-api.vnas.vatsim.net/api/artccs/{artcc}"
VIDEO_MAP_URL = "https://data-api.vnas.vatsim.net/Files/VideoMaps/{artcc}/{map_id}.geojson"
# The ARTCC whose config names each tower-cab fixture airport's tower-cab map.
TOWER_CAB_ARTCC = {"oak": "ZOA"}


def strip_line_comments(body: bytes) -> str:
    """Drop whole-line ``//`` comments so the map validates as JSON.

    CMH's map carries JavaScript-style ``///Parking///`` header lines inside its feature array. Only the
    validation copy is stripped; the raw body is what gets written.
    """
    return LINE_COMMENT.sub("", body.decode("utf-8"))


def is_feature_collection(body: bytes, label: str) -> bool:
    """True when ``body`` parses as a GeoJSON FeatureCollection, printing why when it does not."""
    try:
        doc = json.loads(strip_line_comments(body))
    except json.JSONDecodeError as exc:
        print(f"  {label}: response is not JSON: {exc}", file=sys.stderr)
        return False
    if not isinstance(doc, dict) or "features" not in doc:
        print(f"  {label}: response is not a GeoJSON FeatureCollection", file=sys.stderr)
        return False
    return True


def fetch_map(airport: str) -> bytes | None:
    """Fetch one airport's ground map, or None when the API has none or the request fails."""
    body = fetch_bytes(MAP_URL.format(airport=airport.upper()), airport)
    return body if (body is not None) and is_feature_collection(body, airport) else None


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


def fetch_bytes(url: str, label: str) -> bytes | None:
    """Fetch a URL's body, or None when the request fails."""
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return resp.read()
    except (urllib.error.URLError, TimeoutError) as exc:
        print(f"  {label}: fetch failed: {exc}", file=sys.stderr)
        return None


def find_facility(facility: dict, facility_id: str) -> dict | None:
    """The facility with ``facility_id``, searched depth-first through ``childFacilities`` from ``facility``."""
    if facility.get("id") == facility_id:
        return facility
    for child in facility.get("childFacilities") or []:
        found = find_facility(child, facility_id)
        if found is not None:
            return found
    return None


def tower_cab_map_id(artcc: str, airport: str) -> str | None:
    """The videomap id the ARTCC config gives ``airport``'s tower cab, or None when it has none or the lookup fails."""
    body = fetch_bytes(ARTCC_URL.format(artcc=artcc), artcc)
    if body is None:
        return None
    try:
        config = json.loads(body)
    except json.JSONDecodeError as exc:
        print(f"  {artcc}: response is not JSON: {exc}", file=sys.stderr)
        return None
    facility = find_facility(config.get("facility") or {}, airport.upper())
    if facility is None:
        return None
    return (facility.get("towerCabConfiguration") or {}).get("videoMapId")


def refresh_tower_cab(path: Path, check: bool) -> tuple[str, bool]:
    """Refresh one tower-cab videomap fixture; return its status line and whether it changed (or would change)."""
    artcc = TOWER_CAB_ARTCC.get(path.stem)
    if artcc is None:
        return "no ARTCC known, kept", False
    map_id = tower_cab_map_id(artcc, path.stem)
    body = None if map_id is None else fetch_bytes(VIDEO_MAP_URL.format(artcc=artcc, map_id=map_id), path.stem)
    if body is None:
        return "no tower-cab map, kept", False
    if not is_feature_collection(body, path.stem):
        return "not a FeatureCollection, kept", False
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

    for path in sorted(TOWER_CAB_DIR.glob("*.geojson")):
        status, did_change = refresh_tower_cab(path, args.check)
        print(f"TowerCabMaps/{path.name}: {status}")
        changed += int(did_change)

    if args.check and changed:
        print(f"{changed} fixture(s) would change")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
