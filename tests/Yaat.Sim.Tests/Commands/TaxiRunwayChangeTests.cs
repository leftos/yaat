using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A <c>TAXI</c> that names a runway other than the one the aircraft is already going to. 7110.65 3-7-2 frames the
/// runway in a taxi clearance as a confirmation, so the clearance is honoured — the runway is set, and the SID
/// runway transition and initial climb will be resolved from it — and the controller is warned, because the
/// pre-issued departure clearance does not survive the re-clearance and 5-8-2.b wants the amended one issued
/// before the aircraft enters the runway. Only a genuine change warns: a first assignment, a re-statement of the
/// same runway, and an aircraft with nothing briefed to amend are all silent.
///
/// The same write is gated for an arrival: taxiing an arrival to (or across) a runway must not give it a departure
/// runway, mirroring <c>GroundCommandHandler.TryAssignRunway</c>'s arrival test.
/// </summary>
public class TaxiRunwayChangeTests(ITestOutputHelper output)
{
    /// <summary>The warning a change draws when the departure clearance had been read to the crew.</summary>
    private static string DepartureClearanceWarning(string assigned, string briefed) =>
        $"taxi clearance names runway {assigned} but the departure runway assigned is {briefed} — this is a runway change, "
        + $"not a confirmation (7110.65 3-7-2); the departure clearance issued for {briefed} no longer applies and the SID "
        + $"runway transition will be resolved for {assigned} when the amended clearance is issued, so issue it before the "
        + "aircraft enters the runway (4-3-2.c.1 NOTE 1)";

    /// <summary>The warning a change draws when only a takeoff clearance had been stored during the taxi.</summary>
    private static string TakeoffClearanceWarning(string assigned, string briefed) =>
        $"taxi clearance names runway {assigned} but the departure runway assigned is {briefed} — this is a runway change, "
        + $"not a confirmation (7110.65 3-7-2); the takeoff clearance issued for {briefed} is void — a takeoff clearance is "
        + "runway-specific (3-9-10.a) and must be cancelled explicitly (3-9-11)";

    private static AirportGroundLayout? OakLayout()
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    /// <summary>A jet at KOAK's NEW7 parking with SKYL1 filed.</summary>
    private static AircraftState MakeOakDeparture(AirportGroundLayout layout)
    {
        var parking = layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.Parking) && string.Equals(n.Name, "NEW7", StringComparison.OrdinalIgnoreCase)
        );

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = parking.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSMF",
                Route = "SKYL1",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(23000),
            },
        };
        ac.Ground.Layout = layout;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, layout));
        return ac;
    }

    /// <summary>Assigns <paramref name="runway"/> as the departure runway, with nothing briefed against it yet.</summary>
    private static void AssignDepartureRunway(AircraftState ac, string runway)
    {
        var resolved = CommandDispatcher.ResolveRunway(ac, runway);
        Assert.NotNull(resolved);
        ac.Phases!.AssignedRunway = resolved;
        ac.Procedure.DepartureRunway = resolved.Designator;
    }

    /// <summary>Assigns the runway and the SID initial altitude a departure clearance leaves behind.</summary>
    private static void BriefDepartureRunway(AircraftState ac, string runway)
    {
        AssignDepartureRunway(ac, runway);
        ac.Procedure.SidInitialAltitudeFt = 5000;
    }

    private static CommandResult TaxiTo(AircraftState ac, AirportGroundLayout layout, string runway) =>
        GroundCommandHandler.TryTaxi(ac, new TaxiCommand(["D", "C", "B"], [], DestinationRunway: runway), layout);

    [Fact]
    public void TaxiNamingADifferentRunway_AssignsIt_DropsTheClearance_AndWarnsTheChange()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28L");

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Phases!.AssignedRunway?.Designator);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);

        // Nothing is re-derived at taxi time: the fresh phase list carries no departure clearance, and the SID
        // runway transition is resolved from the new AssignedRunway when the amended clearance is issued.
        Assert.Null(ac.Phases.DepartureClearance);

        Assert.Contains(DepartureClearanceWarning("28R", "28L"), ac.PendingWarnings);
        Assert.DoesNotContain(TakeoffClearanceWarning("28R", "28L"), ac.PendingWarnings);
    }

    /// <summary>The reciprocal end is a different departure runway, not a confirmation of the same one.</summary>
    [Fact]
    public void TaxiNamingTheReciprocalEnd_WarnsTheChange()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28L");

        var result = TaxiTo(ac, layout, "10R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("10R", ac.Procedure.DepartureRunway);
        Assert.Contains(DepartureClearanceWarning("10R", "28L"), ac.PendingWarnings);
    }

    /// <summary>
    /// The state the warning actually guards, reached the way a session reaches it: a departure taxiing to 28L is
    /// cleared for takeoff while still taxiing (the clearance is stored on the phase list), and is then
    /// re-cleared to 28R. Nothing has been briefed off a SID here — only the takeoff clearance is at stake, and
    /// the warning says so and nothing more.
    /// </summary>
    [Fact]
    public void TaxiToAnotherRunwayAfterACtoStoredDuringTaxi_WarnsAndDropsTheClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        AssignDepartureRunway(ac, "28L");
        Assert.True(TaxiTo(ac, layout, "28L").Success);

        var cto = CommandDispatcher.Dispatch(
            CommandParser.Parse("CTO").Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );
        output.WriteLine($"CTO: success={cto.Success} message=\"{cto.Message}\"");
        Assert.True(cto.Success, cto.Message);
        Assert.NotNull(ac.Phases!.DepartureClearance);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Empty(ac.PendingWarnings);

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
        Assert.Contains(TakeoffClearanceWarning("28R", "28L"), ac.PendingWarnings);
        Assert.DoesNotContain(DepartureClearanceWarning("28R", "28L"), ac.PendingWarnings);
        Assert.Null(ac.Phases.DepartureClearance);
    }

    [Fact]
    public void FirstRunwayAssignment_DoesNotWarn()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        ac.Procedure.SidInitialAltitudeFt = 5000;
        Assert.Null(ac.Procedure.DepartureRunway);

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void RestatingTheSameRunway_DoesNotWarn()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28R");

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void VfrDepartureWithNothingBriefed_DoesNotWarn()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        ac.FlightPlan.FlightRules = "VFR";
        ac.FlightPlan.Route = "";
        ac.Procedure.DepartureRunway = "28L";
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Phases!.DepartureClearance);

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void ArrivalTaxiingToARunway_KeepsDepartureRunwayNull()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        var ac = MakeOakDeparture(layout);
        ac.FlightPlan.Destination = "KOAK";
        ac.Procedure.ActiveStarId = "SERFR4";
        Assert.Null(ac.Phases!.DepartureClearance);

        var result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Procedure.DepartureRunway);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
    }
}
