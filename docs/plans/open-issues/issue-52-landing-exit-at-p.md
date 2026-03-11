# Issue #52: SFO Landing Jets Exit at Taxiway P (Wrong Exit)

## Root Cause

**Two compounding problems:**

### 1. `RolloutDecelRate` is set to emergency-stop values (primary fix)

Current: Jet = **5.0 kts/sec**. This models maximum-effort braking (full reverse + max autobrake) — the stop a crew performs when worried about runway length. On a 9,000 ft runway like SFO 28R/28L, this produces:

- Rollout time (135 → 20 kts): (135-20) / 5.0 = **23 seconds**
- Distance covered: avg 77.5 kts × 23s / 3600 = **~0.43 nm = ~2,600 ft from touchdown**
- From threshold (touchdown at ~1,000 ft): **~3,600 ft** — midfield, where taxiway P is

The high-speed exits near the 01R/01L intersection are at ~6,000-7,000 ft from threshold. Normal commercial ops use autobrake 2 + moderate reverse, which is **~2.5 kts/sec average deceleration** (0.13g). At 2.5 kts/sec:
- Rollout time: 46 seconds
- Distance: ~0.99 nm = ~6,000 ft from touchdown
- From threshold: **~7,000 ft** — right at the high-speed exit zone ✓

### 2. `FindNearestExit` scoring algorithm (secondary, see plan notes)

The 0.5 nm search radius and distance-dominant scoring compound the problem by always picking the geometrically closest exit node regardless of whether it's ahead on the runway. However, **fixing the decel rate alone resolves the SFO symptom** — once the aircraft reaches 20 kts at the correct position (7,000 ft from threshold), `FindNearestExit` will find the high-speed exits naturally.

The `FindNearestExit` algorithm remains a latent issue for other airports/runways and should be addressed separately.

## Recommended Fix (Aviation Expert Reviewed)

**File**: `src/Yaat.Sim/AircraftCategory.cs`, `RolloutDecelRate()` method

| Category | Current | Recommended | Rationale |
|----------|---------|-------------|-----------|
| Jet | 5.0 kts/sec | **2.5 kts/sec** | Autobrake 2 + moderate reverse; exits at ~7,000 ft from threshold on 9,000 ft runway |
| Turboprop | 3.5 kts/sec | **2.0 kts/sec** | Proportional; turboprops have less reverser authority |
| Piston | 2.5 kts/sec | **1.5 kts/sec** | Light aircraft with modest wheel braking |

No changes to `RunwayExitSpeed` (25 kts is appropriate for 90° taxi exits), `TouchdownSpeed` (135 kts correct for B737 Vref+5), or `RolloutCompleteSpeed` (20 kts).

## Verification

After fix, on SFO 28R/28L (9,019 ft):
- Jet touches down at ~1,000 ft mark, speed 135 kts
- Decelerates at 2.5 kts/sec → reaches 20 kts after ~46s, ~6,000 ft from touchdown
- `FindNearestExit` at ~7,000 ft from threshold finds high-speed exits in the 01R/01L intersection zone ✓
- Aircraft makes a shallow-angle high-speed turn at correct exit, not a 90° sharp turn at P ✓

## Files to Edit

| File | Change |
|------|--------|
| `src/Yaat.Sim/AircraftCategory.cs` | `RolloutDecelRate()`: Jet 5.0→2.5, Turboprop 3.5→2.0, Piston 2.5→1.5 |

## Future Work (Separate Issue)

Fix `FindNearestExit` scoring in `AirportGroundLayout.cs` to handle cases where the rollout correctly positions the aircraft but the nearest-node search still picks an undesirable exit due to distance-dominant scoring. Tracked as part of issue #52 secondary root cause.
