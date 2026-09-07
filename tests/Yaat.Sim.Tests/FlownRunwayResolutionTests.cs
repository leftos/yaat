using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="PatternCommandHandler.ResolveFlownRunway"/> names the runway a pattern leg was built for
/// from the leg's own threshold waypoint — the input an MLT/MRT runway switch uses to decide what the
/// aircraft is leaving. <see cref="NavigationDatabase.GetRunways"/> returns one
/// <see cref="RunwayInfo"/> per physical runway anchored at one end, so a leg built for the other end
/// has to be matched against that end's geometry: OAK's 28R and 10L are one record, and a resolver that
/// only compares <see cref="RunwayInfo.ThresholdLatitude"/> silently fails for whichever end the record
/// is not anchored at.
/// </summary>
public class FlownRunwayResolutionTests
{
    private const string AirportId = "OAK";

    /// <summary>
    /// Both ends of every runway resolve to themselves. PAO 13/31 is the short-runway case: 2,442 ft
    /// (0.40 nm) end to end, so the opposite threshold sits well inside a fixed half-mile downfield
    /// window — the nearest end has to win, and the window has to be capped at half the pavement, or a
    /// pattern for 31 reads as one for 13 and a same-runway direction change looks like a runway switch.
    /// </summary>
    [Theory]
    [InlineData("OAK", "28R")]
    [InlineData("OAK", "10L")]
    [InlineData("OAK", "28L")]
    [InlineData("OAK", "10R")]
    [InlineData("PAO", "13")]
    [InlineData("PAO", "31")]
    public void PatternWaypoints_ResolveToTheRunwayEndTheyWereBuiltFor(string airportId, string designator)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = NavigationDatabase.Instance.GetRunway(airportId, designator);
        if (runway is null)
        {
            return;
        }

        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var waypoints = PatternGeometry.Compute(
            runway,
            AircraftCategory.Piston,
            "DA42",
            0,
            PatternDirection.Left,
            null,
            null,
            airportRunways,
            authoredRunway: null
        );

        var resolved = PatternCommandHandler.ResolveFlownRunway(waypoints, airportRunways);

        Assert.NotNull(resolved);
        Assert.Equal(RunwayIdentifier.NormalizeDesignator(designator), resolved.Designator);
    }

    /// <summary>
    /// The parallels are 0.165 nm apart at OAK — far enough outside the centerline tolerance that a
    /// 28R leg can never be read as 28L. Getting this wrong makes a runway switch look like a
    /// same-runway direction change and skips the transition circuit entirely.
    /// </summary>
    [Fact]
    public void Waypoints_ForOneParallel_DoNotResolveToTheOther()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R");
        if (rwy28R is null)
        {
            return;
        }

        var airportRunways = NavigationDatabase.Instance.GetRunways(rwy28R.AirportId);
        var waypoints = PatternGeometry.Compute(
            rwy28R,
            AircraftCategory.Piston,
            "DA42",
            0,
            PatternDirection.Right,
            null,
            null,
            airportRunways,
            authoredRunway: null
        );

        var resolved = PatternCommandHandler.ResolveFlownRunway(waypoints, airportRunways);

        Assert.NotNull(resolved);
        Assert.NotEqual("28L", resolved.Designator);
        Assert.Equal("28R", resolved.Designator);
    }
}
