#!/usr/bin/env python3
"""Download an ARTCC config snapshot from vNAS for use in Sim tests.

Tests that exercise replay-time TCP/ERAM resolution need an ArtccConfigRoot
without spinning up a server. This tool fetches the live config and writes it
to TestData/ as a deterministic JSON file. Re-run when the upstream config
changes shape (rare).

Usage:
    python tools/refresh-artcc-snapshot.py --artcc ZOA \
        --out tests/Yaat.Sim.Tests/TestData/artcc-zoa-snapshot.json
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

DATA_API_BASE = "https://data-api.vnas.vatsim.net/api"


def fetch_artcc_config(artcc_id: str) -> dict:
    url = f"{DATA_API_BASE}/artccs/{artcc_id}"
    req = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "yaat-refresh-artcc-snapshot/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            payload = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code} fetching {url}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)
    except urllib.error.URLError as exc:
        print(f"network error fetching {url}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1)

    return json.loads(payload)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artcc", required=True, help="ARTCC ID (e.g. ZOA)")
    parser.add_argument("--out", type=Path, required=True, help="output JSON file path")
    parser.add_argument("--indent", type=int, default=None, help="JSON indent (default: minified)")
    args = parser.parse_args()

    config = fetch_artcc_config(args.artcc)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(config, indent=args.indent), encoding="utf-8", newline="\n")

    size_kb = args.out.stat().st_size / 1024
    print(f"wrote {args.out} ({size_kb:.1f} KB) for ARTCC {args.artcc}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
