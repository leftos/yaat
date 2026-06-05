using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
/// <summary>
/// EF ("Enter Final") is an altitude-aware "make straight-in". A diagonal aircraft
/// descends immediately on the cut-in toward the runway and joins final as CLOSE to the
/// threshold as it can while still reaching the glideslope by the join — a shortcut, not
/// a fixed base. A low aircraft shortcuts to the minimum stabilized final; a higher one
/// (which needs more descent room) joins farther out. The join is capped at the aircraft's
/// along-track so EF never routes it outbound.
///
/// Regression for the N713UP report: a piston ~4.5 nm out along the 28R extended
/// centerline, ~6 nm right (north) of it, 52° off the runway heading, at 2500 ft, was
/// pulled to a fixed ~3.14 nm entry (the TPA/glideslope intercept), forcing a "3.5 mile
/// base." With ~2491 ft to lose over a ~7 nm diagonal at the 700 fpm piston pattern rate
/// it can be on the glideslope by a ~1 nm final, so it should shortcut close-in.
///
/// Uses OAK 28R (heading 292°, right traffic → pattern side NNE), mirroring
/// <see cref="ErbElbNoDistanceTests"/>.
/// </summary>
public class EfAltitudeAwareStraightInTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public EfAltitudeAwareStraightInTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeOak28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.72152,
            thresholdLon: -122.20065,
            endLat: 37.73089,
            endLon: -122.21926,
            heading: 292,
            elevationFt: 9,
            lengthFt: 6213,
            widthFt: 150
        );
    }

    // BE36 piston (Bonanza), VFR — matches the reported N713UP.
    private static AircraftState MakeAircraft(double lat, double lon, double alt, double heading)
    {
        return new AircraftState
        {
            Callsign = "N713UP",
            AircraftType = "BE36",
            Position = new LatLon(lat, lon),
            Altitude = alt,
            TrueHeading = new TrueHeading(heading),
            IndicatedAirspeed = 128,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Phases = new PhaseList(),
        };
    }

    private static (double Lat, double Lon) PositionFromThreshold(RunwayInfo runway, double alongTrackOutboundNm, double crossTrackRightNm)
    {
        var reciprocal = new TrueHeading((runway.TrueHeading.Degrees + 180) % 360);
        var centerline = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, alongTrackOutboundNm);
        double crossHdg = crossTrackRightNm >= 0 ? (runway.TrueHeading.Degrees + 90) % 360 : (runway.TrueHeading.Degrees + 270) % 360;
        var result = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, new TrueHeading(crossHdg), Math.Abs(crossTrackRightNm));
        return (result.Lat, result.Lon);
    }

    private PatternEntryPhase RequireEntryPhase(AircraftState aircraft)
    {
        var entry = aircraft.Phases!.Phases.OfType<PatternEntryPhase>().FirstOrDefault();
        Assert.NotNull(entry);
        return entry!;
    }

    private static double EntryDistanceToThresholdNm(RunwayInfo runway, PatternEntryPhase entry) =>
        GeoMath.DistanceNm(new LatLon(entry.EntryLat, entry.EntryLon), new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));

    private static double EntryCrossTrackNm(RunwayInfo runway, PatternEntryPhase entry) =>
        Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                new LatLon(entry.EntryLat, entry.EntryLon),
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                runway.TrueHeading
            )
        );

    // N713UP geometry: ~4.5 nm along-track, ~6 nm right of centerline, 52° off heading.
    private const double AlongTrackNm = 4.5;
    private const double CrossTrackNm = 6.0;
    private const double HeadingDeg = 240;

    /// <summary>
    /// Runs EF on a BE36 at <paramref name="altAgl"/> from the N713UP geometry and returns
    /// the resulting Final-entry point's distance to the threshold (its along-track join
    /// distance), plus whether a too-high warning was raised.
    /// </summary>
    private (double JoinNm, bool Warned, double CrossNm) RunEf(RunwayInfo runway, double altAgl)
    {
        var (lat, lon) = PositionFromThreshold(runway, AlongTrackNm, CrossTrackNm);
        var aircraft = MakeAircraft(lat, lon, alt: runway.ElevationFt + altAgl, heading: HeadingDeg);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Final, "28R", null);
        Assert.True(result.Success, result.Message);

        var entry = RequireEntryPhase(aircraft);
        double join = EntryDistanceToThresholdNm(runway, entry);
        double cross = EntryCrossTrackNm(runway, entry);
        bool warned = aircraft.PendingWarnings.Count > 0;
        _output.WriteLine($"alt={altAgl:F0} AGL -> join={join:F2} nm, cross={cross:F2} nm, warned={warned}");
        return (join, warned, cross);
    }

    [Fact]
    public void EF_HighButDescendable_ShortcutsCloseToThreshold()
    {
        var runway = MakeOak28R();
        var (join, warned, cross) = RunEf(runway, altAgl: 2491);

        // ~2491 ft over a ~7 nm diagonal at 700 fpm is plenty to reach the glideslope by a
        // ~1 nm final, so EF shortcuts close-in — NOT the old fixed ~3.14 nm base, and NOT
        // a perpendicular base out at the ~4.5 nm along-track.
        Assert.True(join < 2.0, $"Expected a close-in shortcut (< 2 nm), got {join:F2} nm");
        Assert.InRange(join, 0.9, 1.6);
        Assert.True(cross < 0.1, "Entry should be on the extended centerline");
        // Descent is feasible on the diagonal at this altitude → no warning.
        Assert.False(warned, "Descendable aircraft should not raise a too-high warning");
    }

    [Fact]
    public void EF_JoinDistanceIsMonotonicInAltitude()
    {
        var runway = MakeOak28R();

        double low = RunEf(runway, altAgl: 800).JoinNm;
        double mid = RunEf(runway, altAgl: 2491).JoinNm;
        double high = RunEf(runway, altAgl: 5000).JoinNm;
        double veryHigh = RunEf(runway, altAgl: 8000).JoinNm;

        // The higher the aircraft, the more descent room it needs, so it can shortcut less:
        // the join distance never decreases with altitude.
        Assert.True(low <= mid + 1e-6, $"join(800)={low:F2} should be <= join(2491)={mid:F2}");
        Assert.True(mid <= high + 1e-6, $"join(2491)={mid:F2} should be <= join(5000)={high:F2}");
        Assert.True(high <= veryHigh + 1e-6, $"join(5000)={high:F2} should be <= join(8000)={veryHigh:F2}");

        // A low aircraft shortcuts to the minimum stabilized final (close-in)...
        Assert.True(low <= 1.6, $"Low aircraft should shortcut to the minimum final, got {low:F2} nm");
        // ...while a very high one is pushed out toward the along-track cap — strictly farther.
        Assert.True(veryHigh > low + 1.0, $"High aircraft should join meaningfully farther out than a low one");

        // Never beyond the along-track cap (no outbound).
        Assert.True(veryHigh <= AlongTrackNm + 0.2, $"Join must stay capped at along-track, got {veryHigh:F2} nm");
    }

    [Fact]
    public void EF_TooHighToDescend_CapsAtAlongTrackWithWarning()
    {
        var runway = MakeOak28R();
        var (join, warned, _) = RunEf(runway, altAgl: 8000);

        // Command still succeeds (RPO decides), capped at along-track (no outbound)...
        Assert.InRange(join, AlongTrackNm - 0.3, AlongTrackNm + 0.2);

        // ...but warns that the aircraft cannot descend safely.
        Assert.True(warned, "Too-high aircraft should raise a controller-facing warning");
    }
}
