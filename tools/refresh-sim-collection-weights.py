"""Rebuild `tests/Yaat.Sim.Tests/collection-weights.json` from an xunit TRX report.

The Sim test assembly's collection orderer (`CollectionWeightOrderer`) starts the heaviest test
collections first, so the parallel run's slow tail shrinks. Its weight table is this JSON, keyed by
collection: a `[Collection("X")]` class keys on `X`, every other class is its own collection and
keys on its namespace-qualified name - exactly what `CollectionWeightOrderer` derives at run time.

Refresh it from a full Sim run, started from the repo root. The gate may route this command to the
Windows or the Mac builder; either brings the TRX back to this PC, at
`tests/Yaat.Sim.Tests/bin/Release/net10.0/TestResults/<name>.trx`:

    pwsh tools/gate.ps1 -Log .tmp/sim.trx.log -TimeoutSeconds 1500 -Slot heavy -- \\
        dotnet run --project tests/Yaat.Sim.Tests -c Release -- \\
        --filter-not-trait "Category=Nightly" --filter-not-trait "Category=PathfinderGrid" \\
        --report-xunit-trx --report-xunit-trx-filename sim.trx

then pass that path:

    uv run --no-project python tools/refresh-sim-collection-weights.py \\
        tests/Yaat.Sim.Tests/bin/Release/net10.0/TestResults/sim.trx

TRX durations are contended (every test ran next to the others in its run's lane count) and are used
only for ranking. The table's `source` line records the machine and lanes that produced the TRX;
a run the gate routed to the builder passes them (`--machine builder --lanes 64`) so it does not
name this PC.
"""

import argparse
import importlib.util
import json
import os
import platform
import re
import subprocess
import sys
from datetime import UTC, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TEST_PROJECT = ROOT / "tests" / "Yaat.Sim.Tests"
TABLE = TEST_PROJECT / "collection-weights.json"

# Anchored at the line so a doc comment's "class that ..." is not read as a declaration.
MODIFIERS = r"(?:(?:public|internal|private|protected|static|sealed|abstract|partial|file|unsafe|new)\s+)*"
CLASS_DECL = re.compile(rf"^[ \t]*{MODIFIERS}class\s+(\w+)", re.MULTILINE)
NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)\s*[;{]", re.MULTILINE)
COLLECTION_ATTR = re.compile(r'\[Collection\(\s*(?:"([^"]+)"|([\w.]+))\s*\)\]')
CLASS_CONST_NAME = re.compile(rf'^[ \t]*{MODIFIERS}class\s+(\w+)[\s\S]{{0,2000}}?const\s+string\s+Name\s*=\s*"([^"]+)"', re.MULTILINE)


def source_files():
    """Every C# file of the Sim test project, excluding build output."""
    return sorted(path for path in TEST_PROJECT.rglob("*.cs") if not {"bin", "obj"} & set(path.parts))


def named_constants():
    """{class name: its `const string Name = "..."`} - how a collection name is written as a type."""
    constants = {}
    for path in source_files():
        constants.update(CLASS_CONST_NAME.findall(path.read_text(encoding="utf-8")))
    return constants


def collection_name(segment, constants):
    """The `[Collection]` name in `segment`, or None when the class has no such attribute."""
    for literal, identifier in reversed(COLLECTION_ATTR.findall(segment)):
        if literal:
            return literal
        for candidate in (identifier, identifier.split(".", 1)[0]):  # `X.Name` or `X`
            if candidate in constants:
                return constants[candidate]
        raise SystemExit(f'cannot resolve the collection constant {identifier}: no `const string Name = "..."` found for it')
    return None


def collection_keys():
    """Map every test class's full name to its collection key, reading the attribute from source."""
    constants = named_constants()
    keys = {}
    for path in source_files():
        text = path.read_text(encoding="utf-8")
        match = NAMESPACE.search(text)
        prefix = f"{match.group(1)}." if match else ""
        classes = list(CLASS_DECL.finditer(text))
        for index, declaration in enumerate(classes):
            # The attribute sits between the previous class declaration and this one.
            start = classes[index - 1].end() if index else 0
            full_name = prefix + declaration.group(1)
            keys[full_name] = collection_name(text[start : declaration.start()], constants) or full_name
    return keys


def load_schedule():
    """`tools/analyze-test-schedule.py` as a module (its filename is not importable as-is)."""
    path = ROOT / "tools" / "analyze-test-schedule.py"
    spec = importlib.util.spec_from_file_location("analyze_test_schedule", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def git(*arguments):
    """stdout of a git command run in the repo root; a failing command ends the script."""
    try:
        result = subprocess.run(["git", "-C", str(ROOT), *arguments], capture_output=True, text=True, check=True)
    except subprocess.CalledProcessError as error:
        raise SystemExit(f"git {' '.join(arguments)} failed in {ROOT}: {error.stderr.strip()}") from error
    return result.stdout.strip()


def build_table(trx):
    """{collection key: whole seconds} summed over the TRX's classes."""
    schedule = load_schedule()
    keys = collection_keys()
    totals = {}
    for class_name, seconds in schedule.class_totals(schedule.read_tests(trx)).items():
        key = keys.get(class_name)
        if key is None:
            print(f"warning: {class_name} is not declared in {TEST_PROJECT}; keyed by its class name", file=sys.stderr)
            key = class_name
        totals[key] = totals.get(key, 0.0) + seconds
    return {key: round(seconds) for key, seconds in totals.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("trx", help="TRX from a full Sim run (the command is in this module's docstring)")
    parser.add_argument("--machine", default=platform.node(), help="the machine that produced the TRX, when it was not this one")
    parser.add_argument("--lanes", type=int, help="the parallel lanes that produced the TRX (default: this machine's CPU count)")
    args = parser.parse_args()

    lanes = args.lanes if args.lanes is not None else os.cpu_count()
    if not lanes:
        raise SystemExit("--lanes is required here: this machine reports no CPU count, so pass the lane count that produced the TRX")

    table = build_table(Path(args.trx))
    ordered = dict(sorted(table.items(), key=lambda item: (-item[1], item[0])))
    payload = {
        "generated": datetime.now(tz=UTC).date().isoformat(),
        "source": f"{Path(args.trx).name}, {args.machine}, {lanes} lanes, {git('rev-parse', '--short', 'HEAD')}",
        "collections": ordered,
    }
    TABLE.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {TABLE.relative_to(ROOT)}: {len(ordered)} collections, {sum(ordered.values())} s total")
    for key, seconds in list(ordered.items())[:10]:
        print(f"  {seconds:6d}s  {key}")


if __name__ == "__main__":
    main()
