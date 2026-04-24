#!/usr/bin/env python3
"""
One-time upgrader that rewrites YAAT recording bundles to the new LatLon wire
format introduced by commit 4 of the LatLon refactor.

Transforms every snapshot JSON inside each bundle zip so that:

    { "Latitude": X, "Longitude": Y, ... }

becomes

    { "Position": { "Lat": X, "Lon": Y }, ... }

at the five paths that were migrated to LatLon in Yaat.Sim:

    Aircraft[*]                                      (AircraftSnapshotDto)
    Aircraft[*].Targets.NavigationRoute[*]           (NavigationTargetDto)
    Aircraft[*].Phases.DepartureClearance.DepartureRoute[*]
    Aircraft[*].Phases.Phases[*].DepartureRoute[*]

HoldShortPoints are NOT migrated (they keep their own double? Latitude / Longitude
properties on the sim side), so the path
    Aircraft[*].AssignedTaxiRoute.HoldShortPoints[*]
is intentionally skipped.

Idempotent: running twice is a no-op because after the first pass the snapshots
have `Position` and no `Latitude`/`Longitude` at those paths.

Deletable once all TestData bundles have been upgraded.
"""

from __future__ import annotations

import argparse
import io
import os
import sys
import zipfile
from pathlib import Path

try:
    import brotli
except ImportError:
    sys.stderr.write("brotli module required: pip install brotli\n")
    sys.exit(1)

import json

# Paths (relative to each snapshot's root) whose entries should have
# `Latitude`/`Longitude` collapsed into `Position`. Keys are lists of segments;
# `*` matches a single list index.
AIRCRAFT_LEVEL = ("Aircraft",)
NAV_ROUTE = ("Aircraft", "*", "Targets", "NavigationRoute")
DEPCLEARANCE_ROUTE = ("Aircraft", "*", "Phases", "DepartureClearance", "DepartureRoute")
PHASES_ROUTE = ("Aircraft", "*", "Phases", "Phases", "*", "DepartureRoute")


def transform_at_list_path(root: dict | list, segments: tuple[str, ...]) -> int:
    """
    Walk `segments` through `root`. At each `*` recurse into every list element.
    When the final segment is reached, the referenced value should be a list;
    for each dict in that list, collapse top-level Latitude/Longitude into
    Position. Returns the number of mutations.
    """
    return _walk(root, segments, 0)


def _walk(node, segments: tuple[str, ...], i: int) -> int:
    if i == len(segments):
        # Reached the leaf — `node` must be a list of dicts with Lat/Lon.
        if not isinstance(node, list):
            return 0
        mutations = 0
        for item in node:
            if isinstance(item, dict):
                mutations += _collapse_lat_lon(item)
        return mutations

    seg = segments[i]
    if seg == "*":
        if not isinstance(node, list):
            return 0
        total = 0
        for item in node:
            total += _walk(item, segments, i + 1)
        return total

    if not isinstance(node, dict) or seg not in node:
        return 0
    return _walk(node[seg], segments, i + 1)


def _collapse_lat_lon(obj: dict) -> int:
    """If `obj` has top-level `Latitude`+`Longitude`, replace with `Position`."""
    if "Latitude" not in obj or "Longitude" not in obj:
        return 0
    if "Position" in obj:
        # Already migrated (defensive).
        return 0
    lat = obj.pop("Latitude")
    lon = obj.pop("Longitude")

    # Insert `Position` at the front of the dict so the JSON layout resembles
    # what Yaat.Sim would serialize fresh. (dict insertion order is preserved
    # in CPython 3.7+.)
    new_obj = {"Position": {"Lat": lat, "Lon": lon}}
    for k, v in obj.items():
        new_obj[k] = v
    obj.clear()
    obj.update(new_obj)
    return 1


def upgrade_snapshot(obj: dict) -> int:
    """Apply all migrations to one snapshot. Returns total mutations."""
    total = 0
    # Aircraft[*] — transform each top-level aircraft dict.
    if isinstance(obj.get("Aircraft"), list):
        for ac in obj["Aircraft"]:
            if isinstance(ac, dict):
                total += _collapse_lat_lon(ac)

    total += transform_at_list_path(obj, NAV_ROUTE)
    total += transform_at_list_path(obj, DEPCLEARANCE_ROUTE)
    total += transform_at_list_path(obj, PHASES_ROUTE)
    return total


def upgrade_bundle(path: Path, dry_run: bool) -> tuple[int, int]:
    """Returns (snapshots_touched, total_mutations)."""
    with zipfile.ZipFile(path, "r") as zin:
        names = zin.namelist()
        entries: list[tuple[str, bytes]] = []
        for n in names:
            entries.append((n, zin.read(n)))

    snapshots_touched = 0
    total_mutations = 0
    rewritten: list[tuple[str, bytes]] = []

    for name, data in entries:
        if not name.startswith("snapshots/") or not name.endswith(".json.br"):
            rewritten.append((name, data))
            continue

        try:
            decoded = brotli.decompress(data)
            snap = json.loads(decoded)
        except Exception as e:
            sys.stderr.write(f"  {name}: decode failed: {e}\n")
            rewritten.append((name, data))
            continue

        mutations = upgrade_snapshot(snap)
        if mutations == 0:
            rewritten.append((name, data))
            continue

        snapshots_touched += 1
        total_mutations += mutations

        # Re-encode. Preserve the original separators used by System.Text.Json
        # default output (no leading space after colon/comma in minified form,
        # but the snapshots appear to pretty-print). Match the original's
        # indentation by detecting it.
        indent = 2 if b"\n  " in decoded[:200] else None
        encoded = json.dumps(snap, indent=indent, ensure_ascii=False).encode("utf-8")
        compressed = brotli.compress(encoded, quality=4)
        rewritten.append((name, compressed))

    if dry_run or snapshots_touched == 0:
        return (snapshots_touched, total_mutations)

    tmp = path.with_suffix(path.suffix + ".tmp")
    with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED) as zout:
        for name, data in rewritten:
            zout.writestr(name, data)
    os.replace(tmp, path)
    return (snapshots_touched, total_mutations)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("bundles", nargs="*", help="Bundle zip files (or a directory).")
    p.add_argument("--dry-run", action="store_true", help="Scan and report without writing.")
    args = p.parse_args()

    targets: list[Path] = []
    for b in args.bundles:
        path = Path(b)
        if path.is_dir():
            targets.extend(sorted(path.glob("*.zip")))
        elif path.is_file():
            targets.append(path)
        else:
            sys.stderr.write(f"skip: {b}: not found\n")

    if not targets:
        p.print_help()
        return 1

    total_snaps = 0
    total_muts = 0
    for path in targets:
        try:
            snaps, muts = upgrade_bundle(path, args.dry_run)
        except zipfile.BadZipFile:
            sys.stderr.write(f"{path}: not a zip\n")
            continue
        if snaps == 0:
            sys.stdout.write(f"{path.name}: up to date\n")
        else:
            verb = "would rewrite" if args.dry_run else "rewrote"
            sys.stdout.write(f"{path.name}: {verb} {snaps} snapshots, {muts} mutations\n")
        total_snaps += snaps
        total_muts += muts

    sys.stdout.write(f"\nTotal: {total_snaps} snapshots, {total_muts} mutations\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
