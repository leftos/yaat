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
SAY_PILOT = "SayPilot"
WARNING = "Warning"
STRIP = "Strip"


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


def test_exclude_kind_drops_that_kind(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, SAMPLE)

    assert tool.main(["terminal-log", str(path), "--exclude-kind", "command"]) == 0
    assert capsys.readouterr().out.splitlines() == [
        "t=6.5  Response  UAL234  UAL234 climbing to 10000",
        "t=9.0  Chat    ready for departure",
        "t=31.2  Error  N42416  INVALID CALLSIGN",
    ]

    assert tool.main(["terminal-log", str(path), "--exclude-kind", "command", "--exclude-kind", "error"]) == 0
    assert capsys.readouterr().out.splitlines() == [
        "t=6.5  Response  UAL234  UAL234 climbing to 10000",
        "t=9.0  Chat    ready for departure",
    ]


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


def test_srt_without_captions_unchanged(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, SAMPLE)

    assert tool.main(["terminal-log", str(path), "--srt"]) == 0

    assert capsys.readouterr().out == (
        "1\n00:00:04,000 --> 00:00:06,500\nUAL234: CM 10000\n"
        "\n"
        "2\n00:00:06,500 --> 00:00:09,000\nUAL234: UAL234 climbing to 10000\n"
        "\n"
        "3\n00:00:09,000 --> 00:00:13,000\nready for departure\n"
        "\n"
        "4\n00:00:31,250 --> 00:00:35,250\nN42416: INVALID CALLSIGN\n"
    )


# ---------------------------------------------------------------------------
# caption preset
# ---------------------------------------------------------------------------


def test_captions_drops_command_echoes_responses_and_solo_warning(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _entry(1.0, SAY_PILOT, "N52417", "Oakland Tower, N52417 5 miles east, inbound for landing."),
            _entry(1.0, COMMAND, "N52417", "CAINH"),
            _entry(1.0, STRIP, "N52417", "SEP N52417 1000"),
            _entry(1.0, RESPONSE, "N52417", "Conflict alert inhibited for N52417"),
            _entry(2.0, WARNING, "N52417/N738SP", "Solo Coach: keep the downwind inside 2 NM."),
            _entry(2.0, WARNING, "N52417/N738SP", "Solo Warning: N52417: issue traffic advisory for N738SP."),
            _entry(2.0, WARNING, "N738SP", "Solo Safety: go around, traffic on the runway."),
            _entry(3.0, CHAT, "", "ready for departure"),
        ],
    )

    assert tool.main(["terminal-log", str(path), "--srt", "--captions"]) == 0

    out = capsys.readouterr().out
    assert "CAINH" not in out
    assert "SEP" not in out
    assert "Conflict alert" not in out
    assert "Solo Coach" not in out
    assert "Solo Warning" not in out
    assert "Solo Safety" not in out
    assert out == (
        "1\n00:00:01,000 --> 00:00:03,000\nN52417: Oakland Tower, N52417 5 miles east, inbound for landing.\n"
        "\n"
        "2\n00:00:03,000 --> 00:00:04,500\nready for departure\n"
    )


def test_captions_drops_repeat_within_30s_keeps_after(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _entry(0.0, CHAT, "", "one two three"),
            _entry(20.0, CHAT, "", "one two three"),
            # 31 s after the one kept, so past the 30 s window: kept, and it becomes the new baseline.
            _entry(31.0, CHAT, "", "one two three"),
            # Exactly 30.0 s after that one: the window is inclusive, so this one is dropped.
            _entry(61.0, CHAT, "", "one two three"),
        ],
    )

    assert tool.main(["terminal-log", str(path), "--srt", "--captions"]) == 0

    assert capsys.readouterr().out == ("1\n00:00:00,000 --> 00:00:01,500\none two three\n\n2\n00:00:31,000 --> 00:00:32,500\none two three\n")


def test_captions_duration_by_reading_speed(tool: Any, tmp_path: Path, capsys: Any) -> None:
    def captions(entries: list[dict[str, Any]]) -> str:
        path = _bundle(tmp_path, entries, name="captions.zip")
        assert tool.main(["terminal-log", str(path), "--srt", "--captions"]) == 0
        return capsys.readouterr().out

    ten = "one two three four five six seven eight nine ten"
    thirty = " ".join(f"w{i}" for i in range(30))

    # A lone cue lasts its reading-speed duration: 10 words / 2.5 = 4.0 s, 2 words clamped up to 1.5 s,
    # 30 words / 2.5 = 12.0 s clamped down to 6.0 s.
    assert captions([_entry(0.0, CHAT, "", ten)]) == f"1\n00:00:00,000 --> 00:00:04,000\n{ten}\n"
    assert captions([_entry(0.0, CHAT, "", "hello there")]) == "1\n00:00:00,000 --> 00:00:01,500\nhello there\n"
    assert captions([_entry(0.0, CHAT, "", thirty)]) == f"1\n00:00:00,000 --> 00:00:06,000\n{thirty}\n"

    # The next cue cuts it short: 4.0 s of text at 0.0, the next cue at 1.2.
    assert captions([_entry(0.0, CHAT, "", ten), _entry(1.2, CHAT, "", "next line")]) == (
        f"1\n00:00:00,000 --> 00:00:01,200\n{ten}\n\n2\n00:00:01,200 --> 00:00:02,700\nnext line\n"
    )

    # The 1.0 s floor holds however early the next cue starts: 4.0 s of text at 0.0, the next cue at 0.4.
    assert captions([_entry(0.0, CHAT, "", ten), _entry(0.4, CHAT, "", "next line")]) == (
        f"1\n00:00:00,000 --> 00:00:01,000\n{ten}\n\n2\n00:00:00,400 --> 00:00:01,900\nnext line\n"
    )


# ---------------------------------------------------------------------------
# missing terminal log
# ---------------------------------------------------------------------------


def test_missing_terminal_log_exits_1(tool: Any, tmp_path: Path, capsys: Any) -> None:
    absent_flag = _bundle(tmp_path, SAMPLE, has_terminal_log=False, name="no-flag.zip")
    absent_entry = _bundle(tmp_path, [], write_entry=False, name="no-entry.zip")

    for path in (absent_flag, absent_entry):
        assert tool.main(["terminal-log", str(path)]) == 1
        assert capsys.readouterr().err == f"no terminal log in {path} (recorded before the feature)\n"
