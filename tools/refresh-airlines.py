#!/usr/bin/env python3
"""Fetch OpenFlights airlines.dat and emit a filtered ICAO-to-telephony TSV.

Usage:
    python tools/refresh-airlines.py

Reads:
    https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat

Writes:
    src/Yaat.Sim/Speech/Data/airlines.tsv         (tab-separated: icao<TAB>callsign<TAB>name<TAB>country)
    src/Yaat.Sim/Speech/Data/airlines-source.meta (provenance: upstream URL, sha256, fetch date, row counts)

Filter rules:
    - active == "Y"
    - icao non-empty, exactly 3 uppercase ASCII letters
    - callsign non-empty (trimmed)
    - dedupe by ICAO (first-wins since OpenFlights rows are roughly ordered by id)

OpenFlights airlines.dat license: ODbL 1.0 (https://opendatacommons.org/licenses/odbl/1-0/).
This script produces a derivative database. The output file is also ODbL; see
src/Yaat.Sim/Speech/Data/LICENSE-OPENFLIGHTS.txt for the full text and attribution.
"""

import csv
import datetime as dt
import hashlib
import io
import os
import re
import sys
import urllib.request

UPSTREAM_URL = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat"
USER_AGENT = "yaat-refresh-airlines/1.0"

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
OUT_DIR = os.path.join(REPO_ROOT, "src", "Yaat.Sim", "Speech", "Data")
OUT_TSV = os.path.join(OUT_DIR, "airlines.tsv")
OUT_META = os.path.join(OUT_DIR, "airlines-source.meta")

# Known-bad upstream rows. OpenFlights has hand-entry errors where airline names containing
# commas (e.g. "Alaska Airlines, Inc.") caused fields to shift: the "Inc." suffix ended up
# in the callsign column and the real callsign ended up in the country column. These overrides
# patch the callsign for specific ICAOs; the row must still pass the active=Y filter upstream.
# Format: icao -> (correct_callsign, correct_country, note)
OVERRIDES: dict[str, tuple[str, str, str]] = {
    "ASA": ("ALASKA", "United States", "upstream row has callsign='Inc.' country='ALASKA' (field shift)"),
    "AVA": ("AVIANCA", "Colombia", "upstream row has callsign='S.A.' country='AVIANCA' (field shift)"),
}

# Callsign validation: ≥3 chars, starts with at least two letters, then letters/digits/space/hyphen.
# Rejects upstream garbage like "INC.", "S.A.", "T.J. AIR", "5B", "XB", "OA".
_CALLSIGN_RE = re.compile(r"^[A-Z][A-Z][A-Z0-9 \-]*$")


def fetch_upstream() -> bytes:
    req = urllib.request.Request(UPSTREAM_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read()


def is_valid_icao(icao: str) -> bool:
    return len(icao) == 3 and icao.isascii() and icao.isalpha() and icao.isupper()


def is_valid_callsign(callsign: str) -> bool:
    return len(callsign) >= 3 and bool(_CALLSIGN_RE.match(callsign))


Row = tuple[str, str, str, str, bool]  # icao, callsign, name, country, active


def parse_raw(raw: bytes) -> tuple[list[Row], list[str]]:
    """Parse airlines.dat CSV into rows with valid ICAO + valid callsign.

    Returns (rows, skipped). Rows preserve duplicates; callers handle dedup.
    active=="Y" is NOT enforced — defunct airlines are kept (scenarios may reference them).
    """
    text = raw.decode("utf-8", errors="replace")
    reader = csv.reader(io.StringIO(text), quotechar='"', skipinitialspace=False)

    rows: list[Row] = []
    skipped: list[str] = []

    for row in reader:
        if len(row) < 8:
            continue
        _id, name, _alias, _iata, icao, callsign, country, active_raw = (c.strip() for c in row[:8])

        if not is_valid_icao(icao):
            continue

        if icao in OVERRIDES:
            callsign_final, country_final, _note = OVERRIDES[icao]
        else:
            if not callsign or callsign == r"\N":
                continue
            callsign_final = callsign.upper()
            country_final = country

        if not is_valid_callsign(callsign_final):
            skipped.append(f"{icao}\t{callsign_final}\t{name}\t{country_final}")
            continue

        active = active_raw == "Y"
        rows.append((icao, callsign_final, name, country_final, active))

    return rows, skipped


def dedupe_by_icao(rows: list[Row]) -> tuple[list[Row], list[list[Row]]]:
    """Collapse same-ICAO rows. Prefer active; otherwise first.

    Returns (survivors, conflict_groups). conflict_groups lists ICAOs with multiple variants.
    """
    by_icao: dict[str, list[Row]] = {}
    for r in rows:
        by_icao.setdefault(r[0], []).append(r)

    survivors: list[Row] = []
    conflicts: list[list[Row]] = []
    for variants in by_icao.values():
        if len(variants) > 1:
            conflicts.append(variants)
        active_variants = [v for v in variants if v[4]]
        survivors.append(active_variants[0] if active_variants else variants[0])
    return survivors, conflicts


def find_callsign_collisions(rows: list[Row]) -> list[list[Row]]:
    """Identify groups of airlines sharing the same telephony.

    We deliberately KEEP all of them in the output — runtime speech → ICAO resolution
    picks the right one by cross-referencing against active aircraft in the scenario
    (two airlines sharing a telephony *and* the same flight number on the same scenario
    is vanishingly rare). This function just reports the groups for visibility.
    """
    by_callsign: dict[str, list[Row]] = {}
    for r in rows:
        by_callsign.setdefault(r[1], []).append(r)
    groups = [variants for variants in by_callsign.values() if len(variants) > 1]
    groups.sort(key=lambda g: g[0][1])
    return groups


def write_tsv(rows: list[Row]) -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(OUT_TSV, "w", encoding="utf-8", newline="\n") as fp:
        fp.write("# Airline ICAO-to-telephony map, derived from OpenFlights airlines.dat.\n")
        fp.write("# Source: https://openflights.org/data.html\n")
        fp.write("# License: ODbL 1.0 (see LICENSE-OPENFLIGHTS.txt in this directory)\n")
        fp.write("# Columns: icao<TAB>callsign<TAB>name<TAB>country\n")
        fp.write("# Regenerate: python tools/refresh-airlines.py\n")
        for icao, callsign, name, country, _active in rows:
            fp.write(f"{icao}\t{callsign}\t{name}\t{country}\n")


def write_meta(
    raw: bytes,
    row_count: int,
    skipped: list[str],
    icao_conflicts: list[list[Row]],
    callsign_collisions: list[list[Row]],
) -> None:
    sha = hashlib.sha256(raw).hexdigest()
    fetched = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(OUT_META, "w", encoding="utf-8", newline="\n") as fp:
        fp.write(f"upstream_url: {UPSTREAM_URL}\n")
        fp.write(f"upstream_sha256: {sha}\n")
        fp.write(f"upstream_bytes: {len(raw)}\n")
        fp.write(f"fetched_utc: {fetched}\n")
        fp.write(f"output_rows: {row_count}\n")
        fp.write(f"skipped_rows: {len(skipped)}\n")
        fp.write(f"icao_conflicts: {len(icao_conflicts)}\n")
        fp.write(f"callsign_collisions: {len(callsign_collisions)}\n")
        fp.write("license: ODbL-1.0\n")

        if skipped:
            fp.write("\n# Skipped rows (failed callsign validation):\n")
            for line in skipped:
                fp.write(f"# {line}\n")

        def _dump(label: str, groups: list[list[Row]]) -> None:
            if not groups:
                return
            fp.write(f"\n# {label}:\n")
            for variants in groups:
                for icao, cs, name, country, active in variants:
                    flag = "Y" if active else "N"
                    fp.write(f"# {icao}\t{cs}\t{flag}\t{name}\t{country}\n")
                fp.write("#\n")

        _dump("Same-ICAO conflicts (active preferred, else first-wins)", icao_conflicts)
        _dump("Callsign collisions (all variants kept; runtime disambiguates via active aircraft)", callsign_collisions)


def main() -> int:
    print(f"Fetching {UPSTREAM_URL}")
    raw = fetch_upstream()
    print(f"  {len(raw):,} bytes")

    parsed, skipped = parse_raw(raw)
    print(f"Parsed {len(parsed):,} rows with valid ICAO + valid callsign")
    if skipped:
        print(f"Skipped {len(skipped)} rows with invalid callsigns (see meta file)")

    final_rows, icao_conflicts = dedupe_by_icao(parsed)
    if icao_conflicts:
        print(f"Resolved {len(icao_conflicts)} same-ICAO conflicts (active preferred)")

    callsign_collisions = find_callsign_collisions(final_rows)
    if callsign_collisions:
        total_colliding = sum(len(g) for g in callsign_collisions)
        print(
            f"Note: {len(callsign_collisions)} distinct callsigns are shared by {total_colliding} airlines; "
            "all kept (runtime resolves via active aircraft)"
        )

    final_rows.sort(key=lambda r: r[0])
    print(f"Final: {len(final_rows):,} unique-ICAO rows")
    if not final_rows:
        print("ERROR: no rows after dedup", file=sys.stderr)
        return 1

    write_tsv(final_rows)
    write_meta(raw, len(final_rows), skipped, icao_conflicts, callsign_collisions)
    print(f"Wrote {OUT_TSV}")
    print(f"Wrote {OUT_META}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
