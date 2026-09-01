using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// 7110.65 §3-9-4 / §3-10-5 occupancy advisories: which occupants make a landing-family clearance
/// warn. Phase-driven occupants are found by clearance state; a generic holding-in-position aircraft
/// counts only when it is physically on the cleared runway's pavement.
/// </summary>
public class RunwaySafetyAdvisorTests
{
    private static readonly RunwayInfo Runway = MakeRunway();

    private static RunwayInfo MakeRunway()
    {
        var threshold = new LatLon(37.0, -122.0);
        var end = GeoMath.ProjectPoint(threshold, new TrueHeading(280), 10_000 / GeoMath.FeetPerNm);
        return TestRunwayFactory.Make(
            designator: "28",
            thresholdLat: threshold.Lat,
            thresholdLon: threshold.Lon,
            endLat: end.Lat,
            endLon: end.Lon,
            heading: 280,
            elevationFt: 100
        );
    }

    private static LatLon OnRunway(double alongFt, double rightFt)
    {
        var threshold = new LatLon(Runway.ThresholdLatitude, Runway.ThresholdLongitude);
        var along = GeoMath.ProjectPoint(threshold, Runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        return rightFt == 0 ? along : GeoMath.ProjectPoint(along, Runway.TrueHeading + 90, rightFt / GeoMath.FeetPerNm);
    }

    private static AircraftState Occupant(string callsign, LatLon position, Phase phase)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = Runway.ElevationFt,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = Runway },
        };
        ac.Phases.Add(phase);
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return ac;
    }

    private static AircraftState Arrival() =>
        new()
        {
            Callsign = "ARR1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(new LatLon(Runway.ThresholdLatitude, Runway.ThresholdLongitude), Runway.TrueHeading.ToReciprocal(), 4),
            TrueHeading = Runway.TrueHeading,
            TrueTrack = Runway.TrueHeading,
            Altitude = Runway.ElevationFt + 1200,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = Runway },
        };

    private static List<string> WarningsAfterClearance(AircraftState arrival, AircraftState occupant)
    {
        var ctx = TestDispatch.Context(new Random(1), listAircraft: () => [arrival, occupant]);
        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway, ctx);
        return arrival.PendingWarnings;
    }

    [Fact]
    public void LinedUpAndWaitingOccupant_Warns()
    {
        var occupant = Occupant("LUAW1", OnRunway(3000, 500), new LinedUpAndWaitingPhase());

        var warnings = WarningsAfterClearance(Arrival(), occupant);

        Assert.Single(warnings);
        Assert.Contains("LUAW1", warnings[0]);
    }

    [Fact]
    public void HoldingInPositionOnTheParallelTaxiway_IsSilent()
    {
        var occupant = Occupant("HOLD1", OnRunway(3000, 500), new HoldingInPositionPhase());

        Assert.Empty(WarningsAfterClearance(Arrival(), occupant));
    }

    [Fact]
    public void HoldingInPositionOnThePavement_Warns()
    {
        var occupant = Occupant("HOLD1", OnRunway(200, 10), new HoldingInPositionPhase());

        var warnings = WarningsAfterClearance(Arrival(), occupant);

        Assert.Single(warnings);
        Assert.Contains("HOLD1", warnings[0]);
    }

    private static List<string> WarningsAfterSecondLuaw(AircraftState second, AircraftState first)
    {
        var ctx = TestDispatch.Context(new Random(1), listAircraft: () => [second, first]);
        RunwaySafetyAdvisor.WarnIfAnotherHoldingInPosition(second, Runway, ctx);
        return second.PendingWarnings;
    }

    [Fact]
    public void SecondLuaw_WithAnotherAircraftHoldingInPosition_Warns()
    {
        // 3-9-4.h: two aircraft lined up on the same runway at once needs the local assist/monitor staffed.
        var first = Occupant("LUAW1", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        var second = Occupant("LUAW2", OnRunway(4000, 0), new LinedUpAndWaitingPhase());

        var warnings = WarningsAfterSecondLuaw(second, first);

        Assert.Single(warnings);
        Assert.Contains("LUAW1", warnings[0]);
        Assert.Contains("3-9-4.h", warnings[0]);
    }

    [Fact]
    public void SecondLuaw_BehindADepartureAlreadyClearedForTakeoff_IsSilent()
    {
        var rolling = Occupant("DEP1", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        rolling.Phases!.CurrentPhase!.Requirements[0].IsSatisfied = true;
        var second = Occupant("LUAW2", OnRunway(4000, 0), new LinedUpAndWaitingPhase());

        Assert.Empty(WarningsAfterSecondLuaw(second, rolling));
    }

    [Fact]
    public void SecondLuaw_OnTheOppositeEndOfTheSamePavement_Warns()
    {
        var first = Occupant("LUAW1", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        first.Phases!.AssignedRunway = Runway.ForApproach("10");
        var second = Occupant("LUAW2", OnRunway(4000, 0), new LinedUpAndWaitingPhase());

        Assert.Single(WarningsAfterSecondLuaw(second, first));
    }

    [Fact]
    public void SecondLuaw_OnADifferentRunway_IsSilent()
    {
        var first = Occupant("LUAW1", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        first.Phases!.AssignedRunway = TestRunwayFactory.Make(
            designator: "33",
            thresholdLat: 37.02,
            thresholdLon: -122.02,
            endLat: 37.04,
            endLon: -122.03,
            heading: 330
        );
        var second = Occupant("LUAW2", OnRunway(4000, 0), new LinedUpAndWaitingPhase());

        Assert.Empty(WarningsAfterSecondLuaw(second, first));
    }

    [Fact]
    public void DesignatorOverload_HoldingInPositionOnItsOwnRunway_Warns()
    {
        var occupant = Occupant("HOLD1", OnRunway(200, 10), new HoldingInPositionPhase());
        var arrival = Arrival();
        var ctx = TestDispatch.Context(new Random(1), listAircraft: () => [arrival, occupant]);

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, "28", ctx);

        Assert.Single(arrival.PendingWarnings);
    }

    private static List<string> WarningsAfterTakeoffClearance(AircraftState departure, AircraftState occupant)
    {
        var ctx = TestDispatch.Context(new Random(1), listAircraft: () => [departure, occupant]);
        RunwaySafetyAdvisor.WarnIfRunwayOccupiedForTakeoff(departure, Runway, ctx);
        return departure.PendingWarnings;
    }

    [Fact]
    public void TakeoffClearance_OverLuawOccupant_Warns()
    {
        // Issue #409: CTO issued to an aircraft holding short while another aircraft is
        // lined up and waiting on the same runway without a takeoff clearance.
        var occupant = Occupant("LUAW1", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        var departure = Occupant(
            "DEP1",
            OnRunway(0, 300),
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28",
                }
            )
        );

        var warnings = WarningsAfterTakeoffClearance(departure, occupant);

        Assert.Single(warnings);
        Assert.Contains("LUAW1", warnings[0]);
        Assert.Contains("3-9-6", warnings[0]);
    }

    [Fact]
    public void TakeoffClearance_BehindPrecedingDepartureAlreadyCleared_IsSilent()
    {
        // The occupant holds its own takeoff clearance — it is about to roll; clearing the
        // next departure is ordinary anticipated separation (3-9-5).
        var occupant = Occupant("DEP0", OnRunway(200, 0), new LinedUpAndWaitingPhase());
        occupant.Phases!.CurrentPhase!.Requirements[0].IsSatisfied = true;
        var departure = Occupant(
            "DEP1",
            OnRunway(0, 300),
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28",
                }
            )
        );

        Assert.Empty(WarningsAfterTakeoffClearance(departure, occupant));
    }

    [Fact]
    public void TakeoffClearance_HoldingInPositionOnThePavement_Warns()
    {
        var occupant = Occupant("HOLD1", OnRunway(200, 10), new HoldingInPositionPhase());
        var departure = Occupant(
            "DEP1",
            OnRunway(0, 300),
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28",
                }
            )
        );

        var warnings = WarningsAfterTakeoffClearance(departure, occupant);

        Assert.Single(warnings);
        Assert.Contains("HOLD1", warnings[0]);
    }

    [Fact]
    public void TakeoffClearance_HoldingInPositionOnTheParallelTaxiway_IsSilent()
    {
        var occupant = Occupant("HOLD1", OnRunway(3000, 500), new HoldingInPositionPhase());
        var departure = Occupant(
            "DEP1",
            OnRunway(0, 300),
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28",
                }
            )
        );

        Assert.Empty(WarningsAfterTakeoffClearance(departure, occupant));
    }

    [Fact]
    public void TakeoffClearance_LuawOnTheOppositeEndOfTheSamePavement_Warns()
    {
        var occupant = Occupant("LUAW1", OnRunway(9800, 0), new LinedUpAndWaitingPhase());
        occupant.Phases!.AssignedRunway = Runway.ForApproach("10");
        var departure = Occupant(
            "DEP1",
            OnRunway(0, 300),
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28",
                }
            )
        );

        Assert.Single(WarningsAfterTakeoffClearance(departure, occupant));
    }
}
