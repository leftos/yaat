using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// A glidepath is referenced to the touchdown zone elevation of the landing end, not to field
/// elevation. On a sloped runway the two ends differ — KASE 15 is 7,680 ft and 33 is 7,820 ft — so an
/// approach flown to field elevation is offset by that difference the whole way down final.
///
/// The vNAS nav data carries one elevation per airport, so the per-end elevations come from the CIFP
/// airport-runway records' landing threshold elevation field.
/// </summary>
public class RunwayThresholdElevationTests
{
    private readonly ITestOutputHelper _output;

    public RunwayThresholdElevationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private RunwayInfo? Runway(string airport, string designator)
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway(airport, designator);
        if (rwy is not null)
        {
            _output.WriteLine($"{airport} {designator}: thresholdElev={rwy.ElevationFt:F0}ft");
        }

        return rwy;
    }

    /// <summary>
    /// KASE (Aspen) climbs ~140 ft from the 15 threshold to the 33 threshold, and KSJC's four ends
    /// range over 38–57 ft against a 62 ft field elevation. Each end must report its own.
    /// </summary>
    [Theory]
    [InlineData("KASE", "15", 7680)]
    [InlineData("KASE", "33", 7820)]
    [InlineData("KSJC", "12L", 38)]
    [InlineData("KSJC", "30L", 57)]
    [InlineData("KSJC", "30R", 55)]
    [InlineData("KOAK", "28R", 6)]
    public void RunwayEndsCarryTheirOwnThresholdElevation(string airport, string designator, double expectedFt)
    {
        var rwy = Runway(airport, designator);
        if (rwy is null)
        {
            return;
        }

        Assert.Equal(expectedFt, rwy.ElevationFt, 0);
    }

    /// <summary>
    /// The glidepath must sit on the landing end's elevation, so at KASE the two directions'
    /// paths differ by the runway's slope at every distance — not just at the threshold.
    /// </summary>
    [Fact]
    public void GlidepathFollowsTheLandingEndsElevation()
    {
        var fifteen = Runway("KASE", "15");
        var thirtyThree = Runway("KASE", "33");
        if (fifteen is null || thirtyThree is null)
        {
            return;
        }

        double at3Nm15 = GlideSlopeGeometry.AltitudeAtDistance(3.0, fifteen.ElevationFt, AircraftCategory.Jet);
        double at3Nm33 = GlideSlopeGeometry.AltitudeAtDistance(3.0, thirtyThree.ElevationFt, AircraftCategory.Jet);
        _output.WriteLine($"KASE 3 nm final: RWY 15 {at3Nm15:F0} ft MSL, RWY 33 {at3Nm33:F0} ft MSL");

        Assert.Equal(140, at3Nm33 - at3Nm15, 0);
    }

    /// <summary>
    /// The traffic pattern does not tilt with the runway. AIM 4-3-3 recommends one pattern altitude
    /// above the field and the Chart Supplement publishes one value, so both of KASE's directions must
    /// report the same field elevation even though their thresholds are 140 ft apart.
    /// </summary>
    [Fact]
    public void PatternAltitudeKeepsTheAirportDatum()
    {
        var fifteen = Runway("KASE", "15");
        var thirtyThree = Runway("KASE", "33");
        if (fifteen is null || thirtyThree is null)
        {
            return;
        }

        Assert.Equal(fifteen.AirportElevationFt, thirtyThree.AirportElevationFt, 3);
        Assert.NotEqual(fifteen.ElevationFt, thirtyThree.ElevationFt);

        // Published KASE field elevation. It is neither threshold (7,680 / 7,820) nor their mean
        // (7,750), so this pins the value to the airport record rather than anything derived from the
        // ends — including the fallback.
        Assert.Equal(7838, fifteen.AirportElevationFt, 0);
    }

    /// <summary>
    /// A runway built without an explicit field elevation — every hand-made test fixture, and every
    /// snapshot written before the ends carried their own elevations — falls back to the mean of its two
    /// ends. On the level runways those all describe, that is exactly the elevation they used to report.
    /// </summary>
    [Fact]
    public void AirportElevationFallsBackToTheMeanOfTheEnds()
    {
        var level = TestRunwayFactory.Make(elevationFt: 9, endElevationFt: 9);
        Assert.Equal(9, level.AirportElevationFt, 3);

        var sloped = TestRunwayFactory.Make(elevationFt: 100, endElevationFt: 200);
        Assert.Equal(150, sloped.AirportElevationFt, 3);
    }
}
