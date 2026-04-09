# Multi-Layer Cloud Visual Detection

## Status: Open — Needs Planning

## Problem

The RTIS/RFIS live visual acquisition path in `VisualDetection.TryAcquireTraffic` (and the TickProcessor maintained-contact path for visual approaches) currently checks a **single** cloud layer — the METAR "ceiling" (lowest BKN or OVC). Real METARs can report multiple cloud layers:

```
KOAK 121853Z 27012KT 10SM SCT020 BKN070 OVC200 20/12 A2992
```

Here there are three layers:
- SCT at 2,000 ft (scattered — gaps exist, doesn't obscure traffic)
- BKN at 7,000 ft (broken — the current "ceiling")
- OVC at 20,000 ft (overcast — higher than typical training altitudes)

Current behavior: only BKN070 is considered. If one aircraft is at 5,000 ft (below BKN) and another at 8,000 ft (above BKN), we correctly flag `MixedCeiling`. But if both are below BKN with the SCT020 physically between them — or if there's an overcast at 20,000 ft above both — we ignore those layers even when they matter.

## Current Code

- `src/Yaat.Sim/MetarParser.cs` — `ParsedMetar` record has a single `int? CeilingFeetAgl` field.
- `src/Yaat.Sim/VisualDetection.cs` — `TryAcquireTraffic` does the mixed-ceiling check:
  ```csharp
  if (ceilingAgl is not null)
  {
      double ceilingMsl = ceilingAgl.Value + airportElevation;
      bool ownBelow = ownship.Altitude < ceilingMsl;
      bool tgtBelow = target.Altitude < ceilingMsl;
      if (ownBelow != tgtBelow)
      {
          return VisualAcquisitionResult.Fail(VisualAcquisitionFailure.MixedCeiling, ...);
      }
  }
  ```
- `TryAcquireAirport` has a similar single-ceiling "above ceiling" check.
- `src/Yaat.Sim/Commands/NavigationCommandHandler.cs` — `DispatchReportFieldInSight` / `DispatchReportTrafficInSight` call these methods with `metar?.CeilingFeetAgl`.
- `TickProcessor.ProcessVisualDetection` (yaat-server) — same single-ceiling reads.

## Desired Behavior

Given a METAR with multiple layers, the visual acquisition check should:

1. **Traffic ceiling check:** Fail with `MixedCeiling` if any BKN or OVC layer lies strictly between the ownship altitude and the target altitude. Scattered (SCT) and few (FEW) layers should not trigger — they have gaps and pilots can see through them.

2. **Field "above layer" check (RFIS):** Fail with `AboveCeiling` if the aircraft is above ANY BKN or OVC layer, not just the lowest. (An aircraft at 22,000 ft over KOAK with OVC at 20,000 ft can't see the field even if the METAR ceiling is only 7,000.)

3. **Slant-path consideration (stretch goal):** Even when both aircraft are below the same layer, a shallow slant angle through clouds along the line of sight can block the view. Example: ownship at 500 ft, target at 800 ft, both below BKN at 1,000 ft, but they're 8 nm apart — the line between them clips the cloud base mid-path. Probably overkill for v1; flag as future work.

4. **Cloud extent uncertainty:** METAR reports weather at the station. If both aircraft are 50 nm from the reporting station, the observed layer may not extend to their position. Current code already has this limitation and we don't need to fix it here — document it and move on.

## Required Changes

### 1. Extend `ParsedMetar` to carry all layers

```csharp
public enum CloudCover { Few, Scattered, Broken, Overcast }

public record CloudLayer(CloudCover Cover, int BaseFeetAgl);

public record ParsedMetar(
    string StationId,
    int? CeilingFeetAgl,              // Keep for backwards compatibility / "primary ceiling" use
    IReadOnlyList<CloudLayer> Layers, // NEW — ordered ascending by base altitude
    double? VisibilityStatuteMiles,
    // ... existing fields
);
```

`MetarParser.Parse` already scans `FEW/SCT/BKN/OVC\d{3}` regex matches — needs to collect all into `Layers` instead of only picking the lowest BKN/OVC.

### 2. Add a visual-obstruction helper

New helper in `VisualDetection.cs`:

```csharp
/// <summary>
/// Returns true if any BKN or OVC cloud layer lies strictly between the two
/// altitudes (exclusive). FEW and SCT layers are ignored — they have gaps and
/// don't reliably obstruct vision.
/// </summary>
public static bool IsObstructingLayerBetween(
    double altitudeMslA,
    double altitudeMslB,
    double airportElevation,
    IReadOnlyList<CloudLayer> layers);
```

### 3. Rewrite the mixed-ceiling check

`TryAcquireTraffic`:
- Accept `IReadOnlyList<CloudLayer>` instead of `int? ceilingAgl` (or in addition to, for backwards compat).
- Call the new helper on the two altitudes.

`TryAcquireAirport`:
- Check if the aircraft is at or above any BKN/OVC layer in the list.
- Keep the "field below the layer" message but use the specific binding layer in the output (e.g. "below BKN070").

### 4. Update call sites

- `NavigationCommandHandler.DispatchReportFieldInSight` — pass `metar?.Layers`.
- `NavigationCommandHandler.DispatchReportTrafficInSight` — same.
- `TickProcessor.ProcessVisualDetection` (yaat-server) — same.
- Any scenario/weather loaders that build `ParsedMetar` need to be audited so they populate the new field.

### 5. Tests

`MetarParserTests`:
- Assert that `SCT020 BKN070 OVC200` parses into 3 layers with correct covers and altitudes.
- Existing single-ceiling assertions should still pass because `CeilingFeetAgl` is preserved.

`VisualDetectionTests`:
- `CanSeeTraffic_ObstructingLayerBetween` — ownship at 5000, target at 8000, BKN at 6000 → fail `MixedCeiling`.
- `CanSeeTraffic_ScatteredLayerBetween` — same altitudes with SCT at 6000 → should succeed.
- `CanSeeTraffic_MultipleBelowLayer` — both aircraft below lowest BKN → succeed.
- `CanSeeAirport_BetweenTwoLayers` — aircraft at 10000, BKN at 5000 and OVC at 20000 → should fail (it's above BKN, so it can't see the field even though it's below OVC).

`ReportInSightTests`:
- Update the existing `Rfis_Fails_AboveCeiling` and `Rtis_Fails_OnMixedCeiling` to assert the improved messages.
- Add at least one test per new obstruction mode.

## Questions to Resolve

1. **Is FEW truly ignored?** Aviation convention says yes (few = 1/8 to 2/8 sky coverage = lots of holes), but some instructors may argue that a FEW layer can still break eye contact briefly. For now: FEW and SCT ignored, BKN and OVC obstruct.

2. **Should the "binding layer" be surfaced in the error message?** I think yes — "Unable, KOAK below BKN070" is more informative than "Unable, KOAK below the layer".

3. **Slant-path check:** implement now or defer? Probably defer — the geometry is trickier (need to compute the altitude of the line of sight at each horizontal position and compare to each layer base) and the simple above/below check covers the common training cases.

4. **Cloud base uncertainty at distance from reporting station:** the current single-station METAR model is a known limitation. Not addressing here; worth a separate note.

## Files to Modify

| File | Change |
|------|--------|
| `src/Yaat.Sim/MetarParser.cs` | Add `CloudCover` enum and `CloudLayer` record. `Parse` collects all layers. `ParsedMetar` gains `Layers` field. |
| `src/Yaat.Sim/VisualDetection.cs` | New `IsObstructingLayerBetween` helper. `TryAcquireTraffic` and `TryAcquireAirport` call it. |
| `src/Yaat.Sim/Commands/NavigationCommandHandler.cs` | Pass layers through. Improve `FormatFieldFailure` / `FormatTrafficFailure` to mention the binding layer. |
| `src/Yaat.Sim/Simulation/…WeatherProfile.cs` | Any callsite that constructs a `ParsedMetar` manually needs updating (tests and scenario loaders). |
| `../yaat-server/src/Yaat.Server/Simulation/TickProcessor.cs` | Pass layers to the Try* methods. |
| `tests/Yaat.Sim.Tests/MetarParserTests.cs` | Add multi-layer parse tests. |
| `tests/Yaat.Sim.Tests/VisualDetectionTests.cs` | Add obstructing/scattered/multi-layer tests. |
| `tests/Yaat.Sim.Tests/ReportInSightTests.cs` | Add or refine tests per the new behavior. |

## Verification

1. Build clean: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`
2. Sim + client tests: `dotnet test --no-build 2>&1 | tee .tmp/test.log`
3. yaat-server tests: `cd ../yaat-server && dotnet test 2>&1 | tee ../yaat/.tmp/server-test.log`
4. Manual scenario smoke: a S2-OAK scenario with a BKN layer between RPO traffic and student aircraft, confirm RTIS reports `MixedCeiling` reasonably.
5. Aviation review: invoke `aviation-sim-expert` on the final obstruction logic and message phrasing against AIM §8-1-6 and AC 90-48.

## Non-Goals

- Cloud base variation at distance from the reporting station (needs multi-station METAR interpolation — separate feature).
- Slant-path geometry (stretch goal; defer unless explicitly requested).
- Haze / contrast reduction as a function of visibility being below threshold (separate refinement; today we use `min(detectionRange, visibilityNm)` and stop there).
