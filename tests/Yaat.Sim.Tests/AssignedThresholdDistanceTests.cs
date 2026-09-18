using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pins the datum <see cref="RunwayOccupancy.DistanceToAssignedThresholdNm"/> measures from.
///
/// The same-runway arrival protection orders its stream and computes both threshold ETAs from this helper. Its
/// sibling <see cref="RunwayOccupancy.DistanceToLandingThresholdNm"/> re-derives which runway end to measure from
/// using the aircraft's own <em>track</em>, which is right for its short-final callers and wrong here: an arrival
/// still on downwind or base tracks the reciprocal, aligns to the opposite end, and reports a <em>negative</em>
/// distance — "already past the threshold". Such an aircraft sorts to the front of the stream and, as a leader,
/// hands the pass an ETA that suppresses protection for everyone behind it; at 90° off the axis the end choice is a
/// tie, so it flips on a one-degree track wobble and flaps the ceiling on and off.
///
/// The straight-in arms in <c>SfoGroundControlArrivalGoAroundTests</c> cannot catch a regression to the track-derived
/// datum, because a straight-in aircraft is already tracking the landing course and the two agree. Only a downwind
/// or base aircraft separates them, which is what these arms are.
/// </summary>
public class AssignedThresholdDistanceTests
{
    private const double RunwayLengthFt = 10_000;

    private static readonly RunwayInfo Runway = MakeRunway();

    private static RunwayInfo MakeRunway()
    {
        var threshold = new LatLon(37.0, -122.0);
        LatLon end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), RunwayLengthFt / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280
        );
    }

    private static LatLon Threshold => new(Runway.ThresholdLatitude, Runway.ThresholdLongitude);

    /// <summary>An aircraft the given distance out on the final approach course, tracking <paramref name="trackDeg"/>.</summary>
    private static AircraftState Arrival(double distanceOutNm, double trackDeg) =>
        new()
        {
            Callsign = "ARR1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distanceOutNm),
            TrueHeading = new TrueHeading(trackDeg),
            TrueTrack = new TrueHeading(trackDeg),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
        };

    [Fact]
    public void StraightIn_MeasuresToTheLandingThreshold()
    {
        AircraftState ac = Arrival(distanceOutNm: 4.0, trackDeg: 280);

        double assigned = RunwayOccupancy.DistanceToAssignedThresholdNm(ac, Runway, layout: null);

        Assert.Equal(4.0, assigned, precision: 2);
    }

    [Fact]
    public void Downwind_StillMeasuresToTheLandingThreshold_NotTheReciprocalEnd()
    {
        // Same point in space as the straight-in case — only the track differs, as on a downwind leg.
        AircraftState ac = Arrival(distanceOutNm: 4.0, trackDeg: 100);

        double assigned = RunwayOccupancy.DistanceToAssignedThresholdNm(ac, Runway, layout: null);

        Assert.Equal(4.0, assigned, precision: 2);
    }

    [Fact]
    public void Downwind_TrackDerivedDatumGoesNegative_WhichIsWhyTheAssignedDatumExists()
    {
        AircraftState ac = Arrival(distanceOutNm: 4.0, trackDeg: 100);

        double trackDerived = RunwayOccupancy.DistanceToLandingThresholdNm(ac, Runway, layout: null);
        double assigned = RunwayOccupancy.DistanceToAssignedThresholdNm(ac, Runway, layout: null);

        Assert.True(
            trackDerived < 0,
            $"The track-derived datum is expected to read as past-the-threshold for a downwind aircraft, was {trackDerived:F2} nm."
        );
        Assert.True(assigned > 0, $"The assigned datum must keep a downwind aircraft ahead of the threshold, was {assigned:F2} nm.");
    }

    [Fact]
    public void Downwind_EtaStaysPositive_SoTheAircraftDoesNotSortAheadOfTrafficOnFinal()
    {
        AircraftState downwind = Arrival(distanceOutNm: 8.0, trackDeg: 100);
        AircraftState onFinal = Arrival(distanceOutNm: 3.0, trackDeg: 280);

        double downwindEta = RunwayOccupancy.SecondsToAssignedThreshold(downwind, Runway, layout: null);
        double finalEta = RunwayOccupancy.SecondsToAssignedThreshold(onFinal, Runway, layout: null);

        Assert.True(downwindEta > 0, $"Downwind ETA must be positive, was {downwindEta:F0} s.");
        Assert.True(
            downwindEta > finalEta,
            $"The aircraft 8 nm out on downwind must sequence behind the one 3 nm out on final, but its ETA was {downwindEta:F0} s against {finalEta:F0} s."
        );
    }

    [Fact]
    public void Stopped_HasNoEta()
    {
        AircraftState ac = Arrival(distanceOutNm: 4.0, trackDeg: 280);
        ac.IndicatedAirspeed = 0;

        Assert.True(double.IsPositiveInfinity(RunwayOccupancy.SecondsToAssignedThreshold(ac, Runway, layout: null)));
    }
}
