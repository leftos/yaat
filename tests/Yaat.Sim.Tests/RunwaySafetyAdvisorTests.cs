using Xunit;
using Yaat.Sim.Commands;
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

    [Fact]
    public void DesignatorOverload_HoldingInPositionOnItsOwnRunway_Warns()
    {
        var occupant = Occupant("HOLD1", OnRunway(200, 10), new HoldingInPositionPhase());
        var arrival = Arrival();
        var ctx = TestDispatch.Context(new Random(1), listAircraft: () => [arrival, occupant]);

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, "28", ctx);

        Assert.Single(arrival.PendingWarnings);
    }
}
