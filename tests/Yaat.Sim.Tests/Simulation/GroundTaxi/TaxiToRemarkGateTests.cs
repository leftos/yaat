using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>@GATE</c> is the stand the aircraft's flight-plan remarks name: <c>TAXI A @GATE</c> and <c>TAXIAUTO @GATE</c> clear it
/// to that stand with the readback a hand-typed <c>@stand</c> gives. A remark naming only a parking area clears it to a free
/// stand in the area that fits the type (any free stand in the area when none fits), picked the same way every time, and
/// reads back the area's name. When every stand in the area that fits is taken the clearance still stands: the aircraft holds
/// on the area's ramp lane clear of the taxiway and reports that no parking is available. Remarks that name no stand and an
/// area the airport does not know are refused with the aircraft left as it was.
/// </summary>
public class TaxiToRemarkGateTests(ITestOutputHelper output)
{
    /// <summary>UAL733's pose in the hold-short bundle: on T at t=1930, heading 208.98° true, A320.</summary>
    private const double OnTLatitude = 37.62094280885291;

    private const double OnTLongitude = -122.38247885498703;
    private const double OnTHeadingTrueDeg = 208.98;

    /// <summary>
    /// SFO node 1546 on C, east of the T421 junction, heading west-north-west along C toward it: <c>TAXI C</c> reaches SIG1 and
    /// SIG6 from here (LayoutInspector <c>--pathfinder 1546 C</c>, no warnings).
    /// </summary>
    private const double OnCLatitude = 37.626213;

    private const double OnCLongitude = -122.383693;
    private const double OnCHeadingTrueDeg = 297.9;

    private const string Callsign = "UAL733";
    private const string AircraftType = "A320";
    private const string Stand = "E8";
    private const string StandRemark = "/V/ GATE E8, 28R";
    private const string NoStandRemark = "/v/";
    private const string AreaRemark = "/V/ PARKING SIGNATURE, 28R";
    private const string UnknownAreaRemark = "/V/ PARKING ATLANTIC, 28R";

    /// <summary>SFO's sidecar maps the Signature area to the stands named <c>SIG*</c>.</summary>
    private static readonly string[] SignatureStands = ["SIG1", "SIG2", "SIG3", "SIG4", "SIG5", "SIG6", "SIG7", "SIG8", "SIG9", "SIG10"];

    /// <summary>
    /// The Signature stands whose spacing takes a GLF4's 77.8 ft span: SIG1 (79.0 ft) and SIG6 (79.4 ft); SIG2, the next
    /// widest, takes 76.5 ft (StandFitTests' SIG estimates).
    /// </summary>
    private static readonly string[] MidSpanFitting = ["SIG1", "SIG6"];

    /// <summary>The stand UAL733 in a GLF4 draws from <see cref="MidSpanFitting"/> with nothing taken.</summary>
    private const string MidSpanPick = "SIG6";

    /// <summary>The stand UAL733 in an A320, which no Signature stand fits, draws from all of <see cref="SignatureStands"/>.</summary>
    private const string WholeAreaPick = "SIG9";

    /// <summary>A Gulfstream IV (77.8 ft span): it fits some of the Signature stands, not all of them.</summary>
    private const string MidSpanType = "GLF4";

    /// <summary>The type parked on the stands an area test fills.</summary>
    private const string ParkedType = "C172";

    /// <summary>The T421 node a GLF4 holds at when every Signature stand it fits is taken (measured: tail 156 ft from C).</summary>
    private const int GlfFullAreaHoldNode = 2323;

    /// <summary>The T421 node an A320 holds at when every Signature stand is taken (measured: tail 156 ft from C).</summary>
    private const int A320FullAreaHoldNode = 2328;

    /// <summary>What the pilot says on stopping at the ramp edge of a full area, its callsign leading.</summary>
    private const string NoParkingReport = "UAL733, holding on T421 at the SIGNATURE ramp, no parking available, request instructions";

    [Fact]
    public void TaxiAtGate_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark, AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(Stand, route.DestinationParking);
    }

    [Fact]
    public void TaxiAutoAtGate_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark, AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(Stand, route.DestinationParking);
    }

    [Fact]
    public void TaxiAtGate_NoParkingRemark_IsRefusedAndLeavesTheAircraftAlone()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, NoStandRemark, AircraftType);
        Type? phaseBefore = aircraft.Phases?.CurrentPhase?.GetType();
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.False(result.Success, result.Message);
        Assert.Equal("No gate in UAL733's flight plan remarks; name the stand with @", result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Equal(phaseBefore, aircraft.Phases?.CurrentPhase?.GetType());
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>Case 1: an area remark with free stands that fit the type picks one of them, and the same one on a fresh sim.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_PicksTheSameFreeFittingStandInTheArea()
    {
        if ((BuildSfo() is not { } first) || (BuildSfo() is not { } second))
        {
            return;
        }

        string picked = ClearToArea(first, MidSpanType);
        string again = ClearToArea(second, MidSpanType);

        Assert.Equal(MidSpanPick, picked);
        Assert.Equal(MidSpanPick, again);
    }

    /// <summary>Case 2: the stand the pick would take is parked on, so a different free stand in the area is picked.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_WouldBePickTaken_PicksAnotherFreeStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        Park(ground, "PRK1", MidSpanPick);

        string picked = ClearToArea(ground, MidSpanType);

        Assert.Equal(Assert.Single(MidSpanFitting, s => s != MidSpanPick), picked);
    }

    /// <summary>
    /// Case 3: every stand that fits is taken while one that does not is free: the area is full for the type, so the clearance
    /// is accepted and the route ends on the Signature ramp lane T421, its tail clear of C, holding no stand.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryFittingStandTaken_HoldsOnTheRampLaneClearOfC()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnHeldAtFullArea(ground);

        AssertHeldAtRampEdge(ground, aircraft, MidSpanType, GlfFullAreaHoldNode);
    }

    /// <summary>Case 3's readback: the area as the remark spells it, as when a stand in it is free.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryFittingStandTaken_ReadsBackTheArea()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, MidSpanFitting);
        SpawnOnC(ground, MidSpanType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI C @GATE");

        Assert.True(result.Success, result.Message);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        output.WriteLine($"readback: {readback.Speech.Terminal} | {readback.Speech.Tts}");
        Assert.Contains("taxi to SIGNATURE", readback.Speech.Terminal, StringComparison.Ordinal);
    }

    /// <summary>Case 3 on arrival: once stopped at the ramp edge the pilot reports where it holds and that no stand is free.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryFittingStandTaken_ReportsNoParkingWhenItStops()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        Assert.IsType<SimScenarioState>(ground.Engine.Scenario).RpoShowPilotSpeech = true;
        var reports = new List<PilotSpeechText>();
        ground.Engine.PilotSpeechEmitted += (said, speech) =>
        {
            if (said == Callsign)
            {
                reports.Add(speech);
            }
        };
        AircraftState aircraft = SpawnHeldAtFullArea(ground);

        int stopped = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingInPositionPhase, 600, null);

        Assert.True(stopped > 0, $"never stopped at the ramp edge: {aircraft.Phases?.CurrentPhase?.Name}");
        foreach (PilotSpeechText report in reports)
        {
            output.WriteLine($"report: '{report.TerminalForRpo}' / '{report.Tts}'");
        }

        PilotSpeechText said = Assert.Single(reports, r => r.Terminal.Contains("no parking available", StringComparison.Ordinal));
        Assert.Equal(NoParkingReport, $"{Callsign}, {said.TerminalForRpo}");
        Assert.Contains("tango four two one", said.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// Case 3 with a stand freed during the taxi: cleared while every fitting stand is taken, the aircraft finds the last fitting
    /// stand free on reaching the ramp edge, so it carries on to it and parks there without reporting that no parking is available.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_StandFreedDuringTheTaxi_ContinuesToItWithoutReporting()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        var reports = new List<PilotSpeechText>();
        (AircraftState aircraft, string freed) = ClearedToFullAreaThenFreed(ground, "TAXI C @GATE", reports);

        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is AtParkingPhase or HoldingInPositionPhase,
            600,
            null
        );

        foreach (PilotSpeechText report in reports)
        {
            output.WriteLine($"report: '{report.TerminalForRpo}' / '{report.Tts}'");
        }

        Assert.True(stopped > 0, $"never stopped: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.IsType<AtParkingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(freed, aircraft.Ground.ParkingSpot);
        Assert.Empty(reports);
    }

    /// <summary>
    /// Case 3 with a stand freed during an expedited taxi: the taxi on to the freed stand keeps the running expedite, as the
    /// crew's standing instruction.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_StandFreedDuringAnExpeditedTaxi_KeepsTheExpedite()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        var reports = new List<PilotSpeechText>();
        (AircraftState aircraft, string freed) = ClearedToFullAreaThenFreed(ground, "TAXI C @GATE EXP", reports);
        Assert.True(aircraft.Ground.IsExpeditingTaxi, "the clearance did not set the expedite");

        int continuing = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.Phases?.CurrentPhase is TaxiingPhase) && (aircraft.Ground.AssignedTaxiRoute?.DestinationParking == freed),
            600,
            null
        );

        Assert.True(continuing > 0, $"never continued to {freed}: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(aircraft.Ground.IsExpeditingTaxi, "the taxi to the freed stand dropped the expedite");
    }

    /// <summary>
    /// UAL733 in a GLF4 on C cleared by <paramref name="command"/> while every fitting stand is taken, then the last fitting
    /// stand freed by removing the aircraft <see cref="ParkOnEach"/> parked there; every pilot speech UAL733 makes goes to
    /// <paramref name="reports"/>.
    /// </summary>
    private static (AircraftState Aircraft, string Freed) ClearedToFullAreaThenFreed(SfoGround ground, string command, List<PilotSpeechText> reports)
    {
        Assert.IsType<SimScenarioState>(ground.Engine.Scenario).RpoShowPilotSpeech = true;
        ground.Engine.PilotSpeechEmitted += (said, speech) =>
        {
            if (said == Callsign)
            {
                reports.Add(speech);
            }
        };
        ParkOnEach(ground, MidSpanFitting);
        AircraftState aircraft = SpawnOnC(ground, MidSpanType);
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        Assert.True(result.Success, result.Message);

        ground.Engine.World.RemoveAircraft(ParkedCallsign(MidSpanFitting.Length - 1));
        return (aircraft, MidSpanFitting[^1]);
    }

    /// <summary>
    /// Case 3 from a runway hold-short: held short of 28R on K, the route to the full area's ramp edge crosses 28R, and the bar at
    /// its start still holds the aircraft short of the runway.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryFittingStandTaken_FromARunwayHoldShort_KeepsTheBar()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, MidSpanFitting);
        AircraftState aircraft = SfoGroundHarness.SpawnAtHoldShort(ground, Callsign, MidSpanType, ("28R", "K", "Q"));
        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI K C @GATE");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {string.Join(" | ", route.Segments.Select(s => $"{s.TaxiwayName}: {s.FromNodeId}->{s.ToNodeId}"))}");
        output.WriteLine($"bars: {string.Join(" | ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId} cleared={h.IsCleared}"))}");
        Assert.Equal("SIGNATURE", route.DestinationParkingArea);
        HoldShortPoint first = Assert.IsType<HoldShortPoint>(route.HoldShortPoints.FirstOrDefault());
        Assert.True(SfoGroundHarness.HoldShortMatches(first, "28R"), $"first bar protects {first.TargetName}, not 28R");
        Assert.False(first.IsCleared, "the 28R bar at the route's start was cleared");
    }

    /// <summary>
    /// Case 3 again from the hold: stopped at the full area's ramp edge, a bare <c>TAXI @GATE</c> finds the area still full, so the
    /// aircraft stays where it is and reports again.
    /// </summary>
    [Fact]
    public void TaxiAtGate_FromTheHoldNode_StaysAndReportsAgain()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        var reports = new List<PilotSpeechText>();
        AircraftState aircraft = StoppedAtFullAreaHold(ground, reports);
        LatLon stoppedAt = aircraft.Position;

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI @GATE");

        AssertStaysAndReportsAgain(ground, aircraft, result, (stoppedAt, reports), output);
    }

    /// <summary>Case 4: no stand in the area fits the type, so the pick is made from the whole area.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_NoStandFits_PicksFromTheWholeArea()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        string picked = ClearToArea(ground, AircraftType);

        Assert.Equal(WholeAreaPick, picked);
    }

    /// <summary>Case 5: every stand in the area is taken: the A320 holds on T421 too, further along for its longer tail.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryStandTaken_HoldsOnTheRampLaneClearOfC()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, SignatureStands);
        AircraftState aircraft = SpawnOnT(ground, AreaRemark, AircraftType);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        Assert.True(result.Success, result.Message);
        AssertHeldAtRampEdge(ground, aircraft, AircraftType, A320FullAreaHoldNode);
    }

    /// <summary>
    /// An area the sidecar lists whose stand patterns cover no stand of the layout is refused as matching no stand; under the
    /// same sidecar an area covering one stand clears to it, so the test-local sidecar is the one read.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_AreaCoveringNoLayoutStand_IsRefusedAsMatchingNoStand()
    {
        if ((BuildSfo() is not { } ground) || (BuildSfo() is not { } control) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar("KSFO")
            {
                ParkingAreas = [new ParkingAreaAlias("NOWHERE", ["ZZZ*"], null), new ParkingAreaAlias("HANGAR", ["SIG5"], null)],
            },
        ]);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb.WithAirportSidecars(sidecars));

        AircraftState aircraft = SpawnOnT(ground, "/V/ PARKING NOWHERE, 28R", AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");
        AircraftState controlAircraft = SpawnOnT(control, "/V/ PARKING HANGAR, 28R", AircraftType);
        CommandResult controlResult = control.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, "UAL733's remarks name parking area NOWHERE, which matches no stand at this airport; name the stand with @");
        Assert.True(controlResult.Success, controlResult.Message);
        Assert.Equal("SIG5", Assert.IsType<TaxiRoute>(controlAircraft.Ground.AssignedTaxiRoute).DestinationParking);
    }

    /// <summary>Case 6: an area the airport's sidecar has no entry for matches no stand.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_AreaWithNoSidecarEntry_IsRefusedAsMatchingNoStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, UnknownAreaRemark, AircraftType);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, "UAL733's remarks name parking area ATLANTIC, which matches no stand at this airport; name the stand with @");
    }

    /// <summary>Case 7: the readback names the area as the remark spells it, not the stand picked inside it.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_ReadsBackTheAreaName()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            MidSpanType,
            (VirtualNode.Create(OnCLatitude, OnCLongitude), new TrueHeading(OnCHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI C @GATE");

        Assert.True(result.Success, result.Message);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        output.WriteLine($"readback: {readback.Speech.Terminal} | {readback.Speech.Tts}");
        Assert.Contains("taxi to SIGNATURE", readback.Speech.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("parking SIG", readback.Speech.Terminal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case 7 for TAXIAUTO: the readback names the area, not the stand picked inside it.</summary>
    [Fact]
    public void TaxiAutoAtGate_ParkingAreaRemark_ReadsBackTheAreaName()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            MidSpanType,
            (VirtualNode.Create(OnCLatitude, OnCLongitude), new TrueHeading(OnCHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        Assert.True(result.Success, result.Message);
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound("TAXIAUTO @GATE").Value!,
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        output.WriteLine($"readback: {readback?.Terminal} | {readback?.Tts}");
        Assert.NotNull(readback);
        Assert.Contains("taxi to SIGNATURE", readback.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("parking SIG", readback.Terminal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case 7 on the landing rollout: the readback names the area there too, not the stand picked inside it.</summary>
    [Fact]
    public void TaxiAutoAtGate_OnTheLandingRollout_ParkingAreaRemark_ReadsBackTheAreaName()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("SFO", "19L", AircraftType, Callsign, 1.0) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => aircraft.IsOnGround && (aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {aircraft.Phases?.CurrentPhase?.Name}");
        for (int second = 0; second < 4; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        CommandResult result = spawned.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        Assert.True(result.Success, result.Message);
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound("TAXIAUTO @GATE").Value!,
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        output.WriteLine($"readback: {readback?.Terminal} | {readback?.Tts}");
        Assert.NotNull(readback);
        Assert.Contains("taxi to SIGNATURE", readback.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("parking", readback.Terminal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiAtGate_ReadsBackExactlyAsTheTypedStandDoes()
    {
        if ((BuildSfo() is not { } aliasGround) || (BuildSfo() is not { } typedGround))
        {
            return;
        }

        CommandResult alias = Clear(aliasGround, "TAXI A @GATE");
        CommandResult typed = Clear(typedGround, $"TAXI A @{Stand}");

        Assert.True(alias.Success, alias.Message);
        Assert.True(typed.Success, typed.Message);
        Assert.Equal(typed.Message, alias.Message);
    }

    [Fact]
    public void Ttp_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark, AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TTP A");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(Stand, route.DestinationParking);
    }

    [Fact]
    public void Ttp_NoParkingRemark_IsRefusedAndLeavesTheAircraftAlone()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, NoStandRemark, AircraftType);
        Type? phaseBefore = aircraft.Phases?.CurrentPhase?.GetType();
        CommandResult result = ground.Engine.SendCommand(Callsign, "TTP A");

        Assert.False(result.Success, result.Message);
        Assert.Equal("No gate in UAL733's flight plan remarks; name the stand with @", result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Equal(phaseBefore, aircraft.Phases?.CurrentPhase?.GetType());
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>The area remark through TTP: the expansion's <c>@GATE</c> picks a free stand in the area, as <c>TAXIAUTO @GATE</c> does.</summary>
    [Fact]
    public void Ttp_AreaRemark_PicksAFreeStandInTheArea()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnC(ground, MidSpanType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TTP C");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(MidSpanPick, route.DestinationParking);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.Contains("taxi to SIGNATURE", readback.Speech.Terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void Ttp_ReadsBackExactlyAsTaxiAtGateDoes()
    {
        if ((BuildSfo() is not { } aliasGround) || (BuildSfo() is not { } typedGround))
        {
            return;
        }

        CommandResult alias = Clear(aliasGround, "TTP A");
        CommandResult typed = Clear(typedGround, $"TAXI A @{Stand}");

        Assert.True(alias.Success, alias.Message);
        Assert.True(typed.Success, typed.Message);
        Assert.Equal(typed.Message, alias.Message);
    }

    private SfoGround? BuildSfo() => SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off);

    private static AircraftState SpawnOnT(SfoGround ground, string remarks, string type)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            type,
            (VirtualNode.Create(OnTLatitude, OnTLongitude), new TrueHeading(OnTHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(remarks);
        return aircraft;
    }

    /// <summary>Aircraft's own remark stand, cleared by <paramref name="command"/> on a freshly built SFO.</summary>
    private static CommandResult Clear(SfoGround ground, string command)
    {
        SpawnOnT(ground, StandRemark, AircraftType);
        return ground.Engine.SendCommand(Callsign, command);
    }

    /// <summary>Clears an aircraft of <paramref name="type"/> with the Signature remark to <c>@GATE</c>; the stand its route ends at.</summary>
    private static string ClearToArea(SfoGround ground, string type)
    {
        AircraftState aircraft = SpawnOnT(ground, AreaRemark, type);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        return Assert.IsType<string>(route.DestinationParking);
    }

    /// <summary>UAL733 in a <paramref name="type"/> on C east of T421, held in position, its remarks naming the Signature area.</summary>
    private static AircraftState SpawnOnC(SfoGround ground, string type)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            type,
            (VirtualNode.Create(OnCLatitude, OnCLongitude), new TrueHeading(OnCHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);
        return aircraft;
    }

    /// <summary>
    /// UAL733 in a GLF4 on C, cleared <c>TAXI C @GATE</c> while every Signature stand it fits is taken: the clearance is accepted
    /// and the aircraft holds at the area's ramp edge.
    /// </summary>
    internal static AircraftState SpawnHeldAtFullArea(SfoGround ground)
    {
        ParkOnEach(ground, MidSpanFitting);
        AircraftState aircraft = SpawnOnC(ground, MidSpanType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI C @GATE");
        Assert.True(result.Success, result.Message);
        return aircraft;
    }

    /// <summary>
    /// <see cref="SpawnHeldAtFullArea"/>, ticked until the aircraft stops at the ramp edge and holds in position, with its first
    /// no-parking report in <paramref name="reports"/>, which keeps collecting every pilot speech UAL733 makes after.
    /// </summary>
    internal static AircraftState StoppedAtFullAreaHold(SfoGround ground, List<PilotSpeechText> reports)
    {
        Assert.IsType<SimScenarioState>(ground.Engine.Scenario).RpoShowPilotSpeech = true;
        ground.Engine.PilotSpeechEmitted += (said, speech) =>
        {
            if (said == Callsign)
            {
                reports.Add(speech);
            }
        };
        AircraftState aircraft = SpawnHeldAtFullArea(ground);
        int stopped = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingInPositionPhase, 600, null);
        Assert.True(stopped > 0, $"never stopped at the ramp edge: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Single(reports, IsNoParkingReport);
        Assert.Equal(GlfFullAreaHoldNode, Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).Segments[^1].ToNodeId);
        return aircraft;
    }

    /// <summary>
    /// After a clearance sent to the aircraft stopped at the full area's ramp edge: accepted, the route drives nowhere (empty, or
    /// ending at the hold node), the aircraft holds in position again within 5 ft of where it stopped, and it reports again.
    /// </summary>
    internal static void AssertStaysAndReportsAgain(
        SfoGround ground,
        AircraftState aircraft,
        CommandResult result,
        (LatLon StoppedAt, List<PilotSpeechText> Reports) before,
        ITestOutputHelper output
    )
    {
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {string.Join(" | ", route.Segments.Select(s => $"{s.TaxiwayName}: {s.FromNodeId}->{s.ToNodeId}"))}");
        Assert.Equal("SIGNATURE", route.DestinationParkingArea);
        Assert.True((route.Segments.Count == 0) || (route.Segments[^1].ToNodeId == GlfFullAreaHoldNode), "the route drives away from the hold");

        int again = SfoGroundHarness.TickUntil(ground.Engine, () => before.Reports.Count(IsNoParkingReport) >= 2, 120, null);
        Assert.True(again > 0, "no second no-parking report");
        SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is HoldingInPositionPhase, 60, null);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        double movedFt = GeoMath.DistanceNm(before.StoppedAt, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(movedFt <= 5.0, $"moved {movedFt:F1} ft from the hold");
    }

    private static bool IsNoParkingReport(PilotSpeechText speech) => speech.Terminal.Contains("no parking available", StringComparison.Ordinal);

    /// <summary>
    /// The aircraft's route holds no stand, keeps the Signature area pending, drives no apron edge and ends at
    /// <paramref name="holdNodeId"/> with the tail clear of C by the wingtip floor (5 ft of slack).
    /// </summary>
    private void AssertHeldAtRampEdge(SfoGround ground, AircraftState aircraft, string type, int holdNodeId)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {string.Join(" | ", route.Segments.Select(s => $"{s.TaxiwayName}: {s.FromNodeId}->{s.ToNodeId}"))}");
        Assert.Null(route.DestinationParking);
        Assert.Equal("SIGNATURE", route.DestinationParkingArea);
        Assert.DoesNotContain(route.Segments, s => SegmentExpander.EdgeNames(s.Edge.Edge).Contains("RAMP"));
        Assert.Equal(holdNodeId, route.Segments[^1].ToNodeId);

        LatLon holdPosition = route.Segments[^1].Edge.ToNode.Position;
        double tailToCFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "C", holdPosition) - (AircraftLength.ResolveFt(type) / 2.0);
        double floorFt = HoldShortAnnotator.WingtipClearanceFloorFt(ground.Layout.Runways.Max(r => r.WidthFt));
        output.WriteLine($"tail to C {tailToCFt:F1} ft, floor {floorFt:F1} ft");
        Assert.True(tailToCFt >= floorFt - 5.0, $"tail {tailToCFt:F1} ft from C, floor {floorFt:F1} ft");
    }

    private static void AssertRefused(CommandResult result, AircraftState aircraft, string expected)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expected, result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>Another aircraft parked on <paramref name="stand"/>, which is what makes it taken.</summary>
    private static void Park(SfoGround ground, string callsign, string stand)
    {
        AircraftState parked = SfoGroundHarness.SpawnParked(ground, callsign, ParkedType, stand);
        parked.Ground.ParkingSpot = stand;
    }

    private static void ParkOnEach(SfoGround ground, IReadOnlyList<string> stands)
    {
        for (int i = 0; i < stands.Count; i++)
        {
            Park(ground, ParkedCallsign(i), stands[i]);
        }
    }

    /// <summary>The callsign <see cref="ParkOnEach"/> parks on the stand at <paramref name="index"/> of its list.</summary>
    private static string ParkedCallsign(int index) => $"PRK{index + 1}";
}
