# Fillet V2 - Consensus Interface Proposal

**Date:** May 2026
**Author:** Codex
**Inputs:** `claude-interface-review.md`, `cursor-interface-review.md`, `codex-interface-review.md`

## Summary

Adopt a minimal public `IFilletArcGenerator` interface, and keep implementation selection, comparison, optional diagnostics, and V2 planning APIs outside that interface.

This gives all proposals their strongest shared pieces:

- [ ] Cursor's small public interface shape.
- [ ] Claude's explicit `FilletMode` and `NullFilletArcGenerator`.
- [ ] Cursor's registry and comparison helper.
- [ ] Codex's geometry-first and behavior-first comparison criteria.
- [ ] Deferred structured diagnostics, only after the comparison harness proves concrete warning cases.

## Canonical Interface

```csharp
namespace Yaat.Sim.Data.Airport;

public interface IFilletArcGenerator
{
    string Id { get; }
    string DisplayName { get; }

    FilletStatistics Apply(AirportGroundLayout layout);
}
```

Required semantics:

- [ ] Implementations mutate the supplied `AirportGroundLayout` in place.
- [ ] Implementations are stateless and can be run concurrently against different layout instances.
- [ ] Callers comparing implementations must pass independent layout clones.
- [ ] `Id` is the stable machine identifier for CLI flags, JSON reports, logs, and registry lookup.
- [ ] `DisplayName` is the human-readable label for LayoutInspector and test output.
- [ ] `Apply` returns the existing `FilletStatistics` record directly.

## Selection

Expose explicit mode selection at parser and tool boundaries, not on the interface itself.

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

Implementation decisions:

- [ ] Add `NullFilletArcGenerator` so `None` is a real generator.
- [ ] Add `LegacyFilletArcGenerator` as a thin adapter around the current static `FilletArcGenerator.Apply`.
- [ ] Add `FilletArcGeneratorV2` as the clean-room implementation.
- [ ] Keep `FilletMode` out of `IFilletArcGenerator` so future experiments are not coupled to a closed enum.
- [ ] Keep `FilletOptions` out of the v1 interface. If options become necessary, make them required at the relevant call site or keep them on V2-only debug/planning APIs.

Parser and tool behavior:

- [ ] Keep the existing `GeoJsonParser.Parse(..., bool applyFillets)` overload.
- [ ] Map `applyFillets: false` to `FilletMode.None`.
- [ ] Map `applyFillets: true` to `FilletMode.Legacy` until V2 is accepted.
- [ ] Add a new `GeoJsonParser.Parse(..., FilletMode mode)` overload.
- [ ] Add LayoutInspector `--fillet=none|legacy|v2` and dispatch through `FilletGeneratorFactory.Create`.

## Registry And Comparison

```csharp
public static class FilletArcGeneratorRegistry
{
    public static IReadOnlyList<IFilletArcGenerator> All { get; } =
        [NullFilletArcGenerator.Instance, new LegacyFilletArcGenerator(), new FilletArcGeneratorV2()];
}
```

Comparison harness requirements:

- [ ] Add `FilletComparison.Compare(AirportGroundLayout preFilletLayout, IReadOnlyList<IFilletArcGenerator> generators)`.
- [ ] Clone the unfilleted base layout once per generator.
- [ ] Apply each generator independently.
- [ ] Key reports by `IFilletArcGenerator.Id`.
- [ ] Include `FilletStatistics`, node/edge/arc counts, graph validity checks, and elapsed time.
- [ ] Compare rounded node positions and node types instead of node IDs.
- [ ] Compare straight edge signatures by rounded endpoints and taxiway name.
- [ ] Compare arc signatures by rounded endpoints, taxiway set, tangent bearings, minimum radius, and distance.
- [ ] Compare behavior probes for parking-to-hold-short reachability, runway exits, and known OAK/SFO/FLL regressions.
- [ ] Write mismatch reports to `.tmp/` for investigation, not to tracked files unless deliberately promoting a reviewed divergence.

## Deferred Diagnostics

Do not wrap the v1 interface result in `FilletGenerationResult`.

Structured diagnostics remain useful, but they should be added only after the comparison harness identifies concrete warning/error cases that need stable codes.

Possible follow-up options:

- [ ] Add `IReadOnlyList<FilletDiagnostic>` to `FilletStatistics`.
- [ ] Add an explicit diagnostic collector to V2-only planning/debug APIs.
- [ ] Keep existing `ILogger` output active even if structured diagnostics are added.

Do not add optional diagnostic parameters to `IFilletArcGenerator.Apply`.

## Implementation Sequence

- [ ] Add `IFilletArcGenerator`, `FilletMode`, `FilletGeneratorFactory`, `NullFilletArcGenerator`, `LegacyFilletArcGenerator`, and `FilletArcGeneratorRegistry`.
- [ ] Route `GeoJsonParser` fillet application through `FilletGeneratorFactory`.
- [ ] Preserve default production behavior by mapping the existing boolean overload to `Legacy`.
- [ ] Add comparison helpers and LayoutInspector selection before implementing V2.
- [ ] Implement `FilletArcGeneratorV2` behind the same interface.
- [ ] Run parity and behavior comparison on real OAK, SFO, and FLL layouts.
- [ ] Review each divergence as accepted improvement, V2 bug, or legacy quirk.
- [ ] After V2 acceptance and aviation realism review, switch the default mode to `V2`.
- [ ] Delete the legacy production path rather than keeping both generators permanently.

## Explicit Non-Decisions

- [ ] Do not decide V2 planner/executor internals in the interface contract.
- [ ] Do not require exact node ID parity.
- [ ] Do not require exact arc count parity before reviewing intentional deduplication.
- [ ] Do not add `FilletOptions` to the shared interface.
- [ ] Do not add `FilletMode Mode` to the shared interface.
- [ ] Do not make `FilletArcGeneratorRouter.Current` the primary parser selection mechanism.

## Consensus Statement

The shared contract should be boring and stable: public, stateless, in-place, one method, existing statistics return. Selection should be explicit at parser and tool boundaries through `FilletMode` and a factory. Comparison should operate over generator instances and judge outputs by geometry and behavior, not by IDs or implementation details.
