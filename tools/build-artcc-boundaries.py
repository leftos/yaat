#!/usr/bin/env python3
"""Build src/Yaat.Sim/Data/Artcc/ArtccBoundaries.geojson from the FAA NASR 28-day ARB (ARTCC boundary) subscriber files.

Usage:
    python tools/build-artcc-boundaries.py                  # current 28-day cycle, download from nfdc.faa.gov
    python tools/build-artcc-boundaries.py --cycle 2026-08-06
    python tools/build-artcc-boundaries.py --zip .tmp/nasr/arb.zip
    python tools/build-artcc-boundaries.py --strata LOW,HIGH --out path.geojson

Each US ARTCC / CERAP becomes one MultiPolygon feature with one ring per stratum (LOW and HIGH by default), so a
point is "inside the ARTCC" when it is inside either stratum — the union the room scope wants without needing a
polygon library. Facilities with no LOW/HIGH stratum (Honolulu, Guam, San Juan describe a single UNLIMITED volume)
fall back to that volume. Points flagged NAS-description-only (NAS_DESCRIP_FLAG = X) are kept: the NAS description is
the one ATC works to.
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import io
import json
import sys
import urllib.request
import zipfile
from pathlib import Path

CYCLE_ANCHOR = dt.date(2026, 8, 6)  # a known effective date; cycles are 28 days
ARB_URL = "https://nfdc.faa.gov/webContent/28DaySub/extra/{day:02d}_{mon}_{year}_ARB_CSV.zip"
DEFAULT_OUT = Path(__file__).resolve().parent.parent / "src" / "Yaat.Sim" / "Data" / "Artcc" / "ArtccBoundaries.geojson"
COUNTRY = "US"
LOCATION_TYPES = {"ARTCC", "CERAP"}


def current_cycle(today: dt.date) -> dt.date:
    """Latest 28-day cycle effective on or before today."""
    delta = (today - CYCLE_ANCHOR).days
    return CYCLE_ANCHOR + dt.timedelta(days=(delta // 28) * 28)


def cycle_url(cycle: dt.date) -> str:
    return ARB_URL.format(day=cycle.day, mon=cycle.strftime("%b"), year=cycle.year)


def load_zip(data: bytes) -> tuple[list[dict[str, str]], list[dict[str, str]]]:
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        base = list(csv.DictReader(io.TextIOWrapper(zf.open("ARB_BASE.csv"), encoding="utf-8")))
        seg = list(csv.DictReader(io.TextIOWrapper(zf.open("ARB_SEG.csv"), encoding="utf-8")))
    return base, seg


def build_features(base: list[dict[str, str]], seg: list[dict[str, str]], strata: list[str]) -> tuple[list[dict], str]:
    facilities = {
        r["LOCATION_ID"]: r for r in base if r["COUNTRY_CODE"] == COUNTRY and r["LOCATION_TYPE"] in LOCATION_TYPES
    }
    rings: dict[str, dict[str, list[tuple[int, float, float]]]] = {}
    effective = ""
    for r in seg:
        loc = r["LOCATION_ID"]
        if loc not in facilities:
            continue
        effective = effective or r["EFF_DATE"]
        rings.setdefault(loc, {}).setdefault(r["ALTITUDE"], []).append(
            (int(r["POINT_SEQ"]), float(r["LAT_DECIMAL"]), float(r["LONG_DECIMAL"]))
        )

    features = []
    for loc in sorted(facilities):
        by_stratum = rings.get(loc, {})
        chosen = [s for s in strata if s in by_stratum]
        if not chosen:
            chosen = sorted(by_stratum)  # e.g. a lone UNLIMITED volume
        if not chosen:
            print(f"  {loc}: no boundary segments, skipped", file=sys.stderr)
            continue
        polygons = []
        for stratum in chosen:
            pts = sorted(by_stratum[stratum])
            ring = [[round(lon, 6), round(lat, 6)] for _, lat, lon in pts]
            if ring[0] != ring[-1]:
                ring.append(ring[0])
            if len(ring) < 4:
                print(f"  {loc}/{stratum}: fewer than 3 points, skipped", file=sys.stderr)
                continue
            polygons.append([ring])
        if not polygons:
            continue
        features.append(
            {
                "type": "Feature",
                "properties": {"id": loc, "name": facilities[loc]["LOCATION_NAME"], "strata": chosen},
                "geometry": {"type": "MultiPolygon", "coordinates": polygons},
            }
        )
    return features, effective


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--cycle", type=dt.date.fromisoformat, default=None, help="NASR effective date (YYYY-MM-DD); default = current cycle")
    parser.add_argument("--zip", type=Path, default=None, help="use a downloaded *_ARB_CSV.zip instead of fetching")
    parser.add_argument("--strata", default="LOW,HIGH", help="comma-separated ALTITUDE strata to include as rings (default LOW,HIGH)")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()

    if args.zip:
        data = args.zip.read_bytes()
        source = str(args.zip)
    else:
        cycle = args.cycle or current_cycle(dt.date.today())
        url = cycle_url(cycle)
        print(f"downloading {url}", file=sys.stderr)
        with urllib.request.urlopen(url, timeout=60) as resp:
            data = resp.read()
        source = url

    base, seg = load_zip(data)
    strata = [s.strip().upper() for s in args.strata.split(",") if s.strip()]
    features, effective = build_features(base, seg, strata)
    doc = {
        "type": "FeatureCollection",
        "name": "ARTCC Boundaries",
        "source": f"FAA NASR ARB subscriber file, effective {effective} ({source}); built by tools/build-artcc-boundaries.py",
        "crs": {"type": "name", "properties": {"name": "urn:ogc:def:crs:OGC:1.3:CRS84"}},
        "features": features,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(doc, separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")
    points = sum(len(ring) for f in features for poly in f["geometry"]["coordinates"] for ring in poly)
    print(f"wrote {args.out}: {len(features)} facilities, {points} points, effective {effective}", file=sys.stderr)
    for f in features:
        print(f"  {f['properties']['id']:4} {'+'.join(f['properties']['strata']):<14} {f['properties']['name']}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
