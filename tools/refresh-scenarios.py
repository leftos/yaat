#!/usr/bin/env python3
"""Download training scenarios from the vNAS data API for local parse testing.

Usage:
    python tools/refresh-scenarios.py ZOA          # Download all ZOA scenarios
    python tools/refresh-scenarios.py ZOA ZLA      # Multiple ARTCCs
    python tools/refresh-scenarios.py --all         # All ARTCCs with scenarios

Scenarios are saved to tests/Yaat.Sim.Tests/TestData/Scenarios/<ARTCC>/.
These files are gitignored and must be downloaded locally.
"""

import json
import os
import sys
import time
import urllib.request

BASE_URL = "https://data-api.vnas.vatsim.net/api/training"
HEADERS = {"User-Agent": "yaat-scenario-refresh/1.0"}
TESTDATA_ROOT = os.path.join(
    os.path.dirname(__file__),
    "..",
    "tests",
    "Yaat.Sim.Tests",
    "TestData",
    "Scenarios",
)


def fetch_json(url: str) -> object:
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def refresh_artcc(artcc_id: str) -> None:
    artcc_id = artcc_id.upper()
    out_dir = os.path.join(TESTDATA_ROOT, artcc_id)
    os.makedirs(out_dir, exist_ok=True)

    print(f"Fetching scenario summaries for {artcc_id}...")
    summaries_url = f"{BASE_URL}/scenario-summaries/by-artcc/{artcc_id}"
    try:
        summaries = fetch_json(summaries_url)
    except urllib.error.HTTPError as e:
        print(f"  Error fetching summaries: {e}")
        return

    if not summaries:
        print(f"  No scenarios found for {artcc_id}")
        return

    # Save summaries index
    summaries_path = os.path.join(out_dir, "_summaries.json")
    with open(summaries_path, "w") as f:
        json.dump(summaries, f, indent=2)

    print(f"  Found {len(summaries)} scenarios")

    total = len(summaries)
    ok = 0
    fail = 0
    skipped = 0
    for i, s in enumerate(summaries, 1):
        sid = s["id"]
        name = s.get("name", sid)
        path = os.path.join(out_dir, f"{sid}.json")

        if os.path.exists(path) and os.path.getsize(path) > 0:
            skipped += 1
            print(f"\r  [{i}/{total}] {skipped} cached, {ok} downloaded, {fail} failed", end="", flush=True)
            continue

        try:
            url = f"{BASE_URL}/scenarios/{sid}"
            req = urllib.request.Request(url, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=30) as resp:
                data = resp.read()
            with open(path, "wb") as f:
                f.write(data)
            ok += 1
        except Exception as e:
            fail += 1
            print(f"\n  FAIL {name}: {e}")

        print(f"\r  [{i}/{total}] {skipped} cached, {ok} downloaded, {fail} failed", end="", flush=True)
        time.sleep(0.1)

    print(f"\n  Done: {ok} downloaded, {skipped} cached, {fail} failed")


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    if sys.argv[1] == "--all":
        print("Fetching all ARTCC IDs...")
        # There's no list-all endpoint, so use known ARTCCs
        artccs = [
            "ZAB", "ZAU", "ZBW", "ZDC", "ZDV", "ZFW", "ZHU", "ZID",
            "ZJX", "ZKC", "ZLA", "ZLC", "ZMA", "ZME", "ZMP", "ZNY",
            "ZOA", "ZOB", "ZSE", "ZTL",
        ]
        for artcc in artccs:
            refresh_artcc(artcc)
            print()
    else:
        for artcc in sys.argv[1:]:
            refresh_artcc(artcc)


if __name__ == "__main__":
    main()
