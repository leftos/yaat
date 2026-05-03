#!/usr/bin/env python3
"""One-time seeder for `cruiseAltitude` in src/Yaat.Sim/Data/AircraftProfiles.json.

`cruiseSpeed` in the profile is TAS at the aircraft's typical cruise altitude (or
Mach when < 1.0). `cruiseAltitude` records that reference altitude so
`AircraftPerformance.DefaultSpeed` can convert TAS to a constant cruise IAS via
`WindInterpolator.TasToIas(cruiseSpeed, cruiseAltitude)`.

The heuristic picks a sensible default per category. Manual overrides are fine
afterward; re-running the script will overwrite all entries (idempotent for the
heuristic, destructive for any hand-tuned values).

Usage:
    python tools/populate_cruise_altitude.py [--dry-run]
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PROFILES_PATH = REPO_ROOT / "src" / "Yaat.Sim" / "Data" / "AircraftProfiles.json"

# ISA constants matching WindInterpolator.cs
T0 = 288.15
LAPSE = 0.0065
G = 9.80665
R_GAS = 287.05287
GAMMA = 1.4
KT_TO_MS = 0.514444
MS_TO_KT = 1.0 / KT_TO_MS
FT_TO_M = 0.3048
G_OVER_LR = G / (LAPSE * R_GAS)
GAMMA_RATIO = GAMMA / (GAMMA - 1.0)
INV_GAMMA_RATIO = 1.0 / GAMMA_RATIO
TROP_M = 11_000.0
TROP_K = 216.65
A0 = math.sqrt(GAMMA * R_GAS * T0)
DELTA_TROP = (TROP_K / T0) ** G_OVER_LR


def get_atmosphere(altitude_ft: float) -> tuple[float, float]:
    h = altitude_ft * FT_TO_M
    if h <= TROP_M:
        t = T0 - LAPSE * h
        delta = (t / T0) ** G_OVER_LR
        return t, delta
    strat_delta = DELTA_TROP * math.exp(-G * (h - TROP_M) / (R_GAS * TROP_K))
    return TROP_K, strat_delta


def tas_to_ias(tas_kts: float, altitude_ft: float) -> float:
    if tas_kts <= 0:
        return 0.0
    temp_k, delta = get_atmosphere(altitude_ft)
    a_local = math.sqrt(GAMMA * R_GAS * temp_k)
    mach = tas_kts * KT_TO_MS / a_local
    qc_over_p0 = delta * ((1.0 + 0.2 * mach * mach) ** GAMMA_RATIO - 1.0)
    vc_ms = A0 * math.sqrt(5.0 * ((qc_over_p0 + 1.0) ** INV_GAMMA_RATIO - 1.0))
    return vc_ms * MS_TO_KT


def pick_cruise_altitude(profile: dict) -> int:
    """Heuristic: see plan / table in docs."""
    type_code = profile.get("typeCode", "")
    is_helo = bool(profile.get("isHelo"))
    is_prop = bool(profile.get("isProp"))
    ceiling = float(profile.get("ceiling", 0))

    if is_helo:
        return 0
    if type_code.startswith("VEH"):
        return 0
    if not is_prop and not is_helo:
        if ceiling > 25000:
            return round(ceiling * 0.85 / 100) * 100
        return round(ceiling * 0.85 / 100) * 100
    if is_prop:
        if ceiling > 15000:
            return round(ceiling * 0.80 / 100) * 100
        return round(ceiling * 0.55 / 100) * 100
    return round(ceiling * 0.7 / 100) * 100


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true", help="Print what would change without writing")
    args = parser.parse_args()

    with open(PROFILES_PATH, encoding="utf-8") as f:
        profiles = json.load(f)

    changed = 0
    for profile in profiles:
        new_alt = pick_cruise_altitude(profile)
        existing = profile.get("cruiseAltitude")
        if existing != new_alt:
            profile["cruiseAltitude"] = new_alt
            changed += 1

    print(f"Updated {changed} of {len(profiles)} profiles.")

    spot_check = ["M20P", "B738", "C172", "CL60", "R22", "AT72", "PA38", "C150"]
    print("\nSpot check:")
    print(f"  {'type':6} {'ceiling':>8} {'cruiseTAS':>10} {'cruiseAlt':>10} {'cruiseIAS':>10}")
    for tc in spot_check:
        p = next((p for p in profiles if p["typeCode"] == tc), None)
        if not p:
            continue
        cruise = p.get("cruiseSpeed", 0)
        alt = p.get("cruiseAltitude", 0)
        if 0 < cruise < 1.0:
            ias_str = f"Mach {cruise}"
        elif alt > 0:
            ias_str = f"{tas_to_ias(cruise, alt):.1f}"
        else:
            ias_str = f"{cruise:.1f} (SL)"
        print(f"  {tc:6} {p.get('ceiling', 0):>8} {cruise:>10} {alt:>10} {ias_str:>10}")

    if args.dry_run:
        print("\n--dry-run: no changes written")
        return 0

    with open(PROFILES_PATH, "w", encoding="utf-8") as f:
        json.dump(profiles, f, indent=2)
        f.write("\n")
    print(f"\nWrote {PROFILES_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
