# /// script
# requires-python = ">=3.13"
# dependencies = ["anthropic>=2.3.0"]
# ///
"""Propose, review and apply spoken forms for an airport's speech lexicon.

The inputs come from the speech sandbox (`--lexicon-inputs`): the airport's fixes within 40 nm and on its
procedures, its SIDs and STARs, lettered layout gates and spots, named ground places and VFR landmarks, each with
the spoken forms already registered. `generate` asks Claude how controllers and pilots say each one on the radio,
`review` serves a local page where every proposal is accepted, edited or rejected, and `apply` merges the accepted
ones into the ARTCC's data files (FixPronunciations, ProcedurePronunciations, GatePronunciations, CustomFixes).

The API key is read from ANTHROPIC_API_KEY only; it is never printed, logged or written. `generate` writes
candidates.json after every batch; run again, it skips the batches already recorded there (`--fresh` starts over).

Usage:
    dotnet run --project tools/Yaat.SpeechSandbox -c Release -- --lexicon-inputs KOAK .tmp/lexicon/oak/inputs.json
    uv run tools/generate-speech-lexicon.py generate --inputs .tmp/lexicon/oak/inputs.json --out .tmp/lexicon/oak [--fresh]
    uv run tools/generate-speech-lexicon.py review --dir .tmp/lexicon/oak [--port 8766]
    uv run tools/generate-speech-lexicon.py apply --dir .tmp/lexicon/oak --repo . --artcc ZOA
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

import anthropic

MODEL = "claude-opus-5-5"
MAX_TOKENS = 64000
BETAS = ["server-side-fallback-2026-07-01"]
KEY_ENV = "ANTHROPIC_API_KEY"
FIX_BATCH_SIZE = 60
ICAO_LENGTH = 4
READS_AS_SPELLED = "reads_as_spelled"
GROUND_LISTS = ("gates", "spots", "places", "landmarks")
CLOSED_LISTS = ("fixes", "procedures", "gates", "spots")


@dataclass(frozen=True)
class Kind:
    """One kind of lexicon item: its list name in inputs and candidates, its decision prefix and its name field."""

    list_name: str
    decision: str
    key: str
    forms: str


KINDS = (
    Kind("fixes", "fix", "id", "pronunciations"),
    Kind("procedures", "procedure", "baseName", "pronunciations"),
    Kind("gates", "gate", "name", "pronunciations"),
    Kind("spots", "spot", "name", "pronunciations"),
    Kind("places", "place", "name", "pronunciations"),
    Kind("landmarks", "landmark", "alias", "spokenPatterns"),
)
KIND_BY_LIST = {kind.list_name: kind for kind in KINDS}


def _obj(properties: dict[str, Any]) -> dict[str, Any]:
    return {"type": "object", "properties": properties, "required": list(properties), "additionalProperties": False}


def _array(items: dict[str, Any]) -> dict[str, Any]:
    return {"type": "array", "items": items}


STR = {"type": "string"}
STRS = _array(STR)
NUM = {"type": "number"}
NULLABLE_STR = {"anyOf": [STR, {"type": "null"}]}
VERDICT = {"type": "string", "enum": [READS_AS_SPELLED, "needs_form"]}

FIX_SCHEMA = _obj({"fixes": _array(_obj({"id": STR, "verdict": VERDICT, "pronunciations": STRS, "displayName": NULLABLE_STR, "rationale": STR}))})
PROCEDURE_SCHEMA = _obj(
    {"procedures": _array(_obj({"baseName": STR, "verdict": VERDICT, "pronunciations": STRS, "chartedName": NULLABLE_STR, "rationale": STR}))}
)
_LAYOUT_NAME = _obj({"name": STR, "pronunciations": STRS, "rationale": STR})
GROUND_SCHEMA = _obj(
    {
        "gates": _array(_LAYOUT_NAME),
        "spots": _array(_LAYOUT_NAME),
        "places": _array(_obj({"name": STR, "pronunciations": STRS, "stands": STRS, "notes": STR, "rationale": STR})),
        "landmarks": _array(_obj({"alias": STR, "name": STR, "spokenPatterns": STRS, "lat": NUM, "lon": NUM, "source": STR, "rationale": STR})),
    }
)

SYSTEM_PROMPT = """You generate how US air traffic controllers and pilots say names on the radio at one airport, for a \
speech recognizer and for simulated pilots that speak.

Forms:
- Every spoken form is lowercase, space-separated words with no punctuation. Write numbers as digits ("runway 30 numbers", \
"6 bravo"), because the recognizer's text is normalized to digits before matching.
- The first pronunciation is the most common radio form: simulated pilots will speak it. Further forms are other ways \
controllers and pilots really say the name.

Fixes and procedures:
- Mark an item needs_form only when a recognizer that reads the identifier letter by letter or as a written word would miss \
how it is said: made-up letter strings said as a word, truncated names (NIMI is said "nimitz"), names spoken as a different \
word. Otherwise mark it reads_as_spelled with no pronunciations.
- Never invent a name you do not know. When unsure, prefer reads_as_spelled and say so in the rationale.
- Existing forms are given; keep the good ones and improve or extend them.

Ground places and landmarks:
- Places are the FBOs, ramps, cargo areas and bays a ground controller names. Give the stand-name patterns from the gate list \
that the place covers (an exact name or a prefix ending in *), or [] when unknown.
- Landmarks are VFR reporting points and visual landmarks a tower uses. Give real coordinates and a source the owner can \
check, and include one only when you know it.
- Every rationale says briefly why the form is right or where it comes from."""

FIX_TASK = (
    "For every fix below, return one entry with the same id: a verdict, its spoken forms when needs_form, and a displayName when it names a place."
)
PROCEDURE_TASK = (
    "For every distinct SID/STAR baseName below, return one entry with that baseName: a verdict, its spoken base-name forms "
    "(without the version number) when needs_form, and the chartedName printed on the chart when you know it."
)
GROUND_TASK = (
    "Return an entry for every gate and spot below with the forms a ground controller says, then the airport's named ground "
    "places (FBOs, ramps, cargo areas, bays) and the VFR landmarks its tower uses, including the listed ones, which you may improve."
)


class LexiconError(Exception):
    """A failure the command reports with a message and an exit code."""

    def __init__(self, message: str, exit_code: int) -> None:
        super().__init__(message)
        self.exit_code = exit_code


@dataclass(frozen=True)
class Batch:
    """One model request: a name for messages, the kind recorded in candidates.json, its schema, the task line and the input items."""

    name: str
    kind: str
    schema: dict[str, Any]
    task: str
    items: dict[str, Any]

    def sent_ids(self) -> dict[str, list[str]]:
        """Per list in the batch, the upper-cased names it sends, sorted and distinct."""
        return {list_name: sorted({str(item[KIND_BY_LIST[list_name].key]).upper() for item in items}) for list_name, items in self.items.items()}


def batch_key(kind: str, sent: dict[str, list[str]]) -> str:
    """What identifies a batch across runs: its kind and the sorted names it sent."""
    return json.dumps([kind, sent], sort_keys=True)


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, data: Any, *, sort_keys: bool = False) -> None:
    """Writes JSON (2-space indent, LF, trailing newline) through a temp file in the same folder, so a crash never leaves it half-written."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    temporary.write_text(json.dumps(data, indent=2, ensure_ascii=False, sort_keys=sort_keys) + "\n", encoding="utf-8", newline="\n")
    temporary.replace(path)


def faa_code(airport: str) -> str:
    """The FAA id of a US ICAO id (KOAK -> OAK); any other id unchanged."""
    upper = airport.upper()
    return upper[1:] if len(upper) == ICAO_LENGTH and upper.startswith("K") else upper


def build_batches(inputs: dict[str, Any]) -> list[Batch]:
    """Fixes in chunks of FIX_BATCH_SIZE, one batch for procedures, one for gates, spots, places and landmarks; empty ones left out."""
    fixes = inputs.get("fixes", [])
    chunks = [fixes[start : start + FIX_BATCH_SIZE] for start in range(0, len(fixes), FIX_BATCH_SIZE)]
    batches = [Batch(f"fixes {n}/{len(chunks)}", "fixes", FIX_SCHEMA, FIX_TASK, {"fixes": chunk}) for n, chunk in enumerate(chunks, 1)]
    if inputs.get("procedures"):
        batches.append(Batch("procedures", "procedures", PROCEDURE_SCHEMA, PROCEDURE_TASK, {"procedures": inputs["procedures"]}))
    ground = {name: inputs.get(name, []) for name in GROUND_LISTS}
    if any(ground.values()):
        batches.append(Batch("ground", "ground", GROUND_SCHEMA, GROUND_TASK, ground))
    return batches


def user_prompt(airport: str, batch: Batch) -> str:
    items = json.dumps(batch.items, indent=1, ensure_ascii=False)
    return f"Airport: {airport}\n\n{batch.task}\n\nInput items with their existing spoken forms (JSON):\n{items}"


def _field(source: Any, name: str) -> Any:
    return source.get(name) if isinstance(source, dict) else getattr(source, name, None)


def refusal_detail(details: Any) -> str:
    """The refusal's category and explanation from stop_details, when the API gave them."""
    if details is None:
        return ""
    parts = [f"{name} {value}" for name in ("category", "explanation") if (value := _field(details, name))]
    return f" ({'; '.join(parts)})" if parts else ""


def check_stop(batch: Batch, message: Any) -> None:
    """Fails the batch on every stop reason but end_turn."""
    if message.stop_reason == "end_turn":
        return
    detail = refusal_detail(getattr(message, "stop_details", None)) if message.stop_reason == "refusal" else ""
    raise LexiconError(f"batch '{batch.name}' stopped with {message.stop_reason}{detail}; its items are missing from candidates.json", 1)


def answer_text(message: Any) -> str:
    """The text blocks after the last fallback block (a mid-stream model switch restarts the answer), else all of them."""
    content = list(message.content)
    fallback_at = max((index for index, block in enumerate(content) if block.type == "fallback"), default=-1)
    return "".join(block.text for block in content[fallback_at + 1 :] if block.type == "text")


def request_batch(client: Any, airport: str, batch: Batch) -> tuple[dict[str, Any], dict[str, Any]]:
    """Sends one batch; returns the parsed model answer and the batch record. Raises LexiconError on a failed stop or a bad answer."""
    with client.beta.messages.stream(
        model=MODEL,
        max_tokens=MAX_TOKENS,
        betas=BETAS,
        fallbacks="default",
        output_config={"effort": "high", "format": {"type": "json_schema", "schema": batch.schema}},
        system=SYSTEM_PROMPT,
        messages=[{"role": "user", "content": user_prompt(airport, batch)}],
    ) as stream:
        message = stream.get_final_message()
    check_stop(batch, message)
    try:
        answer = json.loads(answer_text(message))
    except json.JSONDecodeError as error:
        raise LexiconError(f"batch '{batch.name}' answer is not valid JSON: {error}", 1) from error
    if not isinstance(answer, dict):
        raise LexiconError(f"batch '{batch.name}' answer is not a JSON object", 1)
    record = {
        "kind": batch.kind,
        "name": batch.name,
        "sent": batch.sent_ids(),
        "requestId": getattr(message, "_request_id", None) or message.id,
        "model": message.model,
        "usage": {"input_tokens": message.usage.input_tokens, "output_tokens": message.usage.output_tokens},
    }
    return answer, record


def input_index(inputs: dict[str, Any]) -> dict[str, dict[str, list[dict[str, Any]]]]:
    """Per list, the input records under each upper-cased name (several procedure ids share one base name)."""
    index: dict[str, dict[str, list[dict[str, Any]]]] = {}
    for kind in KINDS:
        by_name: dict[str, list[dict[str, Any]]] = {}
        for record in inputs.get(kind.list_name, []):
            by_name.setdefault(str(record[kind.key]).upper(), []).append(record)
        index[kind.list_name] = by_name
    return index


def warn_answer(candidates: dict[str, Any], batch: Batch, list_name: str, dropped: int, missing: list[str]) -> None:
    if dropped:
        print(f"warning: batch '{batch.name}': dropped {dropped} {list_name} not in the batch")
    if missing:
        print(f"warning: batch '{batch.name}': no answer for {len(missing)} {list_name}: {', '.join(missing)}")
        candidates["missing"].setdefault(list_name, []).extend(missing)


def merge_result(candidates: dict[str, Any], index: dict[str, dict[str, list[dict[str, Any]]]], batch: Batch, answer: dict[str, Any]) -> None:
    """Appends a batch's answer to the candidates, checked against what the batch sent.

    An item of a closed list (fixes, procedures, gates, spots) naming something the batch did not send is dropped; a sent
    name the answer leaves out is recorded under "missing". Ground items carry inInputs, since places and landmarks may be
    proposed freshly. Every item carries the input records it answers.
    """
    for list_name, sent in batch.sent_ids().items():
        kind = KIND_BY_LIST[list_name]
        answered: set[str] = set()
        dropped = 0
        for item in answer.get(list_name, []):
            name = str(item[kind.key]).upper()
            if list_name in CLOSED_LISTS and name not in sent:
                dropped += 1
                continue
            answered.add(name)
            sources = index[list_name].get(name, [])
            if list_name in GROUND_LISTS:
                item["inInputs"] = bool(sources)
            item["input"] = sources
            candidates[list_name].append(item)
        warn_answer(candidates, batch, list_name, dropped, [name for name in sent if name not in answered])


def print_summary(candidates: dict[str, Any], inputs: dict[str, Any]) -> None:
    for kind in KINDS:
        sent = len(inputs.get(kind.list_name, []))
        items = candidates[kind.list_name]
        if kind.list_name in ("fixes", "procedures"):
            needs = sum(1 for item in items if item.get("verdict") != READS_AS_SPELLED)
            print(f"{kind.list_name}: {sent} sent, {needs} needs_form")
        else:
            print(f"{kind.list_name}: {sent} sent, {len(items)} proposed")
    input_tokens = sum(batch["usage"]["input_tokens"] for batch in candidates["batches"])
    output_tokens = sum(batch["usage"]["output_tokens"] for batch in candidates["batches"])
    print(f"tokens: {input_tokens} input, {output_tokens} output, {input_tokens + output_tokens} total")


def load_candidates(path: Path, airport: str, *, fresh: bool) -> dict[str, Any]:
    """The candidates already generated for this airport in path, or an empty set when there are none or fresh is set."""
    if path.is_file() and not fresh:
        existing = read_json(path)
        if str(existing.get("airport", "")).upper() != airport.upper():
            raise LexiconError(f"{path} holds candidates for {existing.get('airport')}, not {airport}; use another --out or pass --fresh", 2)
        existing.setdefault("missing", {})
        return existing
    return {
        "airport": airport,
        "requestedModel": MODEL,
        "generatedAt": datetime.now(UTC).isoformat(timespec="seconds"),
        "batches": [],
        "missing": {},
        **{kind.list_name: [] for kind in KINDS},
    }


def cmd_generate(args: argparse.Namespace, client_factory: Callable[[], Any]) -> int:
    if not os.environ.get(KEY_ENV):
        raise LexiconError(f"{KEY_ENV} is not set; set it in this shell's environment and rerun", 2)
    inputs = read_json(Path(args.inputs))
    out_path = Path(args.out) / "candidates.json"
    candidates = load_candidates(out_path, inputs["airport"], fresh=args.fresh)
    recorded = {batch_key(record["kind"], record.get("sent", {})) for record in candidates["batches"]}
    batches = build_batches(inputs)
    pending = [batch for batch in batches if batch_key(batch.kind, batch.sent_ids()) not in recorded]
    if len(pending) < len(batches):
        print(f"skipped {len(batches) - len(pending)} recorded batches")
    index = input_index(inputs)
    client = client_factory()
    for batch in pending:
        try:
            answer, record = request_batch(client, inputs["airport"], batch)
        except anthropic.APIError as error:
            raise LexiconError(f"batch '{batch.name}' failed: {error}", 1) from error
        merge_result(candidates, index, batch, answer)
        candidates["batches"].append(record)
        write_json(out_path, candidates)
    print_summary(candidates, inputs)
    return 0


REVIEW_HTML = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<link rel="icon" href="data:,">
<title>Speech lexicon review</title>
<style>
body { font-family: system-ui, sans-serif; margin: 1.5em; max-width: 70em; }
.item { border: 1px solid #bbb; border-radius: 4px; padding: .6em; margin: .6em 0; }
.item.accepted { border-color: #2a2; background: #f3fbf3; }
.item.rejected { border-color: #c33; background: #fbf3f3; }
.name { font-weight: bold; font-family: monospace; font-size: 1.1em; }
.context, .rationale { color: #444; margin: .3em 0; }
textarea { width: 30em; font-family: monospace; }
.status { margin-left: 1em; font-style: italic; }
#bar { position: sticky; top: 0; background: #fff; padding: .5em 0; border-bottom: 1px solid #ccc; }
</style>
</head>
<body>
<div id="bar"><button id="save">Save</button> <span id="result"></span></div>
<h1 id="title">Speech lexicon review</h1>
<div id="root"></div>
<script>
const KINDS = [["fixes", "fix", "id"], ["procedures", "procedure", "baseName"], ["gates", "gate", "name"],
  ["spots", "spot", "name"], ["places", "place", "name"], ["landmarks", "landmark", "alias"]];
let state = { candidates: {}, decisions: {} };

function el(tag, text) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  return node;
}

function lines(text) {
  return text.split("\\n").map(s => s.trim()).filter(s => s.length > 0);
}

function osmLink(lat, lon) {
  const link = el("a", `${lat}, ${lon}`);
  link.href = `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=15/${lat}/${lon}`;
  link.target = "_blank";
  link.rel = "noopener";
  return link;
}

function contextOf(kind, item) {
  const box = el("div");
  box.className = "context";
  const input = item.input || [];
  const first = input[0] || {};
  const existing = first.existingPronunciations || first.pronunciations || first.spokenPatterns || [];
  if (item.inInputs === false) box.append("new name (not in the inputs). ");
  if (kind === "fix") box.append(`${first.distanceNm ?? "?"} nm; on ${(first.onProcedures || []).join(", ") || "no procedure"}. `);
  if (kind === "procedure") box.append(`ids ${input.map(p => p.id).join(", ")}; chart ${item.chartedName ?? "?"}. `);
  if (kind === "landmark") { box.append("at "); box.append(osmLink(item.lat, item.lon)); box.append(`; source ${item.source}. `); }
  box.append(`existing: ${existing.join(" | ") || "none"}`);
  return box;
}

function field(label, value, rows) {
  const wrap = el("div");
  const area = el("textarea");
  area.rows = rows;
  area.value = value;
  wrap.append(el("div", label), area);
  return [wrap, area];
}

function numberInput(value) {
  const input = el("input");
  input.type = "number";
  input.step = "any";
  input.value = value ?? "";
  return input;
}

function textInput(value) {
  const input = el("input");
  input.type = "text";
  input.size = 40;
  input.value = value ?? "";
  return input;
}

function coordinate(text) {
  return text.trim() === "" ? null : Number(text);
}

function card(kind, key, item) {
  const name = item[key];
  const id = `${kind}:${name}`;
  const saved = state.decisions[id];
  const formsKey = kind === "landmark" ? "spokenPatterns" : "pronunciations";
  const box = el("div");
  box.className = "item" + (saved ? " " + saved.status : "");
  box.append(el("div", name));
  box.firstChild.className = "name";
  box.append(contextOf(kind, item));
  const [formsWrap, forms] = field("Forms (first line is what pilots say):", ((saved && saved[formsKey]) || item[formsKey] || []).join("\\n"), 3);
  box.append(formsWrap);
  let stands = null, lat = null, lon = null, display = null;
  if (kind === "fix") {
    display = textInput(saved && "displayName" in saved ? saved.displayName : item.displayName);
    display.className = "display-name";
    box.append("Display name (empty for none) ", display);
  }
  if (kind === "place") {
    const [standsWrap, area] = field("Stands:", ((saved && saved.stands) || item.stands || []).join("\\n"), 2);
    stands = area;
    box.append(standsWrap);
  }
  if (kind === "landmark") {
    lat = numberInput(saved ? saved.lat : item.lat);
    lon = numberInput(saved ? saved.lon : item.lon);
    box.append("lat ", lat, " lon ", lon);
  }
  const why = el("div", item.rationale || "");
  why.className = "rationale";
  box.append(why);
  const status = el("span", saved ? saved.status : "undecided");
  status.className = "status";
  const decide = verdict => {
    const decision = { status: verdict };
    decision[formsKey] = lines(forms.value);
    if (stands) decision.stands = lines(stands.value);
    if (display) decision.displayName = display.value.trim() || null;
    if (lat) { decision.lat = coordinate(lat.value); decision.lon = coordinate(lon.value); }
    state.decisions[id] = decision;
    status.textContent = verdict;
    box.className = "item " + verdict;
  };
  const accept = el("button", "Accept");
  accept.onclick = () => decide("accepted");
  const reject = el("button", "Reject");
  reject.onclick = () => decide("rejected");
  const buttons = el("div");
  buttons.append(accept, " ", reject, status);
  box.append(buttons);
  return box;
}

function section(list, kind, key) {
  const items = state.candidates[list] || [];
  const spelled = items.filter(i => i.verdict === "reads_as_spelled");
  const shown = items.filter(i => i.verdict !== "reads_as_spelled");
  const sec = el("section");
  sec.append(el("h2", `${list} (${shown.length})`));
  for (const item of shown) sec.append(card(kind, key, item));
  if (spelled.length > 0) {
    const details = el("details");
    details.append(el("summary", `${spelled.length} read as spelled`), el("p", spelled.map(i => i[key]).join(", ")));
    sec.append(details);
  }
  return sec;
}

function render() {
  document.getElementById("title").textContent = `Speech lexicon review: ${state.candidates.airport || "?"}`;
  const root = document.getElementById("root");
  root.replaceChildren();
  for (const [list, kind, key] of KINDS) root.append(section(list, kind, key));
}

async function save() {
  const result = document.getElementById("result");
  try {
    const response = await fetch("/api/save", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ decisions: state.decisions }),
    });
    const body = await response.json();
    result.textContent = response.ok ? `saved ${body.count} decisions` : `save failed: ${body.error}`;
  } catch (error) {
    result.textContent = `save failed: ${error}`;
  }
}

async function load() {
  const response = await fetch("/api/state");
  state = await response.json();
  render();
}

document.getElementById("save").onclick = save;
load();
</script>
</body>
</html>
"""


class ReviewHandler(BaseHTTPRequestHandler):
    """Serves the review page, the candidates with the saved decisions, and saves decisions; `root` is set by make_handler."""

    root: Path

    def log_message(self, format: str, *args: Any) -> None:
        status = str(args[1]) if len(args) > 1 else ""
        if status.startswith(("4", "5")):
            super().log_message(format, *args)

    def send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def send_json(self, data: Any, status: int = HTTPStatus.OK) -> None:
        self.send(status, json.dumps(data, ensure_ascii=False).encode("utf-8"), "application/json")

    def decisions_path(self) -> Path:
        return self.root / "decisions.json"

    def do_GET(self) -> None:
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            self.send(HTTPStatus.OK, REVIEW_HTML.encode("utf-8"), "text/html; charset=utf-8")
        elif path == "/api/state":
            decisions = read_json(self.decisions_path()) if self.decisions_path().is_file() else {}
            self.send_json({"candidates": read_json(self.root / "candidates.json"), "decisions": decisions})
        else:
            self.send_json({"error": "not found"}, HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        # The body is read before any refusal: answering without draining it resets the client's connection.
        body = self.read_body()
        if self.path != "/api/save":
            self.send_json({"error": "not found"}, HTTPStatus.NOT_FOUND)
            return
        refusal = self.save_refusal()
        if refusal is not None:
            self.send_json({"error": refusal[1]}, refusal[0])
            return
        decisions = parse_decisions(body)
        if decisions is None:
            self.send_json({"error": "body must be a JSON object {'decisions': {'<kind>:<name>': {...}}}"}, HTTPStatus.BAD_REQUEST)
            return
        write_json(self.decisions_path(), decisions, sort_keys=True)
        self.send_json({"ok": True, "count": len(decisions)})

    def save_refusal(self) -> tuple[int, str] | None:
        """Why a save request is refused: not JSON, or sent from a page other than this server's; None when allowed."""
        content_type = self.headers.get("Content-Type", "").split(";", 1)[0].strip().lower()
        if content_type != "application/json":
            return HTTPStatus.UNSUPPORTED_MEDIA_TYPE, "Content-Type must be application/json"
        origin = self.headers.get("Origin")
        expected = f"http://127.0.0.1:{self.server.server_address[1]}"
        if origin is not None and origin != expected:
            return HTTPStatus.FORBIDDEN, f"origin {origin} is not {expected}"
        return None

    def read_body(self) -> bytes | None:
        """The request body, or None when Content-Length is not a non-negative number."""
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            return None
        return self.rfile.read(length) if length >= 0 else None


def parse_decisions(body: bytes | None) -> dict[str, Any] | None:
    """The body's decisions object, or None when the body is malformed or not shaped {"decisions": {key: {...}}}."""
    try:
        parsed = json.loads(body or b"null")
    except (ValueError, UnicodeDecodeError):
        return None
    decisions = parsed.get("decisions") if isinstance(parsed, dict) else None
    if not isinstance(decisions, dict) or not all(isinstance(value, dict) for value in decisions.values()):
        return None
    return decisions


def make_handler(root: Path) -> type[ReviewHandler]:
    """The review handler bound to a candidates directory."""
    return type("BoundReviewHandler", (ReviewHandler,), {"root": root})


def cmd_review(args: argparse.Namespace) -> int:
    root = Path(args.dir).resolve()
    if not (root / "candidates.json").is_file():
        raise LexiconError(f"{root / 'candidates.json'} not found; run generate first", 2)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(root))
    print(f"review server: http://127.0.0.1:{args.port}/  ({root})", flush=True)
    server.serve_forever()
    return 0


def forms_of(item: dict[str, Any], decision: dict[str, Any], forms_key: str) -> list[str]:
    """The decision's edited forms, else the proposal's, trimmed and without blanks."""
    forms = decision[forms_key] if forms_key in decision else item.get(forms_key, [])
    return [form.strip() for form in forms or [] if form.strip()]


def reviewed(item: dict[str, Any], decision: dict[str, Any], key: str) -> Any:
    """The decision's value for key when the review set one (null included), else the proposal's."""
    return decision[key] if key in decision else item.get(key)


def append_unique(target: list[str], values: list[str]) -> None:
    for value in values:
        if value not in target:
            target.append(value)


def accepted_items(candidates: dict[str, Any], decisions: dict[str, Any]) -> dict[str, list[tuple[dict[str, Any], dict[str, Any]]]]:
    """Per list, the accepted items with their decision; reads_as_spelled items and items left with no forms are never accepted."""
    accepted: dict[str, list[tuple[dict[str, Any], dict[str, Any]]]] = {kind.list_name: [] for kind in KINDS}
    for kind in KINDS:
        for item in candidates.get(kind.list_name, []):
            decision = decisions.get(f"{kind.decision}:{item[kind.key]}")
            if item.get("verdict") == READS_AS_SPELLED or not decision or decision.get("status") != "accepted":
                continue
            if not forms_of(item, decision, kind.forms):
                print(f"skipped {kind.decision} {item[kind.key]}: accepted with no forms")
                continue
            accepted[kind.list_name].append((item, decision))
    return accepted


def upsert(entries: list[dict[str, Any]], key: str, name: str, template: dict[str, Any]) -> tuple[dict[str, Any], bool]:
    """The entry whose key matches name (case ignored), else template appended; with whether it was new."""
    for entry in entries:
        if str(entry.get(key, "")).upper() == name.upper():
            return entry, False
    entries.append(template)
    return template, True


def report(path: Path, added: int, updated: int) -> None:
    print(f"wrote {path}: {added} added, {updated} updated")


def fixes_in_other_files(directory: Path, target: Path) -> dict[str, str]:
    """Fix id -> file name for every fix in the directory's pronunciation files other than target."""
    taken: dict[str, str] = {}
    for path in sorted(directory.glob("*.json")):
        if path.resolve() == target.resolve():
            continue
        for entry in read_json(path) or []:
            taken.setdefault(str(entry.get("fix", "")).upper(), path.name)
    return taken


def apply_fixes(artcc_dir: Path, faa: str, pairs: list[tuple[dict[str, Any], dict[str, Any]]]) -> None:
    directory = artcc_dir / "FixPronunciations"
    target = directory / f"{faa.lower()}-generated.json"
    taken = fixes_in_other_files(directory, target)
    entries: list[dict[str, Any]] = read_json(target) if target.is_file() else []
    added = updated = 0
    for item, decision in pairs:
        fix = str(item["id"]).upper()
        if fix in taken:
            print(f"skipped fix {fix}: already in FixPronunciations/{taken[fix]}")
            continue
        entry, created = upsert(entries, "fix", fix, {"fix": fix, "pronunciations": []})
        append_unique(entry["pronunciations"], forms_of(item, decision, "pronunciations"))
        display_name = str(decision.get("displayName") or "").strip()
        if display_name and "displayName" not in entry:
            entry["displayName"] = display_name
        added, updated = added + created, updated + (not created)
    if added or updated:
        entries.sort(key=lambda entry: str(entry["fix"]))
        write_json(target, entries)
        report(target, added, updated)


def apply_procedures(artcc_dir: Path, artcc: str, pairs: list[tuple[dict[str, Any], dict[str, Any]]]) -> None:
    if not pairs:
        return
    target = artcc_dir / "ProcedurePronunciations" / f"{artcc.lower()}.json"
    entries: list[dict[str, Any]] = read_json(target) if target.is_file() else []
    added = updated = 0
    for item, decision in pairs:
        name = str(item["baseName"]).strip().upper()
        if not name or any(character.isdigit() for character in name):
            print(f"skipped procedure {name!r}: a base name is letters only, without the version digits")
            continue
        entry, created = upsert(entries, "procedure", name, {"procedure": name, "pronunciations": []})
        append_unique(entry["pronunciations"], forms_of(item, decision, "pronunciations"))
        added, updated = added + created, updated + (not created)
    if added or updated:
        write_json(target, entries)
        report(target, added, updated)


def apply_ground(artcc_dir: Path, airport: str, faa: str, accepted: dict[str, list[tuple[dict[str, Any], dict[str, Any]]]]) -> None:
    pairs = [(list_name, item, decision) for list_name in ("gates", "spots", "places") for item, decision in accepted[list_name]]
    if not pairs:
        return
    target = artcc_dir / "GatePronunciations" / f"{faa.lower()}.json"
    document: dict[str, Any] = read_json(target) if target.is_file() else {"airportId": airport}
    added = 0
    for list_name, item, decision in pairs:
        name = str(item["name"]).upper()
        entry, created = upsert(document.setdefault(list_name, []), "name", name, {"name": name, "pronunciations": []})
        append_unique(entry["pronunciations"], forms_of(item, decision, "pronunciations"))
        if list_name == "places":
            stands = forms_of(item, decision, "stands")
            if stands:
                append_unique(entry.setdefault("stands", []), stands)
            if item.get("notes") and not entry.get("notes"):
                entry["notes"] = item["notes"]
        added += created
    write_json(target, document)
    report(target, added, len(pairs) - added)


def apply_landmarks(artcc_dir: Path, faa: str, pairs: list[tuple[dict[str, Any], dict[str, Any]]]) -> None:
    if not pairs:
        return
    target = artcc_dir / "CustomFixes" / f"{faa.lower()}-landmarks.json"
    entries: list[dict[str, Any]] = read_json(target) if target.is_file() else []
    added = updated = 0
    for item, decision in pairs:
        alias = str(item["alias"]).upper()
        lat, lon = reviewed(item, decision, "lat"), reviewed(item, decision, "lon")
        if lat is None or lon is None:
            print(f"skipped landmark {alias}: accepted without a lat/lon")
            continue
        patterns = forms_of(item, decision, "spokenPatterns")
        existing = next((entry for entry in entries if alias in {str(a).upper() for a in entry.get("aliases", [])}), None)
        if existing is None:
            entries.append({"name": item["name"], "aliases": [alias], "spokenPatterns": patterns, "lat": lat, "lon": lon})
            added += 1
            continue
        append_unique(existing.setdefault("spokenPatterns", []), patterns)
        if (existing.get("lat"), existing.get("lon")) != (lat, lon):
            file_position = f"{existing.get('lat')}, {existing.get('lon')}"
            print(f"warning: landmark {alias}: reviewed lat/lon {lat}, {lon} differs from the file's {file_position}; kept the file's")
        updated += 1
    if added or updated:
        write_json(target, entries)
        report(target, added, updated)


def cmd_apply(args: argparse.Namespace) -> int:
    root = Path(args.dir)
    decisions_path = root / "decisions.json"
    candidates_path = root / "candidates.json"
    if not decisions_path.is_file():
        raise LexiconError(f"{decisions_path} not found; review the candidates and Save first", 2)
    if not candidates_path.is_file():
        raise LexiconError(f"{candidates_path} not found; run generate first", 2)
    candidates = read_json(candidates_path)
    accepted = accepted_items(candidates, read_json(decisions_path))
    artcc_dir = Path(args.repo) / "src" / "Yaat.Sim" / "Data" / "ARTCCs" / args.artcc
    if not artcc_dir.is_dir():
        raise LexiconError(f"{artcc_dir} not found; check --repo and --artcc", 2)
    airport = str(candidates["airport"]).upper()
    faa = faa_code(airport)
    apply_fixes(artcc_dir, faa, accepted["fixes"])
    apply_procedures(artcc_dir, args.artcc, accepted["procedures"])
    apply_ground(artcc_dir, airport, faa, accepted)
    apply_landmarks(artcc_dir, faa, accepted["landmarks"])
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    generate = commands.add_parser("generate", help="ask the model for spoken forms of every input item")
    generate.add_argument("--inputs", required=True, help="the sandbox's --lexicon-inputs JSON")
    generate.add_argument("--out", required=True, help="directory for candidates.json")
    generate.add_argument("--fresh", action="store_true", help="start over instead of resuming the batches already in candidates.json")
    review = commands.add_parser("review", help="serve the review page for a candidates directory")
    review.add_argument("--dir", required=True)
    review.add_argument("--port", type=int, default=8766)
    apply = commands.add_parser("apply", help="merge the accepted decisions into the ARTCC data files")
    apply.add_argument("--dir", required=True)
    apply.add_argument("--repo", required=True, help="yaat repo root")
    apply.add_argument("--artcc", required=True, help="ARTCC folder under src/Yaat.Sim/Data/ARTCCs, e.g. ZOA")
    return parser.parse_args(argv)


def main(argv: list[str], client_factory: Callable[[], Any]) -> int:
    """Runs one subcommand; client_factory builds the Anthropic client for generate."""
    args = parse_args(argv)
    try:
        if args.command == "generate":
            return cmd_generate(args, client_factory)
        if args.command == "review":
            return cmd_review(args)
        return cmd_apply(args)
    except LexiconError as error:
        print(f"error: {error}", file=sys.stderr)
        return error.exit_code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:], anthropic.Anthropic))
