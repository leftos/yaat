using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Two E75Ls cross SFO's runway 01L/19R in trail on taxiway F with the crossing pre-cleared
/// (<c>AutoCrossRunway</c>). Both hold their route position on the crossing's entry segment while they cross, so
/// the ground detector reads them as a same-edge in-trail pair for the whole crossing. The follower must keep its
/// gap behind the leader and both must clear the far-side hold-short: ordering the pair by straight-line distance to
/// the entry segment's end node makes a leader already past that node read as the trailer, and the detector then
/// caps the leader for the aircraft behind it.
/// </summary>
public class RunwayCrossingInTrailTests(ITestOutputHelper output)
{
    private const string LeadingCallsign = "SKW401";
    private const string FollowingCallsign = "SKW402";
    private const string AircraftType = "E75L";
    private const string TaxiCommand = "TAXI F 28L";

    /// <summary>How far ahead of the follower the leader starts, along the taxiway.</summary>
    private const double TrailFt = SfoGroundHarness.CrossingTrailFt;

    /// <summary>Margin under the detector's stop ring the pair may close to while a cap takes effect.</summary>
    private const double StopRingMarginFt = 5.0;

    private const int BudgetSeconds = 120;

    [Fact]
    public void TwoInTrail_CrossSfoRunway_KeepTheirGapAndBothClearIt()
    {
        if (SfoGroundHarness.Build(output, autoCross: true) is not { } ground)
        {
            return;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        var crossingBearing = new TrueHeading(GeoMath.BearingTo(crossing.ApproachBar.Position, crossing.FarBar.Position));
        double approachFt = GeoMath.DistanceNm(crossing.ApproachNode.Position, crossing.ApproachBar.Position) * GeoMath.FeetPerNm;
        Assert.True(
            approachFt > TrailFt,
            $"the node the crossing is approached from is {approachFt:F0} ft short of the {SfoGroundHarness.CrossingRunway} bar, "
                + $"inside the {TrailFt:F0} ft trail: the leader would start past the bar"
        );

        AircraftState follower = SfoGroundHarness.SpawnAt(
            ground,
            FollowingCallsign,
            AircraftType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        AircraftState leader = SfoGroundHarness.SpawnAt(
            ground,
            LeadingCallsign,
            AircraftType,
            (crossing.ApproachNode, heading),
            new HoldingInPositionPhase()
        );
        leader.Position = GeoMath.ProjectPoint(crossing.ApproachNode.Position, heading, TrailFt / GeoMath.FeetPerNm);
        leader.TrueTrack = heading;
        output.WriteLine(
            $"{leader.Callsign} spawned {TrailFt:F0} ft ahead of {follower.Callsign} on {SfoGroundHarness.CrossingTaxiway}, "
                + $"{approachFt:F0} ft short of the {SfoGroundHarness.CrossingRunway} bar"
        );

        foreach (AircraftState ac in new[] { follower, leader })
        {
            CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
            output.WriteLine($"{ac.Callsign} {TaxiCommand}: {taxi.Success} — {taxi.Message}");
            Assert.True(taxi.Success, $"'{TaxiCommand}' was refused for {ac.Callsign}: {taxi.Message}");
            SfoGroundHarness.DumpRoute(output, ac.Ground.AssignedTaxiRoute!);
        }

        double separationFloorFt = GroundConflictDetectorTests.StopRingFt(AircraftType, AircraftType) - StopRingMarginFt;
        double minSeparationFt = double.MaxValue;
        int followerClearedAt = -1;
        int leaderClearedAt = -1;
        for (int second = 1; second <= BudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            minSeparationFt = Math.Min(minSeparationFt, GeoMath.DistanceNm(follower.Position, leader.Position) * GeoMath.FeetPerNm);

            if ((followerClearedAt < 0) && HasClearedTheCrossing(follower, crossing, crossingBearing))
            {
                followerClearedAt = second;
            }
            if ((leaderClearedAt < 0) && HasClearedTheCrossing(leader, crossing, crossingBearing))
            {
                leaderClearedAt = second;
            }
        }

        output.WriteLine(
            $"{BudgetSeconds}s: {follower.Callsign} cleared at {followerClearedAt}s ({follower.Phases?.CurrentPhase?.Name}), "
                + $"{leader.Callsign} cleared at {leaderClearedAt}s ({leader.Phases?.CurrentPhase?.Name}), "
                + $"min separation {minSeparationFt:F0} ft"
        );

        Assert.True(
            minSeparationFt > separationFloorFt,
            $"the pair closed to {minSeparationFt:F0} ft, inside the {separationFloorFt:F0} ft stop-ring floor"
        );
        Assert.True(followerClearedAt > 0, $"{follower.Callsign} did not clear the far-side hold-short out of the crossing within {BudgetSeconds}s");
        Assert.True(leaderClearedAt > 0, $"{leader.Callsign} did not clear the far-side hold-short out of the crossing within {BudgetSeconds}s");
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is past the crossing's far-side hold-short, measured along the crossing's
    /// direction, and no longer in a <see cref="CrossingRunwayPhase"/>.
    /// </summary>
    private static bool HasClearedTheCrossing(AircraftState aircraft, SfoCrossing crossing, TrueHeading crossingBearing) =>
        (aircraft.Phases?.CurrentPhase is not CrossingRunwayPhase)
        && (GeoMath.AlongTrackDistanceNm(aircraft.Position, crossing.FarBar.Position, crossingBearing) * GeoMath.FeetPerNm > 0.0);
}
