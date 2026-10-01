"""Tests for tools/bug_bundle.py's `terminal-log` subcommand.

The tool is a script, not an installed module, so it is loaded by path. Each test builds a
minimal v4 recording archive in `tmp_path`: a real-shaped `manifest.json` plus a Brotli-compressed
`terminal-log.json.br` whose entries carry the property names the C# writer emits (PascalCase).
"""

from __future__ import annotations

import importlib.util
import json
import sys
import zipfile
from pathlib import Path
from typing import Any

import brotli
import pytest

TOOL_PATH = Path(__file__).resolve().parents[1] / "bug_bundle.py"
TIMESTAMP = "2026-09-28T17:24:03.1234567+00:00"

# The kinds the server broadcasts (RoomEngine / LiveRoomHost / CrcClientState call sites).
COMMAND = "Command"
RESPONSE = "Response"
ERROR = "Error"
CHAT = "Chat"
SYSTEM = "System"


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


def _entry(elapsed: float, kind: str, callsign: str, message: str, initials: str = "") -> dict[str, Any]:
    return {
        "ElapsedSeconds": elapsed,
        "Timestamp": TIMESTAMP,
        "Initials": initials,
        "Kind": kind,
        "Callsign": callsign,
        "Message": message,
    }


def _manifest(has_terminal_log: bool) -> dict[str, Any]:
    return {
        "Version": 4,
        "RngSeed": 1234,
        "TotalElapsedSeconds": 120.0,
        "ActionCount": 3,
        "HasWeather": False,
        "MetarReissuanceEnabled": False,
        "HasArtccConfig": False,
        "HasTerminalLog": has_terminal_log,
        "ScenarioName": "oak-ground",
        "RecordedAtUtc": "2026-09-28T17:24:03.0000000+00:00",
        "Snapshots": [],
    }


def _bundle(
    tmp_path: Path,
    entries: list[dict[str, Any]],
    *,
    has_terminal_log: bool = True,
    write_entry: bool = True,
    name: str = "bundle.zip",
) -> Path:
    path = tmp_path / name
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("manifest.json", json.dumps(_manifest(has_terminal_log)))
        if write_entry:
            archive.writestr("terminal-log.json.br", brotli.compress(json.dumps(entries).encode("utf-8")))
    return path


SAMPLE = [
    _entry(4.0, COMMAND, "UAL234", "CM 10000", initials="LO"),
    _entry(6.5, RESPONSE, "UAL234", "UAL234 climbing to 10000"),
    _entry(9.0, CHAT, "", "ready for departure"),
    _entry(31.25, ERROR, "N42416", "INVALID CALLSIGN"),
]


# ---------------------------------------------------------------------------
# plain listing
# ---------------------------------------------------------------------------


def test_lists_entries_in_order(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, SAMPLE)

    assert tool.main(["terminal-log", str(path)]) == 0

    assert capsys.readouterr().out == (
        "t=4.0  Command  UAL234  CM 10000\n"
        "t=6.5  Response  UAL234  UAL234 climbing to 10000\n"
        "t=9.0  Chat    ready for departure\n"
        "t=31.2  Error  N42416  INVALID CALLSIGN\n"
    )


# ---------------------------------------------------------------------------
# filters
# ---------------------------------------------------------------------------


def test_filters_by_callsign_kind_and_time(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, SAMPLE)

    assert tool.main(["terminal-log", str(path), "--callsign", "ual234"]) == 0
    assert capsys.readouterr().out.splitlines() == [
        "t=4.0  Command  UAL234  CM 10000",
        "t=6.5  Response  UAL234  UAL234 climbing to 10000",
    ]

    assert tool.main(["terminal-log", str(path), "--kind", "response"]) == 0
    assert capsys.readouterr().out.splitlines() == ["t=6.5  Response  UAL234  UAL234 climbing to 10000"]

    assert tool.main(["terminal-log", str(path), "--from", "7", "--to", "20"]) == 0
    assert capsys.readouterr().out.splitlines() == ["t=9.0  Chat    ready for departure"]

    assert tool.main(["terminal-log", str(path), "--callsign", "n42416", "--kind", "error"]) == 0
    assert capsys.readouterr().out.splitlines() == ["t=31.2  Error  N42416  INVALID CALLSIGN"]


# ---------------------------------------------------------------------------
# JSON
# ---------------------------------------------------------------------------


def test_json_output_round_trips(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, SAMPLE)

    assert tool.main(["terminal-log", str(path), "--kind", "command", "--callsign", "UAL234", "--json"]) == 0

    parsed = json.loads(capsys.readouterr().out)
    assert parsed == [SAMPLE[0]]
    assert list(parsed[0]) == ["ElapsedSeconds", "Timestamp", "Initials", "Kind", "Callsign", "Message"]


# ---------------------------------------------------------------------------
# SubRip captions
# ---------------------------------------------------------------------------


def test_srt_timing_hold_and_next_cue(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _entry(10.0, COMMAND, "UAL234", "CM 10000"),
            _entry(12.0, RESPONSE, "UAL234", "climbing"),
            _entry(30.0, CHAT, "", "see you"),
        ],
    )

    assert tool.main(["terminal-log", str(path), "--srt"]) == 0

    assert capsys.readouterr().out == (
        "1\n"
        "00:00:10,000 --> 00:00:12,000\n"
        "UAL234: CM 10000\n"
        "\n"
        "2\n"
        "00:00:12,000 --> 00:00:16,000\n"
        "UAL234: climbing\n"
        "\n"
        "3\n"
        "00:00:30,000 --> 00:00:34,000\n"
        "see you\n"
    )


def test_srt_offset_and_from(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _entry(4.0, COMMAND, "UAL234", "dropped"),
            _entry(10.0, COMMAND, "UAL234", "kept"),
            _entry(11.5, RESPONSE, "UAL234", "next"),
        ],
    )

    assert tool.main(["terminal-log", str(path), "--srt", "--from", "10", "--offset", "2.5", "--hold", "9"]) == 0

    assert capsys.readouterr().out == ("1\n00:00:02,500 --> 00:00:04,000\nUAL234: kept\n\n2\n00:00:04,000 --> 00:00:13,000\nUAL234: next\n")


# ---------------------------------------------------------------------------
# missing terminal log
# ---------------------------------------------------------------------------


def test_missing_terminal_log_exits_1(tool: Any, tmp_path: Path, capsys: Any) -> None:
    absent_flag = _bundle(tmp_path, SAMPLE, has_terminal_log=False, name="no-flag.zip")
    absent_entry = _bundle(tmp_path, [], write_entry=False, name="no-entry.zip")

    for path in (absent_flag, absent_entry):
        assert tool.main(["terminal-log", str(path)]) == 1
        assert capsys.readouterr().err == f"no terminal log in {path} (recorded before the feature)\n"
