#!/usr/bin/env python3
"""One-time migrator: regroup flat aircraft fields into nested sub-objects.

Drives off tools/aircraft_schema_migration.json. Each entry under "domains"
describes one set of flat fields to move into a nested container. The script
walks every YAAT bundle in a directory (default tests/Yaat.Sim.Tests/TestData/),
decompresses each snapshot, transforms every aircraft inside, and writes the
bundle back.

Idempotent per-domain: if the container key already exists in an aircraft, the
domain is skipped for that aircraft. Safe to re-run after each AircraftState
refactor commit; the JSON config grows as new domains land.

Usage:
    python tools/migrate_aircraft_snapshot_schema.py [path] [--dry-run] [--verify]

If [path] is a directory, every *.zip underneath is migrated. If a file, only
that bundle. Defaults to tests/Yaat.Sim.Tests/TestData/.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
import time
import zipfile
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path
from typing import Any

try:
    import brotli  # type: ignore[import-not-found]
except ImportError:
    print("error: this script needs 'brotli' (pip install brotli)", file=sys.stderr)
    sys.exit(2)

REPO_ROOT = Path(__file__).resolve().parent.parent
TESTDATA_DIR = REPO_ROOT / "tests" / "Yaat.Sim.Tests" / "TestData"
CONFIG_PATH = REPO_ROOT / "tools" / "aircraft_schema_migration.json"


def load_config() -> dict[str, Any]:
    with open(CONFIG_PATH, encoding="utf-8") as fp:
        return json.load(fp)


def transform_aircraft(aircraft: dict[str, Any], domains: list[dict[str, Any]]) -> bool:
    """Apply every domain transform to one aircraft dict in place. Returns True
    if anything changed."""
    changed = False
    for domain in domains:
        container = domain["container_field"]
        if container in aircraft:
            continue  # idempotent: already migrated
        nested: dict[str, Any] = {}
        any_present = False
        for spec in domain["fields"]:
            old, new = spec["old"], spec["new"]
            if old in aircraft:
                nested[new] = aircraft.pop(old)
                any_present = True
            elif "default" in spec:
                nested[new] = spec["default"]
        if not any_present and not domain.get("always_emit", False):
            continue
        aircraft[container] = nested
        changed = True
    return changed


def transform_snapshot(snapshot: dict[str, Any], domains: list[dict[str, Any]]) -> bool:
    aircraft_list = snapshot.get("Aircraft")
    if not isinstance(aircraft_list, list):
        return False
    changed = False
    for ac in aircraft_list:
        if isinstance(ac, dict) and transform_aircraft(ac, domains):
            changed = True
    return changed


def _log(msg: str, *, verbose: bool) -> None:
    if verbose:
        print(msg, flush=True)


def migrate_bundle(path: Path, domains: list[dict[str, Any]], *, dry_run: bool, verbose: bool = False) -> tuple[bool, int]:
    """Migrate one bundle. Returns (changed, snapshot_count). Skips bundles
    that aren't recognizable v4 zips."""
    _log(f"    open {path.name} ({path.stat().st_size:,} bytes)", verbose=verbose)
    with zipfile.ZipFile(path) as outer:
        outer_names = outer.namelist()
        _log(f"    outer entries: {len(outer_names)}", verbose=verbose)
        if "manifest.json" in outer_names:
            archive_bytes = path.read_bytes()
            inner_path = None
        else:
            inner_name = next((n for n in outer_names if n.endswith("recording.yaat-recording.zip")), None)
            if inner_name is None:
                _log("    not a v4 bundle, skipping", verbose=verbose)
                return False, 0
            _log(f"    nested recording: {inner_name}", verbose=verbose)
            archive_bytes = outer.read(inner_name)
            inner_path = inner_name

    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        names = archive.namelist()
        snapshot_names = sorted(n for n in names if n.startswith("snapshots/") and n.endswith(".json.br"))
        _log(f"    archive entries: {len(names)}, snapshots: {len(snapshot_names)}", verbose=verbose)
        if not snapshot_names:
            return False, 0

        new_entries: dict[str, bytes] = {}
        any_changed = False
        for i, name in enumerate(snapshot_names):
            _log(f"    [{i + 1}/{len(snapshot_names)}] {name}", verbose=verbose)
            compressed = archive.read(name)
            text = brotli.decompress(compressed).decode("utf-8")
            snapshot = json.loads(text)
            if transform_snapshot(snapshot, domains):
                any_changed = True
                new_text = json.dumps(snapshot, separators=(",", ":"), ensure_ascii=False)
                # quality=4 is much faster than the default 11 and these are test bundles —
                # size on disk matters less than wall-clock for the migrator.
                new_entries[name] = brotli.compress(new_text.encode("utf-8"), quality=4)

        if not any_changed:
            _log("    no changes needed", verbose=verbose)
            return False, len(snapshot_names)
        if dry_run:
            _log(f"    would rewrite {len(new_entries)} snapshots (dry-run)", verbose=verbose)
            return True, len(snapshot_names)

        _log(f"    rewriting {len(new_entries)} snapshots into archive...", verbose=verbose)
        rewritten_archive = io.BytesIO()
        with zipfile.ZipFile(rewritten_archive, "w", compression=zipfile.ZIP_DEFLATED) as out:
            for name in names:
                if name in new_entries:
                    out.writestr(name, new_entries[name])
                else:
                    out.writestr(name, archive.read(name))
        rewritten_archive_bytes = rewritten_archive.getvalue()
        _log(f"    archive rewritten: {len(rewritten_archive_bytes):,} bytes", verbose=verbose)

    if inner_path is None:
        _log("    writing back to disk...", verbose=verbose)
        path.write_bytes(rewritten_archive_bytes)
        _log("    done", verbose=verbose)
        return True, len(snapshot_names)

    _log("    re-reading outer to repack inner recording...", verbose=verbose)
    with zipfile.ZipFile(path) as outer:
        outer_names = outer.namelist()
        outer_data = {n: outer.read(n) for n in outer_names}
    outer_data[inner_path] = rewritten_archive_bytes

    _log("    rewriting outer zip...", verbose=verbose)
    rewritten_outer = io.BytesIO()
    with zipfile.ZipFile(rewritten_outer, "w", compression=zipfile.ZIP_DEFLATED) as out:
        for name in outer_names:
            out.writestr(name, outer_data[name])
    path.write_bytes(rewritten_outer.getvalue())
    _log("    done (outer rewritten)", verbose=verbose)
    return True, len(snapshot_names)


def verify_bundle(path: Path, domains: list[dict[str, Any]]) -> list[str]:
    """Return a list of error strings; empty list = pass."""
    errors: list[str] = []
    with zipfile.ZipFile(path) as outer:
        outer_names = outer.namelist()
        if "manifest.json" in outer_names:
            archive = outer
            archive_close: zipfile.ZipFile | None = None
        else:
            inner_name = next((n for n in outer_names if n.endswith("recording.yaat-recording.zip")), None)
            if inner_name is None:
                return [f"{path.name}: not a v4 bundle"]
            inner_buf = io.BytesIO(outer.read(inner_name))
            archive = zipfile.ZipFile(inner_buf)
            archive_close = archive

        try:
            for name in sorted(n for n in archive.namelist() if n.startswith("snapshots/") and n.endswith(".json.br")):
                snapshot = json.loads(brotli.decompress(archive.read(name)).decode("utf-8"))
                for ac in snapshot.get("Aircraft", []):
                    if not isinstance(ac, dict):
                        continue
                    for domain in domains:
                        container = domain["container_field"]
                        if domain.get("always_emit", False) and container not in ac:
                            errors.append(f"{path.name}/{name} {ac.get('Callsign', '?')}: missing '{container}'")
                        for spec in domain["fields"]:
                            if spec["old"] in ac:
                                errors.append(f"{path.name}/{name} {ac.get('Callsign', '?')}: stale flat field '{spec['old']}' still present")
        finally:
            if archive_close is not None:
                archive_close.close()
    return errors


def _worker(path_str: str, domains: list[dict[str, Any]], dry_run: bool, verbose: bool) -> tuple[bool, int, str | None]:
    """Subprocess entry point. Returns (changed, snap_count, error_string_or_None)."""
    try:
        changed, snap_count = migrate_bundle(Path(path_str), domains, dry_run=dry_run, verbose=verbose)
        return changed, snap_count, None
    except (zipfile.BadZipFile, KeyError, ValueError) as exc:
        return False, 0, str(exc)


def iter_bundles(target: Path) -> list[Path]:
    if target.is_file():
        return [target]
    return sorted(target.rglob("*.zip"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("path", nargs="?", default=str(TESTDATA_DIR), help=f"Bundle file or directory (default: {TESTDATA_DIR})")
    parser.add_argument("--dry-run", action="store_true", help="Report what would change without writing")
    parser.add_argument("--verify", action="store_true", help="After (or instead of) migrating, check every domain's container is populated and no stale flat fields remain")
    parser.add_argument("-v", "--verbose", action="store_true", help="Per-snapshot progress logging (forces single-process)")
    parser.add_argument("-j", "--jobs", type=int, default=0, help="Parallel worker processes (default: cpu_count-1)")
    args = parser.parse_args()
    if args.verbose and not args.jobs:
        args.jobs = 1

    config = load_config()
    domains = config["domains"]
    target = Path(args.path)
    bundles = iter_bundles(target)
    if not bundles:
        print(f"no bundles found under {target}", file=sys.stderr)
        return 1

    workers = args.jobs or max(1, (os.cpu_count() or 4) - 1)
    print(f"scanning {len(bundles)} bundle(s) under {target} (workers={workers})", flush=True)
    migrated = 0
    skipped = 0
    started = time.monotonic()

    if workers == 1:
        for idx, bundle in enumerate(bundles, start=1):
            print(f"[{idx}/{len(bundles)}] {bundle.name}", flush=True)
            try:
                changed, snap_count = migrate_bundle(bundle, domains, dry_run=args.dry_run, verbose=args.verbose)
            except (zipfile.BadZipFile, KeyError) as exc:
                print(f"  ! {bundle.name}: {exc}", file=sys.stderr, flush=True)
                continue
            if changed:
                migrated += 1
                verb = "would migrate" if args.dry_run else "migrated"
                print(f"  [OK] {verb} {bundle.name} ({snap_count} snapshots)", flush=True)
            else:
                skipped += 1
                print(f"  [-]  {bundle.name} (already up-to-date or no snapshots)", flush=True)
    else:
        with ProcessPoolExecutor(max_workers=workers) as pool:
            futures = {pool.submit(_worker, str(b), domains, args.dry_run, args.verbose): b for b in bundles}
            done = 0
            for fut in as_completed(futures):
                done += 1
                bundle = futures[fut]
                try:
                    changed, snap_count, error = fut.result()
                except Exception as exc:  # pragma: no cover - defensive
                    error = str(exc)
                    changed, snap_count = False, 0
                if error:
                    print(f"[{done}/{len(bundles)}] ! {bundle.name}: {error}", file=sys.stderr, flush=True)
                    continue
                if changed:
                    migrated += 1
                    verb = "would migrate" if args.dry_run else "migrated"
                    print(f"[{done}/{len(bundles)}] [OK] {verb} {bundle.name} ({snap_count} snapshots)", flush=True)
                else:
                    skipped += 1
                    print(f"[{done}/{len(bundles)}] [-]  {bundle.name} (no changes)", flush=True)

    elapsed = time.monotonic() - started
    print(f"\n{'would migrate' if args.dry_run else 'migrated'}: {migrated}, up-to-date: {skipped}, elapsed: {elapsed:.1f}s", flush=True)

    if args.verify and not args.dry_run:
        print()
        print("verifying...")
        all_errors: list[str] = []
        for bundle in bundles:
            try:
                all_errors.extend(verify_bundle(bundle, domains))
            except (zipfile.BadZipFile, KeyError) as exc:
                all_errors.append(f"{bundle.name}: {exc}")
        if all_errors:
            for e in all_errors[:50]:
                print(f"  [FAIL] {e}")
            if len(all_errors) > 50:
                print(f"  ... and {len(all_errors) - 50} more")
            return 1
        print("verify: all clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
