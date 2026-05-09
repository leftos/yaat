#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.13"
# dependencies = []
# ///
"""Regenerate src/Yaat.Sim/Data/AircraftProfileSiblings.json.

For every ICAO type that has an FAA ACD record but no AircraftProfiles.json
entry, find the closest profiled type by ACD facts (engine class, weight,
AAC, MTOW, engine count). Emit a JSON map the runtime uses as a fallback
when AircraftProfileDatabase.Get(type) misses.

Re-run after editing AircraftProfiles.json or FaaAcd.json:

    uv run tools/build_aircraft_sibling_map.py

Threshold and manual overrides are constants at the top of the script.
"""

from __future__ import annotations

import json
import math
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ACD_PATH = REPO_ROOT / "tests/Yaat.Sim.Tests/TestData/FaaAcd.json"
PROFILES_PATH = REPO_ROOT / "src/Yaat.Sim/Data/AircraftProfiles.json"
OUT_PATH = REPO_ROOT / "src/Yaat.Sim/Data/AircraftProfileSiblings.json"

# Auto-include any pair scoring at or below this. 0.30 = same engine class,
# same or one-bucket-off weight, same/near AAC, MTOW within ~25%, engine
# count match. Above this we require a manual override or the entry is dropped.
AUTO_THRESHOLD = 0.30

# Hand-picked overrides: cases where the algorithm's winner was era- or
# family-wrong even though the score was low, plus informal-string aliases the
# algorithm cannot infer (entries not in FAA ACD). Manual entries always win
# over auto-derived ones.
MANUAL_OVERRIDES: dict[str, tuple[str, str]] = {
    # missing : (sibling, justification)
    "A21N": ("A320", "A321neo: A320-family neo. Algorithm picked B722 (727-200) on MTOW; wrong era."),
    "A359": ("A332", "A350-900: same OEM widebody. Algorithm picked B772; A332 keeps Airbus systems behavior."),
    "A35K": ("A333", "A350-1000: same OEM widebody."),
    "DH8D": ("DH8C", "Q400: identical airframe to Dash 8-300 plus stretch. Algorithm picked AT72."),
    "B748": ("B744", "747-8: pin same family even though algorithm agreed."),
    "B38M": ("B738", "737 MAX 8: 738-length fuselage. Algorithm tied 738/739."),
    "B39M": ("B739", "737 MAX 9: 739-length fuselage."),
    "B37M": ("B738", "737 MAX 7: 738-shrink, closer than 737-700."),
    # Informal-string aliases (NOT in FAA ACD — old-generator strings or community shorthand).
    "PA28": ("P28A", "Old AircraftGenerator emitted non-ICAO 'PA28'; ICAO key is 'P28A'."),
}

# Codes allowed in the sibling map even if they are not present in FaaAcd.json.
# These are informal aliases we promise to resolve at runtime (e.g. legacy
# strings frozen in old recordings) — the script otherwise skips non-ACD codes.
ALLOW_NON_ACD = frozenset(["PA28"])

WEIGHTS = ["Small", "Small+", "Large", "Heavy", "Super"]
AAC = ["A", "B", "C", "D", "E"]


def weight_dist(a: str | None, b: str | None) -> float:
    if a not in WEIGHTS or b not in WEIGHTS:
        return 3.0
    d = abs(WEIGHTS.index(a) - WEIGHTS.index(b))
    return [0.0, 1.0, 3.0, 5.0, 7.0][min(d, 4)]


def aac_dist(a: str | None, b: str | None) -> float:
    if a not in AAC or b not in AAC:
        return 1.0
    d = abs(AAC.index(a) - AAC.index(b))
    return [0.0, 0.5, 1.5, 2.5, 3.5][min(d, 4)]


def mtow_dist(a: float | None, b: float | None) -> float:
    if not a or not b or a <= 0 or b <= 0:
        return 1.0
    return min(2.0, abs(math.log2(a / b)))


def engines_dist(a: int | None, b: int | None) -> float:
    if a is None or b is None:
        return 1.0
    d = abs(a - b)
    return [0.0, 0.5, 1.5, 2.5][min(d, 3)]


def main() -> None:
    acd = json.loads(ACD_PATH.read_text())
    profile_codes = {p["typeCode"] for p in json.loads(PROFILES_PATH.read_text())}
    candidates = sorted(profile_codes & set(acd))
    missing = sorted(set(acd) - profile_codes)

    auto: dict[str, tuple[str, float]] = {}
    for m in missing:
        rec = acd[m]
        if rec.get("class") != "Fixed-wing":
            continue
        eng = rec.get("physicalClassEngine")
        pool = [c for c in candidates if acd[c].get("physicalClassEngine") == eng]
        if not pool:
            continue
        scored: list[tuple[float, str]] = []
        for c in pool:
            cr = acd[c]
            s = (
                weight_dist(rec.get("faaWeight"), cr.get("faaWeight"))
                + aac_dist(rec.get("aac"), cr.get("aac"))
                + mtow_dist(rec.get("mtowLb"), cr.get("mtowLb"))
                + engines_dist(rec.get("numEngines"), cr.get("numEngines"))
            )
            scored.append((s, c))
        scored.sort()
        best_score, best = scored[0]
        if best_score <= AUTO_THRESHOLD:
            auto[m] = (best, round(best_score, 2))

    entries: dict[str, dict] = {}
    for m, (sib, justif) in MANUAL_OVERRIDES.items():
        if m not in acd and m not in ALLOW_NON_ACD:
            print(f"WARN: manual override '{m}' not in ACD and not whitelisted")
            continue
        if sib not in profile_codes:
            print(f"WARN: manual override '{m}' -> '{sib}' has no profile")
            continue
        entries[m] = {
            "sibling": sib,
            "score": None,
            "source": "manual",
            "note": justif,
        }
    for m, (sib, score) in auto.items():
        if m not in entries:
            entries[m] = {"sibling": sib, "score": score, "source": "auto"}

    out = {
        "_comment": (
            "Generated by tools/build_aircraft_sibling_map.py. "
            "Do not edit by hand — change MANUAL_OVERRIDES in the script "
            "and re-run. Score < 0.30 means very close fit on engine class, "
            "weight class, approach-speed category, MTOW (within ~25%), and "
            "engine count. 'manual' entries are human-curated overrides."
        ),
        "_threshold": AUTO_THRESHOLD,
        "_generated_count": len(entries),
        "siblings": dict(sorted(entries.items())),
    }
    OUT_PATH.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
    auto_n = sum(1 for v in entries.values() if v["source"] == "auto")
    manual_n = sum(1 for v in entries.values() if v["source"] == "manual")
    print(f"wrote {OUT_PATH.relative_to(REPO_ROOT)}")
    print(f"  {len(entries)} entries  ({auto_n} auto + {manual_n} manual)")


if __name__ == "__main__":
    main()
