"""Refresh the airport GeoJSON test fixtures from the vNAS data API.

Each ``tests/Yaat.Sim.Tests/TestData/<ID>.geojson`` whose stem is a plain 3-4 letter airport ID is re-fetched from
``https://data-api.vnas.vatsim.net/api/training/airports/{ID}/map`` (the endpoint ``AirportLayoutDownloader`` uses)
and rewritten with LF line endings, no trailing spaces and one trailing newline. Stems with a ``-`` or a prefix (``issue172-sfo``,
``sfo-b1short``) are deliberate snapshots and are never touched. An airport the API has no map for is kept as committed.

``--all`` adds every airport listed in ``tests/Yaat.Sim.Tests/TestData/layout-airports.txt`` (the vNAS training airports
whose map has a taxi graph) that has no fixture yet, as ``TestData/<ID>.geojson``. It never rewrites a committed fixture
unless ``--force`` is also given, and it skips the committed-fixture and tower-cab passes. ``--check`` also names each
listed airport that has no fixture, and counts it as a change.

A map is accepted when it parses the way ``GeoJsonParser`` reads it: ``//`` and ``/* */`` comments, trailing commas and
leading zeros on numbers are allowed. Only the validation copy is cleaned; the bytes as served are what gets written.

Each ``tests/Yaat.Sim.Tests/TestData/TowerCabMaps/<id>.geojson`` is the airport's tower-cab videomap: the ARTCC listed for it in
``TOWER_CAB_ARTCC`` is fetched from ``https://data-api.vnas.vatsim.net/api/artccs/{ARTCC}``, the facility whose id is the stem
upper-cased names its ``towerCabConfiguration.videoMapId``, and that map is re-fetched from the vNAS videomap files and written
normalised the same way. A fixture with no ARTCC listed, or whose map cannot be found or fetched, or whose response is not
a GeoJSON FeatureCollection, is kept as committed.

Usage:
    python tools/refresh-test-layouts.py           # rewrite fixtures that changed
    python tools/refresh-test-layouts.py --check   # report what would change or is missing; exit 1 if anything would
    python tools/refresh-test-layouts.py --all     # add a fixture for every listed airport that has none
    python tools/refresh-test-layouts.py --all --force   # also rewrite the listed airports' committed fixtures
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
LAYOUT_LIST = TEST_DATA / "layout-airports.txt"
# Every listed airport has a vNAS taxi map, so a listed id whose map cannot be fetched is a failure, not a missing map.
FETCH_FAILED = "fetch failed, not added"
# GeoJsonParser.LeadingZeroRegex: a number literal with leading zeros (BFI's "heading": 050), which it strips before parsing.
LEADING_ZERO = re.compile(r"([:,\[]\s*)0+(\d)")
TOWER_CAB_DIR = TEST_DATA / "TowerCabMaps"
ARTCC_URL = "https://data-api.vnas.vatsim.net/api/artccs/{artcc}"
VIDEO_MAP_URL = "https://data-api.vnas.vatsim.net/Files/VideoMaps/{artcc}/{map_id}.geojson"
# The ARTCC whose config names each tower-cab fixture airport's tower-cab map.
TOWER_CAB_ARTCC = {"oak": "ZOA"}


def strip_lenient_json(text: str) -> str:
    """Drop comments and trailing commas outside string literals, so a map ``GeoJsonParser`` reads validates as JSON.

    ``GeoJsonParser`` reads with comments skipped and trailing commas allowed: CMH's map carries ``///Parking///`` header
    lines inside its feature array, and several maps (DEN, BFI, ...) end an array or object with a comma. Only the
    validation copy is stripped; the raw body is what gets written. Leading zeros on numbers are stripped first, as
    ``GeoJsonParser.SanitizeJson`` does. A trailing comma is dropped when its closing bracket or brace is reached, so
    a comment between the two (``[1, // c`` then ``]``) does not hide it.
    """
    text = LEADING_ZERO.sub(r"\1\2", text)
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = string_end(text, i)
            out.append(text[i:j])
            i = j
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        else:
            if c in "]}":
                drop_trailing_comma(out)
            out.append(c)
            i += 1
    return "".join(out)


def string_end(text: str, start: int) -> int:
    """The index just past the string literal that opens at ``start``."""
    j = start + 1
    while (j < len(text)) and (text[j] != '"'):
        j += 2 if text[j] == "\\" else 1
    return j + 1


def drop_trailing_comma(out: list[str]) -> None:
    """Before a closing bracket or brace: drop trailing whitespace from ``out``, then a comma if one is last."""
    while out and out[-1].isspace():
        out.pop()
    if out and (out[-1] == ","):
        out.pop()


def is_feature_collection(body: bytes, label: str) -> bool:
    """True when ``body`` parses as a GeoJSON FeatureCollection, printing why when it does not."""
    try:
        doc = json.loads(strip_lenient_json(body.decode("utf-8-sig")))
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


def read_airport_list() -> list[str]:
    """The FAA ids in ``layout-airports.txt``, skipping ``#`` lines and blanks."""
    lines = LAYOUT_LIST.read_text(encoding="utf-8").splitlines()
    return [line.strip().upper() for line in lines if line.strip() and not line.lstrip().startswith("#")]


def committed_fixture(airport: str) -> Path | None:
    """The committed ``TestData`` fixture for ``airport``, its file name matched case-insensitively, or None."""
    for path in TEST_DATA.glob("*.geojson"):
        if path.stem.upper() == airport:
            return path
    return None


def add_listed(airport: str, check: bool, force: bool) -> tuple[str, bool]:
    """Add (or, with ``force``, refresh) one listed airport's fixture; return its status line and whether it changed."""
    existing = committed_fixture(airport)
    if existing is not None:
        return refresh(existing, check) if force else ("committed, kept (pass --force to refresh)", False)
    if check:
        return "would add", True
    body = fetch_map(airport)
    if body is None:
        return FETCH_FAILED, False
    (TEST_DATA / f"{airport}.geojson").write_bytes(normalize(body))
    return "added", True


def report_missing_listed() -> int:
    """Print each listed airport with no committed fixture; return how many there are."""
    missing = [airport for airport in read_airport_list() if committed_fixture(airport) is None]
    for airport in missing:
        print(f"{airport}: listed in {LAYOUT_LIST.name}, missing from TestData")
    return len(missing)


def add_all(check: bool, force: bool) -> int:
    """The ``--all`` pass over ``layout-airports.txt``; exit 1 when a listed map was not added, or under ``check`` when
    anything would change."""
    changed = 0
    failed = 0
    for airport in read_airport_list():
        status, did_change = add_listed(airport, check, force)
        print(f"{airport}: {status}")
        changed += int(did_change)
        failed += int(status == FETCH_FAILED)
    if failed:
        print(f"{failed} listed airport(s) not added")
        return 1
    if check and changed:
        print(f"{changed} fixture(s) would change")
        return 1
    return 0


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
    parser.add_argument("--all", action="store_true", help=f"add a fixture for every airport in {LAYOUT_LIST.name} that has none")
    parser.add_argument("--force", action="store_true", help="with --all, also refresh the listed airports' committed fixtures")
    args = parser.parse_args()
    if args.force and not args.all:
        parser.error("--force requires --all")

    if args.all:
        return add_all(args.check, args.force)

    changed = 0
    for path in sorted(TEST_DATA.glob("*.geojson"), key=lambda p: p.name.lower()):
        status, did_change = refresh(path, args.check)
        print(f"{path.name}: {status}")
        changed += int(did_change)

    for path in sorted(TOWER_CAB_DIR.glob("*.geojson")):
        status, did_change = refresh_tower_cab(path, args.check)
        print(f"TowerCabMaps/{path.name}: {status}")
        changed += int(did_change)

    if args.check:
        changed += report_missing_listed()

    if args.check and changed:
        print(f"{changed} fixture(s) would change or are missing")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
