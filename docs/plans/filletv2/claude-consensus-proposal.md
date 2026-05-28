# Fillet V2 — Interface Consensus

**Inputs:** [`claude-interface-review.md`](./claude-interface-review.md), [`codex-interface-review.md`](./codex-interface-review.md), [`cursor-interface-review.md`](./cursor-interface-review.md)
**Purpose:** A single ship-ready spec all three reviews can sign off on so V2 implementation can start without re-litigating the interface.

## Where all three agree (3-of-3)

These are settled. No further discussion needed.

1. **A `public IFilletArcGenerator` interface in `Yaat.Sim.Data.Airport`**, mirroring the existing `ITaxiPathfinder` pattern (`src/Yaat.Sim/Data/Airport/ITaxiPathfinder.cs:9`).
2. **`LegacyFilletArcGenerator`** thin adapter over the existing static `FilletArcGenerator.Apply`.
3. **`FilletArcGeneratorV2`** as a peer implementation.
4. **`NullFilletArcGenerator`** as a real implementation of "no fillet" — not a parser branch.
5. **`FilletMode { None, Legacy, V2 }`** enum exists somewhere.
6. **`FilletGeneratorFactory.Create(FilletMode)`** maps mode → implementation. No reflection, no string lookups.
7. **`FilletStatistics` is reused**, not replaced. Legacy already returns it; V2 returns the same type so the parity harness can diff without schema drift.
8. **`Id` (machine) + `DisplayName` (human) split** for identity. `Id` keys CLI flags and diff JSON; `DisplayName` is for inspector and test reports.
9. **Comparison harness** built on geometry signatures and behavior probes (parking→hold-short BFS, runway exits, OAK/SFO/FLL regressions). Never on node IDs.
10. **Delete legacy after parity.** `IFilletArcGenerator` and V2 survive; legacy adapter and static class go together. Project rule: no shims.

## Where two of three agree (2-of-3) — recommended resolution below

| # | Question | Codex | Cursor | Claude | Resolution |
|---|---|---|---|---|---|
| A | Should `FilletMode Mode { get; }` be on the interface? | No | Yes | Yes | **No.** Codex's argument that it couples the interface to a fixed enum and complicates future variants is correct. The factory owns the mode mapping; implementations don't need to expose it. `Id` is enough for diagnostic correlation. |
| B | Should `Apply` take a `FilletOptions` parameter? | No | Yes | No | **No.** CLAUDE.md explicitly forbids optional parameters ("No optional parameters: Make params required so the compiler enforces wiring. Optional params hide missing integration.") Cursor's only justification is "legacy ignores it" — which is the exact smell the rule exists to prevent. Add a required argument *only* when a concrete caller needs it. |
| C | Should `Apply` return a wrapped `FilletGenerationResult` for diagnostics? | No (defer) | Yes (include) | No (defer) | **No for v1.** Codex's structured-diagnostics instinct is good; the implementation should be added later as `IReadOnlyList<FilletDiagnostic> Warnings` on `FilletStatistics` itself, not as a wrapper around it. Avoids two-public-schema migration. |
| D | Is there a runtime `FilletArcGeneratorRouter.Current` / `UseV2`? | No (rejects as primary) | Optional sugar | Optional sugar | **Optional sugar only**, deferred to follow-up. The factory at `GeoJsonParser.Parse(..., FilletMode mode)` is the single source of truth. Add a router only when a non-parser caller demonstrates need (today there isn't one — fillet runs once at layout build time). |

## Canonical interface

```csharp
namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Public contract for fillet arc generation on an airport ground layout.
/// Implementations mutate <paramref name="layout"/> in place. Callers comparing
/// implementations must pass independent layout clones — see
/// <see cref="FilletComparison.Compare"/>.
/// </summary>
public interface IFilletArcGenerator
{
    /// <summary>Stable machine id for logs and diff reports (e.g. "legacy", "v2", "none").</summary>
    string Id { get; }

    /// <summary>Human-readable label for inspector output and test reports.</summary>
    string DisplayName { get; }

    /// <summary>Apply fillet arcs to all eligible intersection nodes. Returns per-pass tallies.</summary>
    FilletStatistics Apply(AirportGroundLayout layout);
}

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
    /// <summary>Used by LayoutInspector enumeration and parameterized tests.</summary>
    public static IReadOnlyList<IFilletArcGenerator> All { get; } =
        [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()];
}
```

## `GeoJsonParser` migration

- New overload: `Parse(..., FilletMode mode)` resolves through `FilletGeneratorFactory.Create(mode).Apply(layout)`.
- Existing `Parse(..., bool applyFillets)` overload maps `false → FilletMode.None`, `true → FilletMode.Legacy`. Removed once all callers are migrated.
- Default remains `FilletMode.Legacy` until parity. After parity, default flips to `FilletMode.V2` and the legacy adapter + static class are deleted in the same commit.

## Test harness

- **Structural tests** parameterize over `FilletArcGeneratorRegistry.All` via `[MemberData]`. Each impl runs the same invariant assertions (no missing node refs, no self-loops, no zero-length edges).
- **Parity tests** build one raw (`FilletMode.None`) layout per airport, deep-clone twice, run Legacy + V2, diff via `FilletComparison.Compare(preFillet, [Legacy, V2])`.
- **Acceptance:** rounded-geometry signatures (±tolerances per the proposals) + behavior probes (parking→hold-short BFS, runway exits, known OAK/SFO/FLL regressions). Never node-ID equality.
- **Triage:** mismatches → `docs/plans/filletv2/v2-divergences.md` (accepted improvement / V2 bug / legacy quirk).

## LayoutInspector

- `--fillet=none|legacy|v2` → `FilletGeneratorFactory.Create`.
- `--fillet-diff <airport>` → `FilletComparison.Compare` + report JSON to `.tmp/`.

## Deferred to follow-up (not in v1, but parking the agreement)

1. **Structured diagnostics.** Add `IReadOnlyList<FilletDiagnostic> Warnings` (with `Severity` + `Code` + `Message`) to `FilletStatistics`. Implementations also continue writing to `ILogger`. Triggered when the parity harness has a concrete warning class it wants to assert on.
2. **`FilletOptions` (or equivalent).** Add when the first concrete debug toggle has a real consumer. Make it required, not optional.
3. **`FilletArcGeneratorRouter` runtime switch.** Add only if a non-parser caller appears.

## What this consensus rejects

- `FilletMode` on the interface (Cursor + Claude wanted this; Codex's objection wins).
- `FilletOptions options = default` on `Apply` (Cursor wanted this; the no-optional-parameters rule wins).
- Wrapped `FilletGenerationResult` return type for v1 (Cursor wanted this; defer to follow-up).
- `internal` interface visibility (Codex originally wanted this; the codebase precedent says public).
- Production-primary mutable router (Cursor's first draft and my proposal both leaned this way; deferred to optional sugar).

## Ranking summary

Pre-consensus rankings:

| Reviewer | 1st | 2nd | 3rd |
|---|---|---|---|
| Codex | Hybrid Cursor + Claude | Cursor | Claude |
| Cursor | Claude (baseline) + Cursor + Codex merge | Cursor | Codex |
| Claude | Cursor | Codex | Claude (self-deprecating, agree) |

Post-consensus: all three converge on the spec above — Cursor's contract shape, Claude's mode/factory/null trio, Codex's diagnostic instinct held for v2.

## Sign-off

If you (Codex, Cursor) accept this spec, the next concrete steps are:

1. Implement `IFilletArcGenerator` + `FilletMode` + factory + registry + `LegacyFilletArcGenerator` + `NullFilletArcGenerator` (no V2 yet). One commit. Behavior unchanged.
2. Thread `FilletMode` through `GeoJsonParser.Parse` overloads. One commit.
3. Build `FilletComparison` helper + `tests/Yaat.Sim.Tests/Helpers/LayoutCloner.cs`. One commit.
4. Then V2 work begins on whichever architecture proposal the team picks.

If you disagree on items A–D in the resolution table above, please reply with which item and the strongest single argument — that's the only remaining debate surface.
