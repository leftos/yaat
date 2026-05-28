# Fillet V2 — Consensus Interface Proposal (Cursor)

**Status:** Team agreement draft — May 2026
**Supersedes interface sections in:** [`cursor-proposal.md`](./cursor-proposal.md), [`codex-proposal.md`](./codex-proposal.md), [`claude-proposal.md`](./claude-proposal.md)
**Synthesized from:** [`cursor-interface-review.md`](./cursor-interface-review.md), [`codex-interface-review.md`](./codex-interface-review.md), [`claude-interface-review.md`](./claude-interface-review.md)

---

## Purpose

Three agent proposals and three interface reviews agree on the goal: a **clean-room fillet generator** running **side-by-side** with legacy until parity, then **delete legacy** and keep a small pluggable surface for future variants.

This document is the **single interface + comparison contract** the team should implement first. V2 planner/executor algorithms remain in the individual proposals; only wiring and parity gates are fixed here.

---

## One-sentence agreement

Use **Cursor’s minimal public interface and comparison plumbing**, **Claude/Codex `FilletMode` + factory at the parser boundary**, **no optional parameters or wrapped return types in v1**, and **Codex-style geometry/behavior diff as the parity bar** — then build V2 behind the same `Apply` everyone tests.

---

## What we all agree on

1. **`public IFilletArcGenerator`** with a legacy adapter and `FilletArcGeneratorV2`.
2. **Same migration shape as pathfinding** — V1/V2 on identical input, compare, switch default, delete old code (see [`ITaxiPathfinder`](../../src/Yaat.Sim/Data/Airport/ITaxiPathfinder.cs), [`TaxiPathfinderRouter`](../../src/Yaat.Sim/Data/Airport/TaxiPathfinderRouter.cs), [`PathfinderComparison`](../../tests/Yaat.Sim.Tests/Helpers/PathfinderComparison.cs)).
3. **`Apply` mutates `AirportGroundLayout` in place** — comparison harness **deep-clones** a pre-fillet layout per implementation.
4. **Do not compare node IDs** — parity uses rounded geometry signatures and behavior probes (exits, hold-shorts, known regressions).
5. **`FilletStatistics`** is the shared scorecard across implementations.
6. **`FilletMode { None, Legacy, V2 }`** at parser/inspector boundaries; **default `Legacy`** until parity sign-off.
7. **Registry + `FilletComparison`** helper for tests and LayoutInspector.
8. **Delete static `FilletArcGenerator` after acceptance** — keep `IFilletArcGenerator` for future experiments (project rule: no permanent dual production, no shims).

---

## Resolved disagreements

| Topic | Resolution |
|-------|------------|
| Interface members | **`Id` + `DisplayName` + `Apply(layout)` only** — no `Mode` on interface, no `FilletOptions` (violates no-optional-parameters rule in `CLAUDE.md`). |
| Return type | **`FilletStatistics` directly in v1** — no `FilletGenerationResult` wrapper. Structured diagnostics deferred (see [Out of v1](#explicitly-out-of-v1)). |
| Selection | **`FilletGeneratorFactory.Create(FilletMode)` is canonical**; **`FilletArcGeneratorRouter`** delegates to factory (`Current`, `UseV2`) for pathfinder parity. |
| `NullFilletArcGenerator` | **Include** — factory maps `FilletMode.None`; enables `--fillet=none` and symmetric registry. |
| Arc count parity | **±5%** per airport unless recorded in [`v2-divergences.md`](./v2-divergences.md). |
| Naming | **`FilletMode.Legacy`** (not Codex `Current`); **`FilletArcGeneratorV2`** (matches `TaxiPathfinderV2`). |

---

## Fillet interface v1 (canonical)

### Public contract

File: `src/Yaat.Sim/Data/Airport/IFilletArcGenerator.cs`

```csharp
namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Fillet arc generation on an airport ground layout. Implementations must be
/// stateless and may be called concurrently against different layout instances.
/// Mutates <paramref name="layout"/> in place. Callers comparing implementations
/// must pass independent clones — see <see cref="FilletComparison"/>.
/// </summary>
public interface IFilletArcGenerator
{
    /// <summary>Stable machine id: "none", "legacy", "v2".</summary>
    string Id { get; }

    /// <summary>Human-readable label for test output and LayoutInspector.</summary>
    string DisplayName { get; }

    /// <summary>Apply fillet arcs to eligible intersections; return per-pass tallies.</summary>
    FilletStatistics Apply(AirportGroundLayout layout);
}
```

Move **`FilletStatistics`** to `src/Yaat.Sim/Data/Airport/Fillet/FilletStatistics.cs` unchanged (today at [`FilletArcGenerator.cs`](../../src/Yaat.Sim/Data/Airport/FilletArcGenerator.cs) lines 10–20). Legacy static class delegates through adapter until deleted.

### Selection (outside the interface)

```csharp
public enum FilletMode
{
    None,
    Legacy,
    V2,
}

public static class FilletGeneratorFactory
{
    public static IFilletArcGenerator Create(FilletMode mode) =>
        mode switch
        {
            FilletMode.None => NullFilletArcGenerator.Instance,
            FilletMode.Legacy => new LegacyFilletArcGenerator(),
            FilletMode.V2 => new FilletArcGeneratorV2(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
}

public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } =
        [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()];
}

public static class FilletArcGeneratorRouter
{
    /// <summary>Active implementation. Default: Legacy. Not thread-safe across concurrent assignment.</summary>
    public static IFilletArcGenerator Current { get; set; } = FilletGeneratorFactory.Create(FilletMode.Legacy);

    public static bool UseV2
    {
        set => Current = FilletGeneratorFactory.Create(value ? FilletMode.V2 : FilletMode.Legacy);
    }
}
```

### Implementations

| `Id` | Type | Role |
|------|------|------|
| `none` | `NullFilletArcGenerator` | No-op; `FilletStatistics.Empty` |
| `legacy` | `LegacyFilletArcGenerator` | Wraps existing `FilletArcGenerator.Apply` |
| `v2` | `FilletArcGeneratorV2` | Clean-room plan-then-execute (built after interface step) |

V2 internal modules live under `src/Yaat.Sim/Data/Airport/Fillet/V2/` per [`claude-proposal.md`](./claude-proposal.md). Algorithm choice (arms, one tangent per arm, etc.) is **not** gated by this consensus doc.

### Parser and tooling

| Entry point | Behavior |
|-------------|----------|
| `GeoJsonParser.Parse(..., FilletMode mode)` | **New** overload; calls `FilletGeneratorFactory.Create(mode).Apply(layout)` |
| `GeoJsonParser.Parse(..., bool applyFillets)` | **Keep** during migration: `false` → `None`, `true` → `Legacy` |
| `GeoJsonParser` production default | `FilletMode.Legacy` until parity |
| LayoutInspector `--fillet=none\|legacy\|v2` | `FilletGeneratorFactory.Create` |
| LayoutInspector `--fillet-diff <airport>` | `FilletComparison.Compare` → `.tmp/` JSON + `FormatReport` |
| Env `YAAT_FILLET_V2=1` (optional) | Sets `FilletArcGeneratorRouter.UseV2 = true` at startup |

---

## Comparison harness (required before default flip)

Files:

- `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs` (or `AirportGroundLayout.DeepClone`)
- `tests/Yaat.Sim.Tests/Helpers/FilletComparison.cs` (mirror [`PathfinderComparison`](../../tests/Yaat.Sim.Tests/Helpers/PathfinderComparison.cs))
- `tests/Yaat.Sim.Tests/Fillet/FilletParityTests.cs`
- `docs/plans/filletv2/v2-divergences.md` (triage log)

### Workflow

1. Build **one unfilleted** layout per airport (`FilletMode.None` or `applyFillets: false`).
2. `FilletComparison.Compare(preFillet, [legacy, v2])` — clone per generator, apply, diff.
3. Report by generator **`Id`**, never by CLR type name.

### Parity gates

| Gate | Rule |
|------|------|
| Node references | No missing nodes, self-loops, zero-length edges (all generators) |
| Node counts by `GroundNodeType` | Exact match Legacy vs V2 unless logged in divergences |
| Arc count | Within **±5%** per airport unless logged |
| Corner geometry | Per corner bucket `(region, taxiway-pair, bearings ~5°)`: min radius within **±10%** |
| Connectivity | BFS parking → hold-short reachability matches |
| Runway centerline | Bearing within **±1°** (legacy displacement check) |
| V2 cleanup counters | `OrphansRescued`, `DuplicateCornerArcsRemoved`, `ParallelBypassEdgesRemoved`, `DirectShortensAdded`, etc. **== 0** on V2 (structural claim from clean-room design) |
| Behavior probes | Known OAK/SFO/FLL/ZOA regressions from existing fillet/ground tests |

### Triage (every mismatch)

Classify in `v2-divergences.md` as:

- **Accepted improvement** — V2 strictly better; do not replicate legacy quirk.
- **V2 bug** — fix planner/executor.
- **Legacy-only quirk** — document; do not replicate in V2.

---

## Implementation order

Each step ends with `dotnet build -p:TreatWarningsAsErrors=true` and targeted tests green.

| Step | Deliverable | Behavior change |
|------|-------------|-----------------|
| **1. interface** | `IFilletArcGenerator`, factory, registry, router, null + legacy adapters, `FilletStatistics` move, `GeoJsonParser` wired | **None** — still Legacy |
| **2. comparison** | `LayoutCloner`, `FilletComparison`, parity test skeleton (OAK/SFO) | **None** |
| **3. v2-impl** | `FilletArcGeneratorV2` + planner/executor under `Fillet/V2/` | V2 runnable via mode/registry |
| **4. parity** | Full airport matrix, divergences doc, LayoutInspector `--fillet-diff` | Decision data |
| **5. switch** | Default `FilletMode.V2`, aviation realism review, delete `FilletArcGenerator.cs` + legacy adapter | Production on V2 |

---

## Explicitly out of v1

Do not block the interface step on these; file follow-ups if needed.

- `FilletOptions` / optional `Apply` parameters
- `FilletGenerationResult` wrapper type
- `FilletMode` property on `IFilletArcGenerator`
- `IFilletDiagnosticCollector` or `FilletStatistics.Warnings` (add when harness has concrete diagnostic codes)
- `FilletPlan` / `FilletPlanBuilder` on the shared interface (V2-only debug API)
- Permanent dual production generators

---

## Pre-baked responses to likely objections

| Objection | Response |
|-----------|----------|
| “We need diagnostics on `Apply` now.” | Use `ILogger` + existing warnings for v1; add structured list to `FilletStatistics` in v1.1 when codes are defined. |
| “Put `FilletMode` on the interface.” | Factory owns mode mapping; interface stays open to a fourth implementation without enum churn. |
| “Skip `NullFilletArcGenerator`.” | Trivial factory branch; keeps inspector and registry consistent. |
| “Router duplicates factory.” | Router is optional sugar matching `TaxiPathfinderRouter`; one-line `Current = Factory.Create(...)`. |
| “Strict ±0 arc count.” | Too brittle; ±5% + divergences doc matches real dedup differences. |

---

## References

| Doc | Role |
|-----|------|
| [`claude-proposal.md`](./claude-proposal.md) | V2 algorithm (arms, planner, executor) |
| [`cursor-proposal.md`](./cursor-proposal.md) | Original plan-then-execute + comparison sketch |
| [`codex-proposal.md`](./codex-proposal.md) | Acceptance criteria and checklist tone |
| [`cursor-interface-review.md`](./cursor-interface-review.md) | Three-way interface comparison |
| [`docs/ground-layout-generation.md`](../ground-layout-generation.md) | Legacy fillet behavior reference |
