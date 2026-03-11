# Issue #46: Aircraft icon arrows not oriented correctly

## Root Cause

`GroundRenderer.DrawTriangle` converts a geographic bearing (heading) to a SkiaSharp screen
angle using `(headingDeg - 90) * π / 180`. This formula is correct only when the viewport has
no rotation (`RotationDeg == 0`, i.e. true north up).

`MapViewport.LatLonToScreen` applies `−RotationDeg` to all screen positions when the map is
rotated (e.g. magnetic north up, where `RotationDeg` = magnetic declination ≈ 14° for ZOA).
All geographic positions are transformed correctly by this rotation. However, `DrawTriangle`
computes its direction vectors from the raw geographic heading without applying the same
rotation offset, so the arrow points in the un-rotated direction while the map is rotated.

At 14° east declination (ZOA), this causes the arrow to point ~14° clockwise of the actual
taxi direction — consistent with the reported "1–2 o'clock relative to travel direction" symptom.

## Fix

**File:** `src/Yaat.Client/Views/Ground/GroundRenderer.cs`
**Method:** `DrawAircraft`

Change:
```csharp
DrawTriangle(canvas, sx, sy, (float)ac.Heading, lengthPx, widthPx, _aircraftPaint);
```
To:
```csharp
DrawTriangle(canvas, sx, sy, (float)(ac.Heading - vp.RotationDeg), lengthPx, widthPx, _aircraftPaint);
```

The `vp` parameter is already present in `DrawAircraft`'s signature. When `RotationDeg == 0`
this is a no-op — no regression for true-north-up views.

## Verification

1. Run client with a taxiing aircraft and magnetic-north rotation enabled.
2. Confirm arrow aligns with taxiway direction.
3. Confirm no regression with `RotationDeg == 0`.

## Future work (separate issue)

- Add `Track` to `AircraftDto` and `AircraftModel` (server change required).
- Use `ac.Track` instead of `ac.Heading` in `DrawAircraft` for accurate ground-track display
  when crosswind is present or during pushback.
