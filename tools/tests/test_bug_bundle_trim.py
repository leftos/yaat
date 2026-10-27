"""Tests for tools/bug_bundle.py's `trim --keep-at`: keep the first snapshot and the one a restore at each listed time reads.

A snapshot's file is named by its index in the manifest, so the kept snapshots are renumbered from 000 in order.
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
SNAPSHOT_TIMES = [0.0, 10.0, 20.0, 30.0, 40.0]
USAGE_ERROR = 2


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


def _bundle(tmp_path: Path) -> Path:
    manifest: dict[str, Any] = {
        "Version": 4,
        "RngSeed": 1234,
        "TotalElapsedSeconds": 40.0,
        "ActionCount": 5,
        "HasWeather": False,
        "Snapshots": [{"ElapsedSeconds": t, "ActionIndex": i} for i, t in enumerate(SNAPSHOT_TIMES)],
    }
    path = tmp_path / "bundle.yaat-bug-report-bundle.zip"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("manifest.json", json.dumps(manifest))
        archive.writestr("actions.json.br", b"actions")
        for i, t in enumerate(SNAPSHOT_TIMES):
            archive.writestr(f"snapshots/{i:03d}.json.br", f"snapshot at {t:g}".encode())
    return path


def _read(path: Path) -> tuple[list[dict[str, Any]], dict[str, bytes]]:
    with zipfile.ZipFile(path) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        entries = {name: archive.read(name) for name in archive.namelist() if name != "manifest.json"}
    return manifest["Snapshots"], entries


def test_keep_at_keeps_the_latest_snapshot_at_or_before_each_time_renumbered(tool: Any, tmp_path: Path) -> None:
    src = _bundle(tmp_path)
    out = tmp_path / "trimmed.zip"

    assert tool.main(["trim", "--keep-at", "25,10", "--out", str(out), str(src)]) == 0

    snapshots, entries = _read(out)
    assert snapshots == [
        {"ElapsedSeconds": 0.0, "ActionIndex": 0},
        {"ElapsedSeconds": 10.0, "ActionIndex": 1},
        {"ElapsedSeconds": 20.0, "ActionIndex": 2},
    ]
    assert entries == {
        "actions.json.br": b"actions",
        "snapshots/000.json.br": b"snapshot at 0",
        "snapshots/001.json.br": b"snapshot at 10",
        "snapshots/002.json.br": b"snapshot at 20",
    }


def test_keep_at_always_keeps_the_first_snapshot_the_starting_state(tool: Any, tmp_path: Path) -> None:
    src = _bundle(tmp_path)
    out = tmp_path / "trimmed.zip"

    assert tool.main(["trim", "--keep-at", "35", "--out", str(out), str(src)]) == 0

    snapshots, entries = _read(out)
    assert snapshots == [{"ElapsedSeconds": 0.0, "ActionIndex": 0}, {"ElapsedSeconds": 30.0, "ActionIndex": 3}]
    assert entries["snapshots/000.json.br"] == b"snapshot at 0"
    assert entries["snapshots/001.json.br"] == b"snapshot at 30"


def test_keep_at_two_times_reading_one_snapshot_keep_it_once(tool: Any, tmp_path: Path) -> None:
    src = _bundle(tmp_path)
    out = tmp_path / "trimmed.zip"

    assert tool.main(["trim", "--keep-at", "31,39", "--out", str(out), str(src)]) == 0

    snapshots, entries = _read(out)
    assert snapshots == [{"ElapsedSeconds": 0.0, "ActionIndex": 0}, {"ElapsedSeconds": 30.0, "ActionIndex": 3}]
    assert entries["snapshots/001.json.br"] == b"snapshot at 30"


def test_keep_at_before_the_first_snapshot_is_an_error(tool: Any, tmp_path: Path, capsys: Any) -> None:
    src = _bundle(tmp_path)
    shifted = tmp_path / "shifted.zip"
    with zipfile.ZipFile(src) as zin, zipfile.ZipFile(shifted, "w") as zout:
        for name in zin.namelist():
            data = zin.read(name)
            if name == "manifest.json":
                manifest = json.loads(data)
                manifest["Snapshots"] = [{**s, "ElapsedSeconds": s["ElapsedSeconds"] + 5} for s in manifest["Snapshots"]]
                data = json.dumps(manifest).encode()
            zout.writestr(name, data)

    assert tool.main(["trim", "--keep-at", "2", "--out", str(tmp_path / "trimmed.zip"), str(shifted)]) == 1
    assert "no snapshot at or before it" in capsys.readouterr().err


def test_keep_at_with_max_seconds_is_refused(tool: Any, tmp_path: Path, capsys: Any) -> None:
    src = _bundle(tmp_path)

    assert tool.main(["trim", "--keep-at", "10", "--max-seconds", "20", str(src)]) == USAGE_ERROR
    assert "pass only one of" in capsys.readouterr().err


def test_keep_at_malformed_list_is_a_usage_error(tool: Any, tmp_path: Path) -> None:
    src = _bundle(tmp_path)

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["trim", "--keep-at", "10,abc", str(src)])
    assert exit_info.value.code == USAGE_ERROR
