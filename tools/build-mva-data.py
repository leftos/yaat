# /// script
# requires-python = ">=3.11"
# dependencies = ["folium", "brotli"]
# ///
"""Build YAAT's committed MVA sector data from FAA authoritative AIXM 5.1 charts.

The FAA publishes Minimum Vectoring Altitude charts (AJV-A certified) as AIXM 5.1 XML at
https://aeronav.faa.gov/MVA_Charts/aixm/{FACILITY}_MVA_{FUS3|FUS5}.xml -- each `Airspace`
member carries a single MSL `minimumLimit` (the MVA floor) and a CRS84 (lon,lat) polygon
with an exterior ring plus optional interior rings (holes around higher-floor islands).

This tool parses that XML into a GeoJSON FeatureCollection (one Polygon feature per sector,
`properties.mvaFloorFt`) that YAAT's `MvaDatabase` loads at runtime, mirroring how the
Class B/C airspace fixture is produced from FAA GIS data.

Usage:
    uv run tools/build-mva-data.py --facility NCT --variant FUS3
    uv run tools/build-mva-data.py --input .tmp/NCT_MVA_FUS3.xml          # offline single facility
    uv run tools/build-mva-data.py --facility NCT --overlay .tmp/nct.html  # visual check
    uv run tools/build-mva-data.py --all                                   # every FAA FUS3 facility -> merged Brotli
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

GML = "{http://www.opengis.net/gml/3.2}"
AIXM = "{http://www.aixm.aero/schema/5.1}"
AIXM_URL = "https://aeronav.faa.gov/MVA_Charts/aixm/{facility}_MVA_{variant}.xml"
# FAA listing page that links every facility's MVA chart XML.
LISTING_URL = "https://www.faa.gov/air_traffic/flight_info/aeronav/digital_products/mva_mia/mva/"
REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT_DIR = REPO_ROOT / "src" / "Yaat.Sim" / "Data" / "Mva"

# Sanity window covering CONUS + Alaska + Hawaii + Pacific (Guam ~ +144 lon); catches gross errors only.
LON_RANGE = (-180.0, 180.0)
LAT_RANGE = (0.0, 90.0)
FLOOR_RANGE = (1000, 18000)


def fetch_xml(facility: str, variant: str, input_path: str | None, url: str | None) -> bytes:
    if input_path:
        return Path(input_path).read_bytes()
    target = url or AIXM_URL.format(facility=facility, variant=variant)
    print(f"  downloading {target}", file=sys.stderr)
    req = urllib.request.Request(target, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=120) as resp:  # noqa: S310 (trusted FAA host)
        return resp.read()


def fetch_text(url: str) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=120) as resp:  # noqa: S310 (trusted FAA host)
        return resp.read().decode("utf-8", errors="replace")


def list_facilities(variant: str, list_input: str | None, list_url: str | None) -> list[str]:
    """Scrape the FAA listing page for every '{FAC}_MVA_{variant}.xml' link -> sorted facility ids."""
    html = Path(list_input).read_text(encoding="utf-8", errors="replace") if list_input else fetch_text(list_url or LISTING_URL)
    ids = set(re.findall(rf"([A-Za-z0-9_]+)_MVA_{variant}\.xml", html))
    return sorted(ids)


def ring_from_poslist(poslist_text: str) -> list[list[float]]:
    """CRS84 posList ('lon lat lon lat ...') -> GeoJSON ring [[lon,lat], ...] (closed)."""
    nums = [float(n) for n in poslist_text.split()]
    if len(nums) % 2 != 0:
        raise ValueError(f"odd coordinate count in posList ({len(nums)})")
    ring = [[nums[i], nums[i + 1]] for i in range(0, len(nums), 2)]
    if ring[0] != ring[-1]:
        ring.append(ring[0])
    return ring


def parse_sectors(xml_bytes: bytes, facility: str, variant: str, source: str) -> list[dict]:
    root = ET.fromstring(xml_bytes)
    features: list[dict] = []
    for airspace in root.findall(".//" + AIXM + "Airspace"):
        name_el = airspace.find(".//" + AIXM + "name")
        sector = (name_el.text or "").strip() if name_el is not None else ""
        volumes = airspace.findall(".//" + AIXM + "AirspaceVolume")
        if len(volumes) != 1:
            raise ValueError(f"sector {sector!r}: expected 1 AirspaceVolume, found {len(volumes)}")
        vol = volumes[0]
        ml = vol.find(AIXM + "minimumLimit")
        ref = vol.find(AIXM + "minimumLimitReference")
        if ml is None or not (ml.text or "").strip():
            raise ValueError(f"sector {sector!r}: missing minimumLimit")
        if ml.get("uom") != "FT":
            raise ValueError(f"sector {sector!r}: minimumLimit uom={ml.get('uom')!r}, expected FT")
        if ref is None or (ref.text or "").strip() != "MSL":
            raise ValueError(f"sector {sector!r}: minimumLimitReference != MSL")
        floor_ft = int(round(float(ml.text)))

        patches = vol.findall(".//" + GML + "PolygonPatch")
        if len(patches) != 1:
            raise ValueError(f"sector {sector!r}: expected 1 PolygonPatch, found {len(patches)}")
        patch = patches[0]
        exterior = patch.find(GML + "exterior")
        if exterior is None:
            raise ValueError(f"sector {sector!r}: no exterior ring")
        rings = [ring_from_poslist(exterior.find(".//" + GML + "posList").text)]
        for interior in patch.findall(GML + "interior"):
            rings.append(ring_from_poslist(interior.find(".//" + GML + "posList").text))

        features.append(
            {
                "type": "Feature",
                "properties": {
                    "sector": sector,
                    "mvaFloorFt": floor_ft,
                    "facility": facility,
                    "variant": variant,
                    "source": source,
                },
                "geometry": {"type": "Polygon", "coordinates": rings},
            }
        )
    features.sort(key=lambda f: f["properties"]["sector"])
    return features


def validate(features: list[dict]) -> None:
    if not features:
        raise SystemExit("validation failed: no sectors parsed")
    for f in features:
        sector = f["properties"]["sector"]
        floor = f["properties"]["mvaFloorFt"]
        if not (FLOOR_RANGE[0] <= floor <= FLOOR_RANGE[1]):
            raise SystemExit(f"validation failed: {sector} floor {floor} ft out of range {FLOOR_RANGE}")
        for ri, ring in enumerate(f["geometry"]["coordinates"]):
            if len(ring) < 4:
                raise SystemExit(f"validation failed: {sector} ring {ri} has {len(ring)} pts (<4)")
            if ring[0] != ring[-1]:
                raise SystemExit(f"validation failed: {sector} ring {ri} not closed")
            for lon, lat in ring:
                if not (LON_RANGE[0] <= lon <= LON_RANGE[1] and LAT_RANGE[0] <= lat <= LAT_RANGE[1]):
                    raise SystemExit(f"validation failed: {sector} vertex ({lon},{lat}) out of range")


def geojson_text(features: list[dict], facility: str, variant: str, source: str) -> str:
    generated = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    facility_count = len({f["properties"]["facility"] for f in features})
    # Hand-diffable layout: one compact feature per line under a stable header.
    lines = [
        "{",
        '"type": "FeatureCollection",',
        '"metadata": '
        + json.dumps(
            {
                "facility": facility,
                "variant": variant,
                "source": source,
                "generatedAt": generated,
                "facilityCount": facility_count,
                "sectorCount": len(features),
                "note": "FAA AJV-A certified MVA chart. Not for navigation. Floors are MSL feet.",
            }
        )
        + ",",
        '"features": [',
    ]
    for i, f in enumerate(features):
        comma = "," if i < len(features) - 1 else ""
        lines.append(json.dumps(f, separators=(",", ":")) + comma)
    lines.append("]")
    lines.append("}")
    return "\n".join(lines) + "\n"


def write_geojson(features: list[dict], out_path: Path, facility: str, variant: str, source: str) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    text = geojson_text(features, facility, variant, source)
    if out_path.suffix == ".br":
        import brotli  # lazy: only the merged all-facility build compresses

        out_path.write_bytes(brotli.compress(text.encode("utf-8"), quality=11))
    else:
        out_path.write_text(text, encoding="utf-8", newline="\n")


def build_all(variant: str, list_input: str | None, list_url: str | None, out_path: Path) -> int:
    facilities = list_facilities(variant, list_input, list_url)
    if not facilities:
        raise SystemExit("no facilities found in listing")
    print(f"  {len(facilities)} {variant} facilities", file=sys.stderr)

    all_features: list[dict] = []
    failures: list[tuple[str, str]] = []
    for fac in facilities:
        try:
            xml_bytes = fetch_xml(fac, variant, None, None)
            feats = parse_sectors(xml_bytes, fac, variant, AIXM_URL.format(facility=fac, variant=variant))
            validate(feats)  # per-facility: a bad facility is skipped, not fatal to the whole batch
            all_features.extend(feats)
        except (urllib.error.URLError, ValueError, SystemExit, ET.ParseError) as exc:  # noqa: PERF203
            failures.append((fac, str(exc)))
            print(f"  SKIP {fac}: {exc}", file=sys.stderr)

    if not all_features:
        raise SystemExit("validation failed: no sectors parsed across any facility")

    # Stable order: facility then sector, so the committed file diffs cleanly between rebuilds.
    all_features.sort(key=lambda f: (f["properties"]["facility"], f["properties"]["sector"]))
    write_geojson(all_features, out_path, "ALL", variant, LISTING_URL)

    ok = len(facilities) - len(failures)
    floors = sorted({f["properties"]["mvaFloorFt"] for f in all_features})
    size_kb = out_path.stat().st_size / 1024
    print(
        f"OK: {len(all_features)} sectors from {ok}/{len(facilities)} facilities, "
        f"floors {floors[0]}-{floors[-1]} ft -> {out_path} ({size_kb:.0f} KB)",
        file=sys.stderr,
    )
    if failures:
        print(f"  {len(failures)} facilities skipped: {', '.join(fac for fac, _ in failures)}", file=sys.stderr)
    return 0


def render_overlay(features: list[dict], facility: str, variant: str, overlay_path: Path, videomap_url: str | None) -> None:
    import folium  # lazy: only needed for the visual cross-check

    lats = [c[1] for f in features for c in f["geometry"]["coordinates"][0]]
    lons = [c[0] for f in features for c in f["geometry"]["coordinates"][0]]
    center = [sum(lats) / len(lats), sum(lons) / len(lons)]
    m = folium.Map(location=center, zoom_start=8, tiles="cartodbpositron")
    floors = [f["properties"]["mvaFloorFt"] for f in features]
    lo, hi = min(floors), max(floors)
    for f in features:
        floor = f["properties"]["mvaFloorFt"]
        t = (floor - lo) / (hi - lo) if hi > lo else 0.0
        color = f"#{int(255 * t):02x}{int(80 + 100 * (1 - t)):02x}ff"
        # GeoJSON is [lon,lat]; folium wants [lat,lon].
        rings = [[[c[1], c[0]] for c in ring] for ring in f["geometry"]["coordinates"]]
        folium.Polygon(
            locations=rings[0],
            color=color,
            weight=1,
            fill=True,
            fill_opacity=0.25,
            popup=f"{f['properties']['sector']}: {floor} ft",
            tooltip=f"{floor}",
        ).add_to(m)

    # Overlay the vNAS MVA videomap linework (what controllers actually see) for visual comparison.
    if videomap_url:
        req = urllib.request.Request(videomap_url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=120) as resp:  # noqa: S310
            vmap = json.loads(resp.read())
        for feat in vmap.get("features", []):
            geom = feat.get("geometry") or {}
            if geom.get("type") != "LineString":
                continue
            line = [[c[1], c[0]] for c in geom["coordinates"]]
            folium.PolyLine(line, color="#ffd000", weight=1, opacity=0.8).add_to(m)

    overlay_path.parent.mkdir(parents=True, exist_ok=True)
    m.save(str(overlay_path))
    print(f"  overlay -> {overlay_path}", file=sys.stderr)


def main() -> int:
    ap = argparse.ArgumentParser(description="Build YAAT MVA GeoJSON from FAA AIXM.")
    ap.add_argument("--facility", default="NCT", help="FAA facility id (e.g. NCT, ZOA_QMV)")
    ap.add_argument("--variant", default="FUS3", choices=["FUS3", "FUS5"])
    ap.add_argument("--input", help="path to a cached AIXM XML (offline)")
    ap.add_argument("--url", help="explicit AIXM URL override")
    ap.add_argument("--output", help="output .geojson path (or .geojson.br for Brotli)")
    ap.add_argument("--overlay", help="also render an HTML overlay to this path")
    ap.add_argument("--videomap-url", help="vNAS MVA videomap geojson URL to overlay for visual comparison")
    ap.add_argument("--all", action="store_true", help="download every FAA facility for --variant and merge into one Brotli file")
    ap.add_argument("--list-input", help="cached FAA listing HTML for --all (offline facility discovery)")
    ap.add_argument("--list-url", help="override the FAA listing page URL for --all")
    args = ap.parse_args()

    if args.all:
        out_path = Path(args.output) if args.output else DEFAULT_OUT_DIR / f"FAA_MVA_{args.variant}.geojson.br"
        return build_all(args.variant, args.list_input, args.list_url, out_path)

    source = args.url or (args.input if args.input else AIXM_URL.format(facility=args.facility, variant=args.variant))
    xml_bytes = fetch_xml(args.facility, args.variant, args.input, args.url)
    features = parse_sectors(xml_bytes, args.facility, args.variant, source)
    validate(features)

    out_path = Path(args.output) if args.output else DEFAULT_OUT_DIR / f"{args.facility}_MVA_{args.variant}.geojson"
    write_geojson(features, out_path, args.facility, args.variant, source)
    floors = sorted({f["properties"]["mvaFloorFt"] for f in features})
    holes = sum(1 for f in features if len(f["geometry"]["coordinates"]) > 1)
    print(
        f"OK: {len(features)} sectors ({holes} with holes), "
        f"floors {floors[0]}-{floors[-1]} ft ({len(floors)} distinct) -> {out_path}",
        file=sys.stderr,
    )
    if args.overlay:
        render_overlay(features, args.facility, args.variant, Path(args.overlay), args.videomap_url)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
