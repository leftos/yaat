"""Fetch the USGS NAIPPlus and Esri World Imagery tiles the YAAT-2742 mock shows.

Run from the repo root: uv run --no-project python docs/plans/canvases/yaat-2742-imagery-source/fetch_tiles.py
Writes tiles/usgs/<z>-<x>-<y>.jpg and tiles/esri/<z>-<x>-<y>.jpg beside this script.
"""

import math
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
USER_AGENT = "yaat-mock/1.0"
ORIGIN = 20037508.342789244
WORLD = 2 * ORIGIN
FIRST_SERVER_ERROR = 500
ATTEMPTS = (1, 2)

# Each mosaic is the 3x3 tiles around one point: the KOAK passenger terminal ramp (its ARP lies in the marsh
# between the north and south fields) and the KSFO ARP, where its runways cross.
AIRPORTS = {"KOAK": (37.7112, -122.2150), "KSFO": (37.61897, -122.37514)}
ZOOMS = (17, 18)

USGS = (
    "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage"
    "?bbox={xmin},{ymin},{xmax},{ymax}&bboxSR=3857&imageSR=3857&size=256,256&format=jpg&f=image"
)
ESRI = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"


def tile_of(lat: float, lon: float, z: int) -> tuple[int, int]:
    n = 2**z
    r = math.radians(lat)
    x = int((lon + 180) / 360 * n)
    y = int((1 - math.log(math.tan(r) + 1 / math.cos(r)) / math.pi) / 2 * n)
    return x, y


def usgs_url(z: int, x: int, y: int) -> str:
    s = WORLD / 2**z
    xmin = -ORIGIN + x * s
    ymax = ORIGIN - y * s
    return USGS.format(xmin=f"{xmin:.2f}", ymin=f"{ymax - s:.2f}", xmax=f"{xmin + s:.2f}", ymax=f"{ymax:.2f}")


def fetch(url: str, dest: Path) -> str:
    """Download one tile, retrying a 5xx once; return a status line. A tile already on disk is kept."""
    if dest.exists():
        return f"ok (kept) {dest.parent.name}/{dest.name}"
    for attempt in ATTEMPTS:
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=90) as resp:
                body = resp.read()
                ctype = resp.headers.get("Content-Type", "")
        except urllib.error.HTTPError as e:
            if e.code >= FIRST_SERVER_ERROR and attempt == ATTEMPTS[0]:
                time.sleep(2)
                continue
            return f"FAIL {e.code} {dest.name} {url}"
        if not ctype.startswith("image/"):
            return f"FAIL not an image ({ctype}) {dest.name} {url}"
        dest.write_bytes(body)
        return f"ok{' (retried)' if attempt == ATTEMPTS[-1] else ''} {dest.parent.name}/{dest.name} {len(body)} B"
    return f"FAIL {dest.name}"


def jobs() -> list[tuple[str, Path]]:
    out = []
    for lat, lon in AIRPORTS.values():
        for z in ZOOMS:
            cx, cy = tile_of(lat, lon, z)
            for x in range(cx - 1, cx + 2):
                for y in range(cy - 1, cy + 2):
                    name = f"{z}-{x}-{y}.jpg"
                    out.append((usgs_url(z, x, y), HERE / "tiles" / "usgs" / name))
                    out.append((ESRI.format(z=z, x=x, y=y), HERE / "tiles" / "esri" / name))
    return out


def main() -> int:
    for sub in ("usgs", "esri"):
        (HERE / "tiles" / sub).mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda j: fetch(*j), jobs()))
    for line in results:
        print(line)
    failed = [r for r in results if r.startswith("FAIL")]
    print(f"{len(results) - len(failed)} of {len(results)} tiles fetched")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
