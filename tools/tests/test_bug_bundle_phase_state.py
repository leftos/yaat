"""Tests for tools/bug_bundle.py's `phase-state` subcommand.

The tool is a script, not an installed module, so it is loaded by path. Each test builds a minimal v4
recording archive in `tmp_path`: a real-shaped `manifest.json` plus Brotli-compressed
`snapshots/NNN.json.br` entries whose `Phases` list holds phase DTOs shaped as the C# writer emits them
(the `$type` discriminator, the required `Status` and `ElapsedSeconds`, and `Requirements`).
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
CALLSIGN = "N123AB"
SECONDS_PER_SNAPSHOT = 10.0


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


def _phase(phase_type: str, elapsed: float, **fields: Any) -> dict[str, Any]:
    """A phase DTO shaped as the C# writer emits it: discriminator, required Status and ElapsedSeconds, Requirements."""
    return {"$type": phase_type, "Status": 0, "ElapsedSeconds": elapsed, "Requirements": None, **fields}


def _aircraft(phases: list[dict[str, Any]] | None, current_index: int | None = 0) -> dict[str, Any]:
    """An aircraft holding `phases`, active at `current_index`; no `Phases` at all when `phases` is None."""
    aircraft: dict[str, Any] = {"Callsign": CALLSIGN}
    if phases is not None:
        aircraft["Phases"] = {"Phases": phases, "CurrentIndex": current_index}
    return aircraft


def _bundle(tmp_path: Path, aircraft: list[dict[str, Any] | None]) -> Path:
    """A v4 bundle with one snapshot per entry of `aircraft`, one every SECONDS_PER_SNAPSHOT seconds."""
    times = [SECONDS_PER_SNAPSHOT * i for i in range(len(aircraft))]
    manifest: dict[str, Any] = {
        "Version": 4,
        "RngSeed": 1234,
        "TotalElapsedSeconds": times[-1],
        "ActionCount": 0,
        "HasWeather": False,
        "Snapshots": [{"ElapsedSeconds": t, "ActionIndex": i} for i, t in enumerate(times)],
    }
    path = tmp_path / "bundle.yaat-bug-report-bundle.zip"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("manifest.json", json.dumps(manifest))
        for i, ac in enumerate(aircraft):
            snapshot = {"Aircraft": [ac] if ac is not None else []}
            archive.writestr(f"snapshots/{i:03d}.json.br", brotli.compress(json.dumps(snapshot).encode()))
    return path


def test_phase_state_prints_each_distinct_current_phase_once(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _aircraft([_phase("Downwind", 0.0, Status=1, HoldLevelFt=1000.0)]),
            _aircraft([_phase("Downwind", 10.0, Status=1, HoldLevelFt=1000.0)]),
            _aircraft(None),
            _aircraft([_phase("Downwind", 30.0, Status=3)], current_index=None),
            _aircraft([_phase("Base", 40.0, Status=2, AnchorLat=37.7)]),
            _aircraft([_phase("Base", 50.0, Status=2, AnchorLat=37.7)]),
        ],
    )

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN]) == 0

    assert capsys.readouterr().out.splitlines() == [
        "t=  0.0 [snap=  0] Downwind",
        "  ElapsedSeconds=0.0",
        "  HoldLevelFt=1000.0",
        "  Requirements=null",
        "  Status=1",
        "t= 20.0 [snap=  2] (no current phase)",
        "t= 40.0 [snap=  4] Base",
        "  AnchorLat=37.7",
        "  ElapsedSeconds=40.0",
        "  Requirements=null",
        "  Status=2",
    ]


def test_phase_state_json_lists_the_type_and_the_phase_fields(tool: Any, tmp_path: Path, capsys: Any) -> None:
    waypoints = {"DownwindStartLat": 37.7, "DownwindStartLon": -122.2}
    path = _bundle(
        tmp_path,
        [
            _aircraft([_phase("Downwind", 0.0, Status=1, Waypoints=waypoints)]),
            _aircraft([_phase("Downwind", 10.0, Status=1, Waypoints=waypoints)]),
            _aircraft([_phase("Base", 20.0, Status=2)]),
        ],
    )

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN, "--json"]) == 0

    assert json.loads(capsys.readouterr().out) == [
        {
            "t": 0.0,
            "snap": 0,
            "callsign": CALLSIGN,
            "phase": "Downwind",
            "state": {"Status": 1, "ElapsedSeconds": 0.0, "Requirements": None, "Waypoints": waypoints},
        },
        {
            "t": 20.0,
            "snap": 2,
            "callsign": CALLSIGN,
            "phase": "Base",
            "state": {"Status": 2, "ElapsedSeconds": 20.0, "Requirements": None},
        },
    ]


def test_phase_state_a_gap_makes_the_same_phase_print_again(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _aircraft([_phase("Downwind", 0.0, Status=1)]),
            _aircraft(None),
            _aircraft([_phase("Downwind", 20.0, Status=1)]),
        ],
    )

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN]) == 0

    assert capsys.readouterr().out.splitlines() == [
        "t=  0.0 [snap=  0] Downwind",
        "  ElapsedSeconds=0.0",
        "  Requirements=null",
        "  Status=1",
        "t= 10.0 [snap=  1] (no current phase)",
        "t= 20.0 [snap=  2] Downwind",
        "  ElapsedSeconds=20.0",
        "  Requirements=null",
        "  Status=1",
    ]


def test_phase_state_an_index_past_the_end_and_an_empty_list_are_no_current_phase(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _aircraft([_phase("Downwind", 0.0, Status=1)], current_index=5),
            _aircraft([_phase("Base", 10.0, Status=2)]),
            _aircraft([], current_index=0),
        ],
    )

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN]) == 0

    assert capsys.readouterr().out.splitlines() == [
        "t=  0.0 [snap=  0] (no current phase)",
        "t= 10.0 [snap=  1] Base",
        "  ElapsedSeconds=10.0",
        "  Requirements=null",
        "  Status=2",
        "t= 20.0 [snap=  2] (no current phase)",
    ]


def test_phase_state_skips_a_snapshot_without_the_aircraft_without_resetting(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(
        tmp_path,
        [
            _aircraft([_phase("Downwind", 0.0, Status=1)]),
            None,
            _aircraft([_phase("Downwind", 20.0, Status=1)]),
        ],
    )

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN]) == 0

    assert capsys.readouterr().out.splitlines() == [
        "t=  0.0 [snap=  0] Downwind",
        "  ElapsedSeconds=0.0",
        "  Requirements=null",
        "  Status=1",
    ]


def test_phase_state_json_no_phase_entry_has_null_phase_and_state(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, [_aircraft(None), _aircraft([_phase("Base", 10.0, Status=2)])])

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN, "--json"]) == 0

    assert json.loads(capsys.readouterr().out) == [
        {"t": 0.0, "snap": 0, "callsign": CALLSIGN, "phase": None, "state": None},
        {
            "t": 10.0,
            "snap": 1,
            "callsign": CALLSIGN,
            "phase": "Base",
            "state": {"Status": 2, "ElapsedSeconds": 10.0, "Requirements": None},
        },
    ]


def test_phase_state_the_same_phase_at_a_new_index_prints_again(tool: Any, tmp_path: Path, capsys: Any) -> None:
    phases = [_phase("Downwind", 0.0, Status=1), _phase("Downwind", 0.0, Status=1)]
    path = _bundle(tmp_path, [_aircraft(phases, current_index=0), _aircraft(phases, current_index=1)])

    assert tool.main(["phase-state", str(path), "--callsign", CALLSIGN]) == 0

    assert capsys.readouterr().out.splitlines() == [
        "t=  0.0 [snap=  0] Downwind",
        "  ElapsedSeconds=0.0",
        "  Requirements=null",
        "  Status=1",
        "t= 10.0 [snap=  1] Downwind",
        "  ElapsedSeconds=0.0",
        "  Requirements=null",
        "  Status=1",
    ]


def test_phase_state_unknown_callsign_is_an_error(tool: Any, tmp_path: Path, capsys: Any) -> None:
    path = _bundle(tmp_path, [_aircraft([_phase("Downwind", 0.0, Status=1)])])

    assert tool.main(["phase-state", str(path), "--callsign", "N999ZZ"]) == 1

    err = capsys.readouterr().err
    assert "error: callsign 'N999ZZ' not in any snapshot." in err
    assert f"available: {CALLSIGN}" in err
