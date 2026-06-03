#!/usr/bin/env python3
"""Inspect, extract, install, and validate YAAT v4 bug bundles.

Usage:
    python tools/bug_bundle.py <subcommand> [args] [flags]

Subcommands:
    info      Manifest summary + aircraft callsigns at t=0.
    snapshot  Dump snapshot nearest to --at <seconds> (optionally filtered by --callsign).
    track     Time-series of aircraft state across all snapshots (text or --json).
    actions   List recorded user actions chronologically.
    history   Per-callsign chronological events: commands + phase / route / target / approach changes.
    phases    Per-callsign phase-transition timeline (subset of history).
    commands  Actions filtered to one recipient callsign.
    scenario  Decompress scenario.json.br.
    weather   Print weather.json (if present).
    layouts   List ground layout airport IDs, or dump one with --airport / all with --all.
    logs      Extract yaat-client.log / yaat-server.log to .tmp/ (or --out-dir).
    install   Copy bundle into tests/Yaat.Sim.Tests/TestData/ with issue{N}-{desc}-... naming.
              Either a local path, or --issue N to fetch the attachment from a GitHub issue.
    trim      Drop snapshots past --max-seconds (or keep first --max-snapshots) to shrink
              the bundle. Actions/scenario/logs preserved. Edits in place unless --out.
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


def force_utf8_stdio() -> None:
    """Make stdout/stderr UTF-8 so bundle content with non-ASCII characters
    (e.g. the U+2713 check mark in preset commands, or unicode in routes and
    remarks) doesn't crash on Windows' legacy cp1252 console. The
    ``backslashreplace`` error handler is a fallback for the rare stream that
    cannot switch to UTF-8."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="backslashreplace")


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


def pretty_json(text: str) -> str:
    """Re-serialize a JSON string with indent=2 for greppability.

    Bundle entries (scenario, weather, artcc-config, layouts) come out of
    Brotli decompression as whatever the server wrote — usually one giant line.
    Falls back to the original text if the payload isn't valid JSON.
    """
    try:
        return json.dumps(json.loads(text), indent=2, ensure_ascii=False)
    except (json.JSONDecodeError, ValueError):
        return text


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
# Subcommand: history / phases / commands
# ---------------------------------------------------------------------------


_PATTERN_ENTRY_KIND = {
    0: "Direct",
    1: "FortyFive",
    2: "Crosswind",
    3: "Upwind",
    4: "Base",
    5: "Final",
}


def _format_phase(phase: dict[str, Any]) -> str:
    """Render a single phase dict as 'Name { key=val, ... }'.

    Used by `history` / `phases` for verbose output. Compact single-name view
    in `cmd_track` continues to use `_phase_name`.
    """
    full = phase.get("$type") or "?"
    short = full.removesuffix("Phase") if full.endswith("Phase") else full
    extras: list[str] = []
    if full == "PatternEntryPhase":
        kind_idx = phase.get("Kind")
        kind_name = _PATTERN_ENTRY_KIND.get(kind_idx, str(kind_idx))
        extras.append(f"Kind={kind_name}")
        lat = phase.get("EntryLat")
        lon = phase.get("EntryLon")
        if isinstance(lat, (int, float)) and isinstance(lon, (int, float)):
            extras.append(f"Entry={lat:.4f}/{lon:.4f}")
        alt = phase.get("PatternAltitude")
        if isinstance(alt, (int, float)):
            extras.append(f"PatAlt={alt:.0f}")
    elif full == "FinalApproachPhase":
        fac = phase.get("FinalApproachCourseDeg")
        if isinstance(fac, (int, float)) and fac:
            extras.append(f"FAC={fac:.0f}")
        lat = phase.get("AnchorLat")
        lon = phase.get("AnchorLon")
        if isinstance(lat, (int, float)) and isinstance(lon, (int, float)):
            extras.append(f"Anchor={lat:.4f}/{lon:.4f}")
    elif full == "LandingPhase":
        rh = phase.get("RunwayHeadingDeg")
        if isinstance(rh, (int, float)) and rh:
            extras.append(f"RwyHdg={rh:.0f}")
    if extras:
        return f"{short} {{ {', '.join(extras)} }}"
    return short


def _phase_chain_signature(phases: dict[str, Any] | None) -> tuple[str, ...]:
    """Identity of a phase chain — the ordered list of phase $types."""
    if phases is None:
        return ()
    return tuple((p.get("$type") or "?") for p in (phases.get("Phases") or []))


def _phase_chain_str(phases: dict[str, Any] | None) -> str:
    """Render a PhaseList as 'A -> B -> [C] -> D' with [] marking the active phase."""
    if phases is None:
        return "(none)"
    plist = phases.get("Phases") or []
    if not plist:
        return "(none)"
    idx = phases.get("CurrentIndex")
    parts: list[str] = []
    for i, p in enumerate(plist):
        s = _format_phase(p)
        if i == idx:
            s = f"[{s}]"
        parts.append(s)
    return " -> ".join(parts)


def _route_names(targets: dict[str, Any] | None) -> list[str]:
    if not targets:
        return []
    return [(w.get("Name") or "?") for w in (targets.get("NavigationRoute") or [])]


def _route_str(names: list[str]) -> str:
    return "[" + ", ".join(names) + "]" if names else "[]"


_TGT_KEYS: tuple[tuple[str, str], ...] = (
    ("AssignedAltitude", "AssignedAlt"),
    ("AssignedSpeed", "AssignedSpd"),
    ("AssignedMagneticHeadingDeg", "AssignedMagHdg"),
)
_APPR_KEYS: tuple[tuple[str, str], ...] = (
    ("Expected", "Expected"),
    ("PendingClearance", "PendingClearance"),
    ("FollowingCallsign", "Following"),
    ("HasReportedFieldInSight", "FieldInSight"),
    ("HasReportedTrafficInSight", "TrafficInSight"),
)
_TRACK_KEYS: tuple[tuple[str, str], ...] = (
    ("Owner", "Owner"),
    ("OnHandoff", "OnHandoff"),
    ("HandoffPeer", "HandoffPeer"),
)


def _diff_dict_keys(prev: dict[str, Any], curr: dict[str, Any], keys: tuple[tuple[str, str], ...]) -> list[str]:
    """For each (json_key, label) pair, emit '<label>=<prev>-><curr>' if changed."""
    changes: list[str] = []
    for json_key, label in keys:
        pv = prev.get(json_key)
        cv = curr.get(json_key)
        if pv != cv:
            changes.append(f"{label}={pv!r}->{cv!r}")
    return changes


def _diff_aircraft(
    prev: dict[str, Any] | None,
    curr: dict[str, Any] | None,
) -> Iterator[tuple[str, str, dict[str, Any]]]:
    """Yield (tag, detail, raw) for each state change between consecutive snapshots."""
    if prev is None and curr is None:
        return
    if prev is None:
        # First time we see this aircraft. Emit SPAWN, then synthesize an
        # initial-state diff so non-default fields show up as their first event.
        pos = curr.get("Position") or {} if curr is not None else {}
        lat = pos.get("Lat")
        lon = pos.get("Lon")
        hdg = (curr or {}).get("TrueHeadingDeg")
        alt = (curr or {}).get("Altitude")
        bits: list[str] = []
        if isinstance(lat, (int, float)) and isinstance(lon, (int, float)):
            bits.append(f"pos={lat:.4f}/{lon:.4f}")
        if isinstance(hdg, (int, float)):
            bits.append(f"hdg={hdg:.0f}")
        if isinstance(alt, (int, float)):
            bits.append(f"alt={alt:.0f}")
        yield ("SPAWN", "appeared" + (": " + ", ".join(bits) if bits else ""), {"position": pos, "heading": hdg, "altitude": alt})
        yield from _diff_aircraft({}, curr)
        return
    if curr is None:
        yield ("DESPAWN", "removed from snapshot", {})
        return

    # Phase chain diff
    prev_phases = prev.get("Phases")
    curr_phases = curr.get("Phases")
    prev_sig = _phase_chain_signature(prev_phases)
    curr_sig = _phase_chain_signature(curr_phases)

    if prev_sig != curr_sig:
        if not curr_sig:
            yield ("PHASE-", "phases cleared", {"prev_chain": list(prev_sig)})
        elif not prev_sig:
            yield ("PHASES", _phase_chain_str(curr_phases), {"chain": curr_phases})
        else:
            yield ("PHASES", f"rebuilt: {_phase_chain_str(curr_phases)}", {"prev_chain": list(prev_sig), "chain": curr_phases})
    elif prev_phases is not None and curr_phases is not None:
        prev_idx = prev_phases.get("CurrentIndex", -1)
        curr_idx = curr_phases.get("CurrentIndex", -1)
        if prev_idx != curr_idx:
            plist = curr_phases.get("Phases") or []
            if isinstance(curr_idx, int) and 0 <= curr_idx < len(plist):
                yield ("PHASE+", _format_phase(plist[curr_idx]), {"index": curr_idx, "phase": plist[curr_idx]})
            else:
                yield ("PHASE", f"index {prev_idx}->{curr_idx}", {"prev_idx": prev_idx, "curr_idx": curr_idx})

    # Route diff
    prev_route = _route_names(prev.get("Targets"))
    curr_route = _route_names(curr.get("Targets"))
    if prev_route != curr_route:
        if prev_route:
            yield ("ROUTE", f"{_route_str(curr_route):<28} (was {_route_str(prev_route)})", {"prev": prev_route, "curr": curr_route})
        else:
            yield ("ROUTE", _route_str(curr_route), {"prev": prev_route, "curr": curr_route})

    # Targets diff (assigned values only — not the moment-by-moment TargetTrueHeadingDeg etc.)
    prev_tgt = prev.get("Targets") or {}
    curr_tgt = curr.get("Targets") or {}
    tgt_changes = _diff_dict_keys(prev_tgt, curr_tgt, _TGT_KEYS)
    if tgt_changes:
        yield ("TGT", ", ".join(tgt_changes), {
            "prev": {k: prev_tgt.get(k) for k, _ in _TGT_KEYS},
            "curr": {k: curr_tgt.get(k) for k, _ in _TGT_KEYS},
        })

    # Approach diff
    prev_appr = prev.get("Approach") or {}
    curr_appr = curr.get("Approach") or {}
    appr_changes = _diff_dict_keys(prev_appr, curr_appr, _APPR_KEYS)
    if appr_changes:
        yield ("APPR", ", ".join(appr_changes), {"prev": prev_appr, "curr": curr_appr})

    # Track ownership diff
    prev_track = prev.get("Track") or {}
    curr_track = curr.get("Track") or {}
    trk_changes = _diff_dict_keys(prev_track, curr_track, _TRACK_KEYS)
    if trk_changes:
        yield ("TRACK", ", ".join(trk_changes), {"prev": prev_track, "curr": curr_track})

    # Runway diff
    prev_rwy = (prev.get("Procedure") or {}).get("DestinationRunway")
    curr_rwy = (curr.get("Procedure") or {}).get("DestinationRunway")
    if prev_rwy != curr_rwy:
        yield ("RWY", f"DestinationRunway={prev_rwy!r}->{curr_rwy!r}", {"prev": prev_rwy, "curr": curr_rwy})


def _iter_aircraft_states(
    reader: BundleReader,
    callsign_upper: str,
    start: float | None,
    end: float | None,
) -> Iterator[tuple[int, float, dict[str, Any] | None]]:
    """Yield (snap_idx, t, ac_dict_or_None) for snapshots in [start, end] (inclusive)."""
    snaps_meta = reader.manifest.get("Snapshots") or []
    for i, entry in enumerate(snaps_meta):
        t = entry["ElapsedSeconds"]
        if start is not None and t < start:
            continue
        if end is not None and t > end:
            break
        snap = reader.read_snapshot(i)
        ac = next(
            (cand for cand in (snap.get("Aircraft") or []) if (cand.get("Callsign") or "").upper() == callsign_upper),
            None,
        )
        yield i, t, ac


def _filter_actions_for_callsign(
    actions: list[dict[str, Any]],
    callsign_upper: str,
    include_global: bool,
) -> list[dict[str, Any]]:
    """Actions whose Callsign matches (case-insensitive). With include_global, also keep actions that have no Callsign."""
    out: list[dict[str, Any]] = []
    for a in actions:
        cs = a.get("Callsign")
        if cs is None:
            if include_global:
                out.append(a)
            continue
        if cs.upper() == callsign_upper:
            out.append(a)
    return out


def _enumerate_callsigns(reader: BundleReader) -> list[str]:
    """All callsigns appearing in any snapshot. Sorted."""
    snaps_meta = reader.manifest.get("Snapshots") or []
    seen: set[str] = set()
    for i in range(len(snaps_meta)):
        snap = reader.read_snapshot(i)
        for ac in (snap.get("Aircraft") or []):
            cs = ac.get("Callsign")
            if cs:
                seen.add(cs)
    return sorted(seen)


def _format_action_detail(a: dict[str, Any]) -> str:
    """Compact one-liner for an action: '<Command>' or '<$type> <extras>'."""
    cmd = a.get("Command")
    if cmd:
        return cmd
    kind = a.get("$type") or "?"
    extras = {k: v for k, v in a.items() if k not in {"ElapsedSeconds", "$type", "Callsign", "Command", "Initials", "ConnectionId"}}
    if extras:
        return f"{kind} {json.dumps(extras, separators=(',', ':'))}"
    return kind


def _action_snap_idx(snaps_meta: list[dict[str, Any]], action_t: float) -> int | None:
    """First snapshot index whose ElapsedSeconds >= action_t, or None if past the end."""
    for i, entry in enumerate(snaps_meta):
        if entry["ElapsedSeconds"] >= action_t:
            return i
    return None


def cmd_history(args: argparse.Namespace) -> int:
    cs_upper = args.callsign.upper()
    with BundleReader(args.bundle) as reader:
        snaps_meta = reader.manifest.get("Snapshots") or []
        all_actions = reader.read_actions()
        my_actions = _filter_actions_for_callsign(all_actions, cs_upper, args.include_global)
        if args.start is not None:
            my_actions = [a for a in my_actions if a.get("ElapsedSeconds", 0) >= args.start]
        if args.end is not None:
            my_actions = [a for a in my_actions if a.get("ElapsedSeconds", 0) <= args.end]

        snapshot_events: list[tuple[float, int | None, str, str, dict[str, Any]]] = []
        prev_ac: dict[str, Any] | None = None
        seen_callsign = False
        for snap_idx, t, ac in _iter_aircraft_states(reader, cs_upper, args.start, args.end):
            if ac is not None:
                seen_callsign = True
            for tag, detail, raw in _diff_aircraft(prev_ac, ac):
                snapshot_events.append((t, snap_idx, tag, detail, raw))
            prev_ac = ac

        if not seen_callsign and not my_actions:
            available = _enumerate_callsigns(reader)
            print(
                f"error: callsign '{args.callsign}' not in any snapshot or action.\n"
                f"       available: {', '.join(available) if available else '(none)'}",
                file=sys.stderr,
            )
            return 1

        action_events: list[tuple[float, int | None, str, str, dict[str, Any]]] = []
        for a in my_actions:
            t = a.get("ElapsedSeconds", 0)
            kind = a.get("$type") or "?"
            tag = "CMD" if kind == "Command" else kind[:6].upper()
            detail = _format_action_detail(a)
            action_events.append((t, _action_snap_idx(snaps_meta, t), tag, detail, {"action": a}))

        # Merge: at the same t, actions sort before the snapshot diffs that they caused.
        all_events = sorted(
            action_events + snapshot_events,
            key=lambda e: (e[0], 0 if e[2] == "CMD" else 1),
        )

        if args.json:
            payload = [
                {"t": t, "snap": snap, "tag": tag, "detail": detail, "callsign": args.callsign, "raw": raw}
                for (t, snap, tag, detail, raw) in all_events
            ]
            write_output(json.dumps(payload, indent=2), args.out)
            return 0

        lines: list[str] = []
        for t, snap, tag, detail, _ in all_events:
            snap_str = f"snap={snap:>3}" if snap is not None else "snap=  -"
            lines.append(f"t={t:>5} [{snap_str}] {tag:<6} {detail}")
        write_output("\n".join(lines), args.out)
    return 0


def cmd_phases(args: argparse.Namespace) -> int:
    cs_upper = args.callsign.upper()
    with BundleReader(args.bundle) as reader:
        events: list[tuple[float, int, str, str, dict[str, Any]]] = []
        prev_ac: dict[str, Any] | None = None
        seen_callsign = False
        for snap_idx, t, ac in _iter_aircraft_states(reader, cs_upper, args.start, args.end):
            if ac is not None:
                seen_callsign = True
            for tag, detail, raw in _diff_aircraft(prev_ac, ac):
                if tag in {"PHASES", "PHASE+", "PHASE-", "PHASE"}:
                    events.append((t, snap_idx, tag, detail, raw))
            prev_ac = ac

        if not seen_callsign:
            available = _enumerate_callsigns(reader)
            print(
                f"error: callsign '{args.callsign}' not in any snapshot.\n"
                f"       available: {', '.join(available) if available else '(none)'}",
                file=sys.stderr,
            )
            return 1

        if args.json:
            payload = [
                {"t": t, "snap": snap, "tag": tag, "detail": detail, "callsign": args.callsign, "raw": raw}
                for (t, snap, tag, detail, raw) in events
            ]
            write_output(json.dumps(payload, indent=2), args.out)
            return 0

        if not events:
            print("(no phase transitions in range)", file=sys.stderr)
            return 0

        lines = [f"t={t:>5} [snap={snap:>3}] {tag:<6} {detail}" for (t, snap, tag, detail, _) in events]
        write_output("\n".join(lines), args.out)
    return 0


def cmd_commands(args: argparse.Namespace) -> int:
    cs_upper = args.callsign.upper()
    with BundleReader(args.bundle) as reader:
        actions = reader.read_actions()
        matched: list[dict[str, Any]] = []
        for a in actions:
            cs = a.get("Callsign")
            if not cs or cs.upper() != cs_upper:
                continue
            t = a.get("ElapsedSeconds", 0)
            if args.start is not None and t < args.start:
                continue
            if args.end is not None and t > args.end:
                continue
            matched.append(a)

        if not matched:
            available = sorted({a.get("Callsign") for a in actions if a.get("Callsign")})
            print(
                f"error: no actions for callsign '{args.callsign}'.\n"
                f"       available: {', '.join(available) if available else '(none)'}",
                file=sys.stderr,
            )
            return 1

        if args.json:
            write_output(json.dumps(matched, indent=2), args.out)
            return 0

        lines: list[str] = []
        for a in matched:
            t = a.get("ElapsedSeconds", 0)
            kind = a.get("$type") or "?"
            detail = _format_action_detail(a)
            lines.append(f"t={t:>5} {kind:<10} {detail}")
        write_output("\n".join(lines), args.out)
    return 0


# ---------------------------------------------------------------------------
# Subcommand: scenario / weather
# ---------------------------------------------------------------------------


def cmd_scenario(args: argparse.Namespace) -> int:
    aircraft_filter: list[str] | None = [c.upper() for c in args.aircraft] if args.aircraft else None
    show: str = getattr(args, "show", "full")

    with BundleReader(args.bundle) as reader:
        scenario_text = reader.read_scenario()

        if aircraft_filter is None and show == "full":
            write_output(pretty_json(scenario_text), args.out)
            return 0

        try:
            scenario = json.loads(scenario_text)
        except (json.JSONDecodeError, ValueError) as exc:
            print(f"error: scenario is not valid JSON: {exc}", file=sys.stderr)
            return 1

        all_aircraft = scenario.get("aircraft") or []

        def _callsign(a: dict) -> str:
            return str(a.get("aircraftId") or (a.get("flightplan") or {}).get("callsign") or "")

        if aircraft_filter is not None:
            wanted = set(aircraft_filter)
            matched = [a for a in all_aircraft if _callsign(a).upper() in wanted]
            if not matched:
                avail = ", ".join(sorted({_callsign(a) for a in all_aircraft if _callsign(a)}))
                print(f"error: no scenario aircraft matched {sorted(wanted)}.\n       available: {avail}", file=sys.stderr)
                return 1
        else:
            matched = list(all_aircraft)

        if show == "full":
            payload = matched if aircraft_filter is not None else scenario
            write_output(json.dumps(payload, indent=2, ensure_ascii=False), args.out)
            return 0

        if show == "presets":
            payload = [
                {
                    "callsign": _callsign(a),
                    "spawnDelay": a.get("spawnDelay"),
                    "presetCommands": a.get("presetCommands") or [],
                }
                for a in matched
            ]
            write_output(json.dumps(payload, indent=2, ensure_ascii=False), args.out)
            return 0

        if show == "spawns":
            payload = [
                {
                    "callsign": _callsign(a),
                    "aircraftType": a.get("aircraftType"),
                    "airportId": a.get("airportId"),
                    "startingConditions": a.get("startingConditions"),
                    "onAltitudeProfile": a.get("onAltitudeProfile"),
                    "spawnDelay": a.get("spawnDelay"),
                }
                for a in matched
            ]
            write_output(json.dumps(payload, indent=2, ensure_ascii=False), args.out)
            return 0

        if show == "summary":
            lines = []
            header = f"{'callsign':<10} {'type':<6} {'apt':<5} {'rules':<5} {'dep->dest':<14} {'start':<28} {'spawn':>7}  presets"
            lines.append(header)
            lines.append("-" * len(header))
            for a in matched:
                cs = _callsign(a) or "?"
                fp = a.get("flightplan") or {}
                sc = a.get("startingConditions") or {}
                start_desc = _describe_starting_conditions(sc)
                presets = a.get("presetCommands") or []
                preset_text = " ; ".join((c.get("command") or "?") for c in presets) or "(none)"
                lines.append(
                    f"{cs:<10} "
                    f"{(a.get('aircraftType') or '?'):<6} "
                    f"{(a.get('airportId') or '?'):<5} "
                    f"{(fp.get('rules') or '?'):<5} "
                    f"{(fp.get('departure') or '?')+'->'+(fp.get('destination') or '?'):<14} "
                    f"{start_desc:<28} "
                    f"{(a.get('spawnDelay') if a.get('spawnDelay') is not None else '-')!s:>7}  "
                    f"{preset_text}"
                )
            write_output("\n".join(lines), args.out)
            return 0

        print(f"error: unknown --show value '{show}'", file=sys.stderr)
        return 1


def _describe_starting_conditions(sc: dict) -> str:
    if not sc:
        return "(none)"
    t = sc.get("type") or "?"
    if t == "Parking":
        return f"Parking {sc.get('parking') or '?'}"
    if t == "FixOrFrd":
        alt = sc.get("altitude")
        nav = sc.get("navigationPath")
        return f"Fix {sc.get('fix') or '?'} alt={alt} via={nav}"
    if t == "Position":
        return f"Pos {sc.get('latitude')},{sc.get('longitude')} alt={sc.get('altitude')} hdg={sc.get('heading')}"
    return t


def cmd_weather(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        w = reader.read_weather()
        if w is None:
            print("bundle has no weather.json (HasWeather=false)", file=sys.stderr)
            return 1
        write_output(pretty_json(w), args.out)
    return 0


def cmd_artcc_config(args: argparse.Namespace) -> int:
    with BundleReader(args.bundle) as reader:
        cfg = reader.read_artcc_config()
        if cfg is None:
            print("bundle has no artcc-config.json.br (HasArtccConfig=false)", file=sys.stderr)
            return 1
        write_output(pretty_json(cfg), args.out)
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
                text = pretty_json(reader.read_layout(aid))
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
            write_output(pretty_json(reader.read_layout(args.airport)), args.out)
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


def cmd_trim(args: argparse.Namespace) -> int:
    """Drop snapshots past --max-seconds (or keep only first --max-snapshots) to shrink the bundle.

    Actions, scenario, weather, ARTCC config, layouts, and logs are preserved unchanged.
    The manifest's Snapshots index is rewritten to match the surviving snapshot files.
    TotalElapsedSeconds is left alone — it reflects the original recording duration and
    is informational, not load-bearing for replay or snapshot lookup.
    """
    src: Path = args.bundle
    out: Path = args.out if args.out is not None else src

    if args.max_seconds is None and args.max_snapshots is None:
        print("error: trim requires --max-seconds or --max-snapshots", file=sys.stderr)
        return 2
    if args.max_seconds is not None and args.max_snapshots is not None:
        print("error: pass only one of --max-seconds / --max-snapshots", file=sys.stderr)
        return 2

    with zipfile.ZipFile(src, "r") as zin:
        if "manifest.json" not in zin.namelist():
            print(f"error: {src} has no manifest.json — not a v4 bundle", file=sys.stderr)
            return 1
        with zin.open("manifest.json") as f:
            manifest = json.load(f)

        snapshots = manifest.get("Snapshots") or []
        if not snapshots:
            print(f"error: bundle has no Snapshots — nothing to trim", file=sys.stderr)
            return 1

        if args.max_seconds is not None:
            keep_count = sum(1 for s in snapshots if float(s.get("ElapsedSeconds", 0)) <= args.max_seconds)
        else:
            keep_count = min(args.max_snapshots, len(snapshots))

        if keep_count <= 0:
            print("error: trim would drop every snapshot; refusing", file=sys.stderr)
            return 1
        if keep_count >= len(snapshots):
            print(f"nothing to trim: keep_count={keep_count} >= total={len(snapshots)}")
            return 0

        dropped = len(snapshots) - keep_count
        cutoff_t = float(snapshots[keep_count - 1].get("ElapsedSeconds", 0))
        print(f"keeping {keep_count}/{len(snapshots)} snapshots (through t={cutoff_t:.0f}s), dropping {dropped}")

        tmp_out = out.with_name(out.name + ".trim.tmp")
        with zipfile.ZipFile(tmp_out, "w", zipfile.ZIP_STORED) as zout:
            for info in zin.infolist():
                if info.filename == "manifest.json":
                    new_manifest = dict(manifest)
                    new_manifest["Snapshots"] = snapshots[:keep_count]
                    new_data = json.dumps(new_manifest, separators=(",", ":")).encode("utf-8")
                    ni = zipfile.ZipInfo(info.filename)
                    ni.compress_type = zipfile.ZIP_STORED
                    zout.writestr(ni, new_data)
                elif info.filename.startswith("snapshots/"):
                    base = info.filename[len("snapshots/") :]
                    idx_str = base.split(".", 1)[0]
                    try:
                        idx = int(idx_str)
                    except ValueError:
                        print(f"warn: cannot parse snapshot filename {info.filename}; copying as-is", file=sys.stderr)
                        with zin.open(info) as fin:
                            zout.writestr(info, fin.read())
                        continue
                    if idx < keep_count:
                        with zin.open(info) as fin:
                            zout.writestr(info, fin.read())
                else:
                    with zin.open(info) as fin:
                        zout.writestr(info, fin.read())

    # Replace destination atomically. Reading the source ZIP is done by the time we
    # close the with block above, so writing back over the same file is safe.
    shutil.move(str(tmp_out), str(out))
    size_kb = out.stat().st_size / 1024
    print(f"wrote {out} ({size_kb:.0f} KB)")
    return 0


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

    p_hist = sub.add_parser("history", help="per-callsign chronological events (commands + phase / route / target / approach changes)")
    _add_bundle_arg(p_hist)
    _add_out_arg(p_hist)
    p_hist.add_argument("--callsign", type=str, required=True, help="aircraft callsign (case-insensitive)")
    p_hist.add_argument("--start", type=float, default=None, help="only include events at t >= START")
    p_hist.add_argument("--end", type=float, default=None, help="only include events at t <= END")
    p_hist.add_argument("--include-global", action="store_true", help="also show actions without a Callsign field")
    p_hist.add_argument("--json", action="store_true", help="structured JSON output")
    p_hist.set_defaults(func=cmd_history)

    p_ph = sub.add_parser("phases", help="per-callsign phase-transition timeline")
    _add_bundle_arg(p_ph)
    _add_out_arg(p_ph)
    p_ph.add_argument("--callsign", type=str, required=True, help="aircraft callsign (case-insensitive)")
    p_ph.add_argument("--start", type=float, default=None, help="only include events at t >= START")
    p_ph.add_argument("--end", type=float, default=None, help="only include events at t <= END")
    p_ph.add_argument("--json", action="store_true", help="structured JSON output")
    p_ph.set_defaults(func=cmd_phases)

    p_cmds = sub.add_parser("commands", help="actions filtered to one recipient callsign")
    _add_bundle_arg(p_cmds)
    _add_out_arg(p_cmds)
    p_cmds.add_argument("--callsign", type=str, required=True, help="aircraft callsign (case-insensitive)")
    p_cmds.add_argument("--start", type=float, default=None, help="only include actions at t >= START")
    p_cmds.add_argument("--end", type=float, default=None, help="only include actions at t <= END")
    p_cmds.add_argument("--json", action="store_true", help="structured JSON output")
    p_cmds.set_defaults(func=cmd_commands)

    p_scen = sub.add_parser(
        "scenario",
        help="decompress scenario.json.br (full JSON, or filter to specific aircraft / fields)",
    )
    _add_bundle_arg(p_scen)
    _add_out_arg(p_scen)
    p_scen.add_argument(
        "--aircraft",
        nargs="+",
        default=None,
        metavar="CALLSIGN",
        help="filter to one or more aircraft by callsign (case-insensitive)",
    )
    p_scen.add_argument(
        "--show",
        choices=("full", "presets", "spawns", "summary"),
        default="full",
        help=(
            "what to print per matched aircraft: full block (default), preset commands only, "
            "starting conditions only, or a one-line summary table across all matched aircraft"
        ),
    )
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

    p_trim = sub.add_parser(
        "trim",
        help="drop snapshots past --max-seconds (or keep first --max-snapshots) to shrink the bundle",
    )
    _add_bundle_arg(p_trim)
    p_trim.add_argument("--max-seconds", type=float, default=None, help="keep only snapshots with ElapsedSeconds <= N")
    p_trim.add_argument("--max-snapshots", type=int, default=None, help="keep only the first N snapshots in index order")
    p_trim.add_argument("--out", type=Path, default=None, help="write to PATH instead of overwriting the input bundle")
    p_trim.set_defaults(func=cmd_trim)

    p_val = sub.add_parser("validate", help="check manifest + entry integrity")
    _add_bundle_arg(p_val)
    p_val.set_defaults(func=cmd_validate)

    return p


def main(argv: list[str] | None = None) -> int:
    force_utf8_stdio()
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
