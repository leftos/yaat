# Collect and aggregate — run inside a subagent

This file is the worker half of the `dotnet-test-suite-profiling` skill. The
dispatching session keeps the guardrails and decides what to change; you collect
the traces, run both aggregation scripts, and return the ranked tables. Do not
edit any test or source file.

Paths below assume the repo root is your working directory; confirm with
`git rev-parse --show-toplevel` first and prefix every path with it.

## Step A: Collect a CPU trace

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

## Step B: Rank threads by CPU, not wall

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

## Step C: Attribute allocations

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

## Step D: Return

Reply with, and only with:

1. The thread ranking table and the busiest thread's hot-frame table from Step B.
2. The allocation table from Step C, with the tick and byte columns.
3. The absolute paths of the `.nettrace` and `.speedscope.json` files produced.
4. One line per anomaly you hit (a tool that was not installed, a filter that
   matched nothing, a run that did not reach steady state).

No interpretation and no recommendations; the dispatching session applies the
guardrails and decides.

## Where the output goes

Everything lands in `.tmp/prof/` (untracked). Traces are large — a two-second
single-class run produced a 7 MB `.nettrace` and a 1.5 MB speedscope file, and a
whole-suite trace scales from there. Delete them when the pass is done; keep the
numbers in the plan doc.
