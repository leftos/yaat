# Issue #51: SFO Departures Use Wrong Runway Direction

## Root Cause

**Runway designator format mismatch**: SFO GeoJSON airport layout uses single-digit runway numbers (e.g., `"1L"`, `"1R"`, `"28L"`) while NavData protobuf uses two-digit format (e.g., `"01L"`, `"01R"`, `"28L"`).

The bug chain:
1. `HoldShortAnnotator` stores `TargetName = rwyId.ToString()` → e.g., `"1L/19R"` or `"1R/19L"` from GeoJSON
2. `LineUpFromHoldShort` calls `CommandDispatcher.ResolveRunway(aircraft, "1L/19R", runways)`
3. `ResolveRunway` parses to End1=`"1L"`, End2=`"19R"` and calls `GetRunway("KSFO", "1L")`
4. `GetRunway` → `rwy.Id.Contains("1L")` → **fails** (NavData has `"01L"`, not `"1L"`)
5. Falls through to `GetRunway("KSFO", "19R")` → **succeeds**, returns `RunwayInfo.ForApproach("19R")`
6. Aircraft departs heading ~190° (19R direction) instead of ~010° (01L direction)

### Why `RWY 01R` + bare `CTO` still fails

`RWY 01R` sets `AssignedRunway` correctly (two-digit lookup works), but bare `CTO` on an aircraft still in `HoldingShortPhase` (not yet `LinedUpAndWaitingPhase`) goes through `LineUpFromHoldShort`, which re-reads `holding.HoldShort.TargetName` (still the GeoJSON single-digit string) and ignores the `AssignedRunway` already set. So the fix must address the `TargetName` normalization or the lookup tolerance.

### Evidence from issue comments

- `RWY 1R` → "Runway 19L" (GetRunway fails on "1R", matches "19L")
- `RWY 01R` → "Runway 01R" (correct — two-digit works)
- `RWY 01R` then `CTO` → "Cleared for takeoff runway 19L" (LineUpFromHoldShort ignores AssignedRunway)

## Fix

### Option A: Normalize in `RunwayIdentifier` constructor (recommended)

Always pad runway numbers to two digits at construction time. This fixes all consumers without requiring changes throughout the codebase.

**File**: `src/Yaat.Sim/Data/Airport/RunwayIdentifier.cs`

Add normalization helper called from both constructors:
```csharp
private static string NormalizeDesignator(string d)
{
    // Find the numeric prefix length
    int numLen = 0;
    foreach (char c in d)
        if (char.IsAsciiDigit(c)) numLen++;
        else break;
    if (numLen == 1) // single digit → pad to 2
        return "0" + d;
    return d;
}
```

Call `NormalizeDesignator()` in both constructors:
```csharp
public RunwayIdentifier(string end1, string end2)
{
    End1 = NormalizeDesignator(end1);
    End2 = NormalizeDesignator(end2);
}
```

### Option B: Normalize in `GetRunway` lookup (simpler, more targeted)

Add normalization only at the lookup site in `FixDatabase.GetRunway`:
```csharp
if (rwy.Id.Contains(runwayId) || rwy.Id.Contains(NormalizeRunway(runwayId)))
    return rwy.ForApproach(runwayId);
```

**Option A is preferred** — normalize once at the boundary, not at every comparison site.

## Files to Edit

| File | Change |
|------|--------|
| `src/Yaat.Sim/Data/Airport/RunwayIdentifier.cs` | Add `NormalizeDesignator()`, apply in constructors |

## Tests

- Add to `tests/Yaat.Sim.Tests/RunwayIdentifierTests.cs`:
  - `Contains_SingleDigit_MatchesTwoDigit`: `new RunwayIdentifier("01L", "19R").Contains("1L")` → `true`
  - `Parse_SingleDigit_NormalizesToTwoDigit`: `RunwayIdentifier.Parse("1R").End1` → `"01R"`
- Add E2E test using S2-SFO-1 recording: aircraft holding short 1L → `CTO` → departs heading ~010°

## Related

- `docs/e2e-testing.md` — recording-based E2E test framework
- Same fix resolves `RWY 1R` showing "Runway 19L" (user-input normalization is a bonus fix)
