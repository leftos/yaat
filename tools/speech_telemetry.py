#!/usr/bin/env python3
"""Pull, review, evaluate, and promote opted-in YAAT speech telemetry.

Usage:
    python tools/speech_telemetry.py <subcommand> [args] [flags]

Subcommands:
    pull [--server URL]          Download every new sample bundle into the state directory and unpack
                                 each sample into cases/<cid>-<sampleId>/ (audio.wav + session.json
                                 + meta.json). --since is the newest storedUtc already pulled.
    summary [--all] [--json]     Triage table of the cases awaiting review, worst first: rule-miss
                                 and pipeline failures, then accepted-via-LLM-fallback, then clean
                                 rule hits. --all includes reviewed cases, --json prints rows.
    eval [--trials N] [case ...] Stage the selected cases (default: every unreviewed case) as an
                                 eval corpus and score them through tools/Yaat.SpeechSandbox --eval
                                 (real audio through the production STT + mapping pipeline).
    reviewed (<case> ... | --all-shown)
                                 Mark cases reviewed so `summary` stops showing them. --all-shown
                                 marks everything the default `summary` would show right now.
    promote <case> [--name S]    Append one reviewed case's text-only labels to the tracked
                                 telemetry regression corpus and refuse a duplicate transcript.

State directory: .tmp/speech-telemetry/ (gitignored) holding raw/ (downloaded bundles; one that cannot
be read is moved to raw/quarantine/ and never fetched again), cases/ (one directory per sample),
state.json (pull watermark) and reviewed.json (reviewed case names). Nothing pulled from the server
enters the repo: `promote` is the only subcommand that writes a tracked file, and it writes text only
— never audio, a CID, a session file or a timestamp.

The server endpoints (yaat-server admin API) are:
    GET {server}/admin/telemetry/speech[?since=<ISO-8601 UTC>]   JSON array of stored bundles
    GET {server}/admin/telemetry/speech/{date}/{file}            one bundle zip
Both carry the admin password in the X-Yaat-Admin-Password header; the password is read from the
YAAT_ADMIN_PASSWORD environment variable and is never accepted as a command-line argument.

Requires: Python 3.13 (stdlib only).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, NoReturn

REPO_ROOT = Path(__file__).resolve().parent.parent
STATE_DIR = REPO_ROOT / ".tmp" / "speech-telemetry"
DEFAULT_SERVER = "https://yaat1.leftos.dev"
REGRESSION_CORPUS = Path("tests") / "Yaat.Client.Tests" / "TestData" / "speech-transcripts" / "telemetry-regressions.json"

# Triage order for `summary`: worst first, so the rows a reviewer should open are at the top.
_OUTCOME_RANK = {"nomappingfound": 0, "emptytranscript": 1, "error": 2, "cancelled": 3}
_ACCEPTED_OUTCOME = "commandaccepted"
_LLM_FALLBACK_RANK = 4
_ACCEPTED_RANK = 5
_UNRANKED = 6

# Plain-text summary columns; widths keep a row (with its separators) under 150 characters.
_COLUMN_WIDTHS = {"stored": 20, "cid": 8, "outcome": 15, "llm": 3, "transcript": 55, "canonical": 32}
_EMPTY_CELL = "—"

_CASE_FILES = ("audio.wav", "session.json", "expected.json")
_UNAUTHORIZED = 401

# The manifest and the bundle's file name are uploader-controlled. A sample id names a directory under
# cases/, so it is accepted only in the exact shape the client generates, and a bundle whose file name
# carries no plain CID is refused outright.
_SAMPLE_ID_PATTERN = re.compile(r"^\d{8}-\d{6}-[0-9a-f]{6}$")
_CID_PATTERN = re.compile(r"^[A-Za-z0-9_]+$")

# Per-entry decompression ceilings, checked against the zip's own directory before anything is inflated.
_MAX_MANIFEST_BYTES = 1024 * 1024
_MAX_AUDIO_BYTES = 20 * 1024 * 1024
_MAX_SESSION_BYTES = 5 * 1024 * 1024
_QUARANTINE_NAME = "quarantine"


def _die(message: str) -> NoReturn:
    """Print a fatal error and exit non-zero."""
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(2)


def _warn(message: str) -> None:
    """Print a non-fatal warning and carry on."""
    print(f"warning: {message}", file=sys.stderr)


# ---------------------------------------------------------------------------
# State-directory locations (resolved per call so tests can redirect STATE_DIR)
# ---------------------------------------------------------------------------


def _raw_dir() -> Path:
    return STATE_DIR / "raw"


def _cases_dir() -> Path:
    return STATE_DIR / "cases"


def _quarantine_dir() -> Path:
    return _raw_dir() / _QUARANTINE_NAME


def _state_path() -> Path:
    return STATE_DIR / "state.json"


def _reviewed_path() -> Path:
    return STATE_DIR / "reviewed.json"


def _eval_corpus_dir() -> Path:
    return STATE_DIR / "eval-corpus"


def _regression_path() -> Path:
    return REPO_ROOT / REGRESSION_CORPUS


def _load_json(path: Path, default: Any) -> Any:
    if not path.exists():
        return default
    return json.loads(path.read_text(encoding="utf-8"))


def _write_json(path: Path, payload: Any) -> None:
    """Write indented JSON with LF endings and a trailing newline."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8", newline="\n")


def _field(obj: Any, name: str) -> Any:
    """Case-insensitive field read: session.json is camelCase, older exports are PascalCase."""
    if not isinstance(obj, dict):
        return None
    wanted = name.lower()
    for key, value in obj.items():
        if key.lower() == wanted:
            return value
    return None


def _slug(text: str) -> str:
    return re.sub(r"-+", "-", re.sub(r"[^a-z0-9]+", "-", text.lower())).strip("-")


def _is_inside(path: Path, root: Path) -> bool:
    """True when ``path`` resolves under ``root`` — the guard against a manifest naming a way out of it."""
    return path.resolve().is_relative_to(root.resolve())


# ---------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------


def _admin_password() -> str:
    password = os.environ.get("YAAT_ADMIN_PASSWORD", "")
    if not password:
        _die("YAAT_ADMIN_PASSWORD is not set. Export the server's admin password before pulling; it is never an argument.")
    return password


def _fetch(url: str, password: str) -> bytes:
    request = urllib.request.Request(url, headers={"X-Yaat-Admin-Password": password})
    try:
        with urllib.request.urlopen(request) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        if exc.code == _UNAUTHORIZED:
            _die(f"the server rejected the admin password for {url} (HTTP 401). Check YAAT_ADMIN_PASSWORD.")
        _die(f"GET {url} failed with HTTP {exc.code} {exc.reason}")
    except urllib.error.URLError as exc:
        _die(f"GET {url} failed: {exc.reason}")


def _fetch_listing(server: str, password: str, since: str | None) -> list[dict[str, Any]]:
    url = f"{server}/admin/telemetry/speech"
    if since:
        url += "?since=" + urllib.parse.quote(since, safe="")
    payload = json.loads(_fetch(url, password).decode("utf-8"))
    if not isinstance(payload, list):
        _die(f"{url} did not return a JSON array of stored bundles.")
    return payload


# ---------------------------------------------------------------------------
# pull
# ---------------------------------------------------------------------------


def _read_entry(bundle: zipfile.ZipFile, name: str, limit: int) -> bytes | None:
    """Read one entry, or None when it is missing or declares a size past ``limit``.

    The declared size is checked before anything is inflated, so a decompression bomb never gets to
    expand; the read is capped as well, in case the archive lies about it.
    """
    try:
        info = bundle.getinfo(name)
    except KeyError:
        return None
    if info.file_size > limit:
        return None
    with bundle.open(name) as handle:
        data = handle.read(limit + 1)
    return data if len(data) <= limit else None


def _read_manifest(bundle: zipfile.ZipFile) -> list[Any]:
    """The manifest's sample list. Raises when the manifest is unusable, so the bundle is quarantined."""
    raw = _read_entry(bundle, "manifest.json", _MAX_MANIFEST_BYTES)
    if raw is None:
        raise ValueError("manifest.json is missing, or declares a size past its 1 MB cap")
    document = json.loads(raw.decode("utf-8"))
    samples = document.get("samples") if isinstance(document, dict) else None
    if not isinstance(samples, list):
        raise ValueError("manifest.json carries no samples list")
    return samples


def _unpack_sample(bundle: zipfile.ZipFile, case_dir: Path, sample_id: str, bundle_name: str) -> bool:
    """Write one sample's audio and session into ``case_dir``. False, with a warning, when either is unusable."""
    audio = _read_entry(bundle, f"samples/{sample_id}/audio.wav", _MAX_AUDIO_BYTES)
    session = _read_entry(bundle, f"samples/{sample_id}/session.json", _MAX_SESSION_BYTES)
    if audio is None or session is None:
        _warn(f"{bundle_name}: sample {sample_id} is missing audio.wav or session.json, or one declares a size past its cap; skipping it.")
        return False
    case_dir.mkdir(parents=True, exist_ok=True)
    (case_dir / "audio.wav").write_bytes(audio)
    (case_dir / "session.json").write_bytes(session)
    return True


def _unpack_bundle(zip_path: Path, item: dict[str, Any]) -> int:
    """Unpack every sample of one downloaded bundle. Returns the count of new case directories.

    The manifest is uploader-controlled, so a sample id is accepted only in the shape the client
    generates, and the case directory it names must resolve inside ``cases/``.
    """
    cid = zip_path.name.split("-", 1)[0]
    if not _CID_PATTERN.fullmatch(cid):
        _warn(f"{zip_path.name}: the file name carries no usable CID; skipping the bundle.")
        return 0
    source = str(item.get("path", zip_path.name))
    stored_utc = str(item.get("storedUtc", ""))
    written = 0
    with zipfile.ZipFile(zip_path) as bundle:
        for sample in _read_manifest(bundle):
            sample_id = str(sample.get("id", ""))
            case_dir = _cases_dir() / f"{cid}-{sample_id}"
            if not _SAMPLE_ID_PATTERN.fullmatch(sample_id):
                _warn(f"{zip_path.name}: sample id {sample_id!r} is not a usable sample id; skipping it.")
                continue
            if not _is_inside(case_dir, _cases_dir()):
                _warn(f"{zip_path.name}: sample id {sample_id!r} names a path outside the cases directory; skipping it.")
                continue
            if case_dir.exists():
                continue
            if not _unpack_sample(bundle, case_dir, sample_id, zip_path.name):
                continue
            _write_json(case_dir / "meta.json", {"cid": cid, "storedUtc": stored_utc, "source": source, "sampleId": sample_id})
            written += 1
    return written


def _quarantine(zip_path: Path, reason: Exception) -> None:
    """Move an unreadable bundle aside, so one bad file cannot block every later pull."""
    target = _quarantine_dir() / f"{datetime.now(UTC).strftime('%Y%m%d')}-{zip_path.name}"
    target.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.move(zip_path, target)
    except OSError as exc:
        _warn(f"{zip_path.name}: unreadable ({reason}) and could not be quarantined: {exc}")
        return
    _warn(f"{zip_path.name}: unreadable ({reason}); moved to {target}.")


def _record_quarantine(state: dict[str, Any], relative: str) -> dict[str, Any]:
    """Remember a quarantined bundle, so the next pull does not download the same bytes again."""
    quarantined = state.setdefault("quarantined", [])
    if relative not in quarantined:
        quarantined.append(relative)
    return state


def _record_watermark(state: dict[str, Any], item: dict[str, Any]) -> dict[str, Any]:
    """Advance the pull watermark to the newest storedUtc seen so far."""
    stored_utc = str(item.get("storedUtc", ""))
    if stored_utc > str(state.get("lastStoredUtc", "")):
        state["lastStoredUtc"] = stored_utc
    return state


def _since_for_listing(watermark: str | None) -> str | None:
    """The server's ``since`` is strict, so a sample stored in the same instant as the watermark would be missed.

    Asking from one second earlier re-lists that instant; bundles already on disk are skipped by the pull loop.
    """
    if not watermark:
        return None
    try:
        parsed = datetime.fromisoformat(watermark)
    except ValueError:
        _die(f"state.json has an unreadable lastStoredUtc: {watermark!r}")
    return (parsed - timedelta(seconds=1)).isoformat()


def cmd_pull(args: argparse.Namespace) -> int:
    password = _admin_password()
    server = args.server.rstrip("/")
    state = _load_json(_state_path(), {})
    listing = _fetch_listing(server, password, _since_for_listing(state.get("lastStoredUtc")))

    new_samples = 0
    for item in listing:
        relative = str(item.get("path", ""))
        if not relative:
            _die(f"the server listing has an entry without a path: {item!r}")
        if relative in state.get("quarantined", []):
            continue
        zip_path = _raw_dir() / relative
        if not zip_path.exists():
            zip_path.parent.mkdir(parents=True, exist_ok=True)
            zip_path.write_bytes(_fetch(f"{server}/admin/telemetry/speech/{relative}", password))
        try:
            new_samples += _unpack_bundle(zip_path, item)
        except (KeyError, ValueError, zipfile.BadZipFile) as exc:
            _quarantine(zip_path, exc)
            state = _record_quarantine(state, relative)
        # Recorded per file so an interrupted pull resumes at the next bundle, not from scratch.
        _write_json(_state_path(), _record_watermark(state, item))

    print(f"{new_samples} new sample(s)")
    return 0


# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------


def _load_case(case_dir: Path) -> dict[str, Any]:
    """Read one case directory into the flat shape `summary`, `eval` and `promote` share."""
    meta = _load_json(case_dir / "meta.json", {})
    session = _load_json(case_dir / "session.json", {})
    trace = _field(session, "trace")
    raw_transcript = _field(trace, "rawTranscript") or _field(session, "transcript") or ""
    return {
        "case": case_dir.name,
        "dir": case_dir,
        "cid": str(meta.get("cid") or case_dir.name.split("-", 1)[0]),
        "storedUtc": str(meta.get("storedUtc", "")),
        "outcome": str(_field(session, "outcome") or ""),
        "usedLlmFallback": bool(_field(session, "usedLlmFallback")),
        "transcript": str(raw_transcript),
        "canonical": _field(session, "canonicalCommand"),
    }


def _load_cases() -> list[dict[str, Any]]:
    root = _cases_dir()
    if not root.exists():
        return []
    return [_load_case(path) for path in sorted(root.iterdir()) if (path / "session.json").exists()]


def _reviewed_names() -> set[str]:
    return set(_load_json(_reviewed_path(), []))


def _unreviewed(cases: list[dict[str, Any]]) -> list[dict[str, Any]]:
    reviewed = _reviewed_names()
    return [case for case in cases if case["case"] not in reviewed]


def _find_case(name: str) -> dict[str, Any]:
    for case in _load_cases():
        if case["case"] == name:
            return case
    _die(f"unknown case '{name}'. Run `summary --all` for the case names.")


def _triage_key(case: dict[str, Any]) -> tuple[int, str]:
    outcome = case["outcome"].lower()
    if outcome in _OUTCOME_RANK:
        rank = _OUTCOME_RANK[outcome]
    elif outcome == _ACCEPTED_OUTCOME:
        rank = _LLM_FALLBACK_RANK if case["usedLlmFallback"] else _ACCEPTED_RANK
    else:
        rank = _UNRANKED
    return (rank, case["storedUtc"])


def _clip(text: str, width: int) -> str:
    return text if len(text) <= width else text[: width - 1] + "…"


def _row_text(case: dict[str, Any]) -> dict[str, str]:
    return {
        "stored": case["storedUtc"][:10],
        "cid": case["cid"],
        "outcome": case["outcome"],
        "llm": "yes" if case["usedLlmFallback"] else "no",
        "transcript": case["transcript"],
        "canonical": case["canonical"] or _EMPTY_CELL,
    }


def _print_table(cases: list[dict[str, Any]]) -> None:
    columns = list(_COLUMN_WIDTHS)
    print(" | ".join(name.ljust(_COLUMN_WIDTHS[name]) for name in columns))
    print("-+-".join("-" * _COLUMN_WIDTHS[name] for name in columns))
    for case in cases:
        row = _row_text(case)
        print(" | ".join(_clip(row[name], _COLUMN_WIDTHS[name]).ljust(_COLUMN_WIDTHS[name]) for name in columns))


def cmd_summary(args: argparse.Namespace) -> int:
    cases = _load_cases() if args.all else _unreviewed(_load_cases())
    cases.sort(key=_triage_key)
    if args.json:
        print(json.dumps([{key: value for key, value in case.items() if key != "dir"} for case in cases], indent=2))
        return 0
    _print_table(cases)
    return 0


# ---------------------------------------------------------------------------
# eval
# ---------------------------------------------------------------------------


def stage_case(case_dir: Path, staged_dir: Path) -> None:
    """Copy one case into an eval corpus directory (audio, session, and a reviewed expected.json)."""
    staged_dir.mkdir(parents=True, exist_ok=True)
    for name in _CASE_FILES:
        source = case_dir / name
        if source.exists():
            shutil.copy2(source, staged_dir / name)


def sync_expected_back(staged_dir: Path, case_dir: Path) -> bool:
    """Copy an expected.json the harness wrote into the corpus back onto the case. True when written."""
    staged = staged_dir / "expected.json"
    if not staged.exists():
        return False
    target = case_dir / "expected.json"
    if target.exists() and target.read_bytes() == staged.read_bytes():
        return False
    shutil.copy2(staged, target)
    return True


def _selected_cases(names: list[str]) -> list[dict[str, Any]]:
    if not names:
        return _unreviewed(_load_cases())
    return [_find_case(name) for name in names]


def eval_command(corpus: Path, trials: int, out_dir: Path) -> list[str]:
    """The gated SpeechSandbox eval invocation — pure, so it is testable without dotnet."""
    return [
        "pwsh",
        str(REPO_ROOT / "tools" / "gate.ps1"),
        "-Log",
        str(STATE_DIR / "eval.log"),
        "-TimeoutSeconds",
        "1800",
        "-Slot",
        "heavy",
        "--",
        "dotnet",
        "run",
        "--project",
        str(REPO_ROOT / "tools" / "Yaat.SpeechSandbox"),
        "-c",
        "Release",
        "--",
        "--eval",
        str(corpus),
        "--trials",
        str(trials),
        "--out-dir",
        str(out_dir),
    ]


def cmd_eval(args: argparse.Namespace) -> int:
    cases = _selected_cases(args.case)
    if not cases:
        print("no cases to eval")
        return 0

    corpus = _eval_corpus_dir()
    if corpus.exists():
        shutil.rmtree(corpus)
    for case in cases:
        stage_case(case["dir"], corpus / case["case"])

    stamp = datetime.now(UTC).strftime("%Y%m%d-%H%M%S")
    out_dir = STATE_DIR / f"eval-{stamp}"
    command = eval_command(corpus, args.trials, out_dir)
    print("running: " + " ".join(command))
    result = subprocess.run(command, cwd=REPO_ROOT, check=False)

    for case in cases:
        sync_expected_back(corpus / case["case"], case["dir"])
    print(f"report: {out_dir / 'report.md'}")
    return result.returncode


# ---------------------------------------------------------------------------
# reviewed
# ---------------------------------------------------------------------------


def cmd_reviewed(args: argparse.Namespace) -> int:
    if args.all_shown:
        names = [case["case"] for case in _unreviewed(_load_cases())]
    elif args.case:
        names = [_find_case(name)["case"] for name in args.case]
    else:
        _die("give one or more case names, or --all-shown.")
    reviewed = sorted(_reviewed_names() | set(names))
    _write_json(_reviewed_path(), reviewed)
    print(f"reviewed {len(names)} case(s), {len(reviewed)} total")
    return 0


# ---------------------------------------------------------------------------
# promote
# ---------------------------------------------------------------------------


def _case_slug(case: dict[str, Any]) -> str:
    """A case-derived name that never carries the uploader's CID."""
    prefix = f"{case['cid']}-"
    stem = case["case"][len(prefix) :] if case["case"].startswith(prefix) else case["case"]
    slug = _slug(stem)
    if not slug or (case["cid"] and case["cid"] in slug):
        slug = _slug(str(case["canonical"] or ""))
    return slug or "telemetry-case"


def _build_entry(case: dict[str, Any], expected: dict[str, Any], name: str | None) -> dict[str, Any]:
    """Build the text-only regression entry. Audio, the CID and timestamps never appear in it."""
    session = _load_json(case["dir"] / "session.json", {})
    trace = _field(session, "trace")
    callsigns = _field(expected, "activeCallsigns") or _field(trace, "activeCallsigns") or []
    fixes = _field(expected, "programmedFixes") or _field(trace, "programmedFixes") or []
    return {
        "name": name or _case_slug(case),
        "transcript": str(_field(trace, "rawTranscript") or _field(session, "transcript") or ""),
        "canonical": str(_field(expected, "canonical") or ""),
        "callsign": _field(expected, "callsign"),
        "activeCallsigns": list(callsigns),
        "programmedFixes": list(fixes),
        "availableRunwaysByAirport": _field(trace, "availableRunwaysByAirport") or {},
        "taxiwayNames": list(_field(trace, "taxiwayNames") or []),
        "aircraftDestinations": _field(trace, "aircraftDestinations") or {},
    }


def cmd_promote(args: argparse.Namespace) -> int:
    case = _find_case(args.case)
    expected = _load_json(case["dir"] / "expected.json", None)
    if expected is None:
        _die(f"{case['case']} has no expected.json. Score it with `eval` and label it first.")
    if _field(expected, "unreviewed"):
        _die(f'{case["case"]}\'s expected.json is still an unreviewed stub. Verify the labels and drop the "unreviewed" flag.')

    entry = _build_entry(case, expected, args.name)
    path = _regression_path()
    entries = _load_json(path, [])
    for existing in entries:
        if existing.get("transcript") == entry["transcript"]:
            _die(f"'{existing.get('name')}' already covers this transcript; nothing appended.")
    entries.append(entry)
    entries.sort(key=lambda item: item["name"])
    _write_json(path, entries)
    print(f"promoted {entry['name']} ({len(entries)} entries in {REGRESSION_CORPUS})")
    return 0


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="speech_telemetry.py",
        description=__doc__.splitlines()[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    pull = subparsers.add_parser("pull", help="download new sample bundles and unpack them into cases/")
    pull.add_argument("--server", default=DEFAULT_SERVER, help=f"server base URL (default: {DEFAULT_SERVER})")
    pull.set_defaults(func=cmd_pull)

    summary = subparsers.add_parser("summary", help="triage table of cases awaiting review")
    summary.add_argument("--all", action="store_true", help="include cases already marked reviewed")
    summary.add_argument("--json", action="store_true", help="print the rows as JSON")
    summary.set_defaults(func=cmd_summary)

    evaluate = subparsers.add_parser("eval", help="score the selected cases with the SpeechSandbox harness")
    evaluate.add_argument("case", nargs="*", help="case directory names (default: every unreviewed case)")
    evaluate.add_argument("--trials", type=int, default=1, help="STT trials per case (default: 1)")
    evaluate.set_defaults(func=cmd_eval)

    reviewed = subparsers.add_parser("reviewed", help="stop showing cases in summary")
    reviewed.add_argument("case", nargs="*", help="case directory names")
    reviewed.add_argument("--all-shown", action="store_true", help="mark every case the default summary shows")
    reviewed.set_defaults(func=cmd_reviewed)

    promote = subparsers.add_parser("promote", help="append a reviewed case to the tracked regression corpus")
    promote.add_argument("case", help="case directory name")
    promote.add_argument("--name", help="entry name (default: the case directory name without the CID)")
    promote.set_defaults(func=cmd_promote)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
