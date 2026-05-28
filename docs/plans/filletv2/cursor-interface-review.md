# Fillet V2 — Interface Review (Cursor)

**Date:** May 2026
**Scope:** `IFilletArcGenerator` and side-by-side comparison plumbing only (not V2 planner/executor algorithms)
**Proposals compared:** [`cursor-proposal.md`](./cursor-proposal.md), [`codex-proposal.md`](./codex-proposal.md), [`claude-proposal.md`](./claude-proposal.md)

---

## Summary

Three agent proposals agree on a shared **`IFilletArcGenerator`** contract with a legacy adapter and a clean-room implementation. They diverge on **how you select an implementation**, **what `Apply` returns**, and **how comparison tests are wired**.

**Verdict:** **Claude’s interface is the best baseline** — `FilletMode`, `FilletOptions`, factory-only selection, `NullFilletArcGenerator`, and a stateless contract. **Cursor** adds the best comparison/registry ergonomics. **Codex** adds the best structured return type for diagnostics. The canonical implementation spec should **merge Claude + Cursor + Codex** (see [Recommended merge](#recommended-merge-canonical-interface)).

---

## Three interface designs (side by side)

| | **Claude** | **Cursor** | **Codex** |
|---|------------|------------|-----------|
| Visibility | `public` in `Fillet/` namespace | `public` at `Airport/` root | `internal` |
| Identity | `Name` + `Mode` (`FilletMode`) | `Id` + `DisplayName` | `Name` only |
| `Apply` signature | `(layout, FilletOptions = default)` | `(layout)` | `(layout)` |
| Return type | `FilletStatistics` (moved to shared file) | `FilletStatistics` (existing) | `FilletGenerationResult` + `FilletDiagnostic[]` |
| Selection | `FilletGeneratorFactory.Create(FilletMode)` | `FilletArcGeneratorRouter.Current` + `Registry` | `FilletGenerationMode` + factory |
| Skip fillet | `NullFilletArcGenerator.Instance` + `FilletMode.None` | Not first-class (bool on parser only) | `None` in enum |
| Comparison wiring | `AllGenerators()` in tests + `FilletParityTests` | `FilletComparison` + `Registry.All` | Geometry signatures + behavior probes (doc only) |
| Impl constraints | **Stateless** — concurrent `Apply` on different layouts | Not stated | Not stated |

---

## Claude — interface strengths

### 1. `FilletMode` is the right parser-level API

`GeoJsonParser.Parse(..., FilletMode mode)` with `None | Legacy | V2` maps cleanly to today’s `applyFillets: false` → `None`. Clearer than a `UseV2` bool plus env var (Cursor), and matches Codex’s mode intent without naming drift (`Current` vs `Legacy`).

### 2. `FilletOptions` is the right extension point

```csharp
FilletOptions(EmitDebugLogs, EmitValidationWarnings, FailFastOnOrphans)
```

Shared across implementations; legacy ignores `FailFastOnOrphans`, V2 honors it. Avoids growing `Apply` or relying only on global log levels. Codex-style structured diagnostics belong **in addition to** this, not instead of it.

### 3. Factory as the only selector

`FilletGeneratorFactory.Create(mode)` is a single choke point — parser, LayoutInspector `--fillet=legacy|v2|none`, and tests all dispatch the same way. Less machinery than Cursor’s router **plus** registry (two selection paths). For 2–3 implementations, factory-only is sufficient.

### 4. `NullFilletArcGenerator`

First-class “no fillet” without special-casing the parser. Cursor and Codex mention `None` but only Claude defines a real `IFilletArcGenerator` for it.

### 5. Contract details the others omit

- **Stateless implementations** — important when parity tests clone layouts and may run generators in parallel.
- **`Mode` on the interface** — redundant with `Name` but useful for exhaustiveness and logging without string parsing.
- **Shared `FilletStatistics` with V2 zeroing legacy-only counters** — makes structural claims testable (`OrphansRescued == 0`, `DuplicateCornerArcsRemoved == 0`, etc.).

### 6. Harness shape

`AllGenerators()` × airports for structural invariants, plus dedicated `FilletParity_*` tests that build Legacy vs V2 from the same raw layout — implementation-agnostic, aligned with existing pathfinder comparison patterns.

---

## Claude — interface gaps

| Gap | Who does it better |
|-----|-------------------|
| No structured diagnostics in return type | **Codex** — `FilletGenerationResult` + `FilletDiagnostic` |
| No reusable `FilletComparison` helper | **Cursor** — `Compare` + `FormatReport` (like `PathfinderComparison`) |
| No registry for enumerating impls | **Cursor** — `FilletArcGeneratorRegistry.All` |
| `Name` only (no separate display label) | **Cursor** — `Id` + `DisplayName` for CLI vs reports |
| No runtime `Current` without re-parse | **Cursor** — `FilletArcGeneratorRouter` (optional; matches `TaxiPathfinderRouter`) |
| Edge/arc signature diff by rounded geometry | **Codex** — primary acceptance surface when node IDs differ |

---

## Cursor proposal — interface strengths

- Mirrors **`ITaxiPathfinder` / `TaxiPathfinderRouter` / `PathfinderComparison`** — lowest learning curve for this repo.
- **`Id` + `DisplayName`** — machine id for `--fillet v2` vs human-readable diff reports.
- **`FilletArcGeneratorRegistry.All`** — one list for LayoutInspector and multi-impl tests.
- **`FilletComparison`** with clone-per-generator — correct side-by-side discipline.

**Weaknesses:** `Apply` returns only `FilletStatistics`; no `FilletOptions`; no first-class `None` generator; `UseV2` bool is weaker than `FilletMode` on the parser.

---

## Codex proposal — interface strengths

- **`FilletGenerationResult`** wrapping statistics + **`FilletDiagnostic`** list — comparison and inspector can assert on `Code`/`Severity` without log scraping.
- **`FilletGenerationMode`** (`None` / `Current` / `CleanRoom`) on parse — explicit, maps `applyFillets: false`.
- Comparison acceptance by **geometry signatures and behavior probes** (exits, hold-shorts, routes) — correct parity bar when node IDs are unstable.

**Weaknesses:** `internal` interface fights LayoutInspector/tests unless widened; `Current` vs `Legacy` naming; thin factory/registry story; no `NullFilletArcGenerator` spelled out.

---

## Ranking (interface only)

1. **Claude** — Best integrated interface: mode enum, options, factory, null impl, stateless rule, statistics parity hooks, test pivot pattern.
2. **Cursor** — Best plumbing for compare-at-scale: registry, router, `FilletComparison`, `Id`/`DisplayName`.
3. **Codex** — Best return type for diagnostics; weakest wiring.

Claude is not “third best” on interfaces — it is the strongest **baseline**. Cursor and Codex contributions are **additive**.

---

## Recommended merge (canonical interface)

Adopt **Claude’s** surface as the core:

| Piece | Source |
|-------|--------|
| `IFilletArcGenerator` with `Name`, `Mode`, `Apply(layout, FilletOptions)` | Claude |
| `FilletMode` + `FilletGeneratorFactory` + `NullFilletArcGenerator` | Claude |
| `FilletStatistics` in `Fillet/FilletStatistics.cs` | Claude |
| `FilletOptions` | Claude |
| Stateless requirement on implementations | Claude |
| V2 reports zero on legacy-only cleanup counters | Claude |

Add from **Cursor**:

| Piece | Source |
|-------|--------|
| `FilletArcGeneratorRegistry.All` (backed by factory) | Cursor |
| Optional `FilletArcGeneratorRouter.Current` for pathfinder-style runtime switch | Cursor |
| `FilletComparison` + layout deep-clone helper | Cursor |
| `Id` as alias of `Name` or keep both: `Id` for CLI, `DisplayName` for reports | Cursor |

Add from **Codex**:

| Piece | Source |
|-------|--------|
| `FilletGenerationResult` = `FilletStatistics` + `IReadOnlyList<FilletDiagnostic>` | Codex |
| Signature-based diff (rounded endpoints, taxiway set, bearings, min radius) inside `FilletComparison` | Codex |
| Behavior probes (parking→hold-short BFS, known OAK/SFO/FLL regressions) in parity gate | Codex |

**Naming conventions (this repo):**

- `FilletMode.Legacy` (not Codex’s `Current`)
- `FilletArcGeneratorV2` (consistent with `TaxiPathfinderV2`)
- `Name` / factory id: `"legacy"`, `"v2"`, `"none"`

### Canonical interface sketch

```csharp
namespace Yaat.Sim.Data.Airport.Fillet;

public interface IFilletArcGenerator
{
    string Name { get; }
    FilletMode Mode { get; }
    FilletGenerationResult Apply(AirportGroundLayout layout, FilletOptions options = default);
}

public sealed record FilletGenerationResult(
    FilletStatistics Statistics,
    IReadOnlyList<FilletDiagnostic> Diagnostics
);

public readonly record struct FilletOptions(
    bool EmitDebugLogs = false,
    bool EmitValidationWarnings = true,
    bool FailFastOnOrphans = false);

public enum FilletMode { None, Legacy, V2 }

public static class FilletGeneratorFactory
{
    public static IFilletArcGenerator Create(FilletMode mode) => mode switch
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
```

Optional router (production convenience only):

```csharp
public static class FilletArcGeneratorRouter
{
    public static IFilletArcGenerator Current { get; set; } = FilletGeneratorFactory.Create(FilletMode.Legacy);
    public static bool UseV2 { set => Current = FilletGeneratorFactory.Create(value ? FilletMode.V2 : FilletMode.Legacy); }
}
```

---

## Comparison harness (merged)

1. **Structural tests** — parameterize over `FilletArcGeneratorRegistry.All` (or `AllGenerators()` excluding `None` where fillet is required).
2. **Parity tests** — build `TestAirport.BuildRaw(airport)` then `FilletComparison.Compare(preFillet, [Legacy, V2])`.
3. **Metrics** — Claude/Cursor counts + Codex signatures + behavior probes.
4. **Triage** — mismatches → `docs/plans/filletv2/v2-divergences.md` (accepted improvement / V2 bug / legacy quirk).

LayoutInspector:

- `--fillet=none|legacy|v2` → `FilletGeneratorFactory.Create`
- `--fillet-diff` → `FilletComparison.FormatReport` to `.tmp/`

---

## What not to do

- **Do not** keep static `FilletArcGenerator.Apply` as the production call site after step 0 — route through factory/router.
- **Do not** compare implementations on **node ID** equality — geometry and behavior only (Codex).
- **Do not** add a permanent second production generator after parity — project rule: delete legacy, keep `IFilletArcGenerator` for future variants (Claude).
- **Do not** require both router and factory in tests — tests use factory/registry; router is optional sugar for `GeoJsonParser`-less callers.

---

## Open decisions (unchanged across proposals)

| # | Question | Recommendation |
|---|----------|----------------|
| 1 | Ship `FilletMode.None`? | **Yes** — `NullFilletArcGenerator` + raw layout tests (Claude, Codex). |
| 2 | Arc count parity tolerance | **±5%** per airport with per-divergence review in `v2-divergences.md` (Claude); not strict ±0 (Codex) nor undocumented deltas only (Cursor). |
| 3 | `FilletProvenance` / `FilletEdgeKind` | **Drop after V2** — single `ConstructionTag` string on V2 elements; legacy keeps until delete (Claude open item). |
| 4 | Return `FilletStatistics` vs `FilletGenerationResult` | **Wrap** — `FilletGenerationResult` for interface; harness reads `.Statistics` (merge). |

---

## Next doc step

Update [`cursor-proposal.md`](./cursor-proposal.md) interface section to point at this file as the **canonical interface spec**, or inline the [Recommended merge](#recommended-merge-canonical-interface) section and trim duplicate interface prose in all three proposals.
