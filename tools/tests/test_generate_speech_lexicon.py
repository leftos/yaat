"""Tests for tools/generate-speech-lexicon.py.

The tool is a script, not an installed module, so it is loaded by path. The model is a fake client handed to
`main` in place of the Anthropic client factory, so no test reaches the network; the ARTCC data files it merges
into are copied from the repo into `tmp_path`, and every file the tool writes lands there. The review server
runs on an ephemeral loopback port in a thread.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from http.server import ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
TOOL_PATH = REPO_ROOT / "tools" / "generate-speech-lexicon.py"
ZOA_FIXTURE: dict[str, Any] = {
    "CustomFixes/oak-landmarks.json": [
        {
            "name": "Oakland Runway 30 Numbers",
            "aliases": ["OAK30NUM"],
            "spokenPatterns": ["runway 30 numbers", "the runway 30 numbers"],
            "lat": 37.70208081559119,
            "lon": -122.2152109537947,
        }
    ],
    "ProcedurePronunciations/zoa.json": [{"procedure": "NIMI", "pronunciations": ["nimitz"]}],
    "FixPronunciations/visual.json": [
        {"fix": "VPCOL", "pronunciations": ["Oakland Colliseum", "Oakland Coliseum"], "displayName": "Oakland Coliseum"}
    ],
}
SENTINEL_KEY = "sk-ant-test-SENTINEL-KEY-0000"
EXIT_USAGE = 2
EXIT_FAILED = 1
VPCOL_REVIEWED_LAT = 37.7513
FILE_LAT = 1.5
HTTP_OK = 200
HTTP_BAD_REQUEST = 400
HTTP_FORBIDDEN = 403
HTTP_UNSUPPORTED = 415


def _load_module() -> Any:
    spec = importlib.util.spec_from_file_location("generate_speech_lexicon", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["generate_speech_lexicon"] = module
    spec.loader.exec_module(module)
    return module


lexicon = _load_module()


class FakeStream:
    def __init__(self, message: Any) -> None:
        self.message = message

    def __enter__(self) -> FakeStream:
        return self

    def __exit__(self, *exc: object) -> bool:
        return False

    def get_final_message(self) -> Any:
        return self.message


class FakeMessages:
    def __init__(self, replies: list[Any]) -> None:
        self.replies = list(replies)
        self.calls: list[dict[str, Any]] = []

    def stream(self, **kwargs: Any) -> FakeStream:
        self.calls.append(kwargs)
        return FakeStream(self.replies.pop(0))


class FakeClient:
    def __init__(self, replies: list[Any]) -> None:
        self.beta = SimpleNamespace(messages=FakeMessages(replies))


def text_block(text: str) -> Any:
    return SimpleNamespace(type="text", text=text)


def message(content: list[Any], stop_reason: str = "end_turn", stop_details: Any = None) -> Any:
    return SimpleNamespace(
        stop_reason=stop_reason,
        stop_details=stop_details,
        content=content,
        usage=SimpleNamespace(input_tokens=100, output_tokens=20),
        model="claude-opus-5-5",
        id="msg_fake",
        _request_id="req_fake",
    )


def reply(payload: dict[str, Any], stop_reason: str = "end_turn") -> Any:
    return message([text_block(json.dumps(payload))], stop_reason)


INPUTS = {
    "airport": "KOAK",
    "fixes": [
        {"id": "MITOE", "distanceNm": 7.1, "onProcedures": ["I30"], "existingPronunciations": [], "spellFix": "mitoe"},
        {"id": "OAK", "distanceNm": 0.5, "onProcedures": [], "existingPronunciations": [], "spellFix": "oakland vortac"},
    ],
    "procedures": [{"id": "NIMI5", "kind": "sid", "baseName": "NIMI", "existingPronunciations": ["nimitz"], "tts": "nimitz five"}],
    "gates": [{"name": "SIG1", "existingPronunciations": []}],
    "spots": [],
    "places": [],
    "landmarks": [],
}
FIX_REPLY = {
    "fixes": [
        {"id": "MITOE", "verdict": "needs_form", "pronunciations": ["my toe"], "displayName": None, "rationale": "said as a word"},
        {"id": "OAK", "verdict": "reads_as_spelled", "pronunciations": [], "displayName": None, "rationale": "navaid name"},
    ]
}
PROCEDURE_REPLY = {
    "procedures": [{"baseName": "NIMI", "verdict": "needs_form", "pronunciations": ["nimitz"], "chartedName": "NIMITZ", "rationale": "truncated"}]
}
GROUND_REPLY = {
    "gates": [{"name": "SIG1", "pronunciations": ["signature 1"], "rationale": "FBO stand"}],
    "spots": [],
    "places": [{"name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["SIG*"], "notes": "FBO", "rationale": "known FBO"}],
    "landmarks": [
        {
            "alias": "VPCOL",
            "name": "Oakland Coliseum",
            "spokenPatterns": ["the coliseum"],
            "lat": 37.75,
            "lon": -122.2,
            "source": "TAC",
            "rationale": "reporting point",
        }
    ],
}


def write(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data), encoding="utf-8", newline="\n")


def read(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def run_generate(tmp_path: Path, replies: list[Any], extra: list[str]) -> tuple[int, FakeClient]:
    inputs_path = tmp_path / "inputs.json"
    write(inputs_path, INPUTS)
    client = FakeClient(replies)
    code = lexicon.main(["generate", "--inputs", str(inputs_path), "--out", str(tmp_path / "out"), *extra], lambda: client)
    return code, client


def all_replies() -> list[Any]:
    return [reply(FIX_REPLY), reply(PROCEDURE_REPLY), reply(GROUND_REPLY)]


def make_repo(tmp_path: Path) -> Path:
    """A repo root in tmp_path holding fixed copies of the ZOA files apply merges into, independent of the live data."""
    repo = tmp_path / "repo"
    zoa = repo / "src" / "Yaat.Sim" / "Data" / "ARTCCs" / "ZOA"
    for relative, content in ZOA_FIXTURE.items():
        write(zoa / relative, content)
    return repo


def base_candidates() -> dict[str, Any]:
    return {
        "airport": "KOAK",
        "layoutStands": ["6B", "SIG1", "SIG2"],
        "fixes": [
            {"id": "MITOE", "verdict": "needs_form", "pronunciations": ["my toe"], "displayName": None},
            {"id": "VPCOL", "verdict": "needs_form", "pronunciations": ["coliseum"], "displayName": None},
            {"id": "OAK", "verdict": "reads_as_spelled", "pronunciations": ["oakland"], "displayName": None},
        ],
        "procedures": [
            {"baseName": "NIMI", "verdict": "needs_form", "pronunciations": ["nimitz"]},
            {"baseName": "SKIZM", "verdict": "needs_form", "pronunciations": ["skism"]},
            {"baseName": "OAK", "verdict": "reads_as_spelled", "pronunciations": ["oakland"]},
        ],
        "gates": [{"name": "SIG1", "pronunciations": ["signature 1"]}],
        "spots": [],
        "places": [{"name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["SIG*"], "notes": "FBO"}],
        "landmarks": [{"alias": "VPCOL", "name": "Oakland Coliseum", "spokenPatterns": ["the coliseum"], "lat": 37.75, "lon": -122.2}],
    }


def base_decisions() -> dict[str, Any]:
    return {
        "fix:MITOE": {"status": "accepted", "pronunciations": ["my toe", "mitoe"]},
        "fix:VPCOL": {"status": "accepted", "pronunciations": ["coliseum"]},
        "fix:OAK": {"status": "accepted", "pronunciations": ["oakland"]},
        "procedure:NIMI": {"status": "accepted", "pronunciations": ["nimitz", "nimits"]},
        "procedure:SKIZM": {"status": "accepted", "pronunciations": ["skism"]},
        "procedure:OAK": {"status": "accepted", "pronunciations": ["oakland"]},
        "gate:SIG1": {"status": "rejected", "pronunciations": ["signature 1"]},
        "place:SIGNATURE": {"status": "accepted", "pronunciations": ["signature"], "stands": ["SIG*"]},
        "landmark:VPCOL": {"status": "accepted", "spokenPatterns": ["the coliseum"], "lat": VPCOL_REVIEWED_LAT, "lon": -122.2005},
    }


def apply_with(tmp_path: Path, candidates: dict[str, Any], decisions: dict[str, Any]) -> tuple[int, Path]:
    repo = make_repo(tmp_path)
    directory = tmp_path / "out"
    write(directory / "candidates.json", candidates)
    write(directory / "decisions.json", decisions)
    if not (directory / "inputs.json").is_file():
        write(directory / "inputs.json", INPUTS)
    code = lexicon.main(["apply", "--dir", str(directory), "--repo", str(repo), "--artcc", "ZOA"], lambda: None)
    return code, repo / "src" / "Yaat.Sim" / "Data" / "ARTCCs" / "ZOA"


def run_apply(tmp_path: Path) -> tuple[int, Path]:
    return apply_with(tmp_path, base_candidates(), base_decisions())


# --- generate ---------------------------------------------------------------------------------------------------------


def test_generate_sends_the_briefed_request_and_merges_every_batch(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    code, client = run_generate(tmp_path, all_replies(), [])
    assert code == 0
    call = client.beta.messages.calls[0]
    assert call["model"] == "claude-opus-5-5"
    assert call["betas"] == ["server-side-fallback-2026-07-01"]
    assert call["fallbacks"] == "default"
    assert call["output_config"]["format"]["schema"] == lexicon.FIX_SCHEMA
    assert "thinking" not in call and "temperature" not in call
    candidates = read(tmp_path / "out" / "candidates.json")
    assert candidates["requestedModel"] == "claude-opus-5-5"
    assert candidates["layoutStands"] == ["SIG1"]
    assert [batch["kind"] for batch in candidates["batches"]] == ["fixes", "procedures", "ground"]
    assert candidates["batches"][0]["requestId"] == "req_fake"
    assert candidates["batches"][0]["sent"] == {"fixes": ["MITOE", "OAK"]}
    assert candidates["landmarks"][0]["inInputs"] is False
    assert candidates["gates"][0]["inInputs"] is True
    assert "inInputs" not in candidates["fixes"][0]
    assert not list((tmp_path / "out").glob("*.tmp"))


def test_generate_without_key_exits_2(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    code, client = run_generate(tmp_path, [], [])
    assert code == EXIT_USAGE
    assert client.beta.messages.calls == []


def test_refusal_fails_the_batch_and_keeps_earlier_results(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    code, _ = run_generate(tmp_path, [reply(FIX_REPLY), reply({}, stop_reason="refusal"), reply(GROUND_REPLY)], [])
    assert code == EXIT_FAILED
    assert "'procedures' stopped with refusal" in capsys.readouterr().err
    candidates = read(tmp_path / "out" / "candidates.json")
    assert [fix["id"] for fix in candidates["fixes"]] == ["MITOE", "OAK"]
    assert candidates["procedures"] == []


def test_refusal_message_carries_its_category_and_explanation(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    details = SimpleNamespace(category="cyber", explanation="declined")
    code, _ = run_generate(tmp_path, [message([], "refusal", details)], [])
    assert code == EXIT_FAILED
    assert "'fixes 1/1' stopped with refusal (category cyber; explanation declined)" in capsys.readouterr().err


@pytest.mark.parametrize("stop_reason", ["max_tokens", "pause_turn", "stop_sequence", "tool_use"])
def test_any_stop_but_end_turn_fails_the_batch(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str], stop_reason: str
) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    code, _ = run_generate(tmp_path, [reply(FIX_REPLY, stop_reason)], [])
    assert code == EXIT_FAILED
    assert f"'fixes 1/1' stopped with {stop_reason}" in capsys.readouterr().err


def test_answer_is_read_after_the_last_fallback_block(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    switched = message([text_block('{"fixes": [{"id": "MITOE"'), SimpleNamespace(type="fallback"), text_block(json.dumps(FIX_REPLY))])
    code, _ = run_generate(tmp_path, [switched, reply(PROCEDURE_REPLY), reply(GROUND_REPLY)], [])
    assert code == 0
    assert [fix["id"] for fix in read(tmp_path / "out" / "candidates.json")["fixes"]] == ["MITOE", "OAK"]


def test_malformed_answer_fails_the_batch_by_name(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    code, _ = run_generate(tmp_path, [message([text_block('{"fixes": [')])], [])
    assert code == EXIT_FAILED
    assert "batch 'fixes 1/1' answer is not valid JSON" in capsys.readouterr().err


def test_answer_is_checked_against_its_batch(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    stray = {"id": "BOGUS", "verdict": "needs_form", "pronunciations": ["bogus"], "displayName": None, "rationale": "invented"}
    fixes = {"fixes": [FIX_REPLY["fixes"][0], stray]}
    code, _ = run_generate(tmp_path, [reply(fixes), reply(PROCEDURE_REPLY), reply(GROUND_REPLY)], [])
    assert code == 0
    out = capsys.readouterr().out
    assert "batch 'fixes 1/1': dropped 1 fixes not in the batch" in out
    assert "batch 'fixes 1/1': no answer for 1 fixes: OAK" in out
    candidates = read(tmp_path / "out" / "candidates.json")
    assert [fix["id"] for fix in candidates["fixes"]] == ["MITOE"]
    assert candidates["missing"] == {"fixes": ["OAK"]}


def test_generate_resumes_recorded_batches_and_fresh_starts_over(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    first, _ = run_generate(tmp_path, [reply(FIX_REPLY), reply({}, stop_reason="refusal")], [])
    assert first == EXIT_FAILED
    resumed, client = run_generate(tmp_path, [reply(PROCEDURE_REPLY), reply(GROUND_REPLY)], [])
    assert resumed == 0
    assert "skipped 1 recorded batches" in capsys.readouterr().out
    assert len(client.beta.messages.calls) == len(["procedures", "ground"])
    candidates = read(tmp_path / "out" / "candidates.json")
    assert [fix["id"] for fix in candidates["fixes"]] == ["MITOE", "OAK"]
    fresh, fresh_client = run_generate(tmp_path, all_replies(), ["--fresh"])
    assert fresh == 0
    assert len(fresh_client.beta.messages.calls) == len(all_replies())
    assert len(read(tmp_path / "out" / "candidates.json")["batches"]) == len(all_replies())


def test_generate_refuses_another_airports_candidates(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    write(tmp_path / "out" / "candidates.json", {"airport": "KSFO", "batches": []})
    code, client = run_generate(tmp_path, all_replies(), [])
    assert code == EXIT_USAGE
    assert client.beta.messages.calls == []


# --- apply ------------------------------------------------------------------------------------------------------------


def test_apply_merges_landmarks_without_dropping_existing(tmp_path: Path) -> None:
    code, zoa = run_apply(tmp_path)
    assert code == 0
    landmarks = read(zoa / "CustomFixes" / "oak-landmarks.json")
    assert [entry["aliases"][0] for entry in landmarks] == ["OAK30NUM", "VPCOL"]
    assert landmarks[1]["lat"] == VPCOL_REVIEWED_LAT


def test_apply_merges_procedures_without_dropping_nimi(tmp_path: Path) -> None:
    code, zoa = run_apply(tmp_path)
    assert code == 0
    procedures = {entry["procedure"]: entry["pronunciations"] for entry in read(zoa / "ProcedurePronunciations" / "zoa.json")}
    assert procedures == {"NIMI": ["nimitz", "nimits"], "SKIZM": ["skism"]}


def test_apply_skips_a_fix_already_in_visual_json(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    code, zoa = run_apply(tmp_path)
    assert code == 0
    generated = read(zoa / "FixPronunciations" / "oak-generated.json")
    assert [entry["fix"] for entry in generated] == ["MITOE"]
    assert "skipped fix VPCOL: already in FixPronunciations/visual.json" in capsys.readouterr().out


def test_apply_never_writes_reads_as_spelled_items(tmp_path: Path) -> None:
    code, zoa = run_apply(tmp_path)
    assert code == 0
    assert "OAK" not in {entry["fix"] for entry in read(zoa / "FixPronunciations" / "oak-generated.json")}
    assert "OAK" not in {entry["procedure"] for entry in read(zoa / "ProcedurePronunciations" / "zoa.json")}
    places = read(zoa / "GatePronunciations" / "oak.json")
    assert places["airportId"] == "KOAK"
    assert "gates" not in places
    assert places["places"][0]["stands"] == ["SIG*"]


def test_apply_marks_every_created_fix_entry_generated(tmp_path: Path) -> None:
    code, zoa = run_apply(tmp_path)
    assert code == 0
    assert read(zoa / "FixPronunciations" / "oak-generated.json") == [{"fix": "MITOE", "pronunciations": ["my toe", "mitoe"], "generated": True}]


@pytest.mark.parametrize(
    ("fix", "flags", "reason"),
    [
        ("OAK", {"navaidName": "OAKLAND"}, "a navaid (OAKLAND)"),
        ("KOAK", {"isAirport": True}, "an airport"),
        ("OAK30NUM", {"isLandmark": True}, "a landmark alias"),
    ],
)
def test_apply_skips_a_fix_named_by_navdata_or_a_landmark(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], fix: str, flags: dict[str, Any], reason: str
) -> None:
    inputs = json.loads(json.dumps(INPUTS))
    inputs["fixes"] = [{"id": "MITOE"}, {"id": fix, **flags}]
    write(tmp_path / "out" / "inputs.json", inputs)
    candidates = base_candidates()
    candidates["fixes"] = [candidates["fixes"][0], {"id": fix, "verdict": "needs_form", "pronunciations": ["oakland"], "displayName": None}]
    decisions = base_decisions()
    decisions[f"fix:{fix}"] = {"status": "accepted", "pronunciations": ["oakland"]}
    code, zoa = apply_with(tmp_path, candidates, decisions)
    assert code == 0
    assert [entry["fix"] for entry in read(zoa / "FixPronunciations" / "oak-generated.json")] == ["MITOE"]
    assert f"skipped fix {fix}: {reason} already has its spoken name" in capsys.readouterr().out


def test_apply_without_inputs_exits_2(tmp_path: Path) -> None:
    directory = tmp_path / "out"
    write(directory / "candidates.json", base_candidates())
    write(directory / "decisions.json", base_decisions())
    code = lexicon.main(["apply", "--dir", str(directory), "--repo", str(make_repo(tmp_path)), "--artcc", "ZOA"], lambda: None)
    assert code == EXIT_USAGE


def test_apply_without_decisions_exits_2(tmp_path: Path) -> None:
    directory = tmp_path / "out"
    write(directory / "candidates.json", {"airport": "KOAK"})
    code = lexicon.main(["apply", "--dir", str(directory), "--repo", str(make_repo(tmp_path)), "--artcc", "ZOA"], lambda: None)
    assert code == EXIT_USAGE


def test_apply_takes_emptied_lists_from_the_decision(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    decisions = base_decisions()
    decisions["place:SIGNATURE"]["stands"] = []
    decisions["procedure:SKIZM"]["pronunciations"] = []
    code, zoa = apply_with(tmp_path, base_candidates(), decisions)
    assert code == 0
    assert "stands" not in read(zoa / "GatePronunciations" / "oak.json")["places"][0]
    assert "skipped procedure SKIZM: accepted with no forms" in capsys.readouterr().out
    assert "SKIZM" not in {entry["procedure"] for entry in read(zoa / "ProcedurePronunciations" / "zoa.json")}


def test_apply_takes_the_display_name_from_the_decision_only(tmp_path: Path) -> None:
    candidates = base_candidates()
    candidates["fixes"].append({"id": "ZORRO", "verdict": "needs_form", "pronunciations": ["zorro"], "displayName": "Model Invented"})
    decisions = base_decisions()
    decisions["fix:MITOE"]["displayName"] = "My Toe Point"
    decisions["fix:ZORRO"] = {"status": "accepted", "pronunciations": ["zorro"], "displayName": None}
    candidates["fixes"][0]["displayName"] = "Not Reviewed"
    code, zoa = apply_with(tmp_path, candidates, decisions)
    assert code == 0
    generated = {entry["fix"]: entry for entry in read(zoa / "FixPronunciations" / "oak-generated.json")}
    assert generated["MITOE"]["displayName"] == "My Toe Point"
    assert "displayName" not in generated["ZORRO"]


def test_apply_skips_a_landmark_without_coordinates(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    decisions = base_decisions()
    decisions["landmark:VPCOL"]["lat"] = None
    code, zoa = apply_with(tmp_path, base_candidates(), decisions)
    assert code == 0
    assert "skipped landmark VPCOL: accepted without a lat/lon" in capsys.readouterr().out
    assert [entry["aliases"][0] for entry in read(zoa / "CustomFixes" / "oak-landmarks.json")] == ["OAK30NUM"]


def test_apply_matches_a_landmark_by_any_alias_and_keeps_its_coordinates(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    repo = make_repo(tmp_path)
    landmarks_path = repo / "src" / "Yaat.Sim" / "Data" / "ARTCCs" / "ZOA" / "CustomFixes" / "oak-landmarks.json"
    write(landmarks_path, [{"name": "Coliseum", "aliases": ["COLSM", "VPCOL"], "spokenPatterns": ["coliseum"], "lat": FILE_LAT, "lon": 2.5}])
    directory = tmp_path / "out"
    write(directory / "candidates.json", base_candidates())
    write(directory / "decisions.json", base_decisions())
    write(directory / "inputs.json", INPUTS)
    code = lexicon.main(["apply", "--dir", str(directory), "--repo", str(repo), "--artcc", "ZOA"], lambda: None)
    assert code == 0
    landmarks = read(landmarks_path)
    assert len(landmarks) == 1
    assert landmarks[0]["lat"] == FILE_LAT
    assert landmarks[0]["spokenPatterns"] == ["coliseum", "the coliseum"]
    assert (
        f"warning: landmark VPCOL: reviewed lat/lon {VPCOL_REVIEWED_LAT}, -122.2005 differs from the file's {FILE_LAT}, 2.5"
        in capsys.readouterr().out
    )


@pytest.mark.parametrize("base_name", ["NIMI5", "  "])
def test_apply_skips_a_procedure_base_name_the_loader_would_reject(tmp_path: Path, capsys: pytest.CaptureFixture[str], base_name: str) -> None:
    candidates = base_candidates()
    candidates["procedures"] = [{"baseName": base_name, "verdict": "needs_form", "pronunciations": ["nimitz five"]}]
    decisions = {f"procedure:{base_name}": {"status": "accepted", "pronunciations": ["nimitz five"]}}
    code, zoa = apply_with(tmp_path, candidates, decisions)
    assert code == 0
    assert "skipped procedure" in capsys.readouterr().out
    assert read(zoa / "ProcedurePronunciations" / "zoa.json") == ZOA_FIXTURE["ProcedurePronunciations/zoa.json"]


def test_apply_writes_the_reviewed_place_name_and_landmark_alias(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    candidates = base_candidates()
    candidates["places"].append({"name": "KaiserAir", "pronunciations": ["kaiser"], "stands": [], "notes": ""})
    decisions = base_decisions()
    decisions["place:SIGNATURE"]["name"] = "  signature ramp "
    decisions["place:KaiserAir"] = {"status": "accepted", "pronunciations": ["kaiser"], "stands": [], "name": "Kaiser-Air"}
    decisions["landmark:VPCOL"]["alias"] = "coliseum"
    code, zoa = apply_with(tmp_path, candidates, decisions)
    assert code == 0
    assert [place["name"] for place in read(zoa / "GatePronunciations" / "oak.json")["places"]] == ["SIGNATURE RAMP"]
    assert "skipped place KaiserAir: its name must be letters A-Z, digits and spaces" in capsys.readouterr().out
    assert [entry["aliases"] for entry in read(zoa / "CustomFixes" / "oak-landmarks.json")] == [["OAK30NUM"], ["COLISEUM"]]


def test_apply_drops_a_stand_pattern_that_matches_no_layout_stand(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    decisions = base_decisions()
    decisions["place:SIGNATURE"]["stands"] = ["SIG*", "6b", "NOPE*", "SIG9"]
    code, zoa = apply_with(tmp_path, base_candidates(), decisions)
    assert code == 0
    assert read(zoa / "GatePronunciations" / "oak.json")["places"][0]["stands"] == ["SIG*", "6B"]
    out = capsys.readouterr().out
    assert "dropped stand NOPE* of place SIGNATURE: matches no layout gate or spot" in out
    assert "dropped stand SIG9 of place SIGNATURE" in out


def test_apply_reads_layout_stands_from_inputs_beside_older_candidates(tmp_path: Path) -> None:
    candidates = base_candidates()
    del candidates["layoutStands"]
    write(tmp_path / "out" / "inputs.json", INPUTS)
    code, zoa = apply_with(tmp_path, candidates, base_decisions())
    assert code == 0
    assert read(zoa / "GatePronunciations" / "oak.json")["places"][0]["stands"] == ["SIG*"]


def test_key_is_absent_from_every_written_file(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTHROPIC_API_KEY", SENTINEL_KEY)
    code, _ = run_generate(tmp_path, all_replies(), [])
    assert code == 0
    apply_code, _ = run_apply(tmp_path)
    assert apply_code == 0
    files = [path for path in tmp_path.rglob("*") if path.is_file()]
    assert files
    for path in files:
        assert SENTINEL_KEY not in path.read_text(encoding="utf-8"), path


# --- review server ----------------------------------------------------------------------------------------------------


@pytest.fixture
def review_server(tmp_path: Path) -> Iterator[tuple[str, Path]]:
    write(tmp_path / "candidates.json", base_candidates())
    server = ThreadingHTTPServer(("127.0.0.1", 0), lexicon.make_handler(tmp_path))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{server.server_address[1]}", tmp_path
    server.shutdown()
    server.server_close()


def post(url: str, body: bytes, headers: dict[str, str]) -> int:
    request = urllib.request.Request(f"{url}/api/save", data=body, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


GOOD_BODY = json.dumps({"decisions": {"fix:MITOE": {"status": "accepted", "pronunciations": ["my toe"]}}}).encode()


def test_save_writes_decisions_from_this_servers_page(review_server: tuple[str, Path]) -> None:
    url, root = review_server
    assert post(url, GOOD_BODY, {"Content-Type": "application/json", "Origin": url}) == HTTP_OK
    assert read(root / "decisions.json") == {"fix:MITOE": {"pronunciations": ["my toe"], "status": "accepted"}}
    assert '"Content-Type": "application/json"' in lexicon.REVIEW_HTML


def test_state_carries_the_layout_stands(review_server: tuple[str, Path]) -> None:
    url, _ = review_server
    with urllib.request.urlopen(f"{url}/api/state") as response:
        state = json.loads(response.read())
    assert state["layoutStands"] == ["6B", "SIG1", "SIG2"]
    assert state["decisions"] == {}


def test_save_refuses_a_body_that_is_not_json(review_server: tuple[str, Path]) -> None:
    url, root = review_server
    assert post(url, GOOD_BODY, {"Content-Type": "text/plain"}) == HTTP_UNSUPPORTED
    assert not (root / "decisions.json").exists()


def test_save_refuses_another_origin(review_server: tuple[str, Path]) -> None:
    url, root = review_server
    assert post(url, GOOD_BODY, {"Content-Type": "application/json", "Origin": "http://evil.example"}) == HTTP_FORBIDDEN
    assert not (root / "decisions.json").exists()


@pytest.mark.parametrize("body", [b"{not json", b"[]", b'{"decisions": []}', b'{"decisions": {"fix:A": 1}}'])
def test_save_refuses_a_malformed_body(review_server: tuple[str, Path], body: bytes) -> None:
    url, root = review_server
    assert post(url, body, {"Content-Type": "application/json"}) == HTTP_BAD_REQUEST
    assert not (root / "decisions.json").exists()
