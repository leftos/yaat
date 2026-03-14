#!/usr/bin/env python3
"""E2E scenario validation: wipe cache, download all ARTCCs, validate, produce report.

Usage:
    python tools/validate-all-scenarios.py                # full run (wipe + download + validate + report)
    python tools/validate-all-scenarios.py --no-refresh   # skip wipe/download, validate cached scenarios
    python tools/validate-all-scenarios.py --artcc ZOA ZLA # specific ARTCCs only

Output: .tmp/scenario-validation-report.md
"""

import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
SCENARIOS_ROOT = os.path.join(REPO_ROOT, "tests", "Yaat.Sim.Tests", "TestData", "Scenarios")
VALIDATOR_PROJECT = os.path.join(REPO_ROOT, "tools", "Yaat.ScenarioValidator")
OUTPUT_DIR = os.path.join(REPO_ROOT, ".tmp")
REPORT_PATH = os.path.join(OUTPUT_DIR, "scenario-validation-report.md")

BASE_URL = "https://data-api.vnas.vatsim.net/api/training"
HEADERS = {"User-Agent": "yaat-scenario-refresh/1.0"}

ALL_ARTCCS = [
    "ZAB", "ZAU", "ZBW", "ZDC", "ZDV", "ZFW", "ZHU", "ZID",
    "ZJX", "ZKC", "ZLA", "ZLC", "ZMA", "ZME", "ZMP", "ZNY",
    "ZOA", "ZOB", "ZSE", "ZTL",
]

_print_lock = threading.Lock()


def log(msg: str) -> None:
    with _print_lock:
        print(msg, flush=True)


# ---------------------------------------------------------------------------
# Download
# ---------------------------------------------------------------------------

def fetch_json(url: str) -> object:
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def download_one(sid: str, name: str, out_dir: str) -> str:
    path = os.path.join(out_dir, f"{sid}.json")
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return "cached"
    try:
        url = f"{BASE_URL}/scenarios/{sid}"
        req = urllib.request.Request(url, headers=HEADERS)
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = resp.read()
        with open(path, "wb") as f:
            f.write(data)
        return "ok"
    except Exception as e:
        return f"fail: {name}: {e}"


def refresh_artcc(artcc_id: str) -> tuple[str, int, int, int, int]:
    out_dir = os.path.join(SCENARIOS_ROOT, artcc_id)
    os.makedirs(out_dir, exist_ok=True)

    try:
        summaries = fetch_json(f"{BASE_URL}/scenario-summaries/by-artcc/{artcc_id}")
    except Exception as e:
        log(f"  [{artcc_id}] Error fetching summaries: {e}")
        return (artcc_id, 0, 0, 0, 0)

    if not summaries:
        log(f"  [{artcc_id}] No scenarios")
        return (artcc_id, 0, 0, 0, 0)

    with open(os.path.join(out_dir, "_summaries.json"), "w") as f:
        json.dump(summaries, f, indent=2)

    total = len(summaries)
    ok = cached = fail = 0

    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {pool.submit(download_one, s["id"], s.get("name", s["id"]), out_dir): s for s in summaries}
        for future in as_completed(futures):
            r = future.result()
            if r == "cached":
                cached += 1
            elif r == "ok":
                ok += 1
            else:
                fail += 1

    log(f"  [{artcc_id}] {total} scenarios: {ok} new, {cached} cached, {fail} failed")
    return (artcc_id, total, ok, cached, fail)


# ---------------------------------------------------------------------------
# Validate
# ---------------------------------------------------------------------------

def validate_artcc(artcc_id: str) -> tuple[str, dict | None]:
    scenario_dir = os.path.join(SCENARIOS_ROOT, artcc_id)
    if not os.path.isdir(scenario_dir):
        log(f"  [{artcc_id}] No cached scenarios, skipping")
        return (artcc_id, None)

    result = subprocess.run(
        ["dotnet", "run", "--no-build", "-c", "Release", "--project", VALIDATOR_PROJECT, "--", "--dir", scenario_dir, "--json"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300,
    )

    if result.returncode not in (0, 1):
        log(f"  [{artcc_id}] Validator error: {result.stderr.strip()[:200]}")
        return (artcc_id, None)

    try:
        data = json.loads(result.stdout)
        failures = sum(len(s.get("Failures", [])) for s in data)
        proc_issues = sum(len(s.get("ProcedureIssues", [])) for s in data)
        presets = sum(s.get("TotalPresets", 0) for s in data)
        log(f"  [{artcc_id}] {len(data)} scenarios, {presets} presets, {failures} failures, {proc_issues} procedure issues")
        return (artcc_id, {"scenarios": data, "count": len(data), "presets": presets, "failures": failures, "proc_issues": proc_issues})
    except json.JSONDecodeError:
        log(f"  [{artcc_id}] Failed to parse JSON output")
        return (artcc_id, None)


# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

def build_report(results: dict[str, dict | None]) -> str:
    lines: list[str] = []
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    lines.append(f"# Scenario Validation Report — {now}\n")

    # Summary table
    total_scenarios = total_presets = total_failures = total_proc = 0
    rows: list[tuple[str, int, int, int, int]] = []
    for artcc in ALL_ARTCCS:
        r = results.get(artcc)
        if r is None:
            rows.append((artcc, 0, 0, 0, 0))
            continue
        rows.append((artcc, r["count"], r["presets"], r["failures"], r["proc_issues"]))
        total_scenarios += r["count"]
        total_presets += r["presets"]
        total_failures += r["failures"]
        total_proc += r["proc_issues"]

    lines.append("## Summary\n")
    lines.append(f"**{total_scenarios}** scenarios, **{total_presets}** presets, "
                 f"**{total_failures}** parse failures, **{total_proc}** procedure issues\n")
    lines.append("| ARTCC | Scenarios | Presets | Parse Failures | Procedure Issues |")
    lines.append("|-------|-----------|---------|----------------|-----------------|")
    for artcc, count, presets, failures, proc in rows:
        lines.append(f"| {artcc} | {count} | {presets} | {failures} | {proc} |")
    lines.append(f"| **Total** | **{total_scenarios}** | **{total_presets}** | **{total_failures}** | **{total_proc}** |")
    lines.append("")

    # Per-ARTCC details (only those with issues)
    for artcc in ALL_ARTCCS:
        r = results.get(artcc)
        if r is None:
            continue

        scenarios_with_failures = [s for s in r["scenarios"] if s.get("Failures")]
        scenarios_with_proc = [s for s in r["scenarios"] if s.get("ProcedureIssues")]

        if not scenarios_with_failures and not scenarios_with_proc:
            continue

        lines.append(f"## {artcc}\n")

        if scenarios_with_failures:
            lines.append("### Parse Failures\n")
            for s in scenarios_with_failures:
                lines.append(f"**{s.get('ScenarioName', '?')}** ({len(s['Failures'])} failures)\n")
                by_ac: dict[str, list[str]] = {}
                for f in s["Failures"]:
                    by_ac.setdefault(f["AircraftId"], []).append(f)
                for ac, failures in by_ac.items():
                    lines.append(f"- `{ac}`")
                    for f in failures:
                        reason = f" — {f['Reason']}" if f.get("Reason") else ""
                        lines.append(f"  - `{f['Command']}`{reason}")
                lines.append("")

        if scenarios_with_proc:
            lines.append("### Procedure Issues\n")
            for s in scenarios_with_proc:
                lines.append(f"**{s.get('ScenarioName', '?')}**\n")
                for issue in s["ProcedureIssues"]:
                    kind = issue.get("Kind", "")
                    ac = issue.get("AircraftId", "?")
                    proc_id = issue.get("ProcedureId", "?")
                    resolved = issue.get("ResolvedId", "")
                    if kind == "VersionChanged":
                        lines.append(f"- `{ac}`: {proc_id} -> {resolved}")
                    else:
                        lines.append(f"- `{ac}`: {proc_id} not found")
                lines.append("")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    no_refresh = "--no-refresh" in sys.argv
    artccs = ALL_ARTCCS

    # Parse --artcc args
    if "--artcc" in sys.argv:
        idx = sys.argv.index("--artcc")
        artccs = [a.upper() for a in sys.argv[idx + 1:] if not a.startswith("--")]
        if not artccs:
            print("Error: --artcc requires at least one ARTCC ID")
            sys.exit(1)

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # Step 1: Wipe + download
    if not no_refresh:
        print(f"=== Wiping scenario cache ===")
        for artcc in artccs:
            d = os.path.join(SCENARIOS_ROOT, artcc)
            if os.path.isdir(d):
                shutil.rmtree(d)

        print(f"=== Downloading {len(artccs)} ARTCCs (parallel) ===")
        t0 = time.time()
        with ThreadPoolExecutor(max_workers=4) as pool:
            list(pool.map(refresh_artcc, artccs))
        print(f"  Download complete in {time.time() - t0:.1f}s\n")

    # Step 2: Build validator
    print("=== Building validator ===")
    build = subprocess.run(
        ["dotnet", "build", VALIDATOR_PROJECT, "-c", "Release", "-v", "q", "--nologo"],
        capture_output=True, text=True,
    )
    if build.returncode != 0:
        print(f"Build failed:\n{build.stderr}")
        sys.exit(1)
    print("  Build OK\n")

    # Step 3: Validate all ARTCCs in parallel
    print(f"=== Validating {len(artccs)} ARTCCs (parallel) ===")
    t0 = time.time()
    validation_results: dict[str, dict | None] = {}
    with ThreadPoolExecutor(max_workers=len(artccs)) as pool:
        futures = {pool.submit(validate_artcc, artcc): artcc for artcc in artccs}
        for future in as_completed(futures):
            artcc_id, data = future.result()
            validation_results[artcc_id] = data
    print(f"  Validation complete in {time.time() - t0:.1f}s\n")

    # Step 4: Build report
    report = build_report(validation_results)
    with open(REPORT_PATH, "w", encoding="utf-8") as f:
        f.write(report)
    print(f"=== Report written to {REPORT_PATH} ===")

    # Print summary
    total_f = sum((r or {}).get("failures", 0) for r in validation_results.values())
    total_p = sum((r or {}).get("proc_issues", 0) for r in validation_results.values())
    print(f"  {total_f} parse failures, {total_p} procedure issues")


if __name__ == "__main__":
    main()
