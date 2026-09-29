"""Tests for tools/speech_telemetry.py.

The tool is a script, not an installed module, so it is loaded by path. Everything it touches on
disk lives under the state directory or the repo root, and both are redirected into `tmp_path`; the
only network call goes through `urllib.request.urlopen`, which the tests replace.
"""

from __future__ import annotations

import importlib.util
import io
import json
import sys
import urllib.error
import urllib.parse
import zipfile
from pathlib import Path
from typing import Any

import pytest

TOOL_PATH = Path(__file__).resolve().parents[1] / "speech_telemetry.py"
MAX_AUDIO_MB = 20
MAX_SESSION_MB = 5
SERVER = "https://example.test"
CID = "1234567"
SAMPLE_ID = "20260928-172403-ab12cd"
STORED_UTC = "2026-09-28T17:24:03+00:00"
BUNDLE_NAME = "1234567-172403-ab12cd34.zip"
BUNDLE_PATH = f"2026-09-28/{BUNDLE_NAME}"
CORPUS_REL = Path("tests") / "Yaat.Client.Tests" / "TestData" / "speech-transcripts" / "telemetry-regressions.json"
MAX_ROW_WIDTH = 150


def _load_module() -> Any:
    spec = importlib.util.spec_from_file_location("speech_telemetry", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["speech_telemetry"] = module
    spec.loader.exec_module(module)
    return module


@pytest.fixture()
def tool(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Any:
    """The tool module with its state directory and repo root redirected into tmp_path."""
    module = _load_module()
    monkeypatch.setattr(module, "STATE_DIR", tmp_path / ".tmp" / "speech-telemetry")
    monkeypatch.setattr(module, "REPO_ROOT", tmp_path)
    monkeypatch.setenv("YAAT_ADMIN_PASSWORD", "secret")
    return module


def _state_dir(tmp_path: Path) -> Path:
    return tmp_path / ".tmp" / "speech-telemetry"


def _session(
    transcript: str = "united two three four climb and maintain one zero thousand",
    canonical: str | None = "CM 10000",
    outcome: str = "CommandAccepted",
    used_llm: bool = False,
) -> dict[str, Any]:
    return {
        "timestampUtc": STORED_UTC,
        "transcript": transcript,
        "canonicalCommand": canonical,
        "usedLlmFallback": used_llm,
        "outcome": outcome,
        "errorMessage": None,
        "trace": {
            "rawTranscript": transcript,
            "callsignExtracted": "UAL234",
            "rule": {"outputCanonical": canonical, "failureReason": None},
            "llm": None,
            "activeCallsigns": ["UAL234"],
            "programmedFixes": ["CEPIN"],
            "availableRunwaysByAirport": {"KOAK": ["28R", "10L"]},
            "taxiwayNames": ["A", "B"],
            "aircraftDestinations": {"UAL234": "KOAK"},
        },
    }


def _bundle(sample_id: str, session: dict[str, Any], audio: bytes = b"RIFFfake", session_json: bytes | None = None) -> bytes:
    """A speech-sample bundle zip in the SpeechSampleStore export layout."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as bundle:
        manifest = {
            "schemaVersion": 2,
            "yaatVersion": "1.2.3",
            "exportedUtc": STORED_UTC,
            "samples": [
                {
                    "id": sample_id,
                    "capturedUtc": STORED_UTC,
                    "outcome": session["outcome"],
                    "usedLlmFallback": session["usedLlmFallback"],
                    "canonicalCommand": session["canonicalCommand"],
                }
            ],
        }
        bundle.writestr("manifest.json", json.dumps(manifest))
        bundle.writestr(f"samples/{sample_id}/audio.wav", audio)
        bundle.writestr(f"samples/{sample_id}/session.json", session_json if session_json is not None else json.dumps(session))
    return buffer.getvalue()


def _write_case(
    state_dir: Path,
    case_name: str,
    session: dict[str, Any],
    stored_utc: str = STORED_UTC,
    expected: dict[str, Any] | None = None,
) -> Path:
    case_dir = state_dir / "cases" / case_name
    case_dir.mkdir(parents=True, exist_ok=True)
    (case_dir / "audio.wav").write_bytes(b"RIFFfake")
    (case_dir / "session.json").write_text(json.dumps(session), encoding="utf-8", newline="\n")
    meta = {"cid": CID, "storedUtc": stored_utc, "source": BUNDLE_PATH, "sampleId": case_name.split("-", 1)[1]}
    (case_dir / "meta.json").write_text(json.dumps(meta), encoding="utf-8", newline="\n")
    if expected is not None:
        (case_dir / "expected.json").write_text(json.dumps(expected), encoding="utf-8", newline="\n")
    return case_dir


class _Server:
    """Stands in for urllib.request.urlopen; records every URL it was asked for and lists only what a
    strict ``since`` server returns."""

    def __init__(self, listing: list[dict[str, Any]], bundle: bytes = b"", error: Exception | None = None) -> None:
        self.listing = listing
        self.bundle = bundle
        self.error = error
        self.urls: list[str] = []

    def __call__(self, request: Any) -> io.BytesIO:
        url = request.full_url
        self.urls.append(url)
        if self.error is not None:
            raise self.error
        if url.endswith("/admin/telemetry/speech") or "/speech?" in url:
            return io.BytesIO(json.dumps(self._listed(url)).encode("utf-8"))
        if url.endswith(".zip"):
            return io.BytesIO(self.bundle)
        raise AssertionError(f"unexpected URL {url}")

    def _listed(self, url: str) -> list[dict[str, Any]]:
        """The server's ``since`` is strict: only bundles stored after it are listed."""
        since = urllib.parse.parse_qs(urllib.parse.urlparse(url).query).get("since", [None])[0]
        if not since:
            return self.listing
        return [item for item in self.listing if str(item.get("storedUtc", "")) > since]


def _serve(monkeypatch: pytest.MonkeyPatch, server: _Server) -> _Server:
    monkeypatch.setattr("urllib.request.urlopen", server)
    return server


def _rows(output: str) -> list[list[str]]:
    """Data rows of the plain-text summary table, as lists of stripped cells."""
    return [[cell.strip() for cell in line.split(" | ")] for line in output.splitlines()[2:]]


# ---------------------------------------------------------------------------
# pull
# ---------------------------------------------------------------------------


def test_pull_unpacks_bundle_into_cases_and_records_watermark(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    listing = [{"path": BUNDLE_PATH, "sizeBytes": 123, "storedUtc": STORED_UTC}]
    server = _serve(monkeypatch, _Server(listing, _bundle(SAMPLE_ID, _session())))

    assert tool.main(["pull", "--server", SERVER]) == 0

    out = capsys.readouterr().out
    assert "1 new sample(s)" in out
    assert (tmp_path / ".tmp/speech-telemetry/raw/2026-09-28" / BUNDLE_NAME).exists()

    case_dir = _state_dir(tmp_path) / "cases" / f"{CID}-{SAMPLE_ID}"
    assert sorted(path.name for path in case_dir.iterdir()) == ["audio.wav", "meta.json", "session.json"]
    assert (case_dir / "audio.wav").read_bytes() == b"RIFFfake"

    meta = json.loads((case_dir / "meta.json").read_text(encoding="utf-8"))
    assert meta == {"cid": CID, "storedUtc": STORED_UTC, "source": BUNDLE_PATH, "sampleId": SAMPLE_ID}

    state = json.loads((_state_dir(tmp_path) / "state.json").read_text(encoding="utf-8"))
    assert state == {"lastStoredUtc": STORED_UTC}
    assert server.urls[0] == f"{SERVER}/admin/telemetry/speech"
    assert server.urls[1] == f"{SERVER}/admin/telemetry/speech/{BUNDLE_PATH}"


def test_pull_resumes_with_since_and_downloads_nothing_twice(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    listing = [{"path": BUNDLE_PATH, "sizeBytes": 123, "storedUtc": STORED_UTC}]
    server = _serve(monkeypatch, _Server(listing, _bundle(SAMPLE_ID, _session())))
    assert tool.main(["pull", "--server", SERVER]) == 0
    capsys.readouterr()

    server.urls.clear()
    assert tool.main(["pull", "--server", SERVER]) == 0

    assert "0 new sample(s)" in capsys.readouterr().out
    assert len(server.urls) == 1, "the bundle is already on disk, so only the listing should be fetched"
    # One second before the watermark: the server's since is strict, so the same instant is re-listed and skipped locally.
    assert "since=2026-09-28T17%3A24%3A02%2B00%3A00" in server.urls[0]


def test_pull_reports_a_wrong_password(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    error = urllib.error.HTTPError(f"{SERVER}/admin/telemetry/speech", 401, "Unauthorized", None, None)
    _serve(monkeypatch, _Server([], error=error))

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["pull", "--server", SERVER])

    assert exit_info.value.code != 0
    assert "password" in capsys.readouterr().err.lower()


def test_pull_requires_the_password_environment_variable(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    monkeypatch.delenv("YAAT_ADMIN_PASSWORD")

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["pull", "--server", SERVER])

    assert exit_info.value.code != 0
    assert "YAAT_ADMIN_PASSWORD" in capsys.readouterr().err


def test_pull_reports_other_http_failures_with_url_and_status(tool: Any, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    error = urllib.error.HTTPError(f"{SERVER}/admin/telemetry/speech", 503, "Service Unavailable", None, None)
    _serve(monkeypatch, _Server([], error=error))

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["pull", "--server", SERVER])

    assert exit_info.value.code != 0
    message = capsys.readouterr().err
    assert f"{SERVER}/admin/telemetry/speech" in message
    assert "503" in message


def test_pull_refuses_a_sample_id_that_climbs_out_of_the_cases_dir(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    listing = [{"path": BUNDLE_PATH, "storedUtc": STORED_UTC}]
    _serve(monkeypatch, _Server(listing, _bundle("../../x", _session())))

    assert tool.main(["pull", "--server", SERVER]) == 0

    err = capsys.readouterr().err
    assert "warning" in err
    assert BUNDLE_NAME in err
    assert not (tmp_path / ".tmp/speech-telemetry/x").exists(), "the manifest id must not write outside cases/"
    assert not (_state_dir(tmp_path) / "cases").exists()


def test_pull_refuses_a_bundle_whose_file_name_has_no_usable_cid(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    listing = [{"path": "2026-09-28/evil.script-172403.zip", "storedUtc": STORED_UTC}]
    _serve(monkeypatch, _Server(listing, _bundle(SAMPLE_ID, _session())))

    assert tool.main(["pull", "--server", SERVER]) == 0

    captured = capsys.readouterr()
    assert "0 new sample(s)" in captured.out
    assert "warning" in captured.err
    assert not (_state_dir(tmp_path) / "cases").exists()


def test_pull_skips_a_sample_whose_entry_exceeds_its_size_cap(tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any) -> None:
    listing = [{"path": BUNDLE_PATH, "storedUtc": STORED_UTC}]
    oversized = b"0" * ((MAX_SESSION_MB * 1024 * 1024) + 1)
    _serve(monkeypatch, _Server(listing, _bundle(SAMPLE_ID, _session(), session_json=oversized)))

    assert tool.main(["pull", "--server", SERVER]) == 0

    captured = capsys.readouterr()
    assert "0 new sample(s)" in captured.out
    assert "warning" in captured.err
    assert not (_state_dir(tmp_path) / "cases").exists()


def test_pull_quarantines_a_corrupt_bundle_and_never_fetches_it_again(
    tool: Any, tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: Any
) -> None:
    listing = [{"path": BUNDLE_PATH, "storedUtc": STORED_UTC}]
    server = _serve(monkeypatch, _Server(listing, b"this body is not a zip"))

    assert tool.main(["pull", "--server", SERVER]) == 0

    captured = capsys.readouterr()
    assert "warning" in captured.err
    assert BUNDLE_NAME in captured.err
    assert not (tmp_path / ".tmp/speech-telemetry/raw/2026-09-28" / BUNDLE_NAME).exists()
    quarantined = [path.name for path in (_state_dir(tmp_path) / "raw" / "quarantine").iterdir()]
    assert len(quarantined) == 1
    assert quarantined[0].endswith(BUNDLE_NAME)
    assert json.loads((_state_dir(tmp_path) / "state.json").read_text(encoding="utf-8"))["lastStoredUtc"] == STORED_UTC

    server.urls.clear()
    assert tool.main(["pull", "--server", SERVER]) == 0

    assert "0 new sample(s)" in capsys.readouterr().out
    assert len(server.urls) == 1, "the quarantined bundle is remembered, so it is never downloaded again"


# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------


def _ordering_cases(tmp_path: Path) -> None:
    state_dir = _state_dir(tmp_path)
    _write_case(state_dir, f"{CID}-a", session=_session("t-accepted", "CM 10000"))
    _write_case(state_dir, f"{CID}-b", session=_session("t-llm", "TL 310", used_llm=True))
    _write_case(state_dir, f"{CID}-c", session=_session("t-error", None, outcome="Error"))
    _write_case(state_dir, f"{CID}-d", session=_session("t-nomap", None, outcome="NoMappingFound"))
    _write_case(state_dir, f"{CID}-e", session=_session("", None, outcome="EmptyTranscript"))
    _write_case(state_dir, f"{CID}-f", session=_session("t-cancel", None, outcome="Cancelled"))


def _transcript_column(output: str) -> list[str]:
    return [row[4] for row in _rows(output)]


def test_summary_orders_worst_outcome_first(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _ordering_cases(tmp_path)

    assert tool.main(["summary"]) == 0

    assert _transcript_column(capsys.readouterr().out) == ["t-nomap", "", "t-error", "t-cancel", "t-llm", "t-accepted"]


def test_summary_breaks_ties_by_stored_utc(tool: Any, tmp_path: Path, capsys: Any) -> None:
    state_dir = _state_dir(tmp_path)
    _write_case(state_dir, f"{CID}-later", session=_session("t-later", None, outcome="Error"), stored_utc="2026-09-29T10:00:00+00:00")
    _write_case(state_dir, f"{CID}-earlier", session=_session("t-earlier", None, outcome="Error"), stored_utc="2026-09-27T10:00:00+00:00")

    assert tool.main(["summary"]) == 0

    assert _transcript_column(capsys.readouterr().out) == ["t-earlier", "t-later"]


def test_summary_hides_reviewed_cases_unless_all(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _ordering_cases(tmp_path)
    assert tool.main(["reviewed", f"{CID}-a"]) == 0
    capsys.readouterr()

    assert tool.main(["summary"]) == 0
    assert "t-accepted" not in _transcript_column(capsys.readouterr().out)

    assert tool.main(["summary", "--all"]) == 0
    assert _transcript_column(capsys.readouterr().out) == ["t-nomap", "", "t-error", "t-cancel", "t-llm", "t-accepted"]


def test_summary_json_carries_the_triage_fields(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _write_case(_state_dir(tmp_path), f"{CID}-a", session=_session("t-one", "CM 10000"))

    assert tool.main(["summary", "--json"]) == 0

    rows = json.loads(capsys.readouterr().out)
    assert rows == [
        {
            "case": f"{CID}-a",
            "cid": CID,
            "storedUtc": STORED_UTC,
            "outcome": "CommandAccepted",
            "usedLlmFallback": False,
            "transcript": "t-one",
            "canonical": "CM 10000",
        }
    ]


def test_summary_renders_an_empty_canonical_as_a_dash(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _write_case(_state_dir(tmp_path), f"{CID}-a", session=_session("t-miss", None, outcome="NoMappingFound"))

    assert tool.main(["summary"]) == 0

    assert _rows(capsys.readouterr().out)[0][5] == "—"


def test_summary_row_stays_within_150_characters(tool: Any, tmp_path: Path, capsys: Any) -> None:
    long_session = _session("word " * 80, "CM 10000 " + "TL 310, " * 20)
    _write_case(_state_dir(tmp_path), f"{CID}-a", session=long_session)

    assert tool.main(["summary"]) == 0

    assert all(len(line) <= MAX_ROW_WIDTH for line in capsys.readouterr().out.splitlines())


# ---------------------------------------------------------------------------
# reviewed
# ---------------------------------------------------------------------------


def test_reviewed_all_shown_marks_every_unreviewed_case(tool: Any, tmp_path: Path) -> None:
    _ordering_cases(tmp_path)

    assert tool.main(["reviewed", "--all-shown"]) == 0

    reviewed = json.loads((_state_dir(tmp_path) / "reviewed.json").read_text(encoding="utf-8"))
    assert reviewed == [f"{CID}-{letter}" for letter in "abcdef"]


def test_reviewed_accumulates_without_duplicates(tool: Any, tmp_path: Path) -> None:
    _ordering_cases(tmp_path)

    assert tool.main(["reviewed", f"{CID}-a"]) == 0
    assert tool.main(["reviewed", f"{CID}-b", f"{CID}-a"]) == 0

    reviewed = json.loads((_state_dir(tmp_path) / "reviewed.json").read_text(encoding="utf-8"))
    assert reviewed == [f"{CID}-a", f"{CID}-b"]


def test_reviewed_rejects_an_unknown_case(tool: Any, tmp_path: Path, capsys: Any) -> None:
    with pytest.raises(SystemExit) as exit_info:
        tool.main(["reviewed", "no-such-case"])

    assert exit_info.value.code != 0
    assert "no-such-case" in capsys.readouterr().err


def test_reviewed_without_names_is_an_error(tool: Any, tmp_path: Path, capsys: Any) -> None:
    with pytest.raises(SystemExit) as exit_info:
        tool.main(["reviewed"])

    assert exit_info.value.code != 0
    assert "case names" in capsys.readouterr().err


# ---------------------------------------------------------------------------
# promote
# ---------------------------------------------------------------------------


def _reviewed_case(tool: Any, tmp_path: Path) -> Path:
    expected = {
        "canonical": "CM 10000",
        "transcript": "united two three four climb and maintain one zero thousand",
        "callsign": "UAL234",
        "activeCallsigns": ["UAL234"],
        "programmedFixes": ["CEPIN"],
    }
    return _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}", session=_session(), expected=expected)


def _corpus(tool: Any, tmp_path: Path) -> Any:
    return json.loads((tmp_path / CORPUS_REL).read_text(encoding="utf-8"))


def test_promote_appends_a_text_only_entry(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _reviewed_case(tool, tmp_path)

    assert tool.main(["promote", f"{CID}-{SAMPLE_ID}"]) == 0

    assert "promoted" in capsys.readouterr().out
    text = (tmp_path / CORPUS_REL).read_text(encoding="utf-8")
    assert CID not in text
    assert "audio.wav" not in text
    assert STORED_UTC not in text
    assert text.endswith("\n")
    assert "\r" not in text

    assert _corpus(tool, tmp_path) == [
        {
            "name": SAMPLE_ID,
            "transcript": "united two three four climb and maintain one zero thousand",
            "canonical": "CM 10000",
            "callsign": "UAL234",
            "activeCallsigns": ["UAL234"],
            "programmedFixes": ["CEPIN"],
            "availableRunwaysByAirport": {"KOAK": ["28R", "10L"]},
            "taxiwayNames": ["A", "B"],
            "aircraftDestinations": {"UAL234": "KOAK"},
        }
    ]


def test_promote_accepts_an_explicit_name(tool: Any, tmp_path: Path) -> None:
    _reviewed_case(tool, tmp_path)

    assert tool.main(["promote", f"{CID}-{SAMPLE_ID}", "--name", "united-234-climb"]) == 0

    assert _corpus(tool, tmp_path)[0]["name"] == "united-234-climb"


def test_promote_falls_back_to_the_trace_when_expected_has_no_lists(tool: Any, tmp_path: Path) -> None:
    _write_case(
        _state_dir(tmp_path),
        f"{CID}-{SAMPLE_ID}",
        session=_session(),
        expected={"canonical": "CM 10000", "callsign": None},
    )

    assert tool.main(["promote", f"{CID}-{SAMPLE_ID}"]) == 0

    entry = _corpus(tool, tmp_path)[0]
    assert entry["callsign"] is None
    assert entry["activeCallsigns"] == ["UAL234"]
    assert entry["programmedFixes"] == ["CEPIN"]


def test_promote_keeps_entries_sorted_by_name(tool: Any, tmp_path: Path) -> None:
    state_dir = _state_dir(tmp_path)
    _write_case(state_dir, f"{CID}-bbb", session=_session("second transcript"), expected={"canonical": "TL 310"})
    _write_case(state_dir, f"{CID}-aaa", session=_session("first transcript"), expected={"canonical": "CM 10000"})

    assert tool.main(["promote", f"{CID}-bbb"]) == 0
    assert tool.main(["promote", f"{CID}-aaa"]) == 0

    assert [entry["name"] for entry in _corpus(tool, tmp_path)] == ["aaa", "bbb"]


def test_promote_refuses_an_unreviewed_stub(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _write_case(
        _state_dir(tmp_path),
        f"{CID}-{SAMPLE_ID}",
        session=_session(),
        expected={"unreviewed": True, "canonical": "CM 10000"},
    )

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["promote", f"{CID}-{SAMPLE_ID}"])

    assert exit_info.value.code != 0
    assert "unreviewed" in capsys.readouterr().err
    assert not (tmp_path / CORPUS_REL).exists()


def test_promote_requires_an_expected_json(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}", session=_session())

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["promote", f"{CID}-{SAMPLE_ID}"])

    assert exit_info.value.code != 0
    assert "expected.json" in capsys.readouterr().err


def test_promote_refuses_a_duplicate_transcript(tool: Any, tmp_path: Path, capsys: Any) -> None:
    _reviewed_case(tool, tmp_path)
    assert tool.main(["promote", f"{CID}-{SAMPLE_ID}"]) == 0
    _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}2", session=_session(), expected={"canonical": "CM 10000"})
    capsys.readouterr()

    with pytest.raises(SystemExit) as exit_info:
        tool.main(["promote", f"{CID}-{SAMPLE_ID}2"])

    assert exit_info.value.code != 0
    assert "already covers this transcript" in capsys.readouterr().err
    assert len(_corpus(tool, tmp_path)) == 1


# ---------------------------------------------------------------------------
# eval staging (no dotnet run)
# ---------------------------------------------------------------------------


def test_stage_case_copies_audio_session_and_reviewed_labels(tool: Any, tmp_path: Path) -> None:
    case_dir = _reviewed_case(tool, tmp_path)
    staged = tmp_path / "staged"

    tool.stage_case(case_dir, staged)

    assert sorted(path.name for path in staged.iterdir()) == ["audio.wav", "expected.json", "session.json"]


def test_stage_case_skips_a_missing_expected_json(tool: Any, tmp_path: Path) -> None:
    case_dir = _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}", session=_session())
    staged = tmp_path / "staged"

    tool.stage_case(case_dir, staged)

    assert sorted(path.name for path in staged.iterdir()) == ["audio.wav", "session.json"]


def test_sync_expected_back_lands_a_harness_stub_in_the_case_dir(tool: Any, tmp_path: Path) -> None:
    case_dir = _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}", session=_session())
    staged = tmp_path / "staged"
    tool.stage_case(case_dir, staged)
    stub = {"unreviewed": True, "canonical": "CM 10000"}
    (staged / "expected.json").write_text(json.dumps(stub), encoding="utf-8", newline="\n")

    assert tool.sync_expected_back(staged, case_dir) is True

    assert json.loads((case_dir / "expected.json").read_text(encoding="utf-8")) == stub
    assert tool.sync_expected_back(staged, case_dir) is False


def test_sync_expected_back_is_a_noop_without_a_staged_file(tool: Any, tmp_path: Path) -> None:
    case_dir = _write_case(_state_dir(tmp_path), f"{CID}-{SAMPLE_ID}", session=_session())

    assert tool.sync_expected_back(tmp_path / "staged", case_dir) is False
    assert not (case_dir / "expected.json").exists()


def test_eval_command_runs_the_harness_through_the_gate(tool: Any, tmp_path: Path) -> None:
    command = tool.eval_command(tmp_path / "corpus", 3, tmp_path / "out")

    separator = command.index("--")
    end_of_command = command.index("--", separator + 1)
    assert command[:2] == ["pwsh", str(tmp_path / "tools" / "gate.ps1")]
    assert command[command.index("-Slot") + 1] == "heavy"
    assert command[separator + 1 : end_of_command] == [
        "dotnet",
        "run",
        "--project",
        str(tmp_path / "tools" / "Yaat.SpeechSandbox"),
        "-c",
        "Release",
    ]
    assert command[end_of_command + 1 :] == ["--eval", str(tmp_path / "corpus"), "--trials", "3", "--out-dir", str(tmp_path / "out")]
    assert command[command.index("--trials") + 1] == "3"
    assert command[command.index("--eval") + 1] == str(tmp_path / "corpus")
    assert command[command.index("--out-dir") + 1] == str(tmp_path / "out")
    assert command[command.index("-TimeoutSeconds") + 1] == "1800"
