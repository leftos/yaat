#!/usr/bin/env python3
"""Repin the committed test NavData and CIFP to one AIRAC cycle.

Downloads NavData.dat from vNAS, reads the AIRAC cycle its serial belongs to, downloads the FAA
CIFP zip for that same cycle, and writes all four pin files together: NavData.dat,
navdata-manifest.json, FAACIFP18.gz and cifp-manifest.json. Both manifests record the data's own
cycle, never the day the tool ran, so a repin made before a cycle takes effect still stamps the
cycle the data serves.

Nothing is written until every download has succeeded, so a cycle the FAA has not published yet
leaves the committed pin untouched.

Usage:
    uv run python tools/refresh-navdata.py
    uv run python tools/refresh-navdata.py --out tests/Yaat.Sim.Tests/TestData/NavData.dat
"""

from __future__ import annotations

import argparse
import gzip
import io
import json
import sys
import urllib.error
import urllib.request
import zipfile
from collections.abc import Sequence
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath

import airac

CONFIG_URL = "https://configuration.vnas.vatsim.net/"
CIFP_BASE_URL = "https://aeronav.faa.gov/Upload_313-d/cifp/"
USER_AGENT = "yaat-refresh-navdata/1.0"
DEFAULT_OUT = Path("tests/Yaat.Sim.Tests/TestData/NavData.dat")
DEFAULT_CIFP_OUT = Path("tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz")
CIFP_ENTRY_PREFIX = "FAACIFP"
BUNDLED_CIFP_NAME = "FAACIFP18"
CONFIG_TIMEOUT_SECONDS = 30
DOWNLOAD_TIMEOUT_SECONDS = 120
JSON_INDENT = 2
HTTP_NOT_FOUND = 404
BYTES_PER_MB = 1024 * 1024


def fetch_config() -> dict:
    """Return the vNAS configuration document.

    Returns:
        The parsed configuration object.

    Raises:
        SystemExit: If the document cannot be fetched or parsed.
    """
    request = urllib.request.Request(CONFIG_URL, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=CONFIG_TIMEOUT_SECONDS) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code} fetching {CONFIG_URL}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc
    except urllib.error.URLError as exc:
        print(f"network error fetching {CONFIG_URL}: {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc
    except json.JSONDecodeError as exc:
        print(f"not JSON from {CONFIG_URL}: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc


def download_navdata(url: str) -> bytes:
    """Download the published NavData bytes.

    Args:
        url: The NavData URL from the vNAS configuration.

    Returns:
        The downloaded file contents.

    Raises:
        SystemExit: If the download fails.
    """
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code} downloading NavData ({url}): {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc
    except urllib.error.URLError as exc:
        print(f"network error downloading NavData ({url}): {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc


def cifp_url(cycle_id: str) -> str:
    """Return the FAA download URL for a cycle's CIFP zip."""
    return f"{CIFP_BASE_URL}CIFP_{airac.cycle_date(cycle_id):%y%m%d}.zip"


def download_cifp(cycle_id: str) -> tuple[bytes, str]:
    """Download the FAA CIFP zip for a cycle.

    Args:
        cycle_id: The cycle to fetch.

    Returns:
        The zip's bytes and the URL they came from.

    Raises:
        SystemExit: If the zip is unpublished or the download fails.
    """
    url = cifp_url(cycle_id)
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
            return response.read(), url
    except urllib.error.HTTPError as exc:
        if exc.code == HTTP_NOT_FOUND:
            print(f"FAA has not published CIFP for AIRAC {cycle_id} ({url}: HTTP 404); nothing written", file=sys.stderr)
        else:
            print(f"HTTP {exc.code} downloading CIFP for AIRAC {cycle_id} ({url}): {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc
    except urllib.error.URLError as exc:
        print(f"network error downloading CIFP for AIRAC {cycle_id} ({url}): {exc.reason}", file=sys.stderr)
        raise SystemExit(1) from exc


def extract_cifp(zip_bytes: bytes, url: str) -> bytes:
    """Return the bytes of the `FAACIFP*` entry in a CIFP zip.

    Args:
        zip_bytes: The zip archive.
        url: Where the archive came from, for the error text.

    Returns:
        The CIFP file's contents.

    Raises:
        SystemExit: If the archive is unreadable or holds no `FAACIFP*` entry.
    """
    try:
        with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
            for entry in archive.infolist():
                if PurePosixPath(entry.filename).name.startswith(CIFP_ENTRY_PREFIX):
                    return archive.read(entry)
    except zipfile.BadZipFile as exc:
        print(f"not a zip archive from {url}: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
    print(f"no {CIFP_ENTRY_PREFIX}* entry in the zip from {url}; nothing written", file=sys.stderr)
    raise SystemExit(1)


def gzip_pin_bytes(data: bytes) -> bytes:
    """Gzip CIFP bytes the way the committed pin stores them: no mtime, fixed stored name.

    Args:
        data: The CIFP file contents.

    Returns:
        The gzip stream to commit as `FAACIFP18.gz`.
    """
    buffer = io.BytesIO()
    with gzip.GzipFile(filename=BUNDLED_CIFP_NAME, mode="wb", fileobj=buffer, mtime=0) as stream:
        stream.write(data)
    return buffer.getvalue()


def write_bytes(path: Path, data: bytes) -> None:
    """Write `data` to `path`, creating its directory first."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def write_manifest(path: Path, payload: dict) -> None:
    """Write a manifest as indented JSON with a trailing LF."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=JSON_INDENT) + "\n", encoding="utf-8", newline="\n")


def _size_mb(data: bytes) -> float:
    return len(data) / BYTES_PER_MB


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output NavData.dat path")
    parser.add_argument("--manifest", type=Path, default=None, help="NavData manifest JSON (default: beside --out)")
    parser.add_argument("--cifp-out", type=Path, default=DEFAULT_CIFP_OUT, help="output FAACIFP18.gz path")
    parser.add_argument("--cifp-manifest", type=Path, default=None, help="CIFP manifest JSON (default: beside --cifp-out)")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """Repin both test data files and their manifests, returning the process exit code."""
    args = _parse_args(argv)
    manifest_path = args.manifest or args.out.with_name("navdata-manifest.json")
    cifp_manifest_path = args.cifp_manifest or args.cifp_out.with_name("cifp-manifest.json")

    config = fetch_config()
    serial = config.get("navDataSerial")
    navdata_url = config.get("navDataUrl")
    if serial is None or not navdata_url:
        print("VNAS config missing navDataSerial or navDataUrl", file=sys.stderr)
        return 1

    navdata = download_navdata(navdata_url)
    try:
        cycle = airac.serial_cycle(serial)
    except ValueError as exc:
        print(f"cannot place NavData serial {serial} on the cycle grid: {exc}", file=sys.stderr)
        return 1

    zip_bytes, zip_url = download_cifp(cycle)
    cifp = gzip_pin_bytes(extract_cifp(zip_bytes, zip_url))

    write_bytes(args.out, navdata)
    write_manifest(
        manifest_path,
        {"navDataSerial": serial, "airacCycle": cycle, "lastRefreshed": datetime.now(UTC).isoformat()},
    )
    write_bytes(args.cifp_out, cifp)
    write_manifest(cifp_manifest_path, {"airacCycle": cycle})

    print(f"wrote {args.out} ({_size_mb(navdata):.2f} MB, serial {serial}, AIRAC {cycle})")
    print(f"wrote {manifest_path}")
    print(f"wrote {args.cifp_out} ({_size_mb(cifp):.2f} MB, AIRAC {cycle}, from {zip_url})")
    print(f"wrote {cifp_manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
