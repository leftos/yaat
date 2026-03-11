# Issue #53: AAL2839 Overshoots to M1 Ramp on SFO Scenario Resume

## Reported Behavior

On load/resume of S2-SFO-1, AAL2839 (positioned on taxiway B between M and M1) taxis to the M1/M2 ramp intersection, makes a U-turn, then returns to hold short of runway 1L.

## Confirmed via Recording Tests

Replay test `Replay_Sfo_AAL2839_TaxiRoute_IsDirectNotDetour` (`SfoReplayTests.cs`) **fails**:
> `AAL2839 taxi route has 47 segments — expected ≤ 8. Taxiways: [B, M1].`

The route uses only B and M1 taxiways but traverses them over **47 segments** instead of ~5-8 for a direct path. This confirms the pathfinder produces a huge detour within M1/B.

## Root Cause

**Scenario facts** (from embedded `ScenarioJson`):
- AAL2839 starts at lat 37.609046, lon -122.383669, heading 190°
- Preset fires at timeOffset=0: `"TAXI B M1 1L"`
- Nearest GeoJSON node: **B/M junction** at (37.609180, -122.383648), 15m north

**What happens at t=0**:
1. `FindNearestNode(37.609046, -122.383669)` → returns the B/M junction node (closest at ~15m)
2. `ResolveExplicitPath(layout, bm_junction.Id, ["B", "M1"], destination="1L")` is called

**The pathfinding detour**: The B taxiway at SFO is a long east-west taxiway. The `ResolveExplicitPath` algorithm starts at the B/M junction and walks the FULL taxiway B looking for an M1 connection. It traverses most of B (possibly hundreds of meters east), then finds M1, walks M1 south-then-north to the runway hold short — producing the massive 47-segment route.

The aircraft is already adjacent to M1 at its starting position, but the pathfinder doesn't detect this and traverses the entire taxiway first.

This is the same class of bug as #42-45 (taxi overshoot).

## Fix

**File**: `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs`, `ResolveExplicitPath`

The fix is to detect when the start node already has a direct connection to the next requested taxiway and skip traversing the current taxiway. Specifically:

When the algorithm is about to walk taxiway "B" to find the "M1" connection, first check if the starting node already has adjacent edges labeled "M1". If yes, skip the B-traversal and jump directly to M1.

```csharp
// Before walking currentTaxiway to find nextTaxiway:
// Check if we can reach nextTaxiway directly from the current node
var directJunction = FindDirectTransition(startNode, nextTaxiway);
if (directJunction is not null)
{
    // Already at the junction — skip walking currentTaxiway entirely
    currentNode = directJunction;
    continue;
}
```

## Tests

Regression test in `tests/Yaat.Sim.Tests/Simulation/SfoReplayTests.cs`:
- `Replay_Sfo_AAL2839_TaxiRoute_IsDirectNotDetour` — fails with bug (47 segments), passes with fix (≤ 8)
- `Replay_Sfo_AAL2839_DoesNotOvershotPastRamp` — confirms aircraft stays above lat 37.607

## Files to Edit

| File | Change |
|------|--------|
| `src/Yaat.Sim/Data/Airport/TaxiPathfinder.cs` | Add direct-junction detection in `ResolveExplicitPath` |

## Related

- Issues #42-45 (taxi overshoot, previous work on taxi pathfinding)
- Plan: `docs/plans/open-issues/issues-42-45-taxi-overshoot.md`
