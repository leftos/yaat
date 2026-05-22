#!/usr/bin/env python3
"""Download NavData.dat from vNAS and refresh TestData pins.

Writes tests/Yaat.Sim.Tests/TestData/NavData.dat and navdata-manifest.json
(navDataSerial, airacCycle). Re-run when vNAS publishes a new NavData serial
or when tests need procedure ids that match live vNAS (e.g. NIMI6 vs NIMI5).

Usage:
    python tools/refresh-navdata.py
    python tools/refresh-navdata.py --out tests/Yaat.Sim.Tests/TestData/NavData.dat
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from datetime import date, datetime, timezone
from pathlib import Path

CONFIG_URL = "https://configuration.vnas.vatsim.net/"
DEFAULT_OUT = Path("tests/Yaat.Sim.Tests/TestData/NavData.dat")
AIRAC_EPOCH = date(2025, 1, 23)
CYCLE_DAYS = 28
CYCLES_PER_YEAR = 13


def current_airac_cycle(today: date | None = None) -> str:
    today = today or date.today()
    total_days = (today - AIRAC_EPOCH).days
    if total_days < 0:
        return "2501"
    cycle_index = total_days // CYCLE_DAYS
    year = 2025 + cycle_index // CYCLES_PER_YEAR
    cycle_in_year = cycle_index % CYCLES_PER_YEAR + 1
    return f"{year % 100:02d}{cycle_in_year:02d}"


def fetch_config() -> dict:
    req = urllib.request.Request(CONFIG_URL, headers={"User-Agent": "yaat-refresh-navdata/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code} fetching {CONFIG_URL}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)
    except urllib.error.URLError as exc:
        print(f"network error fetching {CONFIG_URL}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)


def download_navdata(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "yaat-refresh-navdata/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code} downloading NavData: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)
    except urllib.error.URLError as exc:
        print(f"network error downloading NavData: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output NavData.dat path")
    parser.add_argument("--manifest", type=Path, default=None, help="manifest JSON (default: beside --out)")
    args = parser.parse_args()

    manifest_path = args.manifest or args.out.with_name("navdata-manifest.json")

    config = fetch_config()
    serial = config.get("navDataSerial")
    url = config.get("navDataUrl")
    if serial is None or not url:
        print("VNAS config missing navDataSerial or navDataUrl", file=sys.stderr)
        return 1

    data = download_navdata(url)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(data)

    airac = current_airac_cycle()
    manifest = {
        "navDataSerial": serial,
        "airacCycle": airac,
        "lastRefreshed": datetime.now(timezone.utc).isoformat(),
    }
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    size_mb = len(data) / (1024 * 1024)
    print(f"wrote {args.out} ({size_mb:.2f} MB, serial {serial}, AIRAC {airac})")
    print(f"wrote {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
