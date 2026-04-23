using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Snaps an off-graph ground-spawn aircraft onto the nearest taxi edge and
/// rotates its heading to match the edge's bearing (in the direction closer
/// to the original heading). Runs at scenario-load time — before the first
/// tick fires, so a paused-at-load scenario shows the snapped pose from the
/// start (no visible teleport on first play).
///
/// <para>
/// Scope: scenarios spawn ground aircraft via "Coordinates" (or "FixOrFrd")
/// starting conditions when they want the aircraft somewhere other than a
/// named parking spot — typically "ready to taxi from this point". Those
/// coordinates are often a few feet off the nearest taxiway edge, so the
/// pathfinder's start-node approach would cut diagonally across terrain to
/// acquire the first segment. Snap eliminates this by placing the aircraft
/// exactly on a taxi edge at a correctly-aligned heading before any taxi
/// command fires.
/// </para>
///
/// <para>
/// Not applicable to: "Parking" spawns (aircraft explicitly placed at a
/// parking node); "OnRunway" / "OnFinal" (not ground-coord spawns);
/// airborne aircraft.
/// </para>
/// </summary>
public static class GroundSpawnSnap
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundSpawnSnap");

    /// <summary>
    /// Maximum distance the snap will pull an aircraft. Beyond this, the
    /// aircraft is almost certainly not intended to be on a taxi edge
    /// (scenario-author typo, mid-apron coord spawn, etc.); leave the pose
    /// unchanged and log a warning so the issue surfaces.
    /// </summary>
    private const double MaxSnapDistanceFt = 200.0;

    /// <summary>
    /// Apply the snap to <paramref name="aircraft"/> if eligible:
    /// aircraft is on-ground, a ground layout is available, and the nearest
    /// taxi edge is within <see cref="MaxSnapDistanceFt"/>. Mutates
    /// <see cref="AircraftState.Latitude"/>, <see cref="AircraftState.Longitude"/>,
    /// and <see cref="AircraftState.TrueHeading"/> when applied.
    /// </summary>
    public static void Apply(AircraftState aircraft, AirportGroundLayout layout)
    {
        if (!aircraft.IsOnGround)
        {
            return;
        }

        var nearest = layout.FindNearestTaxiEdge(aircraft.Latitude, aircraft.Longitude);
        if (nearest is null)
        {
            Log.LogWarning(
                "{Callsign}: no taxi edge found near spawn ({Lat:F6}, {Lon:F6}) — leaving pose unchanged",
                aircraft.Callsign,
                aircraft.Latitude,
                aircraft.Longitude
            );
            return;
        }

        var result = nearest.Value;
        double distFt = result.DistNm * GeoMath.FeetPerNm;
        if (distFt > MaxSnapDistanceFt)
        {
            Log.LogWarning(
                "{Callsign}: spawn at ({Lat:F6}, {Lon:F6}) is {DistFt:F0} ft from nearest taxi edge ({Taxiway}) — beyond {Max:F0} ft threshold; leaving pose unchanged",
                aircraft.Callsign,
                aircraft.Latitude,
                aircraft.Longitude,
                distFt,
                result.Edge.TaxiwayName,
                MaxSnapDistanceFt
            );
            return;
        }

        // Pick the edge direction whose bearing is closer to the aircraft's
        // original heading. Nodes[0] and Nodes[1] are not directionally
        // meaningful, so we compute both bearings and rotate the aircraft
        // to whichever is a smaller turn from its current heading.
        double bearing01 = GeoMath.BearingTo(
            result.Edge.Nodes[0].Latitude,
            result.Edge.Nodes[0].Longitude,
            result.Edge.Nodes[1].Latitude,
            result.Edge.Nodes[1].Longitude
        );
        double bearing10 = (bearing01 + 180.0) % 360.0;
        double origHdg = aircraft.TrueHeading.Degrees;
        double diff01 = GeoMath.AbsBearingDifference(origHdg, bearing01);
        double diff10 = GeoMath.AbsBearingDifference(origHdg, bearing10);
        double chosenBearing = diff01 <= diff10 ? bearing01 : bearing10;

        Log.LogInformation(
            "{Callsign}: snapping spawn ({OrigLat:F6}, {OrigLon:F6}) hdg {OrigHdg:F0}° → edge {Taxiway} at ({FootLat:F6}, {FootLon:F6}) hdg {NewHdg:F0}° (snap {DistFt:F0} ft, rotate {RotDeg:F0}°)",
            aircraft.Callsign,
            aircraft.Latitude,
            aircraft.Longitude,
            origHdg,
            result.Edge.TaxiwayName,
            result.FootLat,
            result.FootLon,
            chosenBearing,
            distFt,
            Math.Min(diff01, diff10)
        );

        aircraft.Latitude = result.FootLat;
        aircraft.Longitude = result.FootLon;
        aircraft.TrueHeading = new TrueHeading(chosenBearing);
        aircraft.TrueTrack = aircraft.TrueHeading;
    }
}
