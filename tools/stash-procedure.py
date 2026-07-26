#!/usr/bin/env python3
"""Stash a published procedure as an ARTCC CIFP fragment before it ages out.

The FAA sometimes drops a still-charted procedure from the CIFP dataset (KOAK's NIMITZ SID is the
motivating case). YAAT recovers such a procedure by walking cached prior AIRAC cycles, but only for
~12 months and only on a machine that happens to hold the right cycle. Copying the records into
Data/ARTCCs/{ARTCC}/Procedures/*.cifp pins them permanently and identically on every deployment.

This finds the newest AIRAC source that still carries the procedure and writes the verbatim records
there. Nothing is reformatted, so CifpParser reads the fragment exactly as it reads a full cycle.

Usage:
    python tools/stash-procedure.py NIMI --airport KOAK
    python tools/stash-procedure.py NIMI --airport KOAK --dry-run
    python tools/stash-procedure.py BDEGA4 --airport KSFO --artcc ZOA --kind star
    python tools/stash-procedure.py NIMI --airport KOAK --search-path D:/archive/cifp --fetch
"""

from __future__ import annotations

import argparse
import gzip
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ARTCCS_DIR = REPO_ROOT / "src" / "Yaat.Sim" / "Data" / "ARTCCs"
BUNDLED_CIFP_GZ = REPO_ROOT / "tests" / "Yaat.Sim.Tests" / "TestData" / "FAACIFP18.gz"

AIRAC_EPOCH = date(2025, 1, 23)
CYCLE_DAYS = 28
CYCLES_PER_YEAR = 13
FAA_CIFP_URL = "https://aeronav.faa.gov/Upload_313-d/cifp/CIFP_{yymmdd}.zip"

# ARINC 424 column offsets. These mirror CifpParser exactly: a line it would skip must be a line we
# skip, or the fragment we write would not parse back to the procedure we think we captured.
MIN_RECORD_LENGTH = 100
ICAO_COLS = slice(6, 10)
SUBSECTION_COL = 12
PROCEDURE_ID_COLS = slice(13, 19)
# Terminal-waypoint (PC) records: section P, subsection C, waypoint id at [13..18].
WAYPOINT_SUBSECTION = "C"
WAYPOINT_ID_COLS = slice(13, 18)

SUBSECTIONS = {"sid": "D", "star": "E", "approach": "F"}
KIND_NAMES = {"D": "SID", "E": "STAR", "F": "approach"}


# ---------------------------------------------------------------------------
# AIRAC helpers
# ---------------------------------------------------------------------------


def current_airac_cycle(today: date | None = None) -> str:
    today = today or datetime.now(timezone.utc).date()
    total_days = (today - AIRAC_EPOCH).days
    if total_days < 0:
        return "2501"
    cycle_index = total_days // CYCLE_DAYS
    year = 2025 + cycle_index // CYCLES_PER_YEAR
    cycle_in_year = cycle_index % CYCLES_PER_YEAR + 1
    return f"{year % 100:02d}{cycle_in_year:02d}"


def cycle_index(cycle_id: str) -> int:
    """Linear cycle number since the epoch, so ordering survives the year wrap (2513 -> 2601)."""
    year = 2000 + int(cycle_id[:2])
    number = int(cycle_id[2:])
    return (year - 2025) * CYCLES_PER_YEAR + (number - 1)


def cycle_effective_date(cycle_id: str) -> date:
    return AIRAC_EPOCH + timedelta(days=CYCLE_DAYS * cycle_index(cycle_id))


def yaat_cifp_cache_dir() -> Path:
    """Mirrors YaatPaths.AppDataRoot, including the YAAT_APPDATA_DIR test/CI override."""
    override = os.environ.get("YAAT_APPDATA_DIR")
    if override:
        return Path(override) / "cache" / "cifp"

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "yaat" / "cache" / "cifp"

    return Path.home() / ".local" / "share" / "yaat" / "cache" / "cifp"


# ---------------------------------------------------------------------------
# Sources
# ---------------------------------------------------------------------------


class Source:
    """One searchable AIRAC file. `cycle` is the 4-digit id when known, else a descriptive label."""

    def __init__(self, label: str, cycle: str | None, open_lines):
        self.label = label
        self.cycle = cycle
        self._open_lines = open_lines

    def lines(self) -> list[str]:
        return self._open_lines()

    @property
    def sort_key(self) -> int:
        return cycle_index(self.cycle) if self.cycle else -10_000


def _read_text_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8", errors="replace").splitlines()


def _read_gzip_lines(path: Path) -> list[str]:
    with gzip.open(path, "rt", encoding="utf-8", errors="replace") as handle:
        return handle.read().splitlines()


def _cycle_from_name(path: Path) -> str | None:
    match = re.search(r"FAACIFP18-(\d{4})$", path.name)
    return match.group(1) if match else None


def collect_local_sources(extra_paths: list[Path]) -> list[Source]:
    sources: list[Source] = []
    seen: set[Path] = set()

    def add_file(path: Path, label: str | None = None) -> None:
        resolved = path.resolve()
        if resolved in seen or not resolved.is_file():
            return
        seen.add(resolved)
        if resolved.suffix == ".gz":
            sources.append(Source(label or resolved.name, _cycle_from_name(resolved.with_suffix("")), lambda p=resolved: _read_gzip_lines(p)))
        else:
            sources.append(Source(label or resolved.name, _cycle_from_name(resolved), lambda p=resolved: _read_text_lines(p)))

    cache_dir = yaat_cifp_cache_dir()
    if cache_dir.is_dir():
        for path in sorted(cache_dir.glob("FAACIFP18-*")):
            add_file(path)

    for path in extra_paths:
        if path.is_dir():
            for child in sorted(path.iterdir()):
                if child.is_file():
                    add_file(child)
        else:
            add_file(path)

    if BUNDLED_CIFP_GZ.is_file():
        add_file(BUNDLED_CIFP_GZ, label="TestData/FAACIFP18.gz (bundled)")

    # Newest first; unlabelled sources (the bundle) sort last as a final fallback.
    sources.sort(key=lambda s: s.sort_key, reverse=True)
    return sources


def fetch_faa_cycle(cycle_id: str) -> Source | None:
    """Best-effort download of a historical cycle. The FAA generally serves only the current and next
    cycle, so older ones 404 - report that plainly rather than pretending history is retrievable."""
    url = FAA_CIFP_URL.format(yymmdd=cycle_effective_date(cycle_id).strftime("%y%m%d"))
    request = urllib.request.Request(url, headers={"User-Agent": "yaat-stash-procedure/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            payload = response.read()
    except urllib.error.HTTPError as exc:
        print(f"  cycle {cycle_id}: HTTP {exc.code} from {url}")
        return None
    except urllib.error.URLError as exc:
        print(f"  cycle {cycle_id}: network error ({exc.reason})")
        return None

    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        entry = next((n for n in archive.namelist() if Path(n).name.upper().startswith("FAACIFP")), None)
        if entry is None:
            print(f"  cycle {cycle_id}: zip has no FAACIFP entry")
            return None
        text = archive.read(entry).decode("utf-8", errors="replace").splitlines()

    return Source(f"FAA download {cycle_id}", cycle_id, lambda: text)


# ---------------------------------------------------------------------------
# Record selection
# ---------------------------------------------------------------------------


def is_record(line: str) -> bool:
    """The exact gate CifpParser applies before looking at any column."""
    return len(line) >= MIN_RECORD_LENGTH and line.startswith("SUSAP")


def base_name(procedure_id: str) -> str:
    """Mirrors NavigationDatabase.StripTrailingDigits: trim trailing digits, keep at least 2 chars."""
    end = len(procedure_id)
    while end > 2 and procedure_id[end - 1].isdigit():
        end -= 1
    return procedure_id[:end]


def matches_requested(procedure_id: str, requested: str) -> bool:
    """`NIMI` matches NIMI5/NIMI6/NIMI7; an exact id matches only itself."""
    if procedure_id.upper() == requested.upper():
        return True
    if base_name(requested.upper()) != requested.upper():
        return False  # caller gave a versioned id - exact match only
    return base_name(procedure_id.upper()) == requested.upper()


def find_procedures(lines: list[str], icao: str, requested: str, subsections: str) -> dict[tuple[str, str], list[str]]:
    """Returns {(subsection, procedure_id): records} for every matching procedure in this source."""
    found: dict[tuple[str, str], list[str]] = {}
    padded_icao = icao.upper().ljust(4)
    for line in lines:
        if not is_record(line) or line[ICAO_COLS] != padded_icao:
            continue
        subsection = line[SUBSECTION_COL]
        if subsection not in subsections:
            continue
        procedure_id = line[PROCEDURE_ID_COLS].strip()
        if not matches_requested(procedure_id, requested):
            continue
        found.setdefault((subsection, procedure_id), []).append(line)
    return found


def referenced_waypoints(records: list[str]) -> set[str]:
    """Fix ids named anywhere in the selected legs. Superset by design - an extra PC record is inert,
    a missing one silently loses an RF arc center, since CifpParser resolves those from the same file."""
    ids: set[str] = set()
    for line in records:
        for cols in (PROCEDURE_ID_COLS, slice(29, 34), slice(50, 54), slice(106, 111)):
            if len(line) > cols.stop:
                token = line[cols].strip()
                if token:
                    ids.add(token)
    return ids


def collect_waypoint_records(lines: list[str], icao: str, wanted: set[str]) -> list[str]:
    padded_icao = icao.upper().ljust(4)
    out = []
    for line in lines:
        if not is_record(line) or line[ICAO_COLS] != padded_icao:
            continue
        if line[SUBSECTION_COL] != WAYPOINT_SUBSECTION:
            continue
        if line[WAYPOINT_ID_COLS].strip() in wanted:
            out.append(line)
    return out


# ---------------------------------------------------------------------------
# ARTCC inference & output
# ---------------------------------------------------------------------------


def infer_artcc(icao: str) -> tuple[str | None, list[str], str]:
    """Guess the owning ARTCC from existing YAAT data referencing the airport.

    Returns (artcc, candidates, evidence). Ambiguous or unknown -> artcc None, so the caller can ask
    for --artcc rather than silently filing the fragment under the wrong facility.
    """
    faa = icao[1:] if len(icao) == 4 and icao.upper().startswith("K") else icao
    needles = {icao.upper(), faa.upper()}
    hits: dict[str, str] = {}

    if not ARTCCS_DIR.is_dir():
        return None, [], ""

    for artcc_dir in sorted(ARTCCS_DIR.iterdir()):
        if not artcc_dir.is_dir():
            continue
        for path in sorted(artcc_dir.rglob("*.json")):
            if path.stem.upper() in needles:
                hits[artcc_dir.name] = str(path.relative_to(REPO_ROOT))
                break
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            if any(f'"{needle}"' in text.upper() for needle in needles):
                hits[artcc_dir.name] = str(path.relative_to(REPO_ROOT))
                break

    candidates = sorted(hits)
    if len(candidates) == 1:
        return candidates[0], candidates, hits[candidates[0]]
    return None, candidates, ""


def build_fragment(icao: str, source: Source, procedures: dict[tuple[str, str], list[str]], waypoints: list[str], argv: list[str]) -> str:
    ids = ", ".join(f"{pid} ({KIND_NAMES[sub]})" for (sub, pid) in sorted(procedures, key=lambda k: (k[0], k[1])))
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    header = [
        f"# {icao} {ids}",
        f"# Verbatim ARINC 424 records extracted from {source.label}"
        + (f" (AIRAC cycle {source.cycle})" if source.cycle else "")
        + f" on {stamp}.",
        "# Generated by: python tools/stash-procedure.py " + " ".join(argv),
        "# Do not hand-edit. Re-run the tool against a cycle that publishes the procedure instead.",
    ]
    body: list[str] = []
    for key in sorted(procedures, key=lambda k: (k[0], k[1])):
        body.extend(procedures[key])
    if waypoints:
        header.append(f"# Includes {len(waypoints)} terminal-waypoint (PC) record(s) for arc-center resolution.")
        body.extend(waypoints)
    return "\n".join(header + body) + "\n"


def verify_fragment(out_path: Path, icao: str, procedures: dict[tuple[str, str], list[str]]) -> int:
    """Round-trip the written fragment through Yaat.CifpInspector so a mis-selection surfaces now."""
    flag = {"D": "--sid", "E": "--star", "F": "--approach"}
    for subsection, procedure_id in sorted(procedures, key=lambda k: (k[0], k[1])):
        command = [
            "dotnet", "run", "--project", str(REPO_ROOT / "tools" / "Yaat.CifpInspector"),
            "--", "--cifp", str(out_path), "--airport", icao, flag[subsection], procedure_id,
        ]  # fmt: skip
        print(f"\n$ {' '.join(command[-6:])}")
        result = subprocess.run(command, cwd=REPO_ROOT, check=False)
        if result.returncode != 0:
            print(f"verify FAILED for {procedure_id}", file=sys.stderr)
            return 1
    return 0


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("procedure", help="procedure name; a bare base name (NIMI) matches every version (NIMI5, NIMI6)")
    parser.add_argument("--airport", required=True, help="airport ICAO id, e.g. KOAK")
    parser.add_argument("--artcc", help="owning ARTCC (inferred from existing Data/ARTCCs content when omitted)")
    parser.add_argument("--kind", choices=sorted(SUBSECTIONS), action="append", help="restrict to sid/star/approach (repeatable)")
    parser.add_argument("--search-path", type=Path, action="append", default=[], help="extra CIFP file or directory to search (repeatable)")
    parser.add_argument("--fetch", action="store_true", help="also try downloading cycles from the FAA (mostly 404s for past cycles)")
    parser.add_argument("--max-lookback", type=int, default=13, help="cycles back to try with --fetch (default 13, ~1 year)")
    parser.add_argument("--out", type=Path, help="output path (default: Data/ARTCCs/{ARTCC}/Procedures/{icao}-{name}.cifp)")
    parser.add_argument("--dry-run", action="store_true", help="report which sources carry the procedure and write nothing")
    parser.add_argument("--force", action="store_true", help="overwrite an existing fragment")
    parser.add_argument("--verify", action="store_true", help="round-trip the fragment through Yaat.CifpInspector after writing")
    args = parser.parse_args()

    # CIFP is keyed by ICAO id; accept the FAA form too since that is what controllers type.
    icao = args.airport.strip().upper()
    if len(icao) == 3:
        icao = "K" + icao
    if len(icao) != 4:
        print(f"--airport expects an ICAO id (KOAK) or a 3-letter FAA id (OAK), got {args.airport!r}", file=sys.stderr)
        return 2

    requested = args.procedure.strip().upper()
    subsections = "".join(SUBSECTIONS[k] for k in (args.kind or sorted(SUBSECTIONS)))
    kinds_label = "/".join(KIND_NAMES[s] for s in subsections)

    sources = collect_local_sources(args.search_path)
    print(f"searching {len(sources)} local source(s) for {requested}* at {icao} ({kinds_label})")

    hit: tuple[Source, dict[tuple[str, str], list[str]]] | None = None
    for source in sources:
        procedures = find_procedures(source.lines(), icao, requested, subsections)
        if procedures:
            names = ", ".join(f"{pid} ({KIND_NAMES[sub]}, {len(recs)} records)" for (sub, pid), recs in sorted(procedures.items()))
            print(f"  {source.label:<40} FOUND {names}")
            hit = (source, procedures)
            break
        print(f"  {source.label:<40} not found")

    if hit is None and args.fetch:
        print("trying FAA downloads (past cycles are usually not served)...")
        current = current_airac_cycle()
        have = {s.cycle for s in sources if s.cycle}
        for back in range(0, args.max_lookback + 1):
            candidate_index = cycle_index(current) - back
            if candidate_index < 0:
                break
            year = 2025 + candidate_index // CYCLES_PER_YEAR
            candidate = f"{year % 100:02d}{candidate_index % CYCLES_PER_YEAR + 1:02d}"
            if candidate in have:
                continue
            source = fetch_faa_cycle(candidate)
            if source is None:
                continue
            procedures = find_procedures(source.lines(), icao, requested, subsections)
            if procedures:
                print(f"  cycle {candidate}: FOUND")
                hit = (source, procedures)
                break
            print(f"  cycle {candidate}: downloaded, procedure absent")

    if hit is None:
        print(f"\n{requested} not found at {icao} in any reachable AIRAC source.", file=sys.stderr)
        print("Try --search-path with an archived cycle, or --fetch.", file=sys.stderr)
        return 1

    source, procedures = hit
    all_records = [line for records in procedures.values() for line in records]
    waypoints = collect_waypoint_records(source.lines(), icao, referenced_waypoints(all_records))

    if args.dry_run:
        print(f"\ndry run - would capture {len(all_records)} procedure record(s) and {len(waypoints)} waypoint record(s) from {source.label}")
        return 0

    artcc = (args.artcc or "").strip().upper()
    if not artcc:
        artcc, candidates, evidence = infer_artcc(icao)
        if artcc:
            print(f"inferred ARTCC: {artcc} (matched {evidence})")
        else:
            detail = f"candidates: {', '.join(candidates)}" if candidates else f"no existing Data/ARTCCs entry references {icao}"
            print(f"\ncannot infer the owning ARTCC - {detail}. Pass --artcc.", file=sys.stderr)
            return 1

    out_path = args.out or (ARTCCS_DIR / artcc / "Procedures" / f"{icao.lower()}-{base_name(requested).lower()}.cifp")
    if out_path.exists() and not args.force:
        print(f"\n{out_path} already exists - pass --force to overwrite.", file=sys.stderr)
        return 1

    out_path.parent.mkdir(parents=True, exist_ok=True)
    content = build_fragment(icao, source, procedures, waypoints, sys.argv[1:])
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", newline="\n", dir=out_path.parent, delete=False) as handle:
        handle.write(content)
        temp_path = Path(handle.name)
    shutil.move(str(temp_path), out_path)

    try:
        display = out_path.relative_to(REPO_ROOT)
    except ValueError:
        display = out_path
    print(f"\nwrote {display}  ({len(all_records)} procedure + {len(waypoints)} waypoint records from {source.label})")

    if args.verify:
        return verify_fragment(out_path, icao, procedures)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
