"""Tests for tools/airac-pin-status.py.

The tool is a script, not an installed module, so it is loaded by path. The pin manifests it reads
come from a directory the tests redirect into `tmp_path`, and the two hosts it talks to go through
`urllib.request.urlopen`, which every test replaces with `FakeNet`.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

import pytest

TOOLS_DIR = Path(__file__).resolve().parents[1]
TOOL_PATH = TOOLS_DIR / "airac-pin-status.py"
CONFIG_URL = "https://configuration.vnas.vatsim.net/"
PIN_CYCLE = "2611"
PIN_SERIAL = 2026102902
NEW_CYCLE = "2612"
NEWER_CYCLE = "2613"
NEW_SERIAL = 2026112602
NEWER_SERIAL = 2026122402
DECLINED_KEY = "airacPinDeclined"
HTTP_OK = 200
HTTP_NOT_FOUND = 404
HTTP_SERVER_ERROR = 500
EXIT_ERROR = 2
EXIT_NEWER = 10


def _load_module() -> Any:
    if str(TOOLS_DIR) not in sys.path:
        sys.path.insert(0, str(TOOLS_DIR))
    spec = importlib.util.spec_from_file_location("airac_pin_status", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["airac_pin_status"] = module
    spec.loader.exec_module(module)
    return module


tool = _load_module()


class _Response:
    """The slice of an HTTP response the tool reads."""

    def __init__(self, status: int = HTTP_OK, body: bytes = b"") -> None:
        self.status = status
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> _Response:
        return self

    def __exit__(self, *_exc_info: object) -> bool:
        return False


class FakeNet:
    """Stands in for `urllib.request.urlopen` over the vNAS config and the FAA CIFP host."""

    def __init__(
        self,
        published: set[str] | None = None,
        config_serial: int | None = None,
        head_status: int | None = None,
        network_error: str | None = None,
    ) -> None:
        self.published = set(published or ())
        self.config_serial = config_serial
        self.head_status = head_status
        self.network_error = network_error
        self.requests: list[tuple[str, str]] = []

    def __call__(self, request: urllib.request.Request, timeout: float | None = None) -> _Response:
        url = request.full_url
        self.requests.append((request.get_method(), url))
        if url == CONFIG_URL:
            return _Response(body=json.dumps({"navDataSerial": self.config_serial}).encode("utf-8"))
        if self.network_error is not None:
            raise urllib.error.URLError(self.network_error)
        if self.head_status is not None:
            return _Response(status=self.head_status)
        if url.rsplit("/", 1)[-1] in self.published:
            return _Response()
        raise urllib.error.HTTPError(url, HTTP_NOT_FOUND, "Not Found", None, None)


def _no_network(*_args: object, **_kwargs: object) -> None:
    raise AssertionError("this path must not touch the network")


def _install(monkeypatch: pytest.MonkeyPatch, net: FakeNet) -> FakeNet:
    monkeypatch.setattr(urllib.request, "urlopen", net)
    return net


def _write_manifests(test_data: Path, nav_cycle: str = PIN_CYCLE, cifp_cycle: str | None = None) -> None:
    test_data.mkdir(parents=True, exist_ok=True)
    nav = {"navDataSerial": PIN_SERIAL, "airacCycle": nav_cycle, "lastRefreshed": "2026-11-01T00:00:00+00:00"}
    (test_data / "navdata-manifest.json").write_text(json.dumps(nav), encoding="utf-8")
    cifp = {"airacCycle": nav_cycle if cifp_cycle is None else cifp_cycle}
    (test_data / "cifp-manifest.json").write_text(json.dumps(cifp), encoding="utf-8")


def _write_state(path: Path, declined: str, extra: dict[str, object] | None = None) -> Path:
    payload = dict(extra or {})
    payload[DECLINED_KEY] = declined
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def _cifp_names(*cycles: str) -> set[str]:
    return {tool.cifp_zip_name(cycle) for cycle in cycles}


def test_in_sync_when_the_next_cifp_is_not_published(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet())

    assert tool.main(["--test-data", str(tmp_path)]) == 0
    assert capsys.readouterr().out == ("airac-pin: in sync (pin AIRAC 2611, NavData serial 2026102902; FAA CIFP for 2612 not yet published)\n")


def test_published_cycle_with_vnas_navdata_out(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet(published=_cifp_names(NEW_CYCLE), config_serial=NEW_SERIAL))

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_NEWER
    assert capsys.readouterr().out == (
        "airac-pin: AIRAC 2612 is published (FAA CIFP_261126.zip); vNAS NavData for it is out (serial 2026112602); pin is 2611\n"
    )


def test_published_cycle_with_vnas_navdata_not_out_yet(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet(published=_cifp_names(NEW_CYCLE), config_serial=PIN_SERIAL))

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_NEWER
    assert capsys.readouterr().out == (
        "airac-pin: AIRAC 2612 is published (FAA CIFP_261126.zip); vNAS NavData for it is not out yet (serial 2026102902); pin is 2611\n"
    )


def test_two_published_cycles_report_the_newest(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet(published=_cifp_names(NEW_CYCLE, NEWER_CYCLE), config_serial=NEWER_SERIAL))

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_NEWER
    out = capsys.readouterr().out
    assert out.startswith("airac-pin: AIRAC 2613 is published (FAA CIFP_261224.zip); ")


def test_a_declined_cycle_reports_in_sync(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    state = _write_state(tmp_path / "nextup-state.json", NEW_CYCLE)
    net = _install(monkeypatch, FakeNet(published=_cifp_names(NEW_CYCLE), config_serial=NEW_SERIAL))

    assert tool.main(["--test-data", str(tmp_path), "--state", str(state)]) == 0
    assert capsys.readouterr().out == "airac-pin: AIRAC 2612 is published but was declined; pin stays at 2611\n"
    assert all(url != CONFIG_URL for _method, url in net.requests)


def test_a_cycle_newer_than_the_declined_one_still_reports_ten(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_manifests(tmp_path)
    state = _write_state(tmp_path / "nextup-state.json", NEW_CYCLE)
    _install(monkeypatch, FakeNet(published=_cifp_names(NEW_CYCLE, NEWER_CYCLE), config_serial=NEWER_SERIAL))

    assert tool.main(["--test-data", str(tmp_path), "--state", str(state)]) == EXIT_NEWER
    assert capsys.readouterr().out.startswith("airac-pin: AIRAC 2613 is published")


def test_a_non_404_head_status_is_an_error(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet(head_status=HTTP_SERVER_ERROR))

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_ERROR
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err.startswith("airac-pin: error:")
    assert f"HTTP {HTTP_SERVER_ERROR}" in captured.err


def test_a_network_error_is_an_error(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path)
    _install(monkeypatch, FakeNet(network_error="timed out"))

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_ERROR
    captured = capsys.readouterr()
    assert captured.out == ""
    assert "timed out" in captured.err


def test_manifests_that_disagree_are_an_error(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    _write_manifests(tmp_path, nav_cycle=PIN_CYCLE, cifp_cycle=NEW_CYCLE)
    _install(monkeypatch, FakeNet())

    assert tool.main(["--test-data", str(tmp_path)]) == EXIT_ERROR
    err = capsys.readouterr().err
    assert PIN_CYCLE in err
    assert NEW_CYCLE in err


def test_decline_records_the_cycle_and_keeps_other_keys(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    state = _write_state(tmp_path / "state.json", "2601", {"orchestrator": "kept"})
    monkeypatch.setattr(urllib.request, "urlopen", _no_network)

    assert tool.main(["--decline", NEW_CYCLE, "--state", str(state)]) == 0
    assert json.loads(state.read_text(encoding="utf-8")) == {DECLINED_KEY: NEW_CYCLE, "orchestrator": "kept"}
    assert capsys.readouterr().out == "airac-pin: declined AIRAC 2612; asking again only for a newer cycle\n"


def test_decline_creates_a_missing_state_file(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    state = tmp_path / "sub" / "state.json"
    monkeypatch.setattr(urllib.request, "urlopen", _no_network)

    assert tool.main(["--decline", NEW_CYCLE, "--state", str(state)]) == 0
    assert state.read_bytes() == b'{\n  "airacPinDeclined": "2612"\n}\n'


def test_decline_without_state_is_an_argparse_error() -> None:
    with pytest.raises(SystemExit) as failure:
        tool.main(["--decline", NEW_CYCLE])
    assert failure.value.code == EXIT_ERROR


def test_decline_rejects_a_malformed_cycle(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    state = tmp_path / "state.json"
    monkeypatch.setattr(urllib.request, "urlopen", _no_network)

    assert tool.main(["--decline", "26X2", "--state", str(state)]) == EXIT_ERROR
    assert "26X2" in capsys.readouterr().err
    assert not state.exists()
