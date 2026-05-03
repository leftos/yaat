#!/usr/bin/env python3
"""Inspect, extract, install, and validate YAAT v4 bug bundles.

Usage:
    python tools/bug_bundle.py <subcommand> [args] [flags]

Subcommands:
    info      Manifest summary + aircraft callsigns at t=0.
    snapshot  Dump snapshot nearest to --at <seconds> (optionally filtered by --callsign).
    actions   List recorded user actions chronologically.
    scenario  Decompress scenario.json.br.
    weather   Print weather.json (if present).
    layouts   List ground layout airport IDs, or dump one with --airport / all with --all.
    logs      Extract yaat-client.log / yaat-server.log to .tmp/ (or --out-dir).
    install   Copy bundle into tests/Yaat.Sim.Tests/TestData/ with issue{N}-{desc}-... naming.
              Either a local path, or --issue N to fetch the attachment from a GitHub issue.
    validate  Check manifest schema and verify every declared entry decompresses.

V4 bundle layout (ZIP at root):
    manifest.json               plain JSON (metadata + snapshot index)
    scenario.json.br            Brotli-compressed scenario JSON
    actions.json.br             Brotli-compressed user actions list
    snapshots/NNN.json.br       one Brotli-compressed snapshot per index
    layouts/<airport>.json.br   deduplicated ground layouts (optional)
    weather.json                plain JSON (optional; HasWeather in manifest)
    artcc-config.json.br        Brotli-compressed ARTCC config (optional; HasArtccConfig in manifest)
    yaat-client.log             plain text (bug bundles only)
    yaat-server.log             plain text (bug bundles only, local server)

Legacy bug bundles contain a nested `recording.yaat-recording.zip` with the same
inner layout and logs at the outer root; this script handles both.

Requires: brotli (pip install brotli). Manifest/log/weather work without it,
but scenario/actions/snapshots/layouts need brotli to decompress.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path
from typing import Any, Iterator

try:
    import brotli  # type: ignore[import-not-found]
except ImportError:
    brotli = None

REPO_ROOT = Path(__file__).resolve().parent.parent
TESTDATA_DIR = REPO_ROOT / "tests" / "Yaat.Sim.Tests" / "TestData"
DEFAULT_TMP = REPO_ROOT / ".tmp"


def _require_brotli() -> None:
    if brotli is None:
        print(
            "error: this action needs the 'brotli' package.\n"
            "       install with: pip install brotli",
            file=sys.stderr,
        )
        sys.exit(2)


# ---------------------------------------------------------------------------
# BundleReader
# ---------------------------------------------------------------------------


class BundleReader:
    """Reads a v4 bug bundle, a v4 recording archive, or a legacy bug bundle
    with a nested recording.yaat-recording.zip. Exposes manifest + entry I/O.

    Logs (yaat-client.log, yaat-server.log) always come from the outer bundle
    when the recording is nested; all other entries come from the archive
    holding manifest.json.
    """

    def __init__(self, path: Path) -> None:
        self.path = path
        self._outer = zipfile.ZipFile(path)
        self._inner: zipfile.ZipFile | None = None
        self._inner_buf: io.BytesIO | None = None

        outer_names = set(self._outer.namelist())
        if "manifest.json" in outer_names:
            self._archive: zipfile.ZipFile = self._outer
            self._log_source: zipfile.ZipFile = self._outer
        else:
            nested = next(
                (n for n in outer_names if n.endswith("recording.yaat-recording.zip")),
                None,
            )
            if nested is None:
                raise ValueError(
                    f"Not a recognized v4 bundle: {path}\n"
                    f"Top-level entries: {sorted(outer_names)[:10]}"
                )
            self._inner_buf = io.BytesIO(self._outer.read(nested))
            self._inner = zipfile.ZipFile(self._inner_buf)
            self._archive = self._inner
            self._log_source = self._outer

        self._manifest: dict[str, Any] | None = None

    def close(self) -> None:
        if self._inner is not None:
            self._inner.close()
        if self._inner_buf is not None:
            self._inner_buf.close()
        self._outer.close()

    def __enter__(self) -> BundleReader:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    @property
    def manifest(self) -> dict[str, Any]:
        if self._manifest is None:
            raw = self._archive.read("manifest.json")
            self._manifest = json.loads(raw.decode("utf-8"))
        return self._manifest

    @property
    def archive_entries(self) -> list[str]:
        return self._archive.namelist()

    @property
    def log_entries(self) -> list[str]:
        return [n for n in self._log_source.namelist() if n.endswith(".log")]

    def read_plain(self, name: str, *, from_logs: bool = False) -> bytes:
        src = self._log_source if from_logs else self._archive
        return src.read(name)

    def read_brotli_text(self, name: str) -> str:
        _require_brotli()
        compressed = self._archive.read(name)
        return brotli.decompress(compressed).decode("utf-8")  # type: ignore[union-attr]

    def find_nearest_snapshot_index(self, target_seconds: float) -> int | None:
        """Largest index where Snapshots[i].ElapsedSeconds <= target_seconds, or None.
        Mirrors RecordingArchive.FindNearestSnapshotIndex (binary search)."""
        snaps = self.manifest.get("Snapshots") or []
        if not snaps:
            return None
        lo, hi, result = 0, len(snaps) - 1, -1
        while lo <= hi:
            mid = lo + (hi - lo) // 2
            if snaps[mid]["ElapsedSeconds"] <= target_seconds:
                result = mid
                lo = mid + 1
            else:
                hi = mid - 1
        return result if result >= 0 else None

    def read_snapshot(self, index: int) -> dict[str, Any]:
        return json.loads(self.read_brotli_text(f"snapshots/{index:03d}.json.br"))

    def read_scenario(self) -> str:
        return self.read_brotli_text("scenario.json.br")

    def read_actions(self) -> list[dict[str, Any]]:
        return json.loads(self.read_brotli_text("actions.json.br"))

    def read_weather(self) -> str | None:
        if not self.manifest.get("HasWeather"):
            return None
        if "weather.json" not in self.archive_entries:
            return None
        return self.read_plain("weather.json").decode("utf-8")

    def read_artcc_config(self) -> str | None:
        if not self.manifest.get("HasArtccConfig"):
            return None
        if "artcc-config.json.br" not in self.archive_entries:
            return None
        return self.read_brotli_text("artcc-config.json.br")

    def read_layout(self, airport_id: str) -> str:
        return self.read_brotli_text(f"layouts/{airport_id}.json.br")


# ---------------------------------------------------------------------------
# Output helper
# ---------------------------------------------------------------------------


def write_output(text: str, out_path: Path | None) -> None:
    if out_path is None:
        sys.stdout.write(text)
        if not text.endswith("\n"):
            sys.stdout.write("\n")
    else:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        with open(out_path, "w", encoding="utf-8", newline="\n") as fp:
            fp.write(text)
        print(f"wrote {out_path}", file=sys.stderr)


def write_bytes_output(data: bytes, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "wb") as fp:
        fp.write(data)
    print(f"wrote {out_path} ({len(data):,} bytes)", file=sys.stderr)


# ---------------------------------------------------------------------------
# Subcommand: info
# ---------------------------------------------------------------------------


def _aircraft_callsigns_at_t0(reader: BundleReader) -> list[str]:
    snaps = reader.manifest.get("Snapshots") or []
    if not snaps:
        return []
    snap = reader.read_snapshot(0)
    aircraft = snap.get("Aircraft") or []
    return [ac.get("Callsign") for ac in aircraft if ac.get("Callsign")]


def cmd_info(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        m = reader.manifest
        logs = reader.log_entries
        layouts = m.get("LayoutAirportIds") or []
        try:
            callsigns = _aircraft_callsigns_at_t0(reader)
        except Exception as e:  # brotli missing or decompression error
            callsigns = []
            callsign_err = str(e)
        else:
            callsign_err = ""

        if args.json:
            payload = {
                "path": str(args.bundle),
                "manifest": m,
                "logs": logs,
                "layouts": layouts,
                "aircraft_callsigns_t0": callsigns,
            }
            if callsign_err:
                payload["aircraft_error"] = callsign_err
            write_output(json.dumps(payload, indent=2), args.out)
            return 0

        lines: list[str] = []
        lines.append(f"Bundle: {args.bundle}")
        lines.append(f"  Version:             {m.get('Version')}")
        lines.append(f"  RngSeed:             {m.get('RngSeed')}")
        lines.append(f"  Duration:            {m.get('TotalElapsedSeconds')} s")
        lines.append(f"  Snapshots:           {len(m.get('Snapshots') or [])}")
        lines.append(f"  Actions:             {m.get('ActionCount')}")
        lines.append(f"  ARTCC:               {m.get('ArtccId')}")
        lines.append(f"  ScenarioName:        {m.get('ScenarioName')}")
        lines.append(f"  ScenarioId:          {m.get('ScenarioId')}")
        lines.append(f"  RecordedAtUtc:       {m.get('RecordedAtUtc')}")
        lines.append(f"  RecordedBy:          {m.get('RecordedBy')}")
        lines.append(f"  HasWeather:          {m.get('HasWeather')}")
        lines.append(f"  HasArtccConfig:      {m.get('HasArtccConfig', False)}")
        lines.append(f"  Layouts ({len(layouts)}):         {', '.join(layouts) if layouts else '(none)'}")
        lines.append(f"  Logs ({len(logs)}):            {', '.join(logs) if logs else '(none)'}")
        if callsign_err:
            lines.append(f"  Aircraft at t=0:     <error: {callsign_err}>")
        else:
            lines.append(f"  Aircraft at t=0 ({len(callsigns)}): {', '.join(callsigns) if callsigns else '(none)'}")
        write_output("\n".join(lines), args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: snapshot
# ---------------------------------------------------------------------------


def cmd_snapshot(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        idx = reader.find_nearest_snapshot_index(args.at)
        if idx is None:
            print(
                f"error: no snapshot at or before t={args.at}s "
                f"(bundle has {len(reader.manifest.get('Snapshots') or [])} snapshots)",
                file=sys.stderr,
            )
            return 1
        snap = reader.read_snapshot(idx)
        entry = reader.manifest["Snapshots"][idx]
        actual_t = entry["ElapsedSeconds"]

        if args.callsign:
            want = args.callsign.upper()
            matches = [
                ac for ac in (snap.get("Aircraft") or [])
                if (ac.get("Callsign") or "").upper() == want
            ]
            if not matches:
                available = sorted({ac.get("Callsign") for ac in (snap.get("Aircraft") or []) if ac.get("Callsign")})
                print(
                    f"error: callsign '{args.callsign}' not in snapshot at t={actual_t}s.\n"
                    f"       available: {', '.join(available) if available else '(none)'}",
                    file=sys.stderr,
                )
                return 1
            payload = {
                "index": idx,
                "elapsed_seconds": actual_t,
                "action_index": entry["ActionIndex"],
                "callsign": args.callsign,
                "aircraft": matches[0],
            }
        else:
            payload = {
                "index": idx,
                "elapsed_seconds": actual_t,
                "action_index": entry["ActionIndex"],
                "snapshot": snap,
            }

        print(
            f"snapshot[{idx}] at t={actual_t}s (requested --at {args.at}s), "
            f"action_index={entry['ActionIndex']}",
            file=sys.stderr,
        )
        write_output(json.dumps(payload, indent=2), args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: track
# ---------------------------------------------------------------------------


def _haversine_nm(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    import math

    lat1r, lon1r, lat2r, lon2r = map(math.radians, (lat1, lon1, lat2, lon2))
    dlat = lat2r - lat1r
    dlon = lon2r - lon1r
    a = math.sin(dlat / 2) ** 2 + math.cos(lat1r) * math.cos(lat2r) * math.sin(dlon / 2) ** 2
    return 2 * math.atan2(math.sqrt(a), math.sqrt(1 - a)) * 3440.065


def _phase_name(ac: dict[str, Any]) -> str:
    phases = (ac.get("Phases") or {}).get("Phases") or []
    idx = (ac.get("Phases") or {}).get("CurrentIndex")
    if not phases or idx is None or idx < 0 or idx >= len(phases):
        return "-"
    name = phases[idx].get("$type") or "-"
    return name.removesuffix("Phase") if name.endswith("Phase") else name


def cmd_track(args: argparse.Namespace) -> int:
    """Iterate all snapshots and emit a per-snapshot row per callsign.

    For each callsign, prints t, pos/alt/hdg/ias, TargetSpeed, FollowingCallsign,
    and the active phase. When --pair is given, also prints the gap (nm) between
    the two callsigns and a running VfrFollowPhase-style runaway timer (seconds
    the gap has been strictly above the best-seen gap since follow started).
    """
    callsigns = [c.upper() for c in (args.callsigns or [])]
    if args.pair:
        callsigns = list({*callsigns, *[c.upper() for c in args.pair]})
    if not callsigns:
        print("error: supply at least one callsign (or --pair A B)", file=sys.stderr)
        return 2

    with BundleReader(args.bundle) as reader:
        snaps_meta = reader.manifest.get("Snapshots") or []
        if not snaps_meta:
            print("error: bundle has no snapshots", file=sys.stderr)
            return 1

        rows: list[dict[str, Any]] = []
        best_gap = float("inf")
        runaway_elapsed = 0.0
        prev_t: float | None = None
        prev_follow_cs: str | None = None

        for i in range(len(snaps_meta)):
            entry = snaps_meta[i]
            t = entry["ElapsedSeconds"]
            if args.start is not None and t < args.start:
                continue
            if args.end is not None and t > args.end:
                break
            snap = reader.read_snapshot(i)
            by_cs = {
                (ac.get("Callsign") or "").upper(): ac
                for ac in (snap.get("Aircraft") or [])
            }
            present = {cs: by_cs.get(cs) for cs in callsigns}

            row: dict[str, Any] = {"t": t, "index": i}
            for cs in callsigns:
                ac = present[cs]
                if ac is None:
                    row[cs] = None
                    continue
                pos = ac.get("Position") or {}
                row[cs] = {
                    "lat": pos.get("Lat"),
                    "lon": pos.get("Lon"),
                    "alt": ac["Altitude"],
                    "ias": ac["IndicatedAirspeed"],
                    "hdg": ac["TrueHeadingDeg"],
                    "tgt_spd": (ac.get("Targets") or {}).get("TargetSpeed"),
                    "following": (ac.get("Approach") or {}).get("FollowingCallsign"),
                    "phase": _phase_name(ac),
                }

            if args.pair:
                a_cs, b_cs = args.pair[0].upper(), args.pair[1].upper()
                a, b = present.get(a_cs), present.get(b_cs)
                if a is not None and b is not None:
                    a_pos = a.get("Position") or {}
                    b_pos = b.get("Position") or {}
                    gap = _haversine_nm(a_pos["Lat"], a_pos["Lon"], b_pos["Lat"], b_pos["Lon"])
                    row["gap_nm"] = gap
                    follow_cs = (a.get("Approach") or {}).get("FollowingCallsign")
                    if follow_cs != prev_follow_cs:
                        best_gap = float("inf")
                        runaway_elapsed = 0.0
                    if follow_cs == b_cs:
                        delta = (t - prev_t) if prev_t is not None else 1.0
                        if gap <= best_gap:
                            best_gap = gap
                            runaway_elapsed = 0.0
                        else:
                            runaway_elapsed += delta
                        row["best_gap"] = best_gap
                        row["runaway_s"] = runaway_elapsed
                    prev_follow_cs = follow_cs
                prev_t = t
            rows.append(row)

        if args.json:
            write_output(json.dumps(rows, indent=2), args.out)
            return 0

        # Text table
        lines: list[str] = []
        header_cols = ["t"]
        for cs in callsigns:
            header_cols.append(f"{cs}.phase")
            header_cols.append(f"{cs}.ias")
            header_cols.append(f"{cs}.tgt")
            header_cols.append(f"{cs}.foll")
        if args.pair:
            header_cols.extend(["gap_nm", "best", "runaway_s"])
        lines.append(" | ".join(header_cols))
        lines.append("-+-".join("-" * max(6, len(c)) for c in header_cols))
        for row in rows:
            parts: list[str] = [f"{row['t']:4}"]
            for cs in callsigns:
                d = row.get(cs)
                if d is None:
                    parts.extend(["-", "-", "-", "-"])
                else:
                    tgt = d["tgt_spd"]
                    parts.append(f"{d['phase']:<14}")
                    parts.append(f"{d['ias']:5.1f}")
                    parts.append(f"{tgt:5.1f}" if isinstance(tgt, (int, float)) else "  -  ")
                    parts.append(f"{(d['following'] or '-'):<7}")
            if args.pair:
                gap = row.get("gap_nm")
                bg = row.get("best_gap")
                ra = row.get("runaway_s")
                parts.append(f"{gap:6.3f}" if isinstance(gap, (int, float)) else "   -  ")
                parts.append(f"{bg:5.2f}" if isinstance(bg, (int, float)) and bg != float("inf") else "  -  ")
                parts.append(f"{ra:5.1f}" if isinstance(ra, (int, float)) else "  -  ")
            lines.append(" | ".join(parts))
        write_output("\n".join(lines), args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: actions
# ---------------------------------------------------------------------------


def cmd_actions(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        actions = reader.read_actions()
        if args.json:
            write_output(json.dumps(actions, indent=2), args.out)
            return 0
        lines = []
        for a in actions:
            t = a.get("ElapsedSeconds", "?")
            kind = a.get("$type", "?")
            rest = {k: v for k, v in a.items() if k not in {"ElapsedSeconds", "$type"}}
            lines.append(f"t={t:>7} {kind:<24} {json.dumps(rest)}")
        write_output("\n".join(lines), args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: scenario / weather
# ---------------------------------------------------------------------------


def cmd_scenario(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        write_output(reader.read_scenario(), args.out)
    return 0


def cmd_weather(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        w = reader.read_weather()
        if w is None:
            print("bundle has no weather.json (HasWeather=false)", file=sys.stderr)
            return 1
        write_output(w, args.out)
    return 0


def cmd_artcc_config(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        cfg = reader.read_artcc_config()
        if cfg is None:
            print("bundle has no artcc-config.json.br (HasArtccConfig=false)", file=sys.stderr)
            return 1
        write_output(cfg, args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: layouts
# ---------------------------------------------------------------------------


def cmd_layouts(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        layout_ids = reader.manifest.get("LayoutAirportIds") or []

        if args.all:
            out_dir = args.out_dir or (DEFAULT_TMP / f"{args.bundle.stem}.layouts")
            out_dir.mkdir(parents=True, exist_ok=True)
            for aid in layout_ids:
                text = reader.read_layout(aid)
                path = out_dir / f"{aid}.json"
                path.write_text(text, encoding="utf-8", newline="\n")
                print(f"wrote {path}")
            return 0

        if args.airport:
            if args.airport not in layout_ids:
                print(
                    f"error: airport '{args.airport}' not in bundle layouts.\n"
                    f"       available: {', '.join(layout_ids) if layout_ids else '(none)'}",
                    file=sys.stderr,
                )
                return 1
            write_output(reader.read_layout(args.airport), args.out)
            return 0

        # Default: list airport IDs
        if not layout_ids:
            print("(no ground layouts in manifest)")
        else:
            for aid in layout_ids:
                print(aid)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: logs
# ---------------------------------------------------------------------------


def _bundle_slug(bundle_path: Path) -> str:
    """Strip all archive-format suffixes so '.tmp/' filenames stay tidy."""
    name = bundle_path.name
    for suffix in (
        ".yaat-bug-report-bundle.zip",
        ".yaat-recording.zip",
        ".zip",
    ):
        if name.lower().endswith(suffix):
            return name[: -len(suffix)]
    return bundle_path.stem


def cmd_logs(args: argparse.Namespace) -> int:
    out_dir = args.out_dir or DEFAULT_TMP
    out_dir.mkdir(parents=True, exist_ok=True)
    slug = _bundle_slug(args.bundle)
    with BundleReader(args.bundle) as reader:
        logs = reader.log_entries
        if not logs:
            print("bundle has no logs (yaat-client.log / yaat-server.log)", file=sys.stderr)
            return 1
        for name in logs:
            data = reader.read_plain(name, from_logs=True)
            path = out_dir / f"{slug}.{name}"
            write_bytes_output(data, path)
            print(path)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: install
# ---------------------------------------------------------------------------


# Matches GitHub's user-attachments URLs AND any public .zip URL agents might paste.
_ZIP_URL_RE = re.compile(
    r"https?://[^\s<>)\"']+?\.zip",
    re.IGNORECASE,
)


def _gh_fetch_issue_bodies(owner: str, repo: str, issue: int) -> list[str]:
    """Return list of strings (issue body + each comment body) via `gh issue view --json`."""
    cmd = [
        "gh", "issue", "view", str(issue),
        "--repo", f"{owner}/{repo}",
        "--json", "body,comments",
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        raise RuntimeError(f"gh issue view failed: {r.stderr.strip()}")
    data = json.loads(r.stdout)
    bodies = [data.get("body") or ""]
    for c in data.get("comments") or []:
        bodies.append(c.get("body") or "")
    return bodies


def _download(url: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"downloading {url} -> {dest}", file=sys.stderr)
    req = urllib.request.Request(url, headers={"User-Agent": "yaat-bug-bundle/1.0"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = resp.read()
    dest.write_bytes(data)
    print(f"  {len(data):,} bytes", file=sys.stderr)


def cmd_install(args: argparse.Namespace) -> int:
    # Validate naming args
    if not args.desc or not re.fullmatch(r"[a-z0-9][a-z0-9-]*", args.desc):
        print(
            "error: --desc must be a kebab-case slug (lowercase letters, digits, hyphens).\n"
            "       example: --desc oak-runway-exit",
            file=sys.stderr,
        )
        return 2
    if args.issue is not None and args.issue <= 0:
        print("error: --issue must be a positive integer when provided", file=sys.stderr)
        return 2

    prefix = f"issue{args.issue}-" if args.issue is not None else ""

    # Source bundle
    if args.bundle is not None:
        src = args.bundle
        if not src.exists():
            print(f"error: source file not found: {src}", file=sys.stderr)
            return 1
        # Decide extension based on source
        if src.name.endswith(".yaat-bug-report-bundle.zip"):
            ext = "-recording.yaat-bug-report-bundle.zip"
        else:
            ext = "-recording.zip"
        dest = TESTDATA_DIR / f"{prefix}{args.desc}{ext}"
        if dest.exists() and not args.force:
            print(f"error: {dest} already exists (use --force to overwrite)", file=sys.stderr)
            return 1
        shutil.copy2(src, dest)
        print(f"installed {src} -> {dest}")
    else:
        # Fetch from GitHub issue via gh — requires --issue
        if args.issue is None:
            print(
                "error: --issue N is required when no local bundle path is provided "
                "(needed to locate the GitHub issue's attachment)",
                file=sys.stderr,
            )
            return 2
        bodies = _gh_fetch_issue_bodies(args.owner, args.repo, args.issue)
        urls: list[str] = []
        for body in bodies:
            urls.extend(_ZIP_URL_RE.findall(body))
        if not urls:
            print(
                f"error: no .zip URLs found in issue #{args.issue} body or comments",
                file=sys.stderr,
            )
            return 1
        url = urls[0]
        print(f"found {len(urls)} .zip url(s); using first: {url}", file=sys.stderr)
        # Ext inferred from URL filename
        if ".yaat-bug-report-bundle.zip" in url.lower():
            ext = "-recording.yaat-bug-report-bundle.zip"
        else:
            ext = "-recording.zip"
        dest = TESTDATA_DIR / f"{prefix}{args.desc}{ext}"
        if dest.exists() and not args.force:
            print(f"error: {dest} already exists (use --force to overwrite)", file=sys.stderr)
            return 1
        _download(url, dest)
        print(f"installed {dest}")

    # Quick validate so we know we got a real v4 bundle
    try:
        with BundleReader(dest) as r:
            m = r.manifest
        print(
            f"  verified: v{m.get('Version')}, {len(m.get('Snapshots') or [])} snapshots, "
            f"{m.get('TotalElapsedSeconds')}s, {m.get('ArtccId')}"
        )
    except Exception as e:
        print(f"  warning: post-install validation failed: {e}", file=sys.stderr)
        return 1
    return 0


# ---------------------------------------------------------------------------
# Subcommand: validate
# ---------------------------------------------------------------------------


def _required_manifest_fields() -> list[str]:
    return ["Version", "RngSeed", "TotalElapsedSeconds", "ActionCount", "HasWeather", "Snapshots"]


def _validate_one(reader: BundleReader) -> Iterator[str]:
    """Yield error strings; empty iterator = all good."""
    m = reader.manifest
    for f in _required_manifest_fields():
        if f not in m:
            yield f"manifest missing field: {f}"

    snaps = m.get("Snapshots") or []
    for i, entry in enumerate(snaps):
        if "ElapsedSeconds" not in entry or "ActionIndex" not in entry:
            yield f"snapshot index {i} missing ElapsedSeconds/ActionIndex"
        name = f"snapshots/{i:03d}.json.br"
        if name not in reader.archive_entries:
            yield f"missing entry: {name}"
            continue
        try:
            reader.read_snapshot(i)
        except Exception as e:
            yield f"failed to decompress {name}: {e}"

    for required in ("scenario.json.br", "actions.json.br"):
        if required not in reader.archive_entries:
            yield f"missing entry: {required}"
            continue
        try:
            reader.read_brotli_text(required)
        except Exception as e:
            yield f"failed to decompress {required}: {e}"

    for aid in m.get("LayoutAirportIds") or []:
        name = f"layouts/{aid}.json.br"
        if name not in reader.archive_entries:
            yield f"missing layout entry: {name}"
            continue
        try:
            reader.read_layout(aid)
        except Exception as e:
            yield f"failed to decompress {name}: {e}"

    if m.get("HasWeather") and "weather.json" not in reader.archive_entries:
        yield "manifest HasWeather=true but weather.json missing"

    if m.get("HasArtccConfig"):
        if "artcc-config.json.br" not in reader.archive_entries:
            yield "manifest HasArtccConfig=true but artcc-config.json.br missing"
        else:
            try:
                reader.read_brotli_text("artcc-config.json.br")
            except Exception as e:
                yield f"failed to decompress artcc-config.json.br: {e}"


def cmd_validate(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        errors = list(_validate_one(reader))
    if not errors:
        m = reader.manifest
        print(
            f"OK: {args.bundle} - v{m.get('Version')}, "
            f"{len(m.get('Snapshots') or [])} snapshots, "
            f"{m.get('TotalElapsedSeconds')}s"
        )
        return 0
    print(f"FAIL: {args.bundle}", file=sys.stderr)
    for e in errors:
        print(f"  - {e}", file=sys.stderr)
    return 1


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def _add_bundle_arg(p: argparse.ArgumentParser) -> None:
    p.add_argument("bundle", type=Path, help="path to .zip bundle")


def _add_out_arg(p: argparse.ArgumentParser) -> None:
    p.add_argument("--out", type=Path, default=None, help="write output to file instead of stdout")


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="bug_bundle.py",
        description="Inspect, extract, install, and validate YAAT v4 bug bundles.",
    )
    sub = p.add_subparsers(dest="command", required=True, metavar="<command>")

    p_info = sub.add_parser("info", help="manifest summary + aircraft at t=0")
    _add_bundle_arg(p_info)
    _add_out_arg(p_info)
    p_info.add_argument("--json", action="store_true", help="structured JSON output")
    p_info.set_defaults(func=cmd_info)

    p_snap = sub.add_parser("snapshot", help="dump snapshot nearest to --at <seconds>")
    _add_bundle_arg(p_snap)
    _add_out_arg(p_snap)
    p_snap.add_argument("--at", type=float, required=True, help="target elapsed seconds")
    p_snap.add_argument("--callsign", type=str, default=None, help="filter to one aircraft")
    p_snap.set_defaults(func=cmd_snapshot)

    p_trk = sub.add_parser("track", help="time-series of aircraft state across all snapshots")
    _add_bundle_arg(p_trk)
    _add_out_arg(p_trk)
    p_trk.add_argument("--callsigns", nargs="+", default=None, help="one or more callsigns to track")
    p_trk.add_argument("--pair", nargs=2, default=None, metavar=("FOLLOWER", "LEADER"), help="compute gap + runaway timer between two callsigns")
    p_trk.add_argument("--start", type=float, default=None, help="only include snapshots at t >= START")
    p_trk.add_argument("--end", type=float, default=None, help="only include snapshots at t <= END")
    p_trk.add_argument("--json", action="store_true", help="structured JSON output")
    p_trk.set_defaults(func=cmd_track)

    p_act = sub.add_parser("actions", help="list recorded user actions")
    _add_bundle_arg(p_act)
    _add_out_arg(p_act)
    p_act.add_argument("--json", action="store_true", help="structured JSON output")
    p_act.set_defaults(func=cmd_actions)

    p_scen = sub.add_parser("scenario", help="decompress scenario.json.br")
    _add_bundle_arg(p_scen)
    _add_out_arg(p_scen)
    p_scen.set_defaults(func=cmd_scenario)

    p_wx = sub.add_parser("weather", help="print weather.json (if present)")
    _add_bundle_arg(p_wx)
    _add_out_arg(p_wx)
    p_wx.set_defaults(func=cmd_weather)

    p_artcc = sub.add_parser("artcc-config", help="print artcc-config.json.br (if present)")
    _add_bundle_arg(p_artcc)
    _add_out_arg(p_artcc)
    p_artcc.set_defaults(func=cmd_artcc_config)

    p_lay = sub.add_parser("layouts", help="list/dump ground layouts")
    _add_bundle_arg(p_lay)
    _add_out_arg(p_lay)
    p_lay.add_argument("--airport", type=str, default=None, help="dump one layout by airport id")
    p_lay.add_argument("--all", action="store_true", help="dump every layout into --out-dir")
    p_lay.add_argument("--out-dir", type=Path, default=None, help="directory for --all output")
    p_lay.set_defaults(func=cmd_layouts)

    p_log = sub.add_parser("logs", help="extract yaat-client.log / yaat-server.log to .tmp/")
    _add_bundle_arg(p_log)
    p_log.add_argument("--out-dir", type=Path, default=None, help=f"output directory (default: {DEFAULT_TMP})")
    p_log.set_defaults(func=cmd_logs)

    p_ins = sub.add_parser(
        "install",
        help="copy bundle into TestData with [issue{N}-]{desc}-... naming (local path or --issue)",
    )
    p_ins.add_argument("bundle", type=Path, nargs="?", default=None, help="local bundle path (omit to fetch from --issue)")
    p_ins.add_argument(
        "--issue",
        type=int,
        default=None,
        help="GitHub issue number; required when fetching from GitHub, optional when installing a local bundle (omit for descriptive non-numbered name)",
    )
    p_ins.add_argument("--desc", type=str, required=True, help="kebab-case slug for filename")
    p_ins.add_argument("--owner", type=str, default="leftos", help="GitHub owner (default: leftos)")
    p_ins.add_argument("--repo", type=str, default="yaat", help="GitHub repo (default: yaat)")
    p_ins.add_argument("--force", action="store_true", help="overwrite existing TestData file")
    p_ins.set_defaults(func=cmd_install)

    p_val = sub.add_parser("validate", help="check manifest + entry integrity")
    _add_bundle_arg(p_val)
    p_val.set_defaults(func=cmd_validate)

    return p


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
