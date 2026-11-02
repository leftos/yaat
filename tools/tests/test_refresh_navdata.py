"""Tests for tools/refresh-navdata.py, the one-command NavData and CIFP repin.

The script is loaded by path. Every network call goes through `urllib.request.urlopen`, which
`FakeNet` replaces, and every path it writes is redirected into `tmp_path`.
"""

from __future__ import annotations

import gzip
import importlib.util
import io
import json
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Any

import pytest

TOOLS_DIR = Path(__file__).resolve().parents[1]
TOOL_PATH = TOOLS_DIR / "refresh-navdata.py"
CONFIG_URL = "https://configuration.vnas.vatsim.net/"
NAVDATA_URL = "https://data.example.test/NavData.dat"
CIFP_URL = "https://aeronav.faa.gov/Upload_313-d/cifp/CIFP_261029.zip"
SERIAL = 2026102902
PIN_CYCLE = "2611"
NAVDATA_BYTES = b"navdata-bytes"
CIFP_BYTES = b"FAACIFP18 payload\n"
HTTP_OK = 200
HTTP_NOT_FOUND = 404
GZIP_FLG_OFFSET = 3
GZIP_MTIME_OFFSET = 4
GZIP_HEADER_LENGTH = 8
GZIP_NAME_END = 40
FNAME_FLAG = 0x08
EXIT_ERROR = 1


def _load_module() -> Any:
    if str(TOOLS_DIR) not in sys.path:
        sys.path.insert(0, str(TOOLS_DIR))
    spec = importlib.util.spec_from_file_location("refresh_navdata", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["refresh_navdata"] = module
    spec.loader.exec_module(module)
    return module


tool = _load_module()


class _Response:
    """The slice of an HTTP response the script reads."""

    def __init__(self, body: bytes) -> None:
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> _Response:
        return self

    def __exit__(self, *_exc_info: object) -> bool:
        return False


class FakeNet:
    """Stands in for `urllib.request.urlopen` over the vNAS config and download hosts."""

    def __init__(self, cifp_body: bytes | None = None, cifp_status: int = HTTP_OK) -> None:
        self.cifp_body = cifp_body or b""
        self.cifp_status = cifp_status
        self.requested: list[str] = []

    def __call__(self, request: urllib.request.Request, timeout: float | None = None) -> _Response:
        url = request.full_url
        self.requested.append(url)
        if url == CONFIG_URL:
            body = {"navDataSerial": SERIAL, "navDataUrl": NAVDATA_URL}
            return _Response(json.dumps(body).encode("utf-8"))
        if url == NAVDATA_URL:
            return _Response(NAVDATA_BYTES)
        if url.endswith(".zip"):
            if self.cifp_status != HTTP_OK:
                raise urllib.error.HTTPError(url, self.cifp_status, "Not Found", None, None)
            return _Response(self.cifp_body)
        raise AssertionError(f"unexpected request {url}")


def _cifp_zip(entries: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, payload in entries.items():
            archive.writestr(name, payload)
    return buffer.getvalue()


def _targets(directory: Path) -> dict[str, Path]:
    return {
        "out": directory / "NavData.dat",
        "manifest": directory / "navdata-manifest.json",
        "cifp_out": directory / "FAACIFP18.gz",
        "cifp_manifest": directory / "cifp-manifest.json",
    }


def _args(paths: dict[str, Path], *, cifp_manifest: bool = True) -> list[str]:
    args = [
        "--out",
        str(paths["out"]),
        "--manifest",
        str(paths["manifest"]),
        "--cifp-out",
        str(paths["cifp_out"]),
    ]
    if cifp_manifest:
        args += ["--cifp-manifest", str(paths["cifp_manifest"])]
    return args


def _install(monkeypatch: pytest.MonkeyPatch, net: FakeNet) -> FakeNet:
    monkeypatch.setattr(urllib.request, "urlopen", net)
    return net


def test_repins_both_pins_to_the_serials_cycle(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    zip_body = _cifp_zip({"README.txt": b"skip me", "FAACIFP18": CIFP_BYTES})
    net = _install(monkeypatch, FakeNet(cifp_body=zip_body))
    paths = _targets(tmp_path)

    assert tool.main(_args(paths)) == 0

    assert paths["out"].read_bytes() == NAVDATA_BYTES
    nav = json.loads(paths["manifest"].read_text(encoding="utf-8"))
    assert nav["navDataSerial"] == SERIAL
    assert nav["airacCycle"] == PIN_CYCLE
    assert json.loads(paths["cifp_manifest"].read_text(encoding="utf-8")) == {"airacCycle": PIN_CYCLE}

    packed = paths["cifp_out"].read_bytes()
    assert gzip.decompress(packed) == CIFP_BYTES
    assert packed[GZIP_FLG_OFFSET] & FNAME_FLAG
    assert b"FAACIFP18" in packed[GZIP_HEADER_LENGTH:GZIP_NAME_END]
    assert int.from_bytes(packed[GZIP_MTIME_OFFSET:GZIP_HEADER_LENGTH], "little") == 0
    assert CIFP_URL in net.requested


def test_cifp_manifest_defaults_beside_cifp_out(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    zip_body = _cifp_zip({"FAACIFP18": CIFP_BYTES})
    _install(monkeypatch, FakeNet(cifp_body=zip_body))
    paths = _targets(tmp_path)

    assert tool.main(_args(paths, cifp_manifest=False)) == 0

    assert paths["cifp_manifest"].exists()
    assert json.loads(paths["cifp_manifest"].read_text(encoding="utf-8")) == {"airacCycle": PIN_CYCLE}


def test_cifp_404_leaves_the_existing_pins_untouched(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _install(monkeypatch, FakeNet(cifp_status=HTTP_NOT_FOUND))
    paths = _targets(tmp_path)
    for name, path in paths.items():
        path.write_bytes(f"keep-{name}".encode())

    with pytest.raises(SystemExit) as failure:
        tool.main(_args(paths))

    assert failure.value.code == EXIT_ERROR
    for name, path in paths.items():
        assert path.read_bytes() == f"keep-{name}".encode()
    assert capsys.readouterr().err == (f"FAA has not published CIFP for AIRAC {PIN_CYCLE} ({CIFP_URL}: HTTP 404); nothing written\n")


def test_cifp_404_creates_nothing(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _install(monkeypatch, FakeNet(cifp_status=HTTP_NOT_FOUND))
    paths = _targets(tmp_path / "fresh")

    with pytest.raises(SystemExit) as failure:
        tool.main(_args(paths))

    assert failure.value.code == EXIT_ERROR
    assert not any(path.exists() for path in paths.values())


def test_a_zip_without_a_faacifp_entry_writes_nothing(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _install(monkeypatch, FakeNet(cifp_body=_cifp_zip({"README.txt": b"skip me"})))
    paths = _targets(tmp_path)

    with pytest.raises(SystemExit) as failure:
        tool.main(_args(paths))

    assert failure.value.code == EXIT_ERROR
    assert not any(path.exists() for path in paths.values())
    err = capsys.readouterr().err
    assert CIFP_URL in err
    assert "FAACIFP" in err
