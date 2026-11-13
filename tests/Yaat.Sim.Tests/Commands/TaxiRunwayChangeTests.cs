using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
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
        GroundNode parking = layout.Nodes.Values.First(n =>
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
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty, layout));
        return ac;
    }

    /// <summary>Assigns <paramref name="runway"/> as the departure runway, with nothing briefed against it yet.</summary>
    private static void AssignDepartureRunway(AircraftState ac, string runway)
    {
        RunwayInfo? resolved = CommandDispatcher.ResolveRunway(ac, runway);
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
        GroundCommandHandler.TryTaxi(ac, new TaxiCommand(["D", "C", "B"], [], Expedite: false, DestinationRunway: runway), layout);

    [Fact]
    public void TaxiNamingADifferentRunway_AssignsIt_DropsTheClearance_AndWarnsTheChange()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28L");

        CommandResult result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Phases!.AssignedRunway?.Designator);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);

        // Nothing is re-derived at taxi time: the fresh phase list carries no departure clearance, and the SID
        // runway transition is resolved from the new AssignedRunway when the amended clearance is issued.
        Assert.Null(ac.Phases.DepartureClearance);

        Assert.Contains(DepartureClearanceWarning("28R", "28L"), ac.PendingWarnings);
        Assert.DoesNotContain(TakeoffClearanceWarning("28R", "28L"), ac.PendingWarnings);
    }

    /// <summary>
    /// A departure whose clearance put a climb via in force rather than a maintain (a TDLS "CLIMB VIA SID EXC MAINT 4000FT"
    /// default, as at KIAH) has had its departure clearance read just the same: re-taxied to another runway, it draws the
    /// departure-clearance warning.
    /// </summary>
    [Fact]
    public void TaxiNamingADifferentRunway_AfterAClimbViaClearance_WarnsTheChange()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        AssignDepartureRunway(ac, "28L");
        ac.Procedure.SidViaMode = true;
        ac.Procedure.SidViaCeiling = 4000;
        ac.Procedure.ClimbViaTargetFt = 4000;
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);

        CommandResult result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Procedure.DepartureRunway);
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

        AircraftState ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28L");

        CommandResult result = TaxiTo(ac, layout, "10R");

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

        AircraftState ac = MakeOakDeparture(layout);
        AssignDepartureRunway(ac, "28L");
        Assert.True(TaxiTo(ac, layout, "28L").Success);

        CommandResult cto = CommandDispatcher.Dispatch(
            CommandParser.Parse("CTO").Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );
        output.WriteLine($"CTO: success={cto.Success} message=\"{cto.Message}\"");
        Assert.True(cto.Success, cto.Message);
        Assert.NotNull(ac.Phases!.DepartureClearance);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        // The filed SKYL1 is the navdata's SKYL2, and with no ARTCC config there is no TDLS value for it: the bare CTO warns
        // that the departure has no initial altitude, and nothing else.
        Assert.Equal(["no initial altitude for TEST1; assign one"], ac.PendingWarnings);

        CommandResult result = TaxiTo(ac, layout, "28R");

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

        AircraftState ac = MakeOakDeparture(layout);
        ac.Procedure.SidInitialAltitudeFt = 5000;
        Assert.Null(ac.Procedure.DepartureRunway);

        CommandResult result = TaxiTo(ac, layout, "28R");

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

        AircraftState ac = MakeOakDeparture(layout);
        BriefDepartureRunway(ac, "28R");

        CommandResult result = TaxiTo(ac, layout, "28R");

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

        AircraftState ac = MakeOakDeparture(layout);
        ac.FlightPlan.FlightRules = "VFR";
        ac.FlightPlan.Route = "";
        ac.Procedure.DepartureRunway = "28L";
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Phases!.DepartureClearance);

        CommandResult result = TaxiTo(ac, layout, "28R");

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

        AircraftState ac = MakeOakDeparture(layout);
        ac.FlightPlan.Destination = "KOAK";
        ac.Procedure.ActiveStarId = "SERFR4";
        Assert.Null(ac.Phases!.DepartureClearance);

        CommandResult result = TaxiTo(ac, layout, "28R");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Procedure.DepartureRunway);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
    }

    // A re-TAXI installs a fresh phase list, so it drops every clearance stored on the old one, for the same runway as for a
    // different one. It keeps dropping them, and the response names each one it dropped (docs/phases.md, "A re-TAXI drops stored
    // clearances and says so"). A runway change already warns that the takeoff clearance is void, so that one is not named again.

    private const string DroppedNote = "(dropped:";

    private static CommandResult Dispatch(string command, AircraftState ac, AirportGroundLayout layout) =>
        CommandDispatcher.Dispatch(CommandParser.Parse(command).Value!, ac, TestDispatch.Context(new SerializableRandom(42), groundLayout: layout));

    /// <summary>Taxis the departure to <paramref name="runway"/> and stores <paramref name="clearance"/> (CTO or LUAW) during the taxi.</summary>
    private void StoreDepartureClearance(AircraftState ac, AirportGroundLayout layout, string runway, string clearance)
    {
        AssignDepartureRunway(ac, runway);
        Assert.True(TaxiTo(ac, layout, runway).Success);
        CommandResult stored = Dispatch(clearance, ac, layout);
        output.WriteLine($"{clearance}: success={stored.Success} message=\"{stored.Message}\"");
        Assert.True(stored.Success, stored.Message);
        Assert.NotNull(ac.Phases!.DepartureClearance);
    }

    /// <summary>Gives the aircraft the landing clearance for <paramref name="runway"/> a landing leaves on the phase list.</summary>
    private static void StoreLandingClearance(AircraftState ac, string runway)
    {
        ac.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        ac.Phases.ClearedRunwayId = runway;
    }

    [Fact]
    public void ReTaxiToTheSameRunway_AfterAStoredCto_NamesTheDroppedTakeoffClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R)", result.Message);
        Assert.Null(ac.Phases!.DepartureClearance);
    }

    [Fact]
    public void ReTaxiToTheSameRunway_AfterAStoredLuaw_NamesTheDroppedLineUpAndWaitClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "LUAW");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: line up and wait clearance for 28R)", result.Message);
    }

    /// <summary>The runway-change warning already says the takeoff clearance is void, so the response does not name it again.</summary>
    [Fact]
    public void ReTaxiToAnotherRunway_AfterAStoredCto_LeavesItToTheVoidWarning()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28L", "CTO");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
        Assert.Contains(TakeoffClearanceWarning("28R", "28L"), ac.PendingWarnings);
    }

    [Fact]
    public void Taxi_WithAStoredLandingClearance_NamesTheDroppedLandingClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreLandingClearance(ac, "28R");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: landing clearance for 28R)", result.Message);
        Assert.Null(ac.Phases!.LandingClearance);
    }

    [Fact]
    public void ReTaxi_WithATakeoffAndALandingClearanceStored_NamesBothInOneNote()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");
        StoreLandingClearance(ac, "28R");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R; landing clearance for 28R)", result.Message);
    }

    /// <summary>An RTAXI reaching the same runway entry puts the stored takeoff clearance back, so nothing is dropped.</summary>
    [Fact]
    public void ReRouteTaxiToTheSameEntry_KeepsTheStoredCto_AndNamesNothing()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");

        CommandResult result = Dispatch("RTAXI D C B", ac, layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
        Assert.Equal(ClearanceType.ClearedForTakeoff, ac.Phases!.DepartureClearance?.Type);
    }

    /// <summary>An RTAXI keeps or voids a stored takeoff clearance itself, but a landing clearance it drops is named.</summary>
    [Fact]
    public void ReRouteTaxi_WithALandingClearanceStored_NamesOnlyTheLandingClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");
        StoreLandingClearance(ac, "28R");

        CommandResult result = Dispatch("RTAXI D C B", ac, layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: landing clearance for 28R)", result.Message);
        Assert.DoesNotContain("takeoff clearance", result.Message);
    }

    /// <summary>
    /// The taxi a takeoff clearance at a taxiway hold short issues itself (DepartureClearanceHandler) is not a re-TAXI: it names
    /// nothing, whatever the aircraft held before.
    /// </summary>
    [Fact]
    public void ImplicitTaxiOfATakeoffClearance_NamesNothing()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");
        StoreLandingClearance(ac, "28R");
        var implicitTaxi = new GroundCommandHandler.TaxiDispatch(
            AutoCrossRunwayMode.Off,
            ActiveRunways.Empty,
            IsScenarioScripted: false,
            ResultReachesController: true,
            ListAircraft: null,
            GroundCommandHandler.DepartureRunwayClearance.Takeoff
        );

        CommandResult result = GroundCommandHandler.TryTaxi(
            ac,
            new TaxiCommand(["D", "C", "B"], [], Expedite: false, DestinationRunway: "28R"),
            layout,
            implicitTaxi
        );

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
    }

    /// <summary>A TAXI to the stand the aircraft already stands on parks it there, and names what it dropped all the same.</summary>
    [Fact]
    public void TaxiToTheStandItStandsOn_AfterAStoredCto_NamesTheDroppedTakeoffClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");

        CommandResult result = GroundCommandHandler.TryTaxi(ac, new TaxiCommand([], [], Expedite: false, DestinationParking: "NEW7"), layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Taxi via @NEW7", result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R)", result.Message);
    }

    [Fact]
    public void AirTaxi_WithAStoredLandingClearance_NamesTheDroppedLandingClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        GroundNode heliPad = layout.FindSpotByName("HELI") ?? throw new InvalidOperationException("KOAK has no HELI spot");
        var heli = new AircraftState
        {
            Callsign = "TEST2",
            AircraftType = "EC35",
            Position = heliPad.Position,
            TrueHeading = new TrueHeading(90),
            Altitude = 8,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();
        StoreLandingClearance(heli, "28R");

        CommandResult result = GroundCommandHandler.TryAirTaxi(heli, "28R", layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: landing clearance for 28R)", result.Message);
    }

    /// <summary>
    /// An arrival that landed, exited and holds after the exit still carries the landing clearance it landed on; the landing consumed
    /// it, so the taxi-in names nothing.
    /// </summary>
    [Fact]
    public void TaxiAfterLandingAndExiting_NamesNothing()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingAfterExitPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty, layout));
        StoreLandingClearance(ac, "28R");
        Assert.IsType<HoldingAfterExitPhase>(ac.Phases.CurrentPhase);

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
    }

    /// <summary>
    /// With a departure clearance read, the runway-change warning says that clearance no longer applies and never names the takeoff
    /// clearance, so the response names it.
    /// </summary>
    [Fact]
    public void ReTaxiToAnotherRunway_AfterADepartureClearanceAndAStoredCto_NamesTheTakeoffClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");
        // With no ARTCC config the bare CTO leaves no initial altitude; the departure clearance is read here.
        BriefDepartureRunway(ac, "28R");
        Assert.NotNull(ac.Procedure.SidInitialAltitudeFt);

        CommandResult result = TaxiTo(ac, layout, "28L");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R)", result.Message);
        Assert.Contains(DepartureClearanceWarning("28L", "28R"), ac.PendingWarnings);
    }

    [Fact]
    public void TaxiAutoToTheSameRunway_AfterAStoredCto_NamesTheDroppedTakeoffClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        StoreDepartureClearance(ac, layout, "28R", "CTO");

        CommandResult result = Dispatch("TAXIAUTO 28R", ac, layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R)", result.Message);
    }

    /// <summary>An EC35 standing on KOAK's HELI spot with nothing installed.</summary>
    private static AircraftState MakeOakHelicopter(AirportGroundLayout layout)
    {
        GroundNode heliPad = layout.FindSpotByName("HELI") ?? throw new InvalidOperationException("KOAK has no HELI spot");
        var heli = new AircraftState
        {
            Callsign = "TEST2",
            AircraftType = "EC35",
            Position = heliPad.Position,
            TrueHeading = new TrueHeading(90),
            Altitude = 8,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();
        return heli;
    }

    /// <summary>Gives the helicopter <paramref name="runway"/> and a takeoff clearance stored for it.</summary>
    private static void StoreHelicopterTakeoffClearance(AircraftState heli, string runway)
    {
        AssignDepartureRunway(heli, runway);
        heli.Phases!.DepartureClearance = new DepartureClearanceInfo { Type = ClearanceType.ClearedForTakeoff, Departure = new DefaultDeparture() };
    }

    [Fact]
    public void AirTaxiToTheSameRunway_WithAStoredCto_NamesTheDroppedTakeoffClearance()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState heli = MakeOakHelicopter(layout);
        StoreHelicopterTakeoffClearance(heli, "28R");

        CommandResult result = GroundCommandHandler.TryAirTaxi(heli, "28R", layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" (dropped: takeoff clearance for 28R)", result.Message);
    }

    /// <summary>With no departure clearance read, the runway-change warning already says the takeoff clearance is void.</summary>
    [Fact]
    public void AirTaxiToAnotherRunway_WithAStoredCto_LeavesItToTheVoidWarning()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState heli = MakeOakHelicopter(layout);
        StoreHelicopterTakeoffClearance(heli, "28L");

        CommandResult result = GroundCommandHandler.TryAirTaxi(heli, "28R", layout);

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
        Assert.Contains(TakeoffClearanceWarning("28R", "28L"), heli.PendingWarnings);
    }

    /// <summary>Installs <paramref name="phases"/> on the aircraft and runs it to the last one, as the sim would have.</summary>
    private static void RunPhasesToTheLast(AircraftState ac, AirportGroundLayout layout, params Phase[] phases)
    {
        ac.Phases = new PhaseList();
        foreach (Phase phase in phases)
        {
            ac.Phases.Add(phase);
        }

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty, layout);
        ac.Phases.Start(ctx);
        for (int i = 1; i < phases.Length; i++)
        {
            ac.Phases.AdvanceToNext(ctx);
        }

        Assert.Same(phases[^1], ac.Phases.CurrentPhase);
    }

    /// <summary>
    /// An arrival that landed, exited and pulled up to hold short of the parallel (RunwayExitPhase.TryStartParallelCrossing appends that
    /// to the landing's own phase list) still carries the landing clearance it landed on; the taxi-in names nothing.
    /// </summary>
    [Fact]
    public void TaxiFromTheParallelHoldAfterLanding_NamesNothing()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        GroundNode parallelBar = layout.GetRunwayHoldShortNodes("28L")[0];
        var holdShortOfParallel = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = parallelBar.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = "28L",
                Latitude = parallelBar.Position.Lat,
                Longitude = parallelBar.Position.Lon,
            },
            HoldShortArrival.HeldShort
        );
        RunPhasesToTheLast(ac, layout, new LandingPhase(), holdShortOfParallel);
        StoreLandingClearance(ac, "28R");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
    }

    /// <summary>An arrival still exiting the runway it landed on, on a phase list of its own, names nothing either.</summary>
    [Fact]
    public void TaxiWhileExitingTheRunway_NamesNothing()
    {
        if (OakLayout() is not { } layout)
        {
            return;
        }

        AircraftState ac = MakeOakDeparture(layout);
        RunPhasesToTheLast(ac, layout, new RunwayExitPhase());
        StoreLandingClearance(ac, "28R");

        CommandResult result = TaxiTo(ac, layout, "28R");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
    }

    /// <summary>A TAXI on the landing rollout is the exit from the landing, which consumes its landing clearance: nothing is dropped.</summary>
    [Fact]
    public void TaxiOnTheLandingRollout_NamesNothing()
    {
        const string arrival = "ARR1";
        if (ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "B738", arrival, 1.0) is not { } spawned)
        {
            return;
        }

        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
        Assert.NotNull(spawned.Aircraft.Phases!.LandingClearance);

        CommandResult result = spawned.Engine.SendCommand(arrival, "TAXIAUTO 30");

        output.WriteLine(result.Message ?? "");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(DroppedNote, result.Message);
    }
}
