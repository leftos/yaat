---
name: dotnet-test-suite-profiling
description: "Profile the YAAT .NET test suite with dotnet-trace: collect a sampled CPU trace of the MTP test executable, rank threads by CPU (not wall) and find the hot leaf frames, then attribute allocations to the nearest Yaat.* frame with a gc-verbose trace. Ships both aggregation scripts. Use when the user says 'profile the test suite', 'why is Sim.Tests slow', 'the tests take too long', 'where is the test time going', or asks to speed up the tests — and before optimising anything in a test, since the guardrails on what may be trimmed live here."
---

# Profiling the .NET Test Suite

Two measurements answer almost every "why is the suite slow" question: where
CPU goes, and where allocations come from. Both are collected with
`dotnet-trace` against the test **executable** (the Microsoft.Testing.Platform
runner produces one), and both need an aggregation step, because the raw views
are misleading in a specific way documented below.

`dotnet-trace` may not be installed:

```bash
dotnet tool install --global dotnet-trace
```

## Guardrails — read before changing any test

These are checklist items, not advice. They are the parts most likely to be
dropped when the method is re-derived from a paragraph.

- [ ] **Re-profile before optimising.** A finding from a previous pass describes
      a tree that has since moved. Collect first, then decide.
- [ ] **Never trim a fixed-budget replay loop that asserts an invariant over its
      window.** `Replay(recording, N)` loops look like padding and are not: the
      window is the assertion. Every costly loop in this suite has been audited
      and found to be a window invariant. Shortening one silently deletes
      coverage while the test still passes.
- [ ] **Wins come from per-test CPU, not parallelism.** xUnit already saturates
      the cores; adding threads moves nothing.

## Step 1: Collect a CPU trace

Point `dotnet-trace` at the test executable and pass the MTP runner's own
filter flags after it. **Use an absolute path to the executable** — a relative
path fails with `An error occurred trying to start process … The system cannot
find the file specified` and exits 3.

```bash
cd X:/dev/yaat
mkdir -p .tmp/prof
dotnet-trace collect --format Speedscope -o .tmp/prof/suite.nettrace \
  -- X:/dev/yaat/tests/Yaat.Sim.Tests/bin/Release/net10.0/Yaat.Sim.Tests.exe \
     --filter-class "*.<ClassName>"
```

`--format Speedscope` writes `suite.speedscope.json` beside the `.nettrace`. If
you already have a `.nettrace`, convert it instead:

```bash
dotnet-trace convert --format speedscope -o .tmp/prof/converted .tmp/prof/suite.nettrace
```

Profile the **Release** build (`bin/Release/net10.0/`); Debug inlining and
assertions distort the tree. Narrow with `--filter-class`/`--filter-method`
first — a whole-suite trace is large and slow to aggregate, and the per-test
shape is what you are after anyway.

## Step 2: Rank threads by CPU, not wall

Speedscope's own topN is drowned by idle thread-pool waits: those threads span
the entire run in wall time while doing nothing. **Pick the thread with the most
CPU, never the most wall time.**

```bash
python .claude/skills/dotnet-test-suite-profiling/scripts/speedscope_cpu.py \
  .tmp/prof/suite.speedscope.json --top 20
```

The script walks each thread's open/close events, keeps only intervals whose
leaf frame is the sample profiler's `CPU_TIME` marker, and attributes each one
to the real frame directly beneath it. It prints threads ranked by CPU with a
`cpu/wall` ratio, then the busiest thread's hottest self-CPU frames.

The ratio column is the point. A real run looked like this — the top-CPU thread
is not the top-wall thread, and the difference is an order of magnitude:

```
thread                                               cpu        wall  cpu/wall
Thread (13028)                                    1463.7      1938.9   75.5%
Thread (63684) (.NET ThreadPool)                  1002.6      2514.5   39.9%
Thread (40064)                                      91.8      2562.5    3.6%
```

Options: `--threads N` (rows in the ranking), `--thread SUBSTRING` (inspect a
specific thread instead of the busiest), `--marker` (if the profiler's marker
frame is ever renamed).

## Step 3: Attribute allocations

Allocation ticks carry a call stack whose leaf is almost always a BCL or runtime
method, so charging the leaf tells you nothing actionable. Collect with the
`gc-verbose` profile, then attribute each tick to the nearest `Yaat.*` frame:

```bash
dotnet-trace collect --profile gc-verbose -o .tmp/prof/gc.nettrace \
  -- X:/dev/yaat/tests/Yaat.Sim.Tests/bin/Release/net10.0/Yaat.Sim.Tests.exe \
     --filter-class "*.<ClassName>"

tools/gate.sh .tmp/prof/alloc.log dotnet run .claude/skills/dotnet-test-suite-profiling/scripts/alloc_ticks.cs \
  -- .tmp/prof/gc.nettrace --top 25
```

(`dotnet run` goes through `tools/gate.sh` because CLAUDE.md requires every
dotnet invocation to be teed to `.tmp/`, and a bare `… | tee` would report the
pipeline's status instead of the command's.)

`alloc_ticks.cs` is a .NET 10 file-based app (its `#:package` line pulls
TraceEvent on first run; no project file, no `bin/` in the repo). It reads
`GCAllocationTick` events, walks each `CallStack()` outward from the leaf to the
first frame matching `--prefix` (default `Yaat.`), and sums the sampled bytes
there. Output on a real run:

```
7,450 allocation ticks, 1,149.7 MB sampled
197.4 MB had no 'Yaat.' frame on the stack (runtime/test-host allocations)

     248.5 MB   1,255 ticks  Yaat.Sim.Data.Airspace.AirspaceDatabase.ReadGeoJsonText(class System.String)
     116.5 MB   1,097 ticks  Yaat.Sim.Data.Airspace.AirspaceDatabase.ParsePolygon(...)
```

**`GCAllocationTick` fires roughly once per 100 KB allocated.** The byte totals
are a sampled estimate — rank by them, do not quote them as exact. A frame with
few ticks and large bytes (one big array) is a different problem from one with
many ticks and the same total (a hot allocation in a loop); the tick column is
what separates them.

## Step 4: Report and act

Write findings — with the numbers and the trace paths — to
`docs/plans/test-suite-speed.md` rather than to memory: they are a snapshot of a
tree that moves, and memory is for durable rules.

Then, before you change a test, re-read the guardrails at the top of this file.
The invariant-window rule is the one that gets violated, because a costly loop
looks exactly like a wasteful one.

## Where the output goes

Everything lands in `.tmp/prof/` (untracked). Traces are large — a two-second
single-class run produced a 7 MB `.nettrace` and a 1.5 MB speedscope file, and a
whole-suite trace scales from there. Delete them when the pass is done; keep the
numbers in the plan doc.
