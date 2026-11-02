#!/usr/bin/env python3
"""Report whether an AIRAC cycle newer than the committed test pin has been published.

The pin is the pair of manifests beside the committed test data (`navdata-manifest.json` and
`cifp-manifest.json`); the check asks the FAA whether the CIFP zip for each cycle after it exists,
and vNAS which NavData serial it publishes. The answer is one line on stdout and the exit code:

    0   in sync, or the published cycle was already declined
    10  a newer cycle is published (the caller asks the owner whether to repin)
    2   something could not be read or fetched

`--decline <YYNN> --state <json>` records a declined cycle in the caller's state file, so the next
run stays quiet until a still newer cycle appears; it touches the network only to read nothing.

Usage:
    uv run --no-project python tools/airac-pin-status.py
    uv run --no-project python tools/airac-pin-status.py --state <json>
    uv run --no-project python tools/airac-pin-status.py --decline <YYNN> --state <json>
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import urllib.error
import urllib.request
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

import airac

CONFIG_URL = "https://configuration.vnas.vatsim.net/"
CIFP_BASE_URL = "https://aeronav.faa.gov/Upload_313-d/cifp/"
USER_AGENT = "yaat-airac-pin/1.0"
HEAD_TIMEOUT_SECONDS = 20
CONFIG_TIMEOUT_SECONDS = 30
DEFAULT_TEST_DATA = Path("tests/Yaat.Sim.Tests/TestData")
NAVDATA_MANIFEST = "navdata-manifest.json"
CIFP_MANIFEST = "cifp-manifest.json"
DECLINED_KEY = "airacPinDeclined"
JSON_INDENT = 2
HTTP_OK = 200
HTTP_NOT_FOUND = 404
EXIT_IN_SYNC = 0
EXIT_NEWER = 10
EXIT_ERROR = 2


class PinStatusError(Exception):
    """A failure reported as `airac-pin: error: ...` with exit 2."""


@dataclass(frozen=True)
class Pin:
    """The committed test data pin: its AIRAC cycle and its NavData serial."""

    cycle: str
    serial: int


def read_json(path: Path) -> dict:
    """Read a JSON object from `path`.

    Args:
        path: The file to read.

    Returns:
        The parsed JSON object.

    Raises:
        PinStatusError: If the file cannot be read, is not JSON, or is not a JSON object.
    """
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except OSError as exc:
        raise PinStatusError(f"{path}: {exc.strerror or exc}") from exc
    except json.JSONDecodeError as exc:
        raise PinStatusError(f"{path}: not valid JSON ({exc})") from exc
    if not isinstance(payload, dict):
        raise PinStatusError(f"{path}: expected a JSON object")
    return payload


def read_pin(test_data_dir: Path) -> Pin:
    """Read the pin the two manifests record.

    Args:
        test_data_dir: The directory holding the two manifests.

    Returns:
        The pin's cycle and serial.

    Raises:
        PinStatusError: If a manifest is unreadable, incomplete, or disagrees on the cycle.
    """
    nav_path = test_data_dir / NAVDATA_MANIFEST
    cifp_path = test_data_dir / CIFP_MANIFEST
    nav = read_json(nav_path)
    cifp = read_json(cifp_path)
    nav_cycle = nav.get("airacCycle")
    cifp_cycle = cifp.get("airacCycle")
    serial = nav.get("navDataSerial")
    if not isinstance(nav_cycle, str) or not isinstance(cifp_cycle, str):
        raise PinStatusError(f"{nav_path} / {cifp_path}: a manifest has no airacCycle")
    if nav_cycle != cifp_cycle:
        raise PinStatusError(f"the pin manifests disagree: {nav_path} says AIRAC {nav_cycle}, {cifp_path} says AIRAC {cifp_cycle}")
    if not isinstance(serial, int):
        raise PinStatusError(f"{nav_path}: no navDataSerial")
    return Pin(cycle=nav_cycle, serial=serial)


def cifp_zip_name(cycle_id: str) -> str:
    """Return the FAA zip file name that carries a cycle's CIFP, e.g. `CIFP_261126.zip`."""
    return f"CIFP_{airac.cycle_date(cycle_id):%y%m%d}.zip"


def cifp_url(cycle_id: str) -> str:
    """Return the FAA download URL for a cycle's CIFP zip."""
    return f"{CIFP_BASE_URL}{cifp_zip_name(cycle_id)}"


def head_cifp(cycle_id: str) -> int:
    """Return the HTTP status of a HEAD for a cycle's CIFP zip.

    Args:
        cycle_id: The cycle whose zip to probe.

    Returns:
        The response status, `404` included; any status is an answer, not an error.

    Raises:
        PinStatusError: If the request could not be made at all.
    """
    url = cifp_url(cycle_id)
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT}, method="HEAD")
    try:
        with urllib.request.urlopen(request, timeout=HEAD_TIMEOUT_SECONDS) as response:
            return int(response.status)
    except urllib.error.HTTPError as exc:
        return int(exc.code)
    except urllib.error.URLError as exc:
        raise PinStatusError(f"{url}: {exc.reason}") from exc


def newest_published_cycle(pin_cycle: str) -> str | None:
    """Return the newest cycle after `pin_cycle` whose FAA CIFP zip is published.

    Probes the cycle after the pin, then the one after that, and stops at the first 404, so a
    gap in FAA's posting ends the search rather than hiding behind a later cycle.

    Args:
        pin_cycle: The pinned cycle.

    Returns:
        The newest published cycle after the pin, or None when the next one is not out.

    Raises:
        PinStatusError: If a probe fails with anything other than 200 or 404.
    """
    offset = 1
    newest: str | None = None
    while True:
        candidate = airac.next_cycle(pin_cycle, offset)
        status = head_cifp(candidate)
        if status == HTTP_NOT_FOUND:
            return newest
        if status != HTTP_OK:
            raise PinStatusError(f"{cifp_url(candidate)}: HTTP {status}")
        newest = candidate
        offset += 1


def fetch_vnas_serial() -> int:
    """Return the NavData serial vNAS publishes right now.

    Returns:
        The serial from the vNAS configuration document.

    Raises:
        PinStatusError: If the document cannot be fetched, is not JSON, or has no serial.
    """
    request = urllib.request.Request(CONFIG_URL, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=CONFIG_TIMEOUT_SECONDS) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raise PinStatusError(f"{CONFIG_URL}: HTTP {exc.code} ({exc.reason})") from exc
    except urllib.error.URLError as exc:
        raise PinStatusError(f"{CONFIG_URL}: {exc.reason}") from exc
    except json.JSONDecodeError as exc:
        raise PinStatusError(f"{CONFIG_URL}: not valid JSON ({exc})") from exc
    serial = payload.get("navDataSerial") if isinstance(payload, dict) else None
    if not isinstance(serial, int):
        raise PinStatusError(f"{CONFIG_URL}: no navDataSerial")
    return serial


def read_declined(state_path: Path) -> str | None:
    """Return the cycle a previous run declined, or None when the state file says nothing.

    Args:
        state_path: The caller's state file; a missing file means nothing was declined.

    Returns:
        The declined cycle id, if there is one.

    Raises:
        PinStatusError: If the file exists but cannot be read as a JSON object.
    """
    if not state_path.exists():
        return None
    declined = read_json(state_path).get(DECLINED_KEY)
    return declined if isinstance(declined, str) else None


def write_json_atomic(path: Path, payload: dict) -> None:
    """Write `payload` to `path` as indented JSON with an LF ending, replacing it atomically.

    Args:
        path: The file to write.
        payload: The JSON object to store.

    Raises:
        OSError: If the directory or the file cannot be written.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp_name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, indent=JSON_INDENT)
            stream.write("\n")
        Path(temp_name).replace(path)
    except OSError:
        Path(temp_name).unlink(missing_ok=True)
        raise


def decline_cycle(cycle_id: str, state_path: Path) -> int:
    """Record `cycle_id` as declined in the state file, keeping every other key.

    Args:
        cycle_id: The cycle the owner declined.
        state_path: The caller's state file; created when missing.

    Returns:
        The exit code for a quiet run.

    Raises:
        ValueError: If `cycle_id` is not exactly four ASCII digits.
        PinStatusError: If the state file exists but cannot be read.
    """
    airac.cycle_index(cycle_id)
    payload = read_json(state_path) if state_path.exists() else {}
    payload[DECLINED_KEY] = cycle_id
    write_json_atomic(state_path, payload)
    print(f"airac-pin: declined AIRAC {cycle_id}; asking again only for a newer cycle")
    return EXIT_IN_SYNC


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--state", type=Path, default=None, help="JSON state file that remembers a declined cycle")
    parser.add_argument("--decline", metavar="YYNN", default=None, help="record that this AIRAC cycle was declined")
    parser.add_argument("--test-data", type=Path, default=DEFAULT_TEST_DATA, help="directory holding the pin manifests")
    args = parser.parse_args(argv)
    if args.decline is not None and args.state is None:
        parser.error("--decline requires --state")
    return args


def _run(args: argparse.Namespace) -> int:
    if args.decline is not None:
        return decline_cycle(args.decline, args.state)

    pin = read_pin(args.test_data)
    newest = newest_published_cycle(pin.cycle)
    if newest is None:
        print(
            f"airac-pin: in sync (pin AIRAC {pin.cycle}, NavData serial {pin.serial}; "
            f"FAA CIFP for {airac.next_cycle(pin.cycle, 1)} not yet published)"
        )
        return EXIT_IN_SYNC

    declined = read_declined(args.state) if args.state is not None else None
    if declined is not None and airac.cycle_index(newest) <= airac.cycle_index(declined):
        print(f"airac-pin: AIRAC {newest} is published but was declined; pin stays at {pin.cycle}")
        return EXIT_IN_SYNC

    serial = fetch_vnas_serial()
    navdata_published = airac.cycle_index(airac.serial_cycle(serial)) >= airac.cycle_index(newest)
    navdata_state = "out" if navdata_published else "not out yet"
    print(
        f"airac-pin: AIRAC {newest} is published (FAA {cifp_zip_name(newest)}); "
        f"vNAS NavData for it is {navdata_state} (serial {serial}); pin is {pin.cycle}"
    )
    return EXIT_NEWER


def main(argv: Sequence[str] | None = None) -> int:
    """Run the check, or the decline, and return the exit code."""
    args = _parse_args(argv)
    try:
        return _run(args)
    except PinStatusError as exc:
        print(f"airac-pin: error: {exc}", file=sys.stderr)
        return EXIT_ERROR
    except ValueError as exc:
        print(f"airac-pin: error: {exc}", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":
    sys.exit(main())
