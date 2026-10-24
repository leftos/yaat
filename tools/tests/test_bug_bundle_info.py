"""Tests for tools/bug_bundle.py's `info` subcommand: the `Server:` line.

The line names the server that exported the bundle: the manifest's `ServerName`, else the host of the URL in the
client-written `server.json`, else `(unknown)`.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import zipfile
from pathlib import Path
from typing import Any

import pytest

TOOL_PATH = Path(__file__).resolve().parents[1] / "bug_bundle.py"


def _load_module() -> Any:
    spec = importlib.util.spec_from_file_location("bug_bundle", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["bug_bundle"] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture()
def tool() -> Any:
    return _load_module()


def _bundle(tmp_path: Path, *, server_name: str | None, server_url: str | None, server_json: str | None = None) -> Path:
    manifest: dict[str, Any] = {
        "Version": 4,
        "RngSeed": 1234,
        "TotalElapsedSeconds": 0.0,
        "ActionCount": 0,
        "HasWeather": False,
        "Snapshots": [],
    }
    if server_name is not None:
        manifest["ServerName"] = server_name
    path = tmp_path / "bundle.yaat-bug-report-bundle.zip"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("manifest.json", json.dumps(manifest))
        if server_url is not None:
            archive.writestr("server.json", json.dumps({"url": server_url}))
        elif server_json is not None:
            archive.writestr("server.json", server_json)
    return path


def _server_line(tool: Any, path: Path, capsys: Any) -> str:
    assert tool.main(["info", str(path)]) == 0
    lines = [line.strip() for line in capsys.readouterr().out.splitlines() if line.strip().startswith("Server:")]
    assert len(lines) == 1
    return lines[0]


def test_server_line_prefers_the_manifest_server_name(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, server_name="yaat1.leftos.dev", server_url="https://{server}:5443/")

    assert _server_line(tool, path, capsys) == "Server:              yaat1.leftos.dev"


def test_server_line_falls_back_to_the_dialled_url_host(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, server_name=None, server_url="https://yaat1.leftos.dev")

    assert _server_line(tool, path, capsys) == "Server:              yaat1.leftos.dev"


def test_server_line_keeps_the_scrubbed_placeholder_host(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, server_name=None, server_url="https://{server}:5443/")

    assert _server_line(tool, path, capsys) == "Server:              {server}"


def test_server_line_prints_a_url_without_a_scheme_as_is(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, server_name=None, server_url="yaat1.leftos.dev")

    assert _server_line(tool, path, capsys) == "Server:              yaat1.leftos.dev"


@pytest.mark.parametrize("server_json", ["{not json", '["https://yaat1.leftos.dev"]'])
def test_server_line_reports_an_unreadable_server_json(tool: Any, tmp_path: Path, capsys: Any, server_json: str) -> None:
    path = _bundle(tmp_path, server_name=None, server_url=None, server_json=server_json)

    assert _server_line(tool, path, capsys) == "Server:              (unreadable server.json)"


def test_server_line_is_unknown_without_either(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, server_name=None, server_url=None)

    assert _server_line(tool, path, capsys) == "Server:              (unknown)"
