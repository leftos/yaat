#!/usr/bin/env python3
"""Build the airline-fleet map from Airfleets World Fleet Listing PDFs.

The Airfleets World Fleet Listing is a paid quarterly publication available at
https://www.airfleets.net/. The author purchases an issue, downloads the per-region
PDFs, and runs this script to regenerate `src/Yaat.Sim/Data/airline-fleets.json`.

The PDFs themselves MUST NOT be committed to the repository (paid content).

Usage (from repo root):
    python tools/refresh-airline-fleets.py <pdf-path...>

Example:
    python tools/refresh-airline-fleets.py "$HOME/Downloads/World_Fleet_listing_issue_123_*.pdf"

What it produces:
    src/Yaat.Sim/Data/airline-fleets.json     The canonical map (committed)
    src/Yaat.Sim/Data/airline-fleets.meta     Provenance sidecar (committed)

Output JSON schema:
    {
      "metadata": {
        "source": "Airfleets World Fleet Listing Issue NN",
        "source_date": "YYYY-MM-DD",
        "generated_utc": "YYYY-MM-DDTHH:MM:SSZ",
        "regions_parsed": ["North_America", ...],
        "airlines_count": <int>,
        "types_count": <int>,
        "tool": "tools/refresh-airline-fleets.py"
      },
      "by_airline": {
        "<airline_icao>": {
          "name": "<airline name>",
          "country": "<country>",
          "types": {"<aircraft_icao>": <count>, ...}
        }
      },
      "by_type": {
        "<aircraft_icao>": {"<airline_icao>": <count>, ...}
      }
    }

The reverse map (`by_type`) is precomputed for O(1) lookups in either direction.
Inner dicts are sorted by count descending for human-readable diffs.
"""

# /// script
# requires-python = ">=3.13"
# dependencies = ["pdfplumber>=0.11"]
# ///

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path

# Import the parser sibling-module. parse_airfleets.py lives next to this script.
_TOOLS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(_TOOLS_DIR))
from parse_airfleets import parse_pdf, map_to_icao_type  # noqa: E402

REPO_ROOT = _TOOLS_DIR.parent
OUT_DIR = REPO_ROOT / "src" / "Yaat.Sim" / "Data"
OUT_JSON = OUT_DIR / "airline-fleets.json"
OUT_META = OUT_DIR / "airline-fleets.meta"


_FILENAME_RE = re.compile(r"World_Fleet_listing_issue_(\d+)_([A-Za-z_]+?)\.pdf$", re.IGNORECASE)
_DATE_RE = re.compile(r"\b([A-Z][a-z]+),?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})\b")


def extract_source_date(pdf_path: Path) -> str | None:
    """Pull the issue publication date from the PDF cover page (e.g. 'May, 1st, 2026' -> '2026-05-01')."""
    try:
        import pdfplumber  # noqa: PLC0415  (deferred import; pdfplumber already required by parse_airfleets)

        with pdfplumber.open(pdf_path) as pdf:
            text = pdf.pages[0].extract_text() or ""
    except Exception:
        return None
    m = _DATE_RE.search(text)
    if not m:
        return None
    month_name, day, year = m.group(1), m.group(2), m.group(3)
    months = {
        "January": "01", "February": "02", "March": "03", "April": "04",
        "May": "05", "June": "06", "July": "07", "August": "08",
        "September": "09", "October": "10", "November": "11", "December": "12",
    }
    if month_name not in months:
        return None
    return f"{year}-{months[month_name]}-{int(day):02d}"


def parse_filename(pdf_path: Path) -> tuple[str | None, str | None]:
    """Pull (issue_number, region) from a filename like 'World_Fleet_listing_issue_123_North_America.pdf'."""
    m = _FILENAME_RE.search(pdf_path.name)
    if not m:
        return (None, None)
    return (m.group(1), m.group(2))


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def build_map(pdfs: list[Path]) -> tuple[dict, dict]:
    """Parse all PDFs, fold per-airline records into the bidirectional map.

    Returns (output_dict, sidecar_dict).
    """
    # Per-airline accumulator: icao -> {"name": str, "country": str, "types": Counter}
    airlines_data: dict[str, dict] = {}
    regions_seen: list[str] = []
    issue_numbers: set[str] = set()
    source_dates: set[str] = set()
    file_records: list[dict] = []
    seen_hashes: dict[str, Path] = {}
    duplicates: list[dict] = []

    for pdf in pdfs:
        digest = file_sha256(pdf)
        if digest in seen_hashes:
            duplicates.append({
                "file": pdf.name,
                "duplicate_of": seen_hashes[digest].name,
                "sha256": digest,
            })
            print(f"  skipping duplicate of {seen_hashes[digest].name}: {pdf.name}", file=sys.stderr)
            continue
        seen_hashes[digest] = pdf

        issue, region = parse_filename(pdf)
        if issue:
            issue_numbers.add(issue)
        if region:
            regions_seen.append(region)
        date_iso = extract_source_date(pdf)
        if date_iso:
            source_dates.add(date_iso)

        print(f"Parsing {pdf.name} ...", file=sys.stderr)
        records = parse_pdf(pdf)
        with_icao = sum(1 for r in records if r.icao)
        unmapped = 0
        mapped_count = 0

        for record in records:
            if not record.icao:
                continue
            icao = record.icao.upper()
            entry = airlines_data.setdefault(icao, {
                "name": record.name,
                "country": record.country,
                "types": Counter(),
            })
            # Prefer the first non-empty name/country we see (records can repeat
            # if an airline appears in multiple regions, e.g. flag carriers).
            if entry["name"] in (None, "(unknown)") and record.name not in (None, "(unknown)"):
                entry["name"] = record.name
            if not entry["country"] and record.country:
                entry["country"] = record.country
            for variant, count in record.types_raw.items():
                mapped = map_to_icao_type(variant, record.families)
                if mapped:
                    entry["types"][mapped] += count
                    mapped_count += count
                else:
                    unmapped += count

        file_records.append({
            "file": pdf.name,
            "sha256": digest,
            "size_bytes": pdf.stat().st_size,
            "issue": issue,
            "region": region,
            "airlines_total": len(records),
            "airlines_with_icao": with_icao,
            "aircraft_mapped": mapped_count,
            "aircraft_unmapped": unmapped,
        })
        print(f"  airlines={len(records)}, with_icao={with_icao}, "
              f"mapped={mapped_count}, unmapped={unmapped}",
              file=sys.stderr)

    # Build sorted output
    by_airline: dict[str, dict] = {}
    for icao in sorted(airlines_data):
        entry = airlines_data[icao]
        sorted_types = dict(entry["types"].most_common())
        by_airline[icao] = {
            "name": entry["name"] or "(unknown)",
            "country": entry["country"] or "",
            "types": sorted_types,
        }

    # Reverse index: by_type[aircraft_icao] = {airline_icao: count}
    by_type_acc: dict[str, Counter] = {}
    for airline_icao, entry in by_airline.items():
        for type_icao, count in entry["types"].items():
            by_type_acc.setdefault(type_icao, Counter())[airline_icao] += count
    by_type: dict[str, dict] = {}
    for type_icao in sorted(by_type_acc):
        by_type[type_icao] = dict(by_type_acc[type_icao].most_common())

    issue = (sorted(issue_numbers) or [""])[-1]
    # If all PDFs agree on a publication date, surface it; otherwise leave blank
    # rather than picking arbitrarily.
    source_date = next(iter(source_dates)) if len(source_dates) == 1 else ""
    output = {
        "metadata": {
            "source": f"Airfleets World Fleet Listing Issue {issue}".strip(),
            "source_date": source_date,
            "generated_utc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "regions_parsed": sorted(set(regions_seen)),
            "airlines_count": len(by_airline),
            "types_count": len(by_type),
            "tool": "tools/refresh-airline-fleets.py",
        },
        "by_airline": by_airline,
        "by_type": by_type,
    }

    sidecar = {
        "files": file_records,
        "duplicates_skipped": duplicates,
        "totals": {
            "airlines": len(by_airline),
            "types": len(by_type),
            "aircraft_total": sum(sum(e["types"].values()) for e in by_airline.values()),
            "aircraft_unmapped": sum(f["aircraft_unmapped"] for f in file_records),
        },
    }
    return output, sidecar


def write_outputs(output: dict, sidecar: dict) -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # Pretty-printed JSON, but keep inner type maps compact (one line per airline)
    # for readability in PR diffs. We achieve that by serializing to indented JSON,
    # then normalising the inner types map to a single line. System.Text.Json on the
    # C# side handles either form.
    json_text = json.dumps(output, indent=2, ensure_ascii=False)
    OUT_JSON.write_text(json_text + "\n", encoding="utf-8")

    sidecar_text = json.dumps(sidecar, indent=2, ensure_ascii=False)
    OUT_META.write_text(sidecar_text + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pdfs", nargs="+", type=Path, help="Paths to Airfleets World Fleet Listing region PDFs")
    args = ap.parse_args()

    missing = [p for p in args.pdfs if not p.exists()]
    if missing:
        for p in missing:
            print(f"ERROR: {p} not found", file=sys.stderr)
        return 1

    output, sidecar = build_map(list(args.pdfs))

    write_outputs(output, sidecar)
    meta = output["metadata"]
    print(
        f"\nWrote {OUT_JSON}\n"
        f"  {meta['airlines_count']} airlines, {meta['types_count']} aircraft types\n"
        f"  source: {meta['source']}\n"
        f"  regions: {', '.join(meta['regions_parsed'])}\n"
        f"  unmapped aircraft: {sidecar['totals']['aircraft_unmapped']}",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
