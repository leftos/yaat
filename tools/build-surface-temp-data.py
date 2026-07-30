#!/usr/bin/env python3
"""Generate ASDE-X / SAAB SAID final-approach overlays as ARTCC sidecar JSON.

Draws each requested runway's extended centerline outbound from the landing threshold, with a
short perpendicular mark across it at every whole mile — reproducing the geometry vZOA
controllers hand-draw on SFO's ASDE-X so they can confirm which of the closely-spaced parallels
an arrival is lined up on (GitHub issue #312).

The mark shape comes from the reporter's screenshot, measured against its own scale (the 750 ft
between SFO's parallels spans 22 px): each runway carries its own mark, centred on that runway's
centerline and crossing it on both sides, leaving part of the channel between the two runways
unmarked. The two runways' marks never join into a single bar, and there is no text.

Runway ends come from the vNAS airport map GeoJSON, so the centerline matches the surface
map the controller is looking at. Bearings are TRUE, because the output is lat/lon.

Writes src/Yaat.Sim/Data/ARTCCs/{ARTCC}/SurfaceTempData/{FACILITY}.json. Schema and
semantics: src/Yaat.Sim/Data/ARTCCs/README.md.

An airport draws one group per flow. Build the first, then --append the rest into the same file,
each in its own SET so a controller can show only the flow in use:

    python tools/build-surface-temp-data.py --artcc ZOA --facility SFO \\
        --runways 28L 28R --set 1 --set-name 28FINAL
    python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --append \\
        --runways 10L 10R --set 2 --set-name 10FINAL --length 2.5
    python tools/build-surface-temp-data.py --artcc ZOA --facility SFO --append \\
        --runways 19L --set 3 --set-name 19FINAL --length 2.5

    # Preview without writing:
    python tools/build-surface-temp-data.py --artcc ZOA --facility SFO \\
        --runways 28L 28R --dry-run
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import urllib.error
import urllib.request
from pathlib import Path

MAP_URL = "https://data-api.vnas.vatsim.net/api/training/airports/{airport}/map"
REPO_ROOT = Path(__file__).resolve().parent.parent
ARTCC_DATA = REPO_ROOT / "src" / "Yaat.Sim" / "Data" / "ARTCCs"
LOCAL_GEOJSON_FALLBACK = REPO_ROOT / "tests" / "Yaat.Sim.Tests" / "TestData"

EARTH_RADIUS_NM = 3440.065
FEET_PER_NM = 6076.115

# Defaults reviewed against FAA 7110.65 / AIM; rationale in docs/crc-display-state.md.
#
# 7 nm: the approach gate — the outermost point where "is this aircraft on the correct
# centerline?" is still open — sits ~1 nm outside the FAF and no closer than 5 nm from the
# threshold (PCG "approach gate"), which puts it near 6.5-7 nm for a 3-degree final at SFO.
# Vectors to intercept are issued at least 2 nm outside that (7110.65 5-9-1.a.1), so
# everything local control reads happens inside it. Drawing further just adds clutter.
#
# Mark length 0.09 nm (547 ft), centred on the runway's own centerline. Measured off the
# reference screenshot, where each runway's mark runs 545-648 ft and leaves 136-307 ft of the
# 750 ft channel between the parallels unmarked. Long enough to read as a distance ladder,
# short enough that the two runways' marks stay visibly separate — which is what lets a
# controller tell the parallels apart in the first place.
DEFAULT_LENGTH_NM = 7.0
DEFAULT_TICK_INTERVAL_NM = 1.0
DEFAULT_TICK_LENGTH_NM = 0.09


def load_geojson(airport: str, source: Path | None) -> dict:
    """Airport map GeoJSON, from an explicit file or the vNAS training API."""
    if source is not None:
        return json.loads(source.read_text(encoding="utf-8"))

    local = LOCAL_GEOJSON_FALLBACK / f"{airport.lower()}.geojson"
    if local.exists():
        print(f"Using bundled {local.relative_to(REPO_ROOT)}", file=sys.stderr)
        return json.loads(local.read_text(encoding="utf-8"))

    url = MAP_URL.format(airport=airport.upper())
    print(f"Fetching {url}", file=sys.stderr)
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        raise SystemExit(f"Could not fetch the airport map for {airport}: {exc}") from exc


class Runway:
    """One runway end's landing threshold and the true course flown onto it."""

    def __init__(self, designator: str, threshold: tuple[float, float], landing_course: float):
        self.designator = designator
        self.threshold = threshold
        self.landing_course = landing_course

    @property
    def outbound(self) -> float:
        """Bearing from the threshold out along the final approach course."""
        return (self.landing_course + 180.0) % 360.0


def resolve_runway(geojson: dict, designator: str) -> Runway:
    """The landing threshold of `designator` and its TRUE landing course.

    vNAS encodes a runway as one LineString named "10L - 28R", running from the first
    designator's end to the second's, with a matching `threshold` property ("0 - 300")
    giving each end's displacement in feet. The LineString endpoints are PAVEMENT ends —
    verified against the CIFP, which places KSFO 28L/28R's landing thresholds ~300 ft
    downfield of them while 10L/10R match to within 10 ft. Final-approach geometry has to
    start at the landing threshold, so the displacement is applied here.

    The course is derived from this runway's own two endpoints rather than shared across a
    parallel pair: at 7 nm, 0.1 degrees of error is 74 ft, a tenth of SFO's runway spacing.
    """
    wanted = designator.upper()
    for feature in geojson.get("features", []):
        props = feature.get("properties", {})
        if props.get("type") != "runway":
            continue

        parts = [part.strip().upper() for part in str(props.get("name", "")).split("-")]
        if wanted not in parts:
            continue

        coords = feature["geometry"]["coordinates"]
        first = (coords[0][1], coords[0][0])
        last = (coords[-1][1], coords[-1][0])

        # coords run first-designator-end -> second-designator-end.
        index = parts.index(wanted)
        pavement_end, far_end = (first, last) if index == 0 else (last, first)
        landing_course = bearing(pavement_end, far_end)

        displacement_ft = parse_displacement(props.get("threshold"), index)
        threshold = pavement_end
        if displacement_ft > 0:
            threshold = project(pavement_end, landing_course, displacement_ft / FEET_PER_NM)

        return Runway(wanted, threshold, landing_course)

    raise SystemExit(f"No runway named '{designator}' in the airport map")


def parse_displacement(raw: object, index: int) -> float:
    """Displaced-threshold distance in feet for one end, from the `threshold` property."""
    if not isinstance(raw, str):
        return 0.0
    parts = [part.strip() for part in raw.split("-")]
    if index >= len(parts):
        return 0.0
    try:
        return float(parts[index])
    except ValueError:
        return 0.0


def rounded(point: tuple[float, float]) -> list[float]:
    """Coordinate pair at the precision the sidecar stores (~0.1 m)."""
    return [round(point[0], 6), round(point[1], 6)]


def bearing(origin: tuple[float, float], target: tuple[float, float]) -> float:
    """Initial TRUE bearing from origin to target, degrees."""
    lat1, lon1 = math.radians(origin[0]), math.radians(origin[1])
    lat2, lon2 = math.radians(target[0]), math.radians(target[1])
    delta_lon = lon2 - lon1
    y = math.sin(delta_lon) * math.cos(lat2)
    x = math.cos(lat1) * math.sin(lat2) - math.sin(lat1) * math.cos(lat2) * math.cos(delta_lon)
    return (math.degrees(math.atan2(y, x)) + 360.0) % 360.0


def project(origin: tuple[float, float], true_bearing: float, distance_nm: float) -> tuple[float, float]:
    """Great-circle point `distance_nm` from origin along `true_bearing`."""
    angular = distance_nm / EARTH_RADIUS_NM
    lat1, lon1 = math.radians(origin[0]), math.radians(origin[1])
    brg = math.radians(true_bearing)

    lat2 = math.asin(math.sin(lat1) * math.cos(angular) + math.cos(lat1) * math.sin(angular) * math.cos(brg))
    lon2 = lon1 + math.atan2(
        math.sin(brg) * math.sin(angular) * math.cos(lat1),
        math.cos(angular) - math.sin(lat1) * math.sin(lat2),
    )
    return (round(math.degrees(lat2), 6), round(math.degrees(lon2), 6))


def build_runway(runway: Runway, args: argparse.Namespace, preset_id: int | None) -> list[dict]:
    """Centerline plus a whole-mile mark across it, as sidecar item dicts."""
    outbound = runway.outbound
    slug = f"{args.facility.lower()}-{runway.designator.lower()}"
    half = args.tick_length / 2.0
    left = (outbound - 90.0) % 360.0
    right = (outbound + 90.0) % 360.0

    far = project(runway.threshold, outbound, args.length)
    items: list[dict] = [
        {
            "id": f"{slug}-final",
            "presetId": preset_id,
            "type": "RestrictedArea",
            # CRC closes the ring itself, so an out-and-back point list renders as a line.
            "area": [rounded(runway.threshold), rounded(far), rounded(runway.threshold)],
        }
    ]

    distance = args.tick_interval
    while distance <= args.length + 1e-9:
        center = project(runway.threshold, outbound, distance)
        # Centred on this runway's own centerline, crossing it on both sides. The mark belongs to
        # one runway: it must stay clear of the parallel so the two never merge into one bar.
        a = project(center, left, half)
        b = project(center, right, half)
        items.append(
            {
                "id": f"{slug}-tick-{distance:g}",
                "presetId": preset_id,
                "type": "RestrictedArea",
                "area": [rounded(a), rounded(b), rounded(a)],
            }
        )
        distance += args.tick_interval

    print(
        f"{runway.designator}: threshold {runway.threshold[0]:.6f},{runway.threshold[1]:.6f} "
        f"landing {runway.landing_course:.2f}T, drawn outbound {outbound:.2f}T for {args.length:g} nm, "
        f"marks every {args.tick_interval:g} nm ({args.tick_length * FEET_PER_NM:.0f} ft across)",
        file=sys.stderr,
    )
    return items


def format_sidecar(document: dict) -> str:
    """Indented JSON, but with each [lat, lon] pair kept on one line.

    A 10 nm overlay is ~40 objects; fully-expanded coordinate arrays make the committed
    file unreviewable in a diff.
    """
    placeholders: dict[str, str] = {}

    def swap(node):
        if isinstance(node, list) and len(node) == 2 and all(isinstance(v, float) for v in node):
            key = f"__coord_{len(placeholders)}__"
            placeholders[key] = json.dumps(node)
            return key
        if isinstance(node, list):
            return [swap(v) for v in node]
        if isinstance(node, dict):
            return {k: swap(v) for k, v in node.items()}
        return node

    text = json.dumps(swap(document), indent=2)
    for key, literal in placeholders.items():
        text = text.replace(f'"{key}"', literal)
    return text


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--artcc", required=True, help="ARTCC id, e.g. ZOA")
    parser.add_argument("--facility", required=True, help="vNAS facility id owning the display, e.g. SFO")
    parser.add_argument("--airport", help="Airport whose map to read (defaults to --facility)")
    parser.add_argument("--runways", nargs="+", required=True, help="Runway designators, e.g. 28L 28R")
    parser.add_argument("--geojson", type=Path, help="Read the airport map from this file instead of the vNAS API")
    parser.add_argument("--length", type=float, default=DEFAULT_LENGTH_NM, help="Centerline length from the threshold, nm")
    parser.add_argument("--tick-interval", type=float, default=DEFAULT_TICK_INTERVAL_NM, help="Distance between tick marks, nm")
    parser.add_argument("--tick-length", type=float, default=DEFAULT_TICK_LENGTH_NM, help="Total mark length across the centerline, nm")
    parser.add_argument("--set", type=int, dest="preset_id", help="File everything into this SET (1-88) so it can be toggled")
    parser.add_argument("--set-name", default="28FINAL", help="SET label, max 7 characters. Name the flow — the overlay is meaningless on the reciprocal")
    parser.add_argument("--system", choices=["asdex", "said"], default="asdex", help="Which surface display the geometry belongs to")
    parser.add_argument("--append", action="store_true", help="Merge into the facility's existing file instead of replacing it, for a second flow's group")
    parser.add_argument("--dry-run", action="store_true", help="Print the JSON instead of writing it")
    args = parser.parse_args()

    if args.preset_id is not None and not 1 <= args.preset_id <= 88:
        raise SystemExit("--set must be between 1 and 88 (CRC's SET range)")
    if len(args.set_name) > 7:
        raise SystemExit("--set-name must be at most 7 characters (CRC's limit)")

    airport = args.airport or args.facility
    geojson = load_geojson(airport, args.geojson)

    items: list[dict] = []
    for designator in args.runways:
        items.extend(build_runway(resolve_runway(geojson, designator), args, args.preset_id))

    presets: list[dict] = []
    if args.preset_id is not None:
        presets.append({"id": args.preset_id, "dataId": items[0]["id"], "name": args.set_name, "active": True})

    out_dir = ARTCC_DATA / args.artcc.upper() / "SurfaceTempData"
    out_path = out_dir / f"{args.facility.upper()}.json"

    document: dict = {}
    if args.append and out_path.exists():
        document = json.loads(out_path.read_text(encoding="utf-8"))

    section = document.setdefault(args.system, {"items": [], "presets": []})
    section.setdefault("items", [])
    section.setdefault("presets", [])

    # Re-running one group must replace that group's objects, not duplicate them.
    new_ids = {item["id"] for item in items}
    section["items"] = [item for item in section["items"] if item["id"] not in new_ids] + items
    if presets:
        new_set_ids = {preset["id"] for preset in presets}
        section["presets"] = [p for p in section["presets"] if p["id"] not in new_set_ids] + presets

    payload = format_sidecar(document)
    if args.dry_run:
        print(payload)
        return 0

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path.write_text(payload + "\n", encoding="utf-8")
    print(
        f"Wrote {len(items)} objects ({len(section['items'])} in the file) to {out_path.relative_to(REPO_ROOT)}",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
