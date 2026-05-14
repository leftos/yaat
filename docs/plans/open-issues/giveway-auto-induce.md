# GIVEWAY: detector auto-induces yield relationships

## Why this exists

The recent GIVEWAY redesign (commit `a8b514f9`) made the *controller-issued* GIVEWAY relationship observable: the operator sees `→SWA456` on the ground datablock and "Yielding to SWA456" in the right-click menu when the controller says `GW SWA456`. `GroundConflictDetector` emits a `[Pair] ControllerGiveWay yielder→winner` log line for these pairs.

The detector also picks yielders *on its own* for converging / same-edge-trailing pairs — but that yielder selection is anonymous. The detector sets `Ground.SpeedLimit` on the auto-yielder and nothing else. The operator can't tell which aircraft is yielding to which, can't tell whether a slowdown is intent-bearing or kinematic, and can't audit the detector's decisions in the UI.

Today both paths produce the same observable outcome (one aircraft slows for another), but with different state, different UI, and different release semantics. This is the unification the redesign explicitly deferred.

## What's proposed

Let `GroundConflictDetector` *write* `Hold = HoldDirective.GiveWay(target)` on the yielder it picks for `Converging` and `SameEdgeTrailing` pairs, so the auto-yielder shows up in the same `→{target}` UI surface a controller GIVEWAY uses. Differentiate the *origin* of the directive so the badge can say "(auto)" vs "(controller)".

### Model change

Add `HoldOrigin` to `HoldDirective`:

```csharp
public enum HoldOrigin
{
    Controller,           // TryGiveWay / TryHoldPosition / air-taxi HOLD
    DeferredCondition,    // BEHIND-as-condition firing (today identical to Controller)
    AutoConverging,       // GroundConflictDetector picked this as the yielder for a Converging pair
    AutoTrailing,         // ditto for SameEdgeTrailing
}

public sealed record HoldDirective
{
    public HoldKind Kind { get; }
    public string? YieldTarget { get; }
    public HoldOrigin Origin { get; }
    // ...
}
```

Snapshot DTO bumps a new field: `string? HoldOrigin` (default null → Controller). Backwards-compatible because null defaults to the existing semantics.

### Detector behaviour

`ApplySpeedLimits` already classifies pairs. For `Converging` and `SameEdgeTrailing`, instead of (or in addition to) setting `SpeedLimit`, set `Hold = HoldDirective.AutoGiveWay(target, origin)` on the yielder. The geometry-based release in `FlightPhysics.UpdateGiveWayResume` runs the same way regardless of origin — when the target passes, the directive clears.

**Open question:** should the detector still write `SpeedLimit` as well, or is the `Hold`-driven phase short-circuit (`IsImmobile`) enough? Probably both for safety: `SpeedLimit` is the existing closing-proximity safety net; `Hold` is the new "this is why" annotation.

### Lifecycle gotcha

`Hold` set by the detector must clear when the pair classification changes. Today the detector clears `SpeedLimit = null` every tick at the top of `ApplySpeedLimits` and re-derives. The new auto-`Hold` needs the same per-tick reset semantics — but only for `Origin in { AutoConverging, AutoTrailing }`. Controller-set holds must survive across ticks.

```csharp
// Top of ApplySpeedLimits:
for (int i = 0; i < aircraft.Count; i++)
{
    aircraft[i].Ground.SpeedLimit = null;
    if (aircraft[i].Ground.Hold is { Origin: HoldOrigin.AutoConverging or HoldOrigin.AutoTrailing })
    {
        aircraft[i].Ground.Hold = null;
    }
}
```

### UI

The datablock suffix and context menu header already render from `HoldKind` + `HoldYieldTarget`. Add `Origin` to the wire DTO and append "(auto)" to the badge when origin is auto:

- Controller `GW`: `→SWA456` (no suffix)
- Detector auto: `→SWA456 (auto)`
- Right-click menu: "Yielding to: SWA456 (auto-detected)" vs "Yielding to: SWA456"

### Risks

- **Yielder oscillation**: if the detector picks a different yielder tick-over-tick (e.g. because the convergence resolver re-evaluates with stale geometry), the UI badge would flicker. Mitigation: a 2-tick hysteresis on auto-set holds — only clear after two consecutive ticks of "no pair conflict".
- **Controller override**: if the controller issues `GW UAL999` on an aircraft that the detector has already auto-yielded to `SWA456`, the controller wins. `TryGiveWay` must always replace the directive regardless of origin.
- **Test churn**: every test that asserts `SpeedLimit != null` on a converging-yielder now also has to assert `Hold` is set. The `GroundConflictDetectorTests` suite is the main impact site.

## Implementation checklist

- [ ] Add `HoldOrigin` enum to `src/Yaat.Sim/HoldDirective.cs`.
- [ ] Add `Origin` property to `HoldDirective` record. Default `Controller` for the existing `HoldPosition` and `GiveWay` factories. Add `HoldDirective.AutoGiveWay(yieldTarget, origin)` factory.
- [ ] Add `HoldOrigin` (string) field to wire DTO (`TrainingDtos.cs` + `ServerConnection.cs`), populate from `DtoConverter.cs`.
- [ ] Add `HoldOrigin` to `AircraftModel`, mirror in `FromDto` / `UpdateFromDto`. Extend `HoldStatusDisplay` to append "(auto)" when origin is non-controller.
- [ ] Update `AircraftGroundOpsDto.HoldOrigin` if/when we add snapshot persistence (or leave runtime-only and document the gap).
- [ ] In `GroundConflictDetector.ResolveConvergence` / `ResolveSameEdgeTrailing`, in addition to `ApplyMinLimit`, set `Hold = HoldDirective.AutoGiveWay(winner.Callsign, AutoConverging or AutoTrailing)`.
- [ ] In `ApplySpeedLimits` per-tick reset, clear auto-origin holds before re-classifying.
- [ ] In `TryGiveWay`, ensure controller directives override auto directives (already true since `TryGiveWay` always assigns).
- [ ] Update `GroundConflictDetectorTests`: new assertions for auto-yielder `Hold` + origin.
- [ ] Update the redesign tests (`GiveWayRedesignTests`) to cover controller-override-of-auto.
- [ ] Update CHANGELOG.md `### Added`.
- [ ] Update `USER_GUIDE.md` if a screenshot section documents the datablock badge.
- [ ] Delete this plan after merging.

## Verification

1. Two aircraft converging on a Y-junction with no controller intervention → both develop the `→{target} (auto)` badge in the ground datablock, one yields cleanly, no flicker.
2. After the auto-yielder is mid-yield, controller issues `GW {differentTarget}` on it → badge switches to `→{differentTarget}` (no auto suffix). The detector does not stomp the controller's directive on subsequent ticks.
3. Detector picks yielder → both clear the conflict zone → badge disappears on both within one tick.
4. Snapshot a converging scene mid-yield → restore → operator sees the same badge state (requires snapshot persistence — see checklist).
