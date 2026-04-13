#!/usr/bin/env python3
"""Fetch vNAS AircraftSpecs.json and emit a TSV of ICAO type → spoken manufacturer/family names.

Usage:
    python tools/refresh-aircraft-types.py

Reads:
    https://data-api.vnas.vatsim.net/Files/AircraftSpecs.json

Writes:
    src/Yaat.Sim/Speech/Data/aircraft-types.tsv         (designator<TAB>manufacturer<TAB>family)
    src/Yaat.Sim/Speech/Data/aircraft-types-source.meta (provenance)

The TSV drives alternate spoken-form seeding of Whisper's initial_prompt so speech recognition
is primed for both callsign styles pilots use:
  - "cessna three four five" (manufacturer)
  - "skyhawk three four five" (model family)
in addition to the generic "november three four five" from `CallsignParser`.

Extraction heuristic:
  1. Manufacturer: plurality-vote first-word of ManufacturerCode across rows, tiebroken by the
     manufacturer word's cross-designator coverage (so CESSNA beats one-off mod-shops like
     PETERSON when they both appear in a single designator's rows).
  2. Family: count name-like unigrams and adjacent bigrams within the designator's rows.
     Within-designator frequency wins; cross-designator coverage is the tiebreaker. A bigram
     wins over a unigram only when it appears in the designator AND is in the allow-list of
     recognized two-word families (data-discovered + manual inclusions).

Data-discovered two-word families (bigrams appearing across ≥ 3 distinct designators):
  - king air, queen air, regional jet, tiger moth, comp air
Manual inclusions (well-known families with single-designator coverage):
  - twin otter, global express

vNAS AircraftSpecs data is sourced from ICAO Doc 8643 via vNAS. Attribution in the .meta file.
"""

import collections
import datetime as dt
import hashlib
import json
import os
import re
import sys
import urllib.request

UPSTREAM_URL = "https://data-api.vnas.vatsim.net/Files/AircraftSpecs.json"
USER_AGENT = "yaat-refresh-aircraft-types/1.0"

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
OUT_DIR = os.path.join(REPO_ROOT, "src", "Yaat.Sim", "Speech", "Data")
OUT_TSV = os.path.join(OUT_DIR, "aircraft-types.tsv")
OUT_META = os.path.join(OUT_DIR, "aircraft-types-source.meta")

# Words that are never a useful spoken family name — common fillers and variant modifiers.
NOISE = {
    "super", "mk", "mark", "and", "the", "de", "van", "der", "jr",
    "series", "sp", "xp", "slx", "pro", "plus", "classic",
}

# Manual inclusions for known two-word families that don't meet the cross-designator
# threshold (e.g. single designator but well-known). Each entry justified below.
MANUAL_BIGRAM_INCLUSIONS = {
    "twin otter": "DHC6 — De Havilland Twin Otter, one of the most common turboprop twins in commuter/float ops",
    "global express": "GLEX — Bombardier Global Express, a widely recognized business jet family",
}

# Auto-discovered bigrams need to appear across this many distinct designators to count.
BIGRAM_DESIGNATOR_THRESHOLD = 3

# Within-designator unigram/bigram needs at least this many occurrences to qualify. Filters
# out one-off executive/military variants like "Prestige" (A320) or "Arapaho" (B407).
MIN_WITHIN_DESIGNATOR_FREQ = 2

_TOKEN_STRIP = "().,-'"


def fetch_upstream() -> bytes:
    req = urllib.request.Request(UPSTREAM_URL, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return resp.read()


def clean_token(t: str) -> str:
    return t.strip(_TOKEN_STRIP).lower()


def is_name_token(t: str) -> bool:
    if len(t) < 3:
        return False
    if any(c.isdigit() for c in t):
        return False
    if not t.isalpha():
        return False
    return t not in NOISE


def discover_bigrams(data: list[dict]) -> set[str]:
    """Find bigrams that appear across ≥ BIGRAM_DESIGNATOR_THRESHOLD distinct designators."""
    bigram_designators: dict[str, set[str]] = collections.defaultdict(set)
    for row in data:
        des = (row.get("Designator") or "").strip()
        if not des:
            continue
        tokens = [clean_token(t) for t in (row.get("ModelFullName") or "").split()]
        for i in range(len(tokens) - 1):
            a, b = tokens[i], tokens[i + 1]
            if is_name_token(a) and is_name_token(b):
                bigram_designators[f"{a} {b}"].add(des)

    discovered = {bg for bg, des_set in bigram_designators.items() if len(des_set) >= BIGRAM_DESIGNATOR_THRESHOLD}
    return discovered | set(MANUAL_BIGRAM_INCLUSIONS.keys())


def build_cross_designator_counts(data: list[dict]) -> tuple[dict[str, int], dict[str, int]]:
    """For each name-token and manufacturer word, count how many distinct designators it spans."""
    token_designators: dict[str, set[str]] = collections.defaultdict(set)
    mfr_designators: dict[str, set[str]] = collections.defaultdict(set)
    for row in data:
        des = (row.get("Designator") or "").strip()
        if not des:
            continue
        tokens = [clean_token(t) for t in (row.get("ModelFullName") or "").split()]
        for t in tokens:
            if is_name_token(t):
                token_designators[t].add(des)
        mc = row.get("ManufacturerCode") or row.get("Manufacturer") or ""
        word = first_meaningful_mfr_word(mc)
        if word:
            mfr_designators[word].add(des)
    return (
        {t: len(des_set) for t, des_set in token_designators.items()},
        {m: len(des_set) for m, des_set in mfr_designators.items()},
    )


def first_meaningful_mfr_word(raw: str) -> str | None:
    """Pick a manufacturer word: prefer the first ≥3-char word so 'De Havilland Canada' → 'havilland'."""
    for word in raw.split():
        cleaned = word.lower().strip(_TOKEN_STRIP)
        if len(cleaned) >= 3 and cleaned.isalpha():
            return cleaned
    return None


def pick_manufacturer(rows: list[dict], mfr_cross_count: dict[str, int]) -> str | None:
    """Plurality vote across the designator's rows, tiebroken by cross-designator frequency."""
    within_counts: dict[str, int] = collections.Counter()
    for row in rows:
        mc = row.get("ManufacturerCode") or row.get("Manufacturer") or ""
        word = first_meaningful_mfr_word(mc)
        if word:
            within_counts[word] += 1
    if not within_counts:
        return None
    return max(
        within_counts.keys(),
        key=lambda m: (mfr_cross_count.get(m, 0), within_counts[m]),
    )


def pick_family(
    rows: list[dict],
    manufacturer: str | None,
    allowed_bigrams: set[str],
    token_cross_count: dict[str, int],
) -> str | None:
    """Extract the model family word/bigram for a designator, preferring allowed bigrams."""
    uni_counts: dict[str, int] = collections.Counter()
    bi_counts: dict[str, int] = collections.Counter()

    for row in rows:
        tokens = [clean_token(t) for t in (row.get("ModelFullName") or "").split()]
        for t in tokens:
            if is_name_token(t) and t != manufacturer:
                uni_counts[t] += 1
        for i in range(len(tokens) - 1):
            a, b = tokens[i], tokens[i + 1]
            if is_name_token(a) and is_name_token(b) and a != manufacturer and b != manufacturer:
                bg = f"{a} {b}"
                if bg in allowed_bigrams:
                    bi_counts[bg] += 1

    # Allowed bigrams are always preferred when present with any non-zero frequency. They
    # correspond to well-known families like "King Air" that must not be split to "king".
    if bi_counts:
        top_bg, freq = bi_counts.most_common(1)[0]
        if freq >= MIN_WITHIN_DESIGNATOR_FREQ:
            return top_bg

    if not uni_counts:
        return None

    # Unigram path: rank by within-designator frequency, tiebreak by cross-designator coverage.
    candidates = [
        (freq, token_cross_count.get(tok, 0), tok)
        for tok, freq in uni_counts.items()
        if freq >= MIN_WITHIN_DESIGNATOR_FREQ
    ]
    if not candidates:
        return None
    candidates.sort(key=lambda x: (-x[0], -x[1]))
    return candidates[0][2]


def process(data: list[dict]) -> tuple[list[tuple[str, str | None, str | None]], set[str]]:
    by_designator: dict[str, list[dict]] = collections.defaultdict(list)
    for row in data:
        des = (row.get("Designator") or "").strip()
        if des:
            by_designator[des].append(row)

    token_cross_count, mfr_cross_count = build_cross_designator_counts(data)
    allowed_bigrams = discover_bigrams(data)

    rows_out: list[tuple[str, str | None, str | None]] = []
    for des in sorted(by_designator):
        group = by_designator[des]
        mfr = pick_manufacturer(group, mfr_cross_count)
        fam = pick_family(group, mfr, allowed_bigrams, token_cross_count)
        rows_out.append((des, mfr, fam))
    return rows_out, allowed_bigrams


def write_tsv(rows: list[tuple[str, str | None, str | None]]) -> int:
    os.makedirs(OUT_DIR, exist_ok=True)
    written = 0
    with open(OUT_TSV, "w", encoding="utf-8", newline="\n") as fp:
        fp.write("# ICAO aircraft type designators → spoken manufacturer/family names.\n")
        fp.write("# Source: vNAS AircraftSpecs.json (data-api.vnas.vatsim.net).\n")
        fp.write("# Columns: designator<TAB>manufacturer<TAB>family\n")
        fp.write("# Regenerate: python tools/refresh-aircraft-types.py\n")
        fp.write("#\n")
        fp.write("# Either field may be empty if extraction couldn't find a suitable word.\n")
        fp.write("# Runtime fallback: if family is empty, manufacturer is used as the only spoken form.\n")
        for des, mfr, fam in rows:
            if not mfr and not fam:
                continue
            fp.write(f"{des}\t{mfr or ''}\t{fam or ''}\n")
            written += 1
    return written


def write_meta(raw: bytes, row_count: int, allowed_bigrams: set[str]) -> None:
    sha = hashlib.sha256(raw).hexdigest()
    fetched = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(OUT_META, "w", encoding="utf-8", newline="\n") as fp:
        fp.write(f"upstream_url: {UPSTREAM_URL}\n")
        fp.write(f"upstream_sha256: {sha}\n")
        fp.write(f"upstream_bytes: {len(raw)}\n")
        fp.write(f"fetched_utc: {fetched}\n")
        fp.write(f"output_rows: {row_count}\n")
        fp.write(f"bigram_threshold: {BIGRAM_DESIGNATOR_THRESHOLD}\n")
        fp.write(f"within_designator_min_freq: {MIN_WITHIN_DESIGNATOR_FREQ}\n")
        fp.write("\n# Allowed two-word families (cross-designator discovery + manual inclusion):\n")
        for bg in sorted(allowed_bigrams):
            why = MANUAL_BIGRAM_INCLUSIONS.get(bg, "auto-discovered via cross-designator threshold")
            fp.write(f"# {bg}: {why}\n")
        fp.write("\n# Source note:\n")
        fp.write("# vNAS distributes AircraftSpecs.json derived from ICAO Doc 8643 for use by the\n")
        fp.write("# VATSIM community. YAAT consumes the processed TSV; source data remains with vNAS.\n")


def main() -> int:
    print(f"Fetching {UPSTREAM_URL}")
    raw = fetch_upstream()
    print(f"  {len(raw):,} bytes")

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as ex:
        print(f"ERROR: invalid JSON: {ex}", file=sys.stderr)
        return 1
    if not isinstance(data, list):
        print("ERROR: expected a list of AircraftSpecs rows", file=sys.stderr)
        return 1
    print(f"Parsed {len(data):,} rows")

    rows_out, allowed_bigrams = process(data)
    auto_bigrams = sorted(set(allowed_bigrams) - set(MANUAL_BIGRAM_INCLUSIONS.keys()))
    print(f"Auto-discovered bigrams ({len(auto_bigrams)}): {', '.join(auto_bigrams) or '(none)'}")
    print(f"Manual bigrams ({len(MANUAL_BIGRAM_INCLUSIONS)}): {', '.join(MANUAL_BIGRAM_INCLUSIONS)}")

    written = write_tsv(rows_out)
    write_meta(raw, written, allowed_bigrams)
    print(f"Wrote {written:,} designators with at least one spoken name to {OUT_TSV}")
    print(f"Wrote {OUT_META}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
