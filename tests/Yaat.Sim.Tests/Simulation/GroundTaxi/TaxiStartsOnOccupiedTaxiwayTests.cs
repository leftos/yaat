using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #880: an aircraft resting mid-edge on a long straight taxiway, far from any graph node, had its TAXI start
/// node picked by distance to nodes alone. At KOAK, an aircraft on C's centreline west of H was 333 ft from a node on
/// the parallel D and 376 ft from the nearest C node, so its route began with a free-space leg onto D. At KSFO the
/// same pose mid-B started the route on A and "TAXI B" was refused. A taxi starts on the taxiway the aircraft is on.
/// </summary>
public class TaxiStartsOnOccupiedTaxiwayTests(ITestOutputHelper output)
{
    private const int TaxiTickSeconds = 20;
    private const double MaxTurnFromStartDeg = 30.0;
    private const double MinTaxiSpeedKts = 10.0;

    /// <summary>How far past a straight edge's end, into the fillet arc that continues it, the aircraft stands.</summary>
    private const double IntoFilletFt = 20.0;

    /// <summary>Fillet arcs from this long: the arc's far end is clear of the at-node tolerance of the aircraft 20 ft into it.</summary>
    private const double MinFilletFt = 40.0;

    /// <summary>Fillet arcs up to this long: a junction fillet, not a long high-speed exit curve.</summary>
    private const double MaxFilletFt = 400.0;

    /// <summary>How many C nodes the walk behind the aircraft visits looking for a taxiway off C.</summary>
    private const int MaxWalkNodes = 30;

    [Fact]
    public void TaxiOnC_WestOfH_MidEdge_RouteStartsOnC()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(layout);

        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AirportGroundLayout.NearestTaxiEdge? onEdge = layout.FindNearestTaxiEdge(position);
        Assert.NotNull(onEdge);
        Assert.True(onEdge.Value.Edge.MatchesTaxiway("C"), $"nearest taxi edge is {onEdge.Value.Edge.TaxiwayName}, not C");

        double headingDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        AircraftState aircraft = MakeAircraft(layout, position, headingDeg, "OAK", "C");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI C B RWY 28R HS H");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "C"), $"route joins the graph at node {firstFrom.Id}, not on C");
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("D"));
        Assert.Equal("C", route.FormatTaxiwaySequence().Split(' ')[0]);

        HoldShortPoint holdH = Assert.Single(route.HoldShortPoints, hs => string.Equals(hs.TargetName, "H", StringComparison.OrdinalIgnoreCase));
        Assert.True(OnStraightEdgeOf(layout.Nodes[holdH.NodeId], "C"), $"HS H is bound at node {holdH.NodeId}, which has no straight C edge");
    }

    /// <summary>
    /// A C172 mid-C at KOAK, facing toward H, cleared to a taxiway that branches off C behind it. The route from the C node
    /// ahead would come straight back over the edge the aircraft stands on, so it turns about on C: segment 0 is the
    /// free-space leg back to the edge's far node, and no segment drives that edge. Ticked through the turn about and the
    /// leg back to the far node, it pivots no faster than ω·r at its turn-about radius, ends facing the far node, and its
    /// centre stays within C's half-width of the centreline.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnsAboutToTheFarNode() => TurnAboutOnC("C172", AircraftCategory.Piston);

    /// <summary>
    /// The same pose and clearance in a C208, a turboprop the FAA aircraft characteristics database puts in TDG 1A: it turns
    /// about on C at its own turn-about radius and pivot speed, its centre within its own TDG 1A half-width of the centreline.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurbopropTurnsAboutWithinTheTaxiway() => TurnAboutOnC("C208", AircraftCategory.Turboprop);

    /// <summary>
    /// A <paramref name="type"/> mid-C facing H cleared to the taxiway off C behind it, ticked through the turn about (speed
    /// bound) and then through the leg back to the far node (peak centre offset from C's centreline), which must stay within
    /// the half-width of a TDG 1A taxiway.
    /// </summary>
    private void TurnAboutOnC(string type, AircraftCategory category)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, type);
        Assert.Equal(category, AircraftCategorization.Categorize(type));

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{type}: occupied C edge {tangentCut.Id}-{farEnd.Id}; {command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(route.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));

        double maxTurnSpeedKts = 0.0;
        double peakOffsetFt = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        void TrackOffset() => peakOffsetFt = Math.Max(peakOffsetFt, CentreOffsetFt(aircraft.Position, tangentCut, farEnd));
        int facedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            FacesFarEnd,
            TurnAboutTickSeconds,
            _ =>
            {
                maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed);
                TrackOffset();
            }
        );
        output.WriteLine($"faced node {farEnd.Id} after {facedAt}s; max {maxTurnSpeedKts:F2} kt while turning, peak {peakOffsetFt:F1} ft off C");
        Assert.True(facedAt > 0, $"the aircraft did not face node {farEnd.Id} within {TurnAboutTickSeconds}s");

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, _ => TrackOffset());
        output.WriteLine($"left the turn-about leg {legDoneAt}s later; peak {peakOffsetFt:F2} ft off C's centreline");
        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");

        double pivotKts = PivotSpeedKts(type, category);
        Assert.True(
            maxTurnSpeedKts <= pivotKts + TurnSpeedOvershootKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt, above the {pivotKts:F2} kt pivot speed (ω·r at its "
                + $"{TurnAboutFit.Evaluate(type, category).RadiusFt:F1} ft turn-about radius) plus {TurnSpeedOvershootKts:F1} kt"
        );
        Assert.True(
            peakOffsetFt <= (Tdg1AHalfWidthFt + CentreTrackingToleranceFt),
            $"the aircraft's centre swung {peakOffsetFt:F2} ft off C's centreline turning about, beyond {Tdg1AHalfWidthFt:F1} ft, "
                + $"{Tdg1AHalfWidthSource}, plus the {CentreTrackingToleranceFt:F1} ft tracking tolerance"
        );
    }

    /// <summary>Half the 25 ft width of a TDG 1A taxiway (AC 150/5300-13B).</summary>
    private const double Tdg1AHalfWidthFt = 12.5;

    /// <summary>Where that width comes from, for the failure messages of the turn-about tests.</summary>
    private const string Tdg1AHalfWidthSource = "the TDG 1A taxiway half-width (AC 150/5300-13B: 25 ft wide)";

    /// <summary>
    /// How far past the bound the centre may swing turning about on C: the half-foot the playback's settling onto the reversal
    /// arc and the straight after it adds to the turn-about radius.
    /// </summary>
    private const double CentreTrackingToleranceFt = 0.5;

    /// <summary>
    /// The speed a turn about on a taxiway pivots at: the gear-limited turn rate held on the type's turn-about radius
    /// (<see cref="TurnAboutFit"/>), v = ω·r.
    /// </summary>
    private static double PivotSpeedKts(string type, AircraftCategory category) =>
        CategoryPerformance.GroundTurnRate(category)
        * (Math.PI / 180.0)
        * TurnAboutFit.Evaluate(type, category).RadiusFt
        * 3600.0
        / GeoMath.FeetPerNm;

    /// <summary>How far <paramref name="position"/> lies off the centreline through <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static double CentreOffsetFt(LatLon position, GroundNode a, GroundNode b) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, a.Position, new TrueHeading(GeoMath.BearingTo(a.Position, b.Position))))
        * GeoMath.FeetPerNm;

    /// <summary>
    /// The same pose in a B738: a jet has no room to turn about on a taxiway, so where only the far-node route avoids
    /// coming back over the edge it stands on, it refuses the TAXI and its state is untouched.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.EndsWith("no room to turn around on charlie, request a route ahead.", result.PilotUnable?.Tts, StringComparison.Ordinal);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The same B738 angled 45° across C: its gear does not fit a turn about on a TDG 3 taxiway at any angle (turning from
    /// off the edge saves little of a full 180's room), so it refuses the controller's TAXI as when lined up.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetAngledAcrossCRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");
        TrueHeading angled = AngleAcrossC(aircraft);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command} at heading {angled.Degrees:F0}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The angled B738 given the same TAXI by a scenario preset: no controller is there to re-issue it, so it is not
    /// refused, and since its gear does not fit a turn about it keeps the route from the C node ahead rather than taking
    /// the one from the far node. That route opens by driving straight back over the C edge it stands on, so it turns
    /// about where it stands and reports it in place, toward the far node, as the lined-up preset does.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_ScriptedJetAngledAcrossCKeepsTheRouteAheadAndTurnsAboutInPlace()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");
        TrueHeading angled = AngleAcrossC(aircraft);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        output.WriteLine($"scripted {command} at heading {angled.Degrees:F0}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"the scripted '{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(tangentCut.Id, route.Segments[0].FromNodeId);
        Assert.False(VirtualNode.IsVirtualEdge(route.Segments[0].Edge.Edge), "segment 0 is the far-end route's free-space leg back");
        Assert.NotEqual(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The pose of the lined-up B738 in other types, from the controller: a type whose gear fits a turn about on its own
    /// TDG taxiway (a C208, TDG 1A) turns about toward the far node; one whose gear does not (a C25A, TDG 2A, nose gear
    /// 19.6 ft out against 17.5 ft; an AT76, TDG 1B but a 35 ft wheelbase) refuses for want of room to turn around.
    /// </summary>
    [Theory]
    [InlineData("C208", true)]
    [InlineData("C25A", false)]
    [InlineData("AT76", false)]
    public void TaxiOnC_DestinationBehind_LinedUpTypeTurnsAboutOnlyWhenItsGearFits(string type, bool turnsAbout)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, type);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{type} {command}: {result.Success} — {result.Message}");

        if (turnsAbout)
        {
            Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
            TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
            SfoGroundHarness.DumpRoute(output, route);
            Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);
            Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        }
        else
        {
            Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
            Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
            Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        }
    }

    /// <summary>Turns <paramref name="aircraft"/>'s heading and track <see cref="AcrossEdgeDeg"/> across C, and returns the new heading.</summary>
    private static TrueHeading AngleAcrossC(AircraftState aircraft)
    {
        var angled = new TrueHeading(aircraft.TrueHeading.Degrees + AcrossEdgeDeg);
        aircraft.TrueHeading = angled;
        aircraft.TrueTrack = angled;
        return angled;
    }

    /// <summary>How far the angled jet's heading is turned off C: well off the taxiway's line, still short of across it.</summary>
    private const double AcrossEdgeDeg = 45.0;

    /// <summary>
    /// The C172 turned about on C by a controller's clearance reports the turn about as pending (the overlay draws it
    /// from that) until it has driven the leg back to the far node, and no longer once that leg is done.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingUntilTheLegCompletes()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);

        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(farEnd.Id, aircraft.Ground.TaxiTurnAboutTargetNodeId);

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 turned about on C, snapshotted right after the clearance and restored into a fresh engine: the turn about is
    /// still pending there, and no longer once the restored aircraft has driven the leg back to the far node.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingSurvivesASnapshotRestore()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(aircraft.Callsign));
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, restoredAircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(farEnd.Id, restoredAircraft.Ground.TaxiTurnAboutTargetNodeId);

        TaxiRoute route = restoredAircraft.Ground.AssignedTaxiRoute!;
        int legDoneAt = SfoGroundHarness.TickUntil(restored.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"restored: left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the restored aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, restoredAircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(restoredAircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>A C172 mid-C facing H, cleared along C ahead of it: the clearance turns nothing about, so nothing is pending.</summary>
    [Fact]
    public void TaxiOnC_RouteAhead_NoTurnAboutPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        const string Command = "TAXI C B RWY 28R HS H";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, Command);
        Assert.True(result.Success, $"'{Command}' was refused: {result.Message}");
        Assert.NotEqual(farEnd.Id, aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);

        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>How long the leg back to the far C node may take, turn included.</summary>
    private const int TurnAboutLegTickSeconds = 300;

    /// <summary>
    /// A B738 lined up along C toward H, but 20 ft off the centreline and 30 ft short of the C node ahead: the bearing to
    /// that node is 34° off its nose, yet the jet is lined up with the taxiway, so it still refuses to turn about.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetLinedUpOffCentrelineNearTheNodeIsRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        double alongDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        LatLon onCentreline = GeoMath.ProjectPoint(tangentCut.Position, new TrueHeading(alongDeg + 180.0), ShortOfNodeFt / GeoMath.FeetPerNm);
        LatLon position = GeoMath.ProjectPoint(onCentreline, new TrueHeading(alongDeg + 90.0), OffCentrelineFt / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, alongDeg, "OAK", "C");
        ground.Engine.World.AddAircraft(aircraft);
        double offNoseDeg = GeoMath.AbsBearingDifference(alongDeg, GeoMath.BearingTo(position, tangentCut.Position));

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}, node {tangentCut.Id} {offNoseDeg:F0} deg off the nose: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
    }

    /// <summary>How far short of the C node ahead the lined-up, off-centreline jet stands.</summary>
    private const double ShortOfNodeFt = 30.0;

    /// <summary>How far off C's centreline the lined-up jet stands.</summary>
    private const double OffCentrelineFt = 20.0;

    /// <summary>
    /// The same jet pose with the TAXI coming from a scenario preset: no controller is there to hear "unable" and re-issue,
    /// so the jet is not refused and takes the route from the C node ahead, as before the turn about existed.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_ScriptedJetTaxiIsNotRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        output.WriteLine($"scripted {command}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"the scripted '{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(tangentCut.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// A C172 mid-C facing H, cleared along a route drawn to the C node behind it: from that node the drawn route is already
    /// taxied and resolves nothing, so the route from the node ahead is kept, and its first segment drives straight back
    /// over the edge the aircraft stands on. The aircraft turns about where it stands, and the route reports it.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_KeepsTheRouteAheadAndReportsTheTurnAbout()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI #{farEnd.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(ReferenceEquals(first.Edge.Edge, occupied), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 on the drawn route to the C node behind it, ticked: it turns about on the painted C edge it stands on, which
    /// its kept route drives backwards, and its centre stays within C's half-width of the centreline through the turn and
    /// the leg back to the far node.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_TurnsAboutWithinTheTaxiway()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI #{farEnd.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(ReferenceEquals(route.Segments[0].Edge.Edge, occupied), "segment 0 is not the occupied C edge driven backwards");

        double peakOffsetFt = 0.0;
        double maxTurnSpeedKts = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        int legDoneAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (route.CurrentSegmentIndex > 0) || (aircraft.Ground.AssignedTaxiRoute is null),
            TurnAboutLegTickSeconds,
            _ =>
            {
                peakOffsetFt = Math.Max(peakOffsetFt, CentreOffsetFt(aircraft.Position, tangentCut, farEnd));
                maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed);
            }
        );
        output.WriteLine(
            $"{command}: leg back to node {farEnd.Id} done after {legDoneAt}s; "
                + $"peak {peakOffsetFt:F1} ft off C, max {maxTurnSpeedKts:F2} kt while turning"
        );

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        double pivotKts = PivotSpeedKts("C172", AircraftCategory.Piston);
        Assert.True(
            maxTurnSpeedKts <= pivotKts + TurnSpeedOvershootKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt, above the {pivotKts:F2} kt pivot speed (ω·r at its "
                + $"{TurnAboutFit.Evaluate("C172", AircraftCategory.Piston).RadiusFt:F1} ft turn-about radius) plus {TurnSpeedOvershootKts:F1} kt"
        );
        Assert.True(
            peakOffsetFt <= (Tdg1AHalfWidthFt + CentreTrackingToleranceFt),
            $"the aircraft's centre swung {peakOffsetFt:F2} ft off C's centreline turning about, beyond {Tdg1AHalfWidthFt:F1} ft, "
                + $"{Tdg1AHalfWidthSource}, plus the {CentreTrackingToleranceFt:F1} ft tracking tolerance"
        );
    }

    /// <summary>
    /// The same drawn route from the controller to a B738 lined up along C: the only route is the one from the node ahead,
    /// which turns the jet about where it stands, so it refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_LinedUpJetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI #{farEnd.Id}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A C172 mid-way along a straight KOAK taxi edge whose end behind it is a runway holding position, facing the other
    /// end, cleared along a route drawn through that holding position: the route from the holding position wins, and no
    /// approach leg is driven up to a holding position, so that route opens with no leg back to it. The aircraft still turns
    /// about toward the holding position, and the route reports it from the far end, with that node as its target.
    /// </summary>
    [Fact]
    public void DrawnRouteBackThroughARunwayHoldShort_NoLegToIt_ReportsTheTurnAboutFromTheFarEnd()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(holdShort.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        Assert.Equal(holdShort.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 cleared back through the runway holding position behind it, ticked: the route opens on the holding position
    /// with no leg back to it, so segment 0 runs on past it. The turn about is pending until the aircraft reaches the holding
    /// position, and no longer once it is past it, though still on segment 0.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_PastTheTarget_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        List<(int Second, TaxiTurnAboutShape Shape)> shortOfTarget = [];
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => PastTheTargetOnSegment0(aircraft, route, holdShort),
            PastTheTargetTickSeconds,
            second =>
            {
                if (AlongPastTargetFt(aircraft, route, holdShort) < 0.0)
                {
                    shortOfTarget.Add((second, aircraft.Ground.TaxiTurnAboutShape));
                }
            }
        );
        output.WriteLine($"{AlongPastTargetFt(aircraft, route, holdShort):F0} ft past node {holdShort.Id} on segment 0 after {pastAt}s");

        Assert.True(
            pastAt > 0,
            $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} on segment 0 within {PastTheTargetTickSeconds}s"
        );
        Assert.NotEmpty(shortOfTarget);
        Assert.All(shortOfTarget, s => Assert.Equal(TaxiTurnAboutShape.FromFarEnd, s.Shape));
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// The same C172 snapshotted and restored into a fresh engine: a snapshot taken right after the clearance restores the
    /// turn about from the far end as pending, and one taken once the aircraft is past the holding position restores it as
    /// no longer pending.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_PastTheTarget_SnapshotRestore_StaysNotPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;

        AircraftState beforeTarget = RestoreIntoAFreshEngine(ground.Engine.CaptureSnapshot(), aircraft.Callsign);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, beforeTarget.Ground.TaxiTurnAboutShape);
        Assert.Equal(holdShort.Id, beforeTarget.Ground.TaxiTurnAboutTargetNodeId);

        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => PastTheTargetOnSegment0(aircraft, route, holdShort),
            PastTheTargetTickSeconds,
            null
        );
        Assert.True(
            pastAt > 0,
            $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} on segment 0 within {PastTheTargetTickSeconds}s"
        );

        AircraftState pastTarget = RestoreIntoAFreshEngine(ground.Engine.CaptureSnapshot(), aircraft.Callsign);
        Assert.Equal(0, pastTarget.Ground.AssignedTaxiRoute!.CurrentSegmentIndex);
        Assert.Equal(TaxiTurnAboutShape.None, pastTarget.Ground.TaxiTurnAboutShape);
        Assert.Null(pastTarget.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// A C172 half-way along a long KOAK stub to a runway holding position, on the taxiway side and facing away from it,
    /// cleared along a route drawn back through the holding position across the runway: the route opens on that bar with no
    /// leg back to it. The aircraft turns about and holds at the bar, and the turn about is no longer pending while it holds
    /// there, nor once a <c>CROSS</c> has taken it across the runway past the holding position.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_HeldAtTheTargetBar_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackAcrossARunway(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.True(route.GetHoldShortAt(holdShort.Id) is { IsCleared: false }, $"no uncleared hold short at node {holdShort.Id}");

        int heldAt = TickUntilHeldAt(ground, aircraft, route, holdShort);
        Assert.True(heldAt > 0, $"the aircraft did not hold short at node {holdShort.Id} within {PastTheTargetTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);

        SendAndLog(ground, aircraft, $"CROSS {BarRunway(holdShort)}");
        AssertNotPendingOncePast(ground, aircraft, route, holdShort);
    }

    /// <summary>
    /// The same C172 on the long stub, cleared across the runway (<c>CROSS</c>) before it moves: it turns about and crosses
    /// past the holding position without stopping there, and the turn about is no longer pending once it is past it.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_BarClearedUpFront_PastTheTarget_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackAcrossARunway(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SendAndLog(ground, aircraft, $"CROSS {BarRunway(holdShort)}");
        Assert.True(route.GetHoldShortAt(holdShort.Id) is { IsCleared: true }, $"no cleared hold short at node {holdShort.Id}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => AlongPastTargetFt(aircraft, route, holdShort) >= PastTargetFt,
            PastTheTargetTickSeconds,
            null
        );
        output.WriteLine(
            $"past node {holdShort.Id} after {pastAt}s, phase {aircraft.Phases?.CurrentPhase?.Name}, segment {route.CurrentSegmentIndex}"
        );

        Assert.True(pastAt > 0, $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} within {PastTheTargetTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// Stubs from this long put the aircraft half-way along them beyond the start-node hold radius (150 ft), so it turns
    /// about before it can take the hold.
    /// </summary>
    private const double MinTurnAboutStubFt = 400.0;

    /// <summary>Ticks until the aircraft holds short and logs where it holds; the second it held, or -1.</summary>
    private int TickUntilHeldAt(SfoGround ground, AircraftState aircraft, TaxiRoute route, GroundNode holdShort)
    {
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase,
            PastTheTargetTickSeconds,
            null
        );
        output.WriteLine(
            $"held after {heldAt}s on segment {route.CurrentSegmentIndex}, "
                + $"{AlongPastTargetFt(aircraft, route, holdShort):F0} ft past node {holdShort.Id}, heading "
                + $"{GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, route.Segments[0].Edge.DepartureBearing):F0} deg off segment 0"
        );
        return heldAt;
    }

    /// <summary>
    /// A C172 half-way along the first KOAK stub to a runway holding position whose taxiway crosses that runway
    /// (<see cref="FirstStubToARunwayCrossing"/>), facing away from the holding position, cleared along a route drawn back
    /// through it to the far side's holding position: the aircraft and the near holding position. Asserts the route opens
    /// on that holding position with a turn about from the far end.
    /// </summary>
    private (AircraftState Aircraft, GroundNode HoldShort) TaxiBackAcrossARunway(SfoGround ground)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub, GroundNode farSide) =
            FirstStubToARunwayCrossing(ground.Layout)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxi edge of {MinTurnAboutStubFt:F0} ft or more ends at a holding position its taxiway crosses"
            );
        AircraftState aircraft = TaxiBackThrough(ground, (ahead, holdShort, stub), farSide);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(holdShort.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);
        return (aircraft, holdShort);
    }

    /// <summary>
    /// The first straight KOAK taxi edge (by its lower node id) of at least <see cref="MinTurnAboutStubFt"/> from a taxi node
    /// to a runway holding position whose taxiway goes on across that runway to its far-side holding position
    /// (<see cref="FarSideBar"/>), half-way along which the aircraft stands mid-edge; null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode HoldShort, GroundEdge Stub, GroundNode FarSide)? FirstStubToARunwayCrossing(
        AirportGroundLayout layout
    )
    {
        IEnumerable<GroundEdge> taxiEdges = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0))
            .Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= MinTurnAboutStubFt)
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in taxiEdges)
        {
            GroundNode[] holds = [.. edge.Nodes.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))];
            if (holds.Length != 1)
            {
                continue;
            }

            GroundNode ahead = edge.OtherNode(holds[0]);
            bool midEdge = layout.FindMidEdgeTaxiStart(Along(holds[0].Position, ahead.Position, 0.5)) == edge;
            if (midEdge && (FarSideBar(holds[0], edge) is { } farSide))
            {
                return (ahead, holds[0], edge, farSide);
            }
        }

        return null;
    }

    /// <summary>How many nodes the walk across a runway visits looking for the far side's holding position.</summary>
    private const int MaxCrossingWalkNodes = 8;

    /// <summary>
    /// The holding position for the same runway reached by walking on from <paramref name="hold"/> away from
    /// <paramref name="stub"/>, taking the straightest edge at each node; null when the walk meets none within
    /// <see cref="MaxCrossingWalkNodes"/> nodes.
    /// </summary>
    private static GroundNode? FarSideBar(GroundNode hold, IGroundEdge stub)
    {
        GroundNode node = hold;
        IGroundEdge from = stub;
        double bearingDeg = GeoMath.BearingTo(stub.OtherNode(hold).Position, hold.Position);
        for (int i = 0; i < MaxCrossingWalkNodes; i++)
        {
            GroundNode at = node;
            double inDeg = bearingDeg;
            IGroundEdge? next = at
                .Edges.Where(e => e != from)
                .MinBy(e => GeoMath.AbsBearingDifference(e.Directed(at, e.OtherNode(at)).DepartureBearing, inDeg));
            if (next is null)
            {
                return null;
            }

            GroundNode to = next.OtherNode(at);
            if ((to.Type == GroundNodeType.RunwayHoldShort) && Equals(to.RunwayId, hold.RunwayId))
            {
                return to;
            }

            bearingDeg = next.Directed(at, to).ArrivalBearing;
            from = next;
            node = to;
        }

        return null;
    }

    /// <summary>The first designator of the runway <paramref name="holdShort"/> holds short of.</summary>
    private static string BarRunway(GroundNode holdShort) =>
        holdShort.RunwayId is { } runway ? runway.End1 : throw new InvalidOperationException($"node {holdShort.Id} holds short of no runway");

    /// <summary>Sends <paramref name="command"/> to the aircraft, logs the outcome and asserts it was accepted.</summary>
    private void SendAndLog(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}; phase {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
    }

    /// <summary>
    /// Ticks until the aircraft is <see cref="PastTargetFt"/> past <paramref name="target"/> along <paramref name="route"/>'s
    /// segment 0, asserting it gets there and that no turn about is pending on any second along the way.
    /// </summary>
    private void AssertNotPendingOncePast(SfoGround ground, AircraftState aircraft, TaxiRoute route, GroundNode target)
    {
        List<(int Second, string? Phase)> pending = [];
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => AlongPastTargetFt(aircraft, route, target) >= PastTargetFt,
            PastTheTargetTickSeconds,
            second =>
            {
                if (aircraft.Ground.TaxiTurnAboutShape != TaxiTurnAboutShape.None)
                {
                    pending.Add((second, aircraft.Phases?.CurrentPhase?.Name));
                }
            }
        );
        output.WriteLine($"past node {target.Id} after {pastAt}s, phase {aircraft.Phases?.CurrentPhase?.Name}, segment {route.CurrentSegmentIndex}");

        Assert.True(pastAt > 0, $"the aircraft was not {PastTargetFt:F0} ft past node {target.Id} within {PastTheTargetTickSeconds}s");
        Assert.True(pending.Count == 0, $"the turn about was pending at {string.Join(", ", pending.Select(p => $"{p.Second}s ({p.Phase})"))}");
    }

    /// <summary>How far past the target, along segment 0, the aircraft must be to count as past it.</summary>
    private const double PastTargetFt = 20.0;

    /// <summary>How long the turn about and the roll past the holding position may take.</summary>
    private const int PastTheTargetTickSeconds = 180;

    /// <summary>
    /// A C172 half-way along the first KOAK stub to a runway holding position (<see cref="FirstStubToARunwayHoldShort"/>),
    /// facing away from it, cleared along a route drawn back through the holding position to the node beyond it: the
    /// aircraft and the holding position.
    /// </summary>
    private (AircraftState Aircraft, GroundNode HoldShort) TaxiBackThroughARunwayHoldShort(SfoGround ground)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub) =
            FirstStubToARunwayHoldShort(ground.Layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends at a runway holding position");
        GroundNode beyond = holdShort.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(holdShort);
        return (TaxiBackThrough(ground, (ahead, holdShort, stub), beyond), holdShort);
    }

    /// <summary>
    /// A C172 half-way along <paramref name="stub"/>'s edge, facing its node ahead, cleared along a route drawn back through
    /// its holding position to <paramref name="to"/>.
    /// </summary>
    private AircraftState TaxiBackThrough(SfoGround ground, (GroundNode Ahead, GroundNode HoldShort, GroundEdge Edge) stub, GroundNode to)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge edge) = stub;
        LatLon position = Along(holdShort.Position, ahead.Position, 0.5);
        AircraftState aircraft = MakeAircraft(
            ground.Layout,
            position,
            GeoMath.BearingTo(holdShort.Position, ahead.Position),
            "OAK",
            edge.TaxiwayName
        );
        aircraft.AircraftType = "C172";
        ground.Engine.World.AddAircraft(aircraft);

        string command = $"TAXI #{holdShort.Id} #{to.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine(
            $"{edge.TaxiwayName} edge {ahead.Id}-{holdShort.Id} ({edge.DistanceNm * GeoMath.FeetPerNm:F0} ft), holding position for "
                + $"{holdShort.RunwayId}; {command}: {result.Success} — {result.Message}"
        );
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);
        return aircraft;
    }

    /// <summary>
    /// How far (ft) the aircraft's centre is past <paramref name="target"/> along the departure of <paramref name="route"/>'s
    /// segment 0, measured along that bearing; negative when short of the line through the node square to it.
    /// </summary>
    private static double AlongPastTargetFt(AircraftState aircraft, TaxiRoute route, GroundNode target)
    {
        double bearingDeg = route.Segments[0].Edge.DepartureBearing;
        double distFt = GeoMath.DistanceNm(target.Position, aircraft.Position) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(bearingDeg, GeoMath.BearingTo(target.Position, aircraft.Position)) * Math.PI / 180.0;
        return distFt * Math.Cos(offRad);
    }

    /// <summary>
    /// Whether the aircraft is still on segment 0 of <paramref name="route"/> and <see cref="PastTargetFt"/> past
    /// <paramref name="target"/>.
    /// </summary>
    private static bool PastTheTargetOnSegment0(AircraftState aircraft, TaxiRoute route, GroundNode target) =>
        (route.CurrentSegmentIndex == 0) && (AlongPastTargetFt(aircraft, route, target) >= PastTargetFt);

    /// <summary>The aircraft <paramref name="callsign"/> from <paramref name="snapshot"/>, restored into a fresh KOAK engine.</summary>
    private AircraftState RestoreIntoAFreshEngine(StateSnapshotDto snapshot, string callsign)
    {
        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        return Assert.IsType<AircraftState>(restored.Engine.FindAircraft(callsign));
    }

    /// <summary>
    /// The first straight KOAK taxi edge (by its lower node id) from a taxi node to a runway holding position with another
    /// edge beyond it, half-way along which the aircraft stands mid-edge (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>);
    /// null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode HoldShort, GroundEdge Stub)? FirstStubToARunwayHoldShort(AirportGroundLayout layout)
    {
        IEnumerable<GroundEdge> taxiEdges = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0))
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in taxiEdges)
        {
            GroundNode[] holds = [.. edge.Nodes.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.Edges.Count > 1))];
            if (holds.Length != 1)
            {
                continue;
            }

            GroundNode ahead = edge.OtherNode(holds[0]);
            if (layout.FindMidEdgeTaxiStart(Along(holds[0].Position, ahead.Position, 0.5)) == edge)
            {
                return (ahead, holds[0], edge);
            }
        }

        return null;
    }

    /// <summary>
    /// A C172 on M2 cleared <c>TAXI @B26</c>: the route from the M2 node ahead resolves none, so the clearance is re-planned
    /// from the node behind, and the aircraft turns about on M2 toward it.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ReplansFromTheNodeBehind()
    {
        if (SpawnOnM2("C172") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.True(result.Success, $"'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from a scenario preset: the route from the M2 node ahead resolves none,
    /// so the scripted jet is not left without a route, and takes the one from the node behind.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ScriptedJetTakesTheRouteFromTheNodeBehind()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: true);

        Assert.True(result.Success, $"the scripted 'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// The scripted B738 lined up along M2 whose node ahead resolves no route to <c>@B26</c>: it takes the route from the
    /// node behind, so it reports the turn about from the far end with that node as the target, though it is lined up.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ScriptedJetReportsTheTurnAboutFromTheFarEnd()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: true);

        Assert.True(result.Success, $"the scripted 'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, spawned.Aircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from the controller: the route from the M2 node ahead resolves none and
    /// the one from the node behind needs a turn about on M2, so the jet refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_LinedUpJetRefusesToTurnAround()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{M2Gate}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 lined up along M2, cleared to a gate whose ramp-confined route resolves from neither end of M2: the jet
    /// gives the first attempt's own refusal, not the turn-about one.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromBothEnds_LinedUpJetGivesTheFirstRefusal()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{UnreachableGate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{UnreachableGate}' was accepted: {result.Message}");
        Assert.NotEqual(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.StartsWith($"Unable, need a route to gate {UnreachableGate}", result.Message, StringComparison.Ordinal);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 held after a push on M2, lined up toward <see cref="M2NodeAhead"/>, given the scenario preset
    /// <c>PUSH; TAXI @B26</c>: the push tows it straight back along M2 and it ends still lined up toward the node ahead, so
    /// the TAXI from the controller would be refused for want of room to turn around. A preset is not refused: it takes
    /// the route from the node behind. Snapshotted mid-push and restored, the queued TAXI still fires as the preset it is
    /// and takes the same route as the live run.
    /// </summary>
    [Fact]
    public void ScriptedPushThenTaxi_RestoredMidPush_TaxisAsLive()
    {
        if (SpawnHeldAfterPushOnM2() is not { } controller)
        {
            return;
        }

        // The pose: after the push, the controller's TAXI is refused.
        PushAndHold(controller, isScenarioScripted: false);
        ParseResult<ParsedCommand> parsedTaxi = CommandParser.Parse($"TAXI @{M2Gate}");
        Assert.True(parsedTaxi.IsSuccess, parsedTaxi.Reason);
        CommandResult refused = GroundCommandHandler.TryTaxi(
            controller.Aircraft,
            Assert.IsType<TaxiCommand>(parsedTaxi.Value),
            controller.Ground.Layout
        );
        output.WriteLine($"controller TAXI @{M2Gate} after the push: {refused.Success} — {refused.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), refused.Message);

        TaxiRoute live = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: false);
        TaxiRoute restored = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: true);

        Assert.Equal(M2NodeBehind, live.Segments[0].ToNodeId);
        Assert.Equal(live.Segments.Select(s => s.ToNodeId), restored.Segments.Select(s => s.ToNodeId));
        Assert.Equal(live.TurnAboutShape, restored.TurnAboutShape);
        Assert.Equal(live.TurnAboutTargetNodeId, restored.TurnAboutTargetNodeId);
    }

    /// <summary>How far along M2 from <see cref="M2NodeBehind"/> to <see cref="M2NodeAhead"/> the aircraft held after a push stands.</summary>
    private const double HeldAfterPushFraction = 0.85;

    /// <summary>How long a push, or a push and the TAXI queued behind it, may take.</summary>
    private const int PushTickSeconds = 240;

    /// <summary>
    /// A stopped B738 in <see cref="HoldingAfterPushbackPhase"/> <see cref="HeldAfterPushFraction"/> of the way along SFO's
    /// M2 edge toward <see cref="M2NodeAhead"/> and lined up toward it, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft)? SpawnHeldAfterPushOnM2()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, HeldAfterPushFraction);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = "B738";
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft);
    }

    /// <summary>Dispatches a bare <c>PUSH</c> and ticks until the aircraft is held after it.</summary>
    private void PushAndHold((SfoGround Ground, AircraftState Aircraft) spawned, bool isScenarioScripted)
    {
        AircraftState aircraft = spawned.Aircraft;
        CommandResult pushed = CommandDispatcher.DispatchCompound(
            ParseCompound("PUSH"),
            aircraft,
            spawned.Ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted, facilityHint: null)
        );
        Assert.True(pushed.Success, pushed.Message);
        int heldAt = SfoGroundHarness.TickUntil(
            spawned.Ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase,
            PushTickSeconds,
            null
        );
        output.WriteLine(
            $"push held after {heldAt}s at ({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) hdg {aircraft.TrueHeading.Degrees:F0}, "
                + $"mid-edge {spawned.Ground.Layout.FindMidEdgeTaxiStart(aircraft.Position)?.TaxiwayName ?? "none"}"
        );
        Assert.True(heldAt > 0, $"the push did not end within {PushTickSeconds}s");
    }

    /// <summary>
    /// Dispatches the preset <c>PUSH; TAXI @B26</c> and ticks until the queued TAXI has fired, after restoring a snapshot
    /// taken mid-push into a fresh engine when <paramref name="snapshotMidPush"/>; the route it took.
    /// </summary>
    private TaxiRoute PushThenTaxiFires((SfoGround Ground, AircraftState Aircraft) spawned, bool snapshotMidPush)
    {
        (SfoGround ground, AircraftState aircraft) = spawned;
        CommandResult dispatched = CommandDispatcher.DispatchCompound(
            ParseCompound($"PUSH; TAXI @{M2Gate}"),
            aircraft,
            ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null)
        );
        Assert.True(dispatched.Success, dispatched.Message);

        if (snapshotMidPush)
        {
            int movingAt = SfoGroundHarness.TickUntil(
                ground.Engine,
                () => (aircraft.Phases?.CurrentPhase is PushbackPhase) && (aircraft.GroundSpeed > 1.0),
                PushTickSeconds,
                null
            );
            Assert.True(movingAt > 0, "the push never got moving");
            StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
            ground = SfoGroundHarness.Build(output, autoCross: false)!.Value;
            ground.Engine.RestoreFromSnapshot(snapshot);
            aircraft = Assert.IsType<AircraftState>(ground.Engine.FindAircraft(aircraft.Callsign));
            Assert.IsType<PushbackPhase>(aircraft.Phases?.CurrentPhase);
        }

        AircraftState flown = aircraft;
        int firedAt = SfoGroundHarness.TickUntil(ground.Engine, () => flown.Ground.AssignedTaxiRoute is not null, PushTickSeconds, null);
        output.WriteLine($"{(snapshotMidPush ? "restored" : "live")}: TAXI fired after {firedAt}s, phase {flown.Phases?.CurrentPhase?.Name}");
        Assert.True(firedAt > 0, $"the queued TAXI took no route within {PushTickSeconds}s (phase {flown.Phases?.CurrentPhase?.Name})");
        SfoGroundHarness.DumpRoute(output, flown.Ground.AssignedTaxiRoute!);
        return flown.Ground.AssignedTaxiRoute!;
    }

    private static CompoundCommand ParseCompound(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    /// <summary>The M2 node ahead of the aircraft on M2: no route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeAhead = 14;

    /// <summary>The M2 node behind the aircraft on M2: the route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeBehind = 15;

    /// <summary>The gate cleared from M2 with no taxiway named, so the route stays inside the ramp.</summary>
    private const string M2Gate = "B26";

    /// <summary>A gate in another terminal's ramp: no route inside the ramp reaches it from either end of M2.</summary>
    private const string UnreachableGate = "G10";

    /// <summary>
    /// A stopped <paramref name="type"/> 60% of the way along SFO's M2 edge from <see cref="M2NodeBehind"/> to
    /// <see cref="M2NodeAhead"/>, facing the node ahead and lined up along M2, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft, GroundNode Behind)? SpawnOnM2(string type)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, 0.6);
        GroundEdge? occupied = ground.Layout.FindMidEdgeTaxiStart(position);
        Assert.True(
            (occupied is not null) && occupied.MatchesTaxiway("M2") && occupied.Nodes.Contains(ahead) && occupied.Nodes.Contains(behind),
            $"the aircraft is not mid-way along the M2 edge {behind.Id}-{ahead.Id}"
        );

        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft, behind);
    }

    /// <summary><paramref name="command"/> for the aircraft on M2, from the controller or from a scenario preset.</summary>
    private CommandResult TaxiOnM2((SfoGround Ground, AircraftState Aircraft, GroundNode Behind) spawned, string command, bool isScenarioScripted)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);
        CommandResult result = isScenarioScripted
            ? CommandDispatcher.Dispatch(
                taxi,
                spawned.Aircraft,
                spawned.Ground.Engine.BuildDispatchContext(spawned.Aircraft, isScenarioScripted: true, facilityHint: null)
            )
            : GroundCommandHandler.TryTaxi(spawned.Aircraft, taxi, spawned.Ground.Layout);
        output.WriteLine(
            $"{(isScenarioScripted ? "scripted " : "")}{command} ({spawned.Aircraft.AircraftType}): {result.Success} — {result.Message}"
        );
        if (spawned.Aircraft.Ground.AssignedTaxiRoute is { } route)
        {
            SfoGroundHarness.DumpRoute(output, route);
        }

        return result;
    }

    /// <summary>How long the turn about at the far C node may take.</summary>
    private const int TurnAboutTickSeconds = 90;

    /// <summary>How far above the slow-turn speed the ground speed may read during the turn about: physics overshoot within one sub-tick.</summary>
    private const double TurnSpeedOvershootKts = 0.2;

    /// <summary>Builds an engine over the committed KOAK layout, or null when the layout or navdata is unavailable.</summary>
    private SfoGround? BuildOak()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-c-turn-about",
                ScenarioName = "OAK C Turn About",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = false,
            },
        };
        return new SfoGround(engine, layout);
    }

    /// <summary>A stopped <paramref name="type"/> 35% of the way along C edge tangent cut → far end, facing the tangent cut (toward H).</summary>
    private static AircraftState SpawnMidCFacingH(SfoGround ground, GroundNode tangentCut, GroundNode farEnd, string type)
    {
        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(farEnd.Position, tangentCut.Position), "OAK", "C");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// An aircraft mid-C at KOAK, a quarter of the edge short of one end and facing it, whose route leaves that node by
    /// another edge turning more than 90°: it has passed neither end of the edge it is on, so it drives the free-space leg
    /// to the node rather than being read as past it.
    /// </summary>
    [Fact]
    public void ApproachLeg_MidEdge_RouteTurnsSharplyAtTheNodeAhead_DrivesToTheNode()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode node, GroundNode behind, IGroundEdge turn) =
            FirstSharpTurnOffAStraightEdge(layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends in a turn of more than 90°");
        LatLon position = Along(behind.Position, node.Position, 0.75);
        output.WriteLine($"on edge {behind.Id}-{node.Id}, route leaves node {node.Id} by {turn.TaxiwayName} to {turn.OtherNode(node).Id}");
        var heading = new TrueHeading(GeoMath.BearingTo(behind.Position, node.Position));
        var graphRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = turn.TaxiwayName, Edge = turn.Directed(node, turn.OtherNode(node)) }],
            HoldShortPoints = [],
        };

        TaxiRoute route = TaxiApproachLeg.Prepend(layout, position, heading, graphRoute);
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(node.Id, first.ToNodeId);
    }

    /// <summary>Straight taxi edges from this long: three quarters along one, the aircraft is clear of both end nodes.</summary>
    private const double MinSharpTurnEdgeFt = 300.0;

    /// <summary>
    /// The first (by edge order) straight taxi edge of at least <see cref="MinSharpTurnEdgeFt"/>, three quarters along which
    /// the aircraft is on it (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>), with an end that is no holding
    /// position and has an edge leaving it more than 90° off the direction of arrival (<see cref="SharpTurnAt"/>), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? FirstSharpTurnOffAStraightEdge(AirportGroundLayout layout)
    {
        foreach (GroundEdge edge in layout.Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0)))
        {
            if ((edge.DistanceNm * GeoMath.FeetPerNm) < MinSharpTurnEdgeFt)
            {
                continue;
            }

            foreach ((GroundNode node, GroundNode behind) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                bool onEdge = layout.FindOccupiedTaxiEdge(Along(behind.Position, node.Position, 0.75)) == edge;
                if (onEdge && (node.Type != GroundNodeType.RunwayHoldShort) && (SharpTurnAt(node, behind, edge) is { } found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An edge other than <paramref name="occupied"/> leaving <paramref name="node"/> more than 90° off the direction of
    /// arrival from <paramref name="behind"/> (no ramp connector, no runway centreline), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? SharpTurnAt(GroundNode node, GroundNode behind, IGroundEdge occupied)
    {
        double arrivalDeg = GeoMath.BearingTo(behind.Position, node.Position);
        IGroundEdge? turn = node.Edges.FirstOrDefault(e =>
            (e != occupied)
            && !e.IsRamp
            && !e.IsRunwayCenterline
            && (GeoMath.AbsBearingDifference(e.Directed(node, e.OtherNode(node)).DepartureBearing, arrivalDeg) > 90.0)
        );
        return turn is null ? null : (node, behind, turn);
    }

    [Fact]
    public void TaxiOnB_MidEdge_Sfo_RouteStartsOnB()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundEdge longestB = LongestStraightB(layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");

        LatLon position = Along(from.Position, to.Position, 0.5);
        AircraftState aircraft = MakeAircraft(layout, position, GeoMath.BearingTo(from.Position, to.Position), "SFO", "B");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI B");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "B"), $"route joins the graph at node {firstFrom.Id}, not on B");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, more than the free-space bound from the B node ahead: the route opens with the
    /// approach leg along B, starting where the aircraft stands.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_ApproachLegStartsAtTheAircraft()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out _);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(
            VirtualNode.IsVirtualEdge(first.Edge.Edge),
            $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}, not the approach leg"
        );
        double startOffFt = GeoMath.DistanceNm(first.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(startOffFt <= 1.0, $"the approach leg starts {startOffFt:F1} ft from the aircraft");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, ticked: the aircraft rolls on along B at taxi speed rather than crawling at the
    /// navigator's reacquire speed toward a route start hundreds of feet ahead, and it does not turn around.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_KeepsTaxiSpeed()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out double startHeadingDeg);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);

        SfoGroundHarness.TickUntil(ground.Engine, () => false, TaxiTickSeconds, null);

        double offBFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "B", aircraft.Position);
        double turnedDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, startHeadingDeg);
        output.WriteLine($"after {TaxiTickSeconds}s: {offBFt:F0} ft off B, turned {turnedDeg:F0} deg, GS {aircraft.GroundSpeed:F1} kt");
        Assert.True(offBFt <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt, $"the aircraft is {offBFt:F0} ft off B");
        Assert.True(turnedDeg <= MaxTurnFromStartDeg, $"the aircraft turned {turnedDeg:F0} deg from its start heading");
        Assert.True(aircraft.GroundSpeed > MinTaxiSpeedKts, $"ground speed is {aircraft.GroundSpeed:F1} kt after {TaxiTickSeconds}s");
    }

    /// <summary>
    /// An aircraft 20 ft into a fillet arc past a straight edge's end is not on that edge: the edge's nearest point is its
    /// end node, behind the aircraft. The taxi start must not be that node, nor any other node behind the aircraft: it is
    /// the fillet's own end ahead.
    /// </summary>
    [Fact]
    public void AircraftInAFilletPastAnEdgeEnd_DoesNotStartBehind()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        if (PoseIntoAFillet(layout) is not { } pose)
        {
            Assert.Fail("no fillet arc at SFO runs past the end of the straight taxi edge nearest it");
            return;
        }

        (GroundNode arcEntry, GroundNode arcExit, LatLon position, TrueHeading heading) = pose;
        output.WriteLine($"{IntoFilletFt:F0} ft into the fillet {arcEntry.Id}-{arcExit.Id}, heading {heading.Degrees:F0}");

        // The TAXI command's own start resolution: the heading-biased pick, else the nearest node.
        GroundNode? headingBiased = layout.FindNearestNodeForTaxi(position, heading);
        GroundNode? start = headingBiased ?? layout.FindNearestNode(position);
        output.WriteLine($"heading-biased start: {(headingBiased is null ? "none" : $"node {headingBiased.Id}")}");
        Assert.NotNull(start);
        double startFt = GeoMath.DistanceNm(position, start.Position) * GeoMath.FeetPerNm;
        double offHeadingDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, start.Position), heading.Degrees);
        output.WriteLine($"taxi start node {start.Id}: {startFt:F0} ft away, {offHeadingDeg:F0} deg off the heading");
        Assert.True(
            (startFt <= AirportGroundLayout.AtNodeToleranceFt) || (offHeadingDeg < 90.0),
            $"the taxi starts at node {start.Id}, {startFt:F0} ft away and {offHeadingDeg:F0} deg off the heading: behind the aircraft"
        );
        Assert.True(start == arcExit, $"the taxi starts at node {start.Id}, not at the fillet's end ahead, node {arcExit.Id}");
    }

    /// <summary>
    /// Spawns a B738 stopped half-way along SFO's longest straight B edge, on its centreline, facing along it. Half that
    /// edge is longer than the free-space bound, so the B node ahead is out of a free-space leg's reach.
    /// </summary>
    private AircraftState SpawnMidLongestB(SfoGround ground, out double startHeadingDeg)
    {
        GroundEdge longestB = LongestStraightB(ground.Layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        double halfFt = longestB.DistanceNm * GeoMath.FeetPerNm / 2.0;
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        Assert.True(
            halfFt > RampLaneReposition.MaxCrossingFt,
            $"half the longest B edge is {halfFt:F0} ft, inside the {RampLaneReposition.MaxCrossingFt:F0} ft free-space bound"
        );

        startHeadingDeg = GeoMath.BearingTo(from.Position, to.Position);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "DAL880",
            "B738",
            (from, new TrueHeading(startHeadingDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.Position = Along(from.Position, to.Position, 0.5);
        aircraft.TrueTrack = new TrueHeading(startHeadingDeg);
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }

    /// <summary>SFO's longest straight B edge (no ramp connector, no runway centreline).</summary>
    private static GroundEdge LongestStraightB(AirportGroundLayout layout)
    {
        GroundEdge? longest = layout.Edges.Where(e => e.MatchesTaxiway("B") && !e.IsRamp && !e.IsRunwayCenterline).MaxBy(e => e.DistanceNm);
        Assert.NotNull(longest);
        return longest;
    }

    /// <summary>
    /// Walks C on from <paramref name="start"/>, away from <paramref name="cameFrom"/>, to the first node with an edge on
    /// another taxiway (no ramp connector, no runway centreline), and returns that taxiway's name.
    /// </summary>
    private static string FirstTaxiwayOffCBeyond(GroundNode start, GroundNode cameFrom)
    {
        GroundNode previous = cameFrom;
        GroundNode current = start;
        for (int i = 0; i < MaxWalkNodes; i++)
        {
            if (OtherTaxiwayAt(current, "C") is { } name)
            {
                return name;
            }

            GroundNode next = NextAlong(current, "C", GeoMath.BearingTo(previous.Position, current.Position));
            Assert.True(next != previous, $"C ends at node {current.Id} with no other taxiway off it");
            previous = current;
            current = next;
        }

        Assert.Fail($"no taxiway off C within {MaxWalkNodes} nodes of node {start.Id}");
        return "";
    }

    private static string? OtherTaxiwayAt(GroundNode node, string taxiway)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRamp || edge.IsRunwayCenterline)
            {
                continue;
            }

            string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
            string? other = names.FirstOrDefault(n => !string.Equals(n, taxiway, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// The first (by node id) fillet arc, entered from either end, where an aircraft <see cref="IntoFilletFt"/> into the arc
    /// is past the end of the straight taxi edge nearest it: that edge's nearest point clamps to an end node, and both of
    /// its ends lie behind the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? PoseIntoAFillet(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if ((edge is not GroundArc arc) || arc.IsRamp)
                {
                    continue;
                }

                CubicBezier curve = arc.ToBezier();
                if (arc.Nodes[1] == node)
                {
                    curve = new CubicBezier(curve.P3Lat, curve.P3Lon, curve.P2Lat, curve.P2Lon, curve.P1Lat, curve.P1Lon, curve.P0Lat, curve.P0Lon);
                }

                if (IntoFilletPose(layout, node, arc, curve) is { } pose)
                {
                    return pose;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The pose <see cref="IntoFilletFt"/> along <paramref name="curve"/> (the arc as entered at <paramref name="arcEntry"/>),
    /// heading along its tangent, or null when the arc does not fit the case: too short or too long, a node within the
    /// at-node tolerance, the nearest straight taxi edge beyond the on-taxiway offset, its nearest point inside the edge, or
    /// an end of it ahead of the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? IntoFilletPose(
        AirportGroundLayout layout,
        GroundNode arcEntry,
        GroundArc arc,
        CubicBezier curve
    )
    {
        double arcFt = arc.DistanceNm * GeoMath.FeetPerNm;
        if ((arcFt < MinFilletFt) || (arcFt > MaxFilletFt))
        {
            return null;
        }

        double t = IntoFilletFt / arcFt;
        (double lat, double lon) = curve.Evaluate(t);
        var position = new LatLon(lat, lon);
        var heading = new TrueHeading(curve.TangentBearing(t));
        if (
            layout.Nodes.Values.Any(n =>
                (n.Edges.Count > 0) && ((GeoMath.DistanceNm(position, n.Position) * GeoMath.FeetPerNm) <= AirportGroundLayout.AtNodeToleranceFt)
            )
        )
        {
            return null;
        }

        if (
            (layout.FindNearestTaxiEdge(position) is not { } nearest)
            || ((nearest.DistNm * GeoMath.FeetPerNm) > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt)
        )
        {
            return null;
        }

        GroundEdge straight = nearest.Edge;
        (LatLon _, double _, bool clamped) = GeoMath.FootOfPerpendicular(position, straight.Nodes[0].Position, straight.Nodes[1].Position);
        bool bothEndsBehind = straight.Nodes.All(n => GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, n.Position), heading.Degrees) >= 90.0);
        return (clamped && bothEndsBehind) ? (arcEntry, arc.OtherNode(arcEntry), position, heading) : null;
    }

    private AirportGroundLayout? LoadLayout(string airportId)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout(airportId);
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg, string airportId, string taxiway)
    {
        var aircraft = new AircraftState
        {
            Callsign = "DAL880",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = airportId, Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        return aircraft;
    }

    private TaxiRoute Resolve(AirportGroundLayout layout, AircraftState aircraft, string command)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        for (int i = 0; i < Math.Min(4, route.Segments.Count); i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        return route;
    }

    private static TaxiRouteSegment FirstLayoutSegment(TaxiRoute route) => route.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));

    private static bool OnStraightEdgeOf(GroundNode node, string taxiway) => node.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway));

    private static LatLon Along(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + ((to.Lat - from.Lat) * fraction), from.Lon + ((to.Lon - from.Lon) * fraction));

    private static GroundNode NextAlong(GroundNode node, string taxiway, double bearingDeg)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;
        foreach (IGroundEdge edge in node.Edges)
        {
            if ((edge is not GroundEdge) || !edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            GroundNode other = edge.OtherNode(node);
            double diff = GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = other;
            }
        }

        Assert.NotNull(best);
        return best;
    }
}
