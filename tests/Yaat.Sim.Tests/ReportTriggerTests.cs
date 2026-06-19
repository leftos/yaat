using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Firing of the deferred REPORT triggers (GitHub issue #211): the one-shot n-mile-final and
/// at-fix reports voiced by <see cref="PilotProactive.TickReportTriggers"/>, and the pattern-leg
/// reports voiced by <see cref="PatternReportHelper.EmitTurningLeg"/> (which re-arm each circuit).
/// </summary>
public class ReportTriggerTests
{
    [Fact]
    public void AtFix_Fires_AndClears_WhenReached()
    {
        var ac = MakeAircraft(37.75, -122.0);
        ac.Approach.ReportAtFixName = "SUNOL";
        ac.Approach.ReportAtFixLat = 37.75;
        ac.Approach.ReportAtFixLon = -122.0;

        PilotProactive.TickReportTriggers(ac, MakeScenario());

        Assert.Null(ac.Approach.ReportAtFixName);
        var tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains("passing SUNOL", tx.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtFix_DoesNotFire_WhenFarAway()
    {
        var ac = MakeAircraft(38.5, -122.0);
        ac.Approach.ReportAtFixName = "SUNOL";
        ac.Approach.ReportAtFixLat = 37.75;
        ac.Approach.ReportAtFixLon = -122.0;

        PilotProactive.TickReportTriggers(ac, MakeScenario());

        Assert.Equal("SUNOL", ac.Approach.ReportAtFixName);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void MileFinal_Fires_AndClears_WhenInsideDistance()
    {
        var ac = MakeAircraft(37.788, -122.221); // ~4 nm north of the 28R threshold
        ac.Phases = new PhaseList { AssignedRunway = MakeRunway() };
        ac.Phases.Add(new FinalApproachPhase()); // makes the aircraft inbound-to-land
        ac.Approach.ReportFinalMileTarget = 5;

        PilotProactive.TickReportTriggers(ac, MakeScenario());

        Assert.Null(ac.Approach.ReportFinalMileTarget);
        var tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains("5-mile final", tx.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MileFinal_DoesNotFire_WhenNotInbound()
    {
        // No FinalApproachPhase → not inbound-to-land → a same-runway departure never reports final.
        var ac = MakeAircraft(37.788, -122.221);
        ac.Phases = new PhaseList { AssignedRunway = MakeRunway() };
        ac.Approach.ReportFinalMileTarget = 5;

        PilotProactive.TickReportTriggers(ac, MakeScenario());

        Assert.Equal(5, ac.Approach.ReportFinalMileTarget);
        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void PatternLeg_Fires_WhenArmed_AndStaysArmedForNextLap()
    {
        var ac = MakeAircraft(37.75, -122.221);
        ac.Approach.ReportArmedBase = true;

        PatternReportHelper.EmitTurningLeg(MakePhaseContext(ac), ReportTrigger.Base);

        var tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains("turning base", tx.Text, StringComparison.OrdinalIgnoreCase);
        // Pattern-leg reports persist so they re-arm on the next circuit.
        Assert.True(ac.Approach.ReportArmedBase);
    }

    [Fact]
    public void PatternLeg_Silent_WhenNotArmed()
    {
        var ac = MakeAircraft(37.75, -122.221);

        PatternReportHelper.EmitTurningLeg(MakePhaseContext(ac), ReportTrigger.Base);

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    private static AircraftState MakeAircraft(double lat, double lon) =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };

    private static SimScenarioState MakeScenario() =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
        };

    private static PhaseContext MakePhaseContext(AircraftState ac) =>
        new()
        {
            Aircraft = ac,
            Targets = new ControlTargets(),
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1,
            Logger = NullLogger.Instance,
            Runway = MakeRunway(),
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
        };

    private static RunwayInfo MakeRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.721,
            Lon1 = -122.221,
            Elevation1Ft = 9,
            TrueHeading1 = new TrueHeading(284),
            Lat2 = 37.73,
            Lon2 = -122.18,
            Elevation2Ft = 9,
            TrueHeading2 = new TrueHeading(104),
            LengthFt = 10000,
            WidthFt = 150,
        };
}
