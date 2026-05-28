# Fillet V2 - Codex Interface Review

**Date:** May 2026
**Reviewer:** Codex
**Scope:** `IFilletArcGenerator`, implementation selection, and side-by-side comparison plumbing only.

## Verdict

The best interface design is a hybrid:

- Use **Cursor's literal interface contract** as the base.
- Use **Claude's `FilletMode` + factory + `NullFilletArcGenerator`** for parser and CLI selection.
- Use **Cursor's registry and `FilletComparison` helper** for side-by-side comparison.
- Keep **Codex-style structured diagnostics** as a follow-up, not part of the first shared interface.

The interface itself should stay small:

```csharp
namespace Yaat.Sim.Data.Airport;

public interface IFilletArcGenerator
{
    string Id { get; }
    string DisplayName { get; }

    FilletStatistics Apply(AirportGroundLayout layout);
}
```

## Why This Contract Wins

Cursor's interface is closest to the existing YAAT pattern. `ITaxiPathfinder` is already a public interface in `Yaat.Sim.Data.Airport`, with adapters and a router for V1/V2 migration. Fillet generation has the same problem shape: keep legacy behavior available, add V2, compare both on identical inputs, then switch and delete the old implementation.

The interface should be public because tooling and tests need to consume it directly. An `internal` interface, as in the Codex proposal, would make LayoutInspector and cross-assembly tests fight the abstraction.

`Id` and `DisplayName` should be separate. `Id` is for CLI flags, JSON reports, and stable matching. `DisplayName` is for test output and inspector reports. Claude's `Name` alone overloads both jobs.

`Apply` should return `FilletStatistics` directly because that is the existing legacy result. A wrapper result type is attractive for diagnostics, but it adds a new public schema before there is a concrete diagnostic consumer. Keep the first contract easy to adopt.

## What To Take From Each Proposal

### Cursor

Cursor has the best literal interface and comparison plumbing:

- [ ] `public interface IFilletArcGenerator`
- [ ] `string Id`
- [ ] `string DisplayName`
- [ ] `FilletStatistics Apply(AirportGroundLayout layout)`
- [ ] `LegacyFilletArcGenerator` adapter
- [ ] `FilletArcGeneratorV2`
- [ ] `FilletArcGeneratorRegistry.All`
- [ ] `FilletComparison.Compare(...)`

I would not make a mutable router the primary selection mechanism for parsing. It matches `TaxiPathfinderRouter`, but fillet generation runs at layout construction time, so explicit parser mode selection is cleaner and easier to compare.

### Claude

Claude has the best selection architecture:

- [ ] `FilletMode.None`
- [ ] `FilletMode.Legacy`
- [ ] `FilletMode.V2`
- [ ] `FilletGeneratorFactory.Create(FilletMode mode)`
- [ ] `NullFilletArcGenerator`
- [ ] Stateless implementation requirement

I would not put `FilletMode` on the interface. That closes the interface around one enum and makes future experiments awkward. The factory can map modes to implementations without every implementation exposing a mode.

I would also reject `FilletOptions options = default`. The repo explicitly avoids optional parameters because they hide missing integration. If options become necessary, make them required at the call site or keep them on a separate V2-only planning/debug API.

### Codex

Codex has the right instinct on comparison acceptance:

- [ ] Compare geometry signatures, not node IDs.
- [ ] Compare behavior probes, not only graph counts.
- [ ] Keep mode selection explicit.
- [ ] Consider structured diagnostics for warning/error parity.

The weak parts of the Codex proposal are `internal` visibility and `FilletGenerationResult` as the first return type. Both add friction before the interface has proven its shape.

## Recommended Design

Use the public interface in `Yaat.Sim.Data.Airport`:

```csharp
public interface IFilletArcGenerator
{
    string Id { get; }
    string DisplayName { get; }
    FilletStatistics Apply(AirportGroundLayout layout);
}
```

Use explicit mode selection at parser/tool boundaries:

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
```

Use a registry for comparison and inspector enumeration:

```csharp
public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } =
        [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()];
}
```

`GeoJsonParser.Parse(..., bool applyFillets)` remains for compatibility during the migration and maps `false` to `FilletMode.None` and `true` to `FilletMode.Legacy`. Add a new overload that takes `FilletMode` directly.

## Comparison Harness

- [ ] Build one unfilleted base layout.
- [ ] Clone it once per `IFilletArcGenerator`.
- [ ] Apply each generator independently.
- [ ] Compare rounded node positions and node types.
- [ ] Compare straight edge signatures by rounded endpoints and taxiway name.
- [ ] Compare arc signatures by rounded endpoints, taxiway set, tangent bearings, minimum radius, and distance.
- [ ] Compare behavior probes for parking-to-hold-short reachability, runway exits, and known OAK/SFO/FLL regressions.
- [ ] Report differences by generator `Id`, never by implementation type name.

## Decisions

- [ ] Use Cursor's public `IFilletArcGenerator` shape.
- [ ] Use Claude's `FilletMode` and factory outside the interface.
- [ ] Include `NullFilletArcGenerator` so `None` is a real generator.
- [ ] Keep options out of the interface for v1.
- [ ] Keep diagnostics out of the interface return type for v1.
- [ ] Add structured diagnostics later only after the comparison harness has concrete warning/error cases to preserve.
- [ ] Do not keep both generators as permanent production choices after V2 is accepted; delete legacy per project rules.

## Ranking

1. **Hybrid Cursor + Claude** - best actual design.
2. **Cursor alone** - best literal interface and comparison plumbing.
3. **Claude alone** - best selection model, but too coupled and violates the no-optional-parameters rule.
4. **Codex alone** - good comparison instincts, but the interface visibility and wrapper result are not the right first step.
