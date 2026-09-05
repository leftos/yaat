---
name: dotnet-test-suite-profiling
description: "Profile the YAAT .NET test suite with dotnet-trace: collect a sampled CPU trace of the MTP test executable, rank threads by CPU (not wall) and find the hot leaf frames, then attribute allocations to the nearest Yaat.* frame with a gc-verbose trace. Ships both aggregation scripts. Use when the user says 'profile the test suite', 'why is Sim.Tests slow', 'the tests take too long', 'where is the test time going', or asks to speed up the tests — and before optimising anything in a test, since the guardrails on what may be trimmed live here."
---

# Profiling the .NET Test Suite

Two measurements answer almost every "why is the suite slow" question: where
CPU goes, and where allocations come from. Both are collected with
`dotnet-trace` against the test **executable** (the Microsoft.Testing.Platform
runner produces one), and both need an aggregation step, because the raw views
are misleading in a specific way documented in `collect.md`.

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
- [ ] **Wins come from per-test CPU, not parallelism.** Measured 2026-09-04:
      811 s of test-work over 16 workers has a floor of 50.7 s and the suite runs
      in 57.2 s — **~89% scheduling efficiency**. `tools/analyze-test-schedule.py`
      LPT-packs a TRX under both a per-class and a per-test scheduler and finds
      **no difference**, so neither more threads nor a parallel-by-default
      framework can move this. Re-measure before doubting it — but do not
      re-derive the conclusion from CPU/wall (next bullet).
- [ ] **CPU/wall is not core utilisation, and not scheduling efficiency.** It
      measures how CPU-bound the tests are. It fell from 14.8× to 8.6× of 16
      cores over 2026-08/09 while the schedule stayed just as full, because the
      optimisations removed CPU work (−64%) faster than wall time (−38%).
      Reading that drop as "41% of the machine is idle" nearly reversed a
      recommendation. To ask whether the *scheduler* is leaving capacity on the
      table, extract per-test durations from a TRX and simulate the packing with
      `tools/analyze-test-schedule.py` — never infer it from a ratio.

## Step 1: Collect and aggregate in a subagent

Trace collection and the two aggregation passes produce output that the main
session only needs as ranked tables, so they run in a forked context. Dispatch
one `general-purpose` agent with a prompt of this shape:

> Read `.claude/skills/dotnet-test-suite-profiling/collect.md` and follow it end
> to end for the test filter `--filter-class "*.<ClassName>"` in the repo at
> `<absolute repo root>`. Return exactly what its Step D asks for.

Pass the class or method filter, never "the whole suite": a whole-suite trace is
large and slow to aggregate, and the per-test shape is what you are after
anyway. If the agent reports `dotnet-trace` missing, install it
(`dotnet tool install --global dotnet-trace`) and re-dispatch.

`collect.md` carries the mechanics: absolute executable path (a relative one
fails with exit 3), Release build only, CPU-not-wall thread ranking via
`scripts/speedscope_cpu.py`, and allocation attribution to the nearest `Yaat.*`
frame via `scripts/alloc_ticks.cs` (a sampled estimate, roughly one tick per
100 KB; rank by it, do not quote it as exact).

## Step 2: Read the tables the agent returned

The ratio column in the thread ranking is the point: the top-CPU thread is not
the top-wall thread, often by an order of magnitude, because idle thread-pool
waits span the whole run doing nothing. Work from the busiest thread's self-CPU
frames and from the allocation rows with the most ticks.

## Step 3: Report and act

Write findings — with the numbers and the trace paths — to
`docs/plans/test-suite-speed.md` rather than to memory: they are a snapshot of a
tree that moves, and memory is for durable rules.

Then, before you change a test, re-read the guardrails at the top of this file.
The invariant-window rule is the one that gets violated, because a costly loop
looks exactly like a wasteful one.

## Where the output goes

Everything lands in `.tmp/prof/` (untracked) and is described in `collect.md`.
Delete the traces when the pass is done; keep the numbers in the plan doc.
