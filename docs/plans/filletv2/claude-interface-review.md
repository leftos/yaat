# Interface Design Review — Three Proposals for `IFilletArcGenerator`

**Reviewer:** Claude (agent author of `claude-proposal.md`)
**Compared:** `codex-proposal.md`, `cursor-proposal.md`, `claude-proposal.md`
**Scope:** the interface contract only — not the V2 generator architecture, which is largely independent.

## TL;DR

**Cursor's interface design wins**, and it isn't close once you account for the existing YAAT codebase pattern. The other two proposals (mine included) reinvent something the codebase already has — Cursor's is a one-for-one copy of the established convention for the same architectural problem.

## The decisive evidence

The codebase already has the exact pattern Cursor proposes, for the analogous V1→V2 migration of the taxi pathfinder:

| Fillet (proposed) | Pathfinder (already in repo) | File |
|---|---|---|
| `IFilletArcGenerator` | `ITaxiPathfinder` | `src/Yaat.Sim/Data/Airport/ITaxiPathfinder.cs:9` |
| `LegacyFilletArcGenerator` | `TaxiPathfinderV1Adapter` | `src/Yaat.Sim/Data/Airport/TaxiPathfinderV1Adapter.cs` |
| `FilletArcGeneratorV2` | `TaxiPathfinderV2` | `src/Yaat.Sim/Data/Airport/TaxiPathfinderV2.cs` |
| `FilletArcGeneratorRouter.Current` / `UseV2` | `TaxiPathfinderRouter.Current` / `UseV2` | `src/Yaat.Sim/Data/Airport/TaxiPathfinderRouter.cs:18-44` |
| `FilletComparison.Compare(layout, generators)` | `PathfinderComparison.Compare(v1, v2, …)` | `tests/Yaat.Sim.Tests/Helpers/PathfinderComparison.cs:31-80` |

Cursor's proposal is the only one of the three that recognized this. Following an established convention beats inventing a parallel one — fewer mental models for future readers, no risk of the two patterns drifting, and the deletion path is already proven (the pathfinder is mid-migration on the same template).

## Head-to-head on the contract itself

### Codex — minimal-but-novel

```csharp
internal interface IFilletArcGenerator
{
    string Name { get; }
    FilletGenerationResult Apply(AirportGroundLayout layout);
}

internal sealed record FilletGenerationResult(
    string GeneratorName,
    FilletStatistics Statistics,
    IReadOnlyList<FilletDiagnostic> Diagnostics);
```

**Strengths:**
- Diagnostics list is a real value-add — `FilletArcGenerator.cs` currently emits `Log.LogWarning(...)` for runway-displacement and orphan cases, and those warnings vanish into the log stream. A structured list lets the harness diff them.
- Factory/registry maps a `FilletGenerationMode` enum without reflection or string lookups — type-safe.

**Weaknesses:**
- `internal` visibility is wrong for this codebase. The sibling `ITaxiPathfinder` is `public` and is consumed by `GeoJsonParser` (also public). Marking the fillet interface `internal` would force callers into the static class anyway, defeating the abstraction.
- `FilletGenerationResult` wraps `FilletStatistics` and adds two new types (`FilletDiagnostic`, `FilletDiagnosticSeverity`). `FilletStatistics` already exists at `FilletArcGenerator.cs:10`. Wrapping it bifurcates the schema; callers that just want stats now have to `.Statistics`. Not worth two new types for V1.
- The diagnostics list belongs *outside* the `Apply` return type — it's better as a sink (`ILogger`, an `IDiagnosticCollector` argument) so existing log infrastructure keeps working and the interface stays minimal.

### Cursor — pattern-aligned, two-property contract

```csharp
public interface IFilletArcGenerator
{
    string Id { get; }            // "legacy", "v2"
    string DisplayName { get; }
    FilletStatistics Apply(AirportGroundLayout layout);
}
```

Plus `FilletArcGeneratorRouter` (Current + UseV2), `FilletArcGeneratorRegistry.All` (for multi-way compare), and `FilletComparison.Compare(...)`.

**Strengths:**
- Mirrors `ITaxiPathfinder` exactly — visibility, single-method contract, router shape, registry shape, helper shape.
- `FilletStatistics` unchanged. Zero new public types beyond the interface itself.
- `Id` / `DisplayName` separation lets the LayoutInspector show "Legacy (pair + cleanup passes)" while the diff JSON keys on `"legacy"` — both matter for different audiences.
- Explicit "implementations mutate `layout` in place; comparison harness owns cloning" — pushes the deep-clone concern to one place (the harness) instead of every implementation.
- `FilletPlanBuilder.Build(layout) → FilletPlan` is documented as *out-of-interface*, V2-only. Right call: it's debugging machinery for one implementation and doesn't belong on the shared contract.

**Weaknesses:**
- `string Id` instead of an enum allows typos in the LayoutInspector `--fillet=` flag handling, which is annoying but not load-bearing. Could be tightened later.
- The Router being a mutable `public static` property is technically a global. The codebase has the same footgun in `TaxiPathfinderRouter` so the precedent is set, but it deserves a "switch only at startup or single-threaded test setup" comment (which Cursor includes — `cursor-proposal.md:168`).

### Claude (mine) — over-decorated

```csharp
public interface IFilletArcGenerator
{
    string Name { get; }
    FilletMode Mode { get; }
    FilletStatistics Apply(AirportGroundLayout layout, FilletOptions options = default);
}
```

Plus `FilletGeneratorFactory.Create(mode)`, `NullFilletArcGenerator`, `FilletOptions` record.

**Strengths:**
- `FilletMode` enum is more type-safe than Cursor's `string Id` — the LayoutInspector flag handler gets compile-time coverage.
- `NullFilletArcGenerator` cleanly handles `FilletMode.None` without a parser-level branch.

**Weaknesses:**
- Carries **both** `Name` and `Mode` — redundant. Pick one.
- `FilletOptions` with `FailFastOnOrphans` is YAGNI: I admitted in the proposal that "Legacy ignores" it. That's the canonical smell of an option that shouldn't be on the interface. CLAUDE.md says explicitly: *"No optional parameters: Make params required so the compiler enforces wiring. Optional params hide missing integration."* My own proposal violates this.
- `FilletGeneratorFactory` is one indirection level beyond what the codebase needs. The Router pattern (Cursor's choice) already maps `UseV2 = true` → V2; the factory exists only to convert `FilletMode` → instance, which is one switch expression at the parser call site.
- `NullFilletArcGenerator` is cute but `FilletMode.None` is already a parser-level concept (`applyFillets: false` exists today as a parameter, see `GeoJsonParser.cs:38`). Promoting it to a class is unnecessary abstraction.
- The `FilletMode` enum overlaps with `string Id` — if both existed, what's the source of truth?

## What I changed my mind on

I wrote my proposal before re-reading the codebase. Once I confirmed `ITaxiPathfinder` exists with the exact pattern Cursor describes (`TaxiPathfinderRouter.UseV2`, `PathfinderComparison.Compare`), my objections to Cursor's design evaporated:

- **`string Id` vs enum**: minor. The router doesn't take an `Id` — it takes a concrete instance. The `Id` is only used in test reports and inspector flags. A typo in `--fillet=v3` returns `null` from the registry; that's a fine failure mode.
- **`public` interface for one assembly**: matches `ITaxiPathfinder`. Codex's `internal` would have to be relaxed when `GeoJsonParser` consumes it anyway.
- **`FilletOptions`**: I was wrong to add it. Drop.

The one thing I'd genuinely take from my proposal into Cursor's design: **the `FilletMode` enum lives at the parser entry-point, not on the interface**. That gives the parser type safety on its public surface (`GeoJsonParser.Parse(..., FilletMode mode)`) without coupling the interface to a fixed set of identities. The factory maps `FilletMode → IFilletArcGenerator` in one place; the interface itself stays `string Id`-based per Cursor.

The one thing I'd genuinely take from Codex: **structured diagnostics**. But added as a second pass — `IFilletDiagnosticCollector` passed as a separate argument or kept on `FilletStatistics` as `IReadOnlyList<FilletDiagnostic> Warnings`. Not as a wrapped result type.

## Recommended composite contract

```csharp
namespace Yaat.Sim.Data.Airport;

public interface IFilletArcGenerator
{
    /// <summary>Stable machine id for logs and diff reports (e.g. "legacy", "v2").</summary>
    string Id { get; }

    /// <summary>Human-readable label for inspector output and test reports.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Apply fillet arcs to all eligible intersection nodes. Mutates
    /// <paramref name="layout"/> in place. Callers comparing implementations
    /// must pass independent layout clones — see
    /// <see cref="FilletComparison.Compare"/>.
    /// </summary>
    FilletStatistics Apply(AirportGroundLayout layout);
}
```

Plus:
- `FilletArcGeneratorRouter` (Current / UseV2) — Cursor's, verbatim.
- `FilletArcGeneratorRegistry.All` — Cursor's, verbatim.
- `FilletComparison.Compare(preFilletLayout, IReadOnlyList<IFilletArcGenerator>)` — Cursor's, verbatim.
- `enum FilletMode { None, Legacy, V2 }` exposed only at the `GeoJsonParser.Parse(..., FilletMode mode)` boundary; the parser maps the enum to a registry lookup. The interface stays string-id-based.
- Optional later (not v1): add `IReadOnlyList<FilletDiagnostic> Warnings` to `FilletStatistics` for harness-visible warnings. Implementation should also still write to `ILogger`.

## Final ranking

1. **Cursor** — best. Pattern-aligned, minimal, complete. Ship as-is with one nit (consider enum at the boundary).
2. **Codex** — second. Good instincts on diagnostics; wrong on visibility and on wrapping the result type.
3. **Claude (mine)** — third. Type-safe in a few places, but over-decorated, violates the codebase's no-optional-parameters rule, and reinvents what the codebase already has.

Acceptance criterion if I were the reviewer: take Cursor's design unchanged for v1, file a follow-up issue for the `FilletMode` enum at the parser boundary and for structured diagnostics on `FilletStatistics`. Don't let the perfect be the enemy of the shippable.
