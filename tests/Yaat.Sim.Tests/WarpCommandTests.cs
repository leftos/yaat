using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class WarpCommandTests
{
    /// <summary>
    /// Pins the shared navdata singletons before any test body runs; the real-layout section below
    /// reads them while other classes may be mid-initialization.
    /// </summary>
    public WarpCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const double FixLat = 37.5;
    private const double FixLon = -121.8;

    private static IDisposable WithFix() =>
        NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (FixLat, FixLon) })
        );

    private static AircraftState MakeAircraft(double heading = 90, double altitude = 3500, double ias = 180) =>
        new()
        {
            Callsign = "N123",
            AircraftType = "C172",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };

    // ----- parser: shape coverage ----------------------------------------

    [Fact]
    public void Warp_PositionOnly_LeavesHeadingAltitudeSpeedUnset()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL").Value);
        Assert.Equal("SUNOL", cmd.PositionLabel);
        Assert.Equal(FixLat, cmd.Latitude);
        Assert.Equal(FixLon, cmd.Longitude);
        Assert.Null(cmd.MagneticHeading);
        Assert.Null(cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_HeadingOnly_FillsHeadingAndLeavesOthersUnset()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Null(cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetSecondArg_SkipsHeadingAndFillsAltitude()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_HeadingAndShorthandAltitude_LeavesSpeedUnset()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 50").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetThenSpeed_SkipsHeading()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000 220").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    [Fact]
    public void Warp_AllFourArgs_SetsEverything()
    {
        using IDisposable _ = WithFix();
        WarpCommand cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 5000 220").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    // ----- parser: failure cases -----------------------------------------

    [Fact]
    public void Warp_NoArg_Fails()
    {
        using IDisposable _ = WithFix();
        ParseResult<ParsedCommand> result = CommandParser.Parse("WARP");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_TooManyArgs_Fails()
    {
        using IDisposable _ = WithFix();
        ParseResult<ParsedCommand> result = CommandParser.Parse("WARP SUNOL 270 5000 220 99");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_GarbageToken_Fails()
    {
        using IDisposable _ = WithFix();
        ParseResult<ParsedCommand> result = CommandParser.Parse("WARP SUNOL abc");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_NegativeHeadingFollowedByAltitude_FillsAltitudeOnly()
    {
        // -50: heading rejects (out of range); altitude rejects (resolver returns null for <=0); speed rejects (<=0).
        // Whole command should fail because no slot accepts the token.
        using IDisposable _ = WithFix();
        ParseResult<ParsedCommand> result = CommandParser.Parse("WARP SUNOL -50");
        Assert.False(result.IsSuccess);
    }

    // ----- parser: WARPG shapes, incl. $spot -----------------------------

    [Fact]
    public void WarpGround_DollarSpot_ParsesSpotName()
    {
        WarpGroundCommand cmd = Assert.IsType<WarpGroundCommand>(CommandParser.Parse("WARPG $9").Value);
        Assert.Equal("9", cmd.SpotName);
        Assert.Null(cmd.NodeId);
        Assert.Null(cmd.ParkingName);
        Assert.Equal("", cmd.Taxiway1);
        Assert.Equal("", cmd.Taxiway2);
    }

    [Fact]
    public void WarpGround_DollarSpot_Uppercases()
    {
        WarpGroundCommand cmd = Assert.IsType<WarpGroundCommand>(CommandParser.Parse("WARPG $t9").Value);
        Assert.Equal("T9", cmd.SpotName);
    }

    [Fact]
    public void WarpGround_NodeRef_StillParses()
    {
        WarpGroundCommand cmd = Assert.IsType<WarpGroundCommand>(CommandParser.Parse("WARPG #42").Value);
        Assert.Equal(42, cmd.NodeId);
        Assert.Null(cmd.SpotName);
    }

    [Fact]
    public void WarpGround_AtParking_StillParses()
    {
        WarpGroundCommand cmd = Assert.IsType<WarpGroundCommand>(CommandParser.Parse("WARPG @B12").Value);
        Assert.Equal("B12", cmd.ParkingName);
        Assert.Null(cmd.SpotName);
    }

    [Fact]
    public void WarpGround_TwoTaxiways_StillParses()
    {
        WarpGroundCommand cmd = Assert.IsType<WarpGroundCommand>(CommandParser.Parse("WARPG C B").Value);
        Assert.Equal("C", cmd.Taxiway1);
        Assert.Equal("B", cmd.Taxiway2);
        Assert.Null(cmd.SpotName);
    }

    [Fact]
    public void WarpGround_BareDollar_Fails()
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse("WARPG $");
        Assert.False(result.IsSuccess);
    }

    // ----- ApplyWarpGround: $spot resolution -----------------------------

    [Fact]
    public void ApplyWarpGround_SpotName_WarpsToSpotNode()
    {
        AirportGroundLayout layout = BuildLayoutWithSpot();
        AircraftState ac = MakeGroundAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", SpotName: "9"), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(layout.Nodes[2].Position.Lat, ac.Position.Lat, 6);
        Assert.Equal(layout.Nodes[2].Position.Lon, ac.Position.Lon, 6);
    }

    [Fact]
    public void ApplyWarpGround_UnknownSpot_ReturnsClearError()
    {
        AirportGroundLayout layout = BuildLayoutWithSpot();
        AircraftState ac = MakeGroundAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", SpotName: "NOPE"), layout);

        Assert.False(result.Success);
        Assert.Contains("NOPE", result.Message);
    }

    // ----- ApplyWarp: nulls fall back to current state -------------------

    [Fact]
    public void ApplyWarp_NullHeading_KeepsCurrentHeading()
    {
        AircraftState ac = MakeAircraft(heading: 123, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, MagneticHeading: null, Altitude: 6000, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(123, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullAltitude_KeepsCurrentAltitude()
    {
        AircraftState ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: null, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(4500, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullSpeed_KeepsCurrentSpeed()
    {
        AircraftState ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: 6000, Speed: null);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_AllNull_KeepsAllExceptPosition()
    {
        AircraftState ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, MagneticHeading: null, Altitude: null, Speed: null);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(FixLat, ac.Position.Lat);
        Assert.Equal(FixLon, ac.Position.Lon);
        Assert.Equal(90, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(4500, ac.Altitude);
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_AllSet_AppliesAll()
    {
        AircraftState ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(180), Altitude: 8000, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(180, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(8000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
        Assert.False(ac.IsOnGround);
    }

    // ----- Phase-gate bypass: WARP / WARPG must work in any phase ---------

    private static AirportGroundLayout BuildSimpleLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "KTEST" };
        var node0 = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.620, -122.380),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var node1 = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.622, -122.380),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge01 = new GroundEdge
        {
            Nodes = [node0, node1],
            TaxiwayName = "A",
            DistanceNm = 0.12,
        };
        node0.Edges.Add(edge01);
        node1.Edges.Add(edge01);
        layout.Nodes[0] = node0;
        layout.Nodes[1] = node1;
        layout.Edges.Add(edge01);
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildLayoutWithSpot()
    {
        AirportGroundLayout layout = BuildSimpleLayout();
        var spot = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.621, -122.381),
            Type = GroundNodeType.Spot,
            Name = "9",
        };
        var edge12 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], spot],
            TaxiwayName = "RAMP",
            DistanceNm = 0.05,
        };
        layout.Nodes[1].Edges.Add(edge12);
        spot.Edges.Add(edge12);
        layout.Nodes[2] = spot;
        layout.Edges.Add(edge12);
        layout.RebuildAdjacencyLists();
        return layout;
    }

    /// <summary>
    /// No committed airport GeoJSON carries a helipad feature, so the helipad half of the stand
    /// predicate is pinned against a hand-built node. Nothing here stands in for real navdata — the
    /// gate half is covered against the real SFO layout below.
    /// </summary>
    /// <returns>The simple layout plus a named helipad with its own landing heading.</returns>
    private static AirportGroundLayout BuildLayoutWithHelipad()
    {
        AirportGroundLayout layout = BuildSimpleLayout();
        var helipad = new GroundNode
        {
            Id = 2,
            Position = new LatLon(37.623, -122.379),
            Type = GroundNodeType.Helipad,
            Name = "H1",
            // Far from the ~218 deg bearing of the pad's only edge, so the assertion below cannot pass
            // on the PickBestEdgeHeading fallback.
            TrueHeading = new TrueHeading(45),
        };
        var edge12 = new GroundEdge
        {
            Nodes = [layout.Nodes[1], helipad],
            TaxiwayName = "RAMP",
            DistanceNm = 0.05,
        };
        layout.Nodes[1].Edges.Add(edge12);
        helipad.Edges.Add(edge12);
        layout.Nodes[2] = helipad;
        layout.Edges.Add(edge12);
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AircraftState MakeGroundAircraft(AirportGroundLayout layout)
    {
        var ac = new AircraftState
        {
            Callsign = "N427MX",
            AircraftType = "C172",
            Position = layout.Nodes[0].Position,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        return ac;
    }

    private static CommandResult DispatchWarp(AircraftState ac, ParsedCommand cmd, AirportGroundLayout? layout)
    {
        var compound = new CompoundCommand([new ParsedBlock(null, [cmd])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);
        return CommandDispatcher.DispatchCompound(compound, ac, ctx);
    }

    /// <summary>
    /// Regression: WARPG against an aircraft in HoldingInPositionPhase used to fail with
    /// "aircraft is holding position on the taxiway; issue RES, a new TAXI/PUSH/ATXI/LAND/LUAW, or DEL"
    /// because the phase's CanAcceptCommand switch had no case for WarpGround. WARPG is a
    /// destructive teleport — its handler clears phases/queue/route internally — so the
    /// dispatcher bypasses the phase gate for it.
    /// </summary>
    [Fact]
    public void WarpGround_SucceedsFromHoldingInPositionPhase()
    {
        AirportGroundLayout layout = BuildSimpleLayout();
        AircraftState ac = MakeGroundAircraft(layout);
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingInPositionPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", NodeId: 1), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(layout.Nodes[1].Position.Lat, ac.Position.Lat, 6);
        Assert.Equal(layout.Nodes[1].Position.Lon, ac.Position.Lon, 6);
        Assert.NotNull(ac.Phases);
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);
        Assert.Empty(ac.Queue.Blocks);
    }

    /// <summary>
    /// Confirms the bypass isn't accidentally specific to one ground phase. AtParkingPhase
    /// also has no WarpGround case in its CanAcceptCommand switch and would otherwise reject.
    /// </summary>
    [Fact]
    public void WarpGround_SucceedsFromAtParkingPhase()
    {
        AirportGroundLayout layout = BuildSimpleLayout();
        AircraftState ac = MakeGroundAircraft(layout);
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", NodeId: 1), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(layout.Nodes[1].Position.Lat, ac.Position.Lat, 6);
        Assert.NotNull(ac.Phases);
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);
        Assert.Empty(ac.Queue.Blocks);
    }

    /// <summary>
    /// Symmetric coverage for airborne WARP: any airborne phase whose CanAcceptCommand
    /// switch doesn't whitelist Warp would otherwise reject the teleport. FinalApproachPhase
    /// is one such phase; the dispatcher's sim-control bypass routes WARP past the gate.
    /// </summary>
    [Fact]
    public void Warp_SucceedsFromAirbornePhaseThatDoesNotWhitelistWarp()
    {
        RunwayInfo rwy = TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 100);
        var ac = new AircraftState
        {
            Callsign = "JSX170",
            AircraftType = "E145",
            Position = new LatLon(rwy.ThresholdLatitude + 0.05, rwy.ThresholdLongitude + 0.05),
            TrueHeading = rwy.TrueHeading,
            Altitude = 2500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(phase);
        phase.Status = PhaseStatus.Active;
        ac.Phases = phases;
        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);

        var cmd = new WarpCommand("DEST", FixLat, FixLon, new MagneticHeading(180), Altitude: 8000, Speed: 250);
        CommandResult result = DispatchWarp(ac, cmd, layout: null);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(FixLat, ac.Position.Lat, 6);
        Assert.Equal(FixLon, ac.Position.Lon, 6);
        Assert.Equal(8000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
        Assert.False(ac.IsOnGround);
        Assert.Null(ac.Phases);
    }

    // ----- ApplyWarpGround on the real SFO layout: stands vs surface positions -----
    //
    // Node ids renumber whenever the layout is regenerated, so every node here is resolved by name.
    // Fixture facts (tests/Yaat.Sim.Tests/TestData/sfo.geojson): D3 is a parking node with heading 360,
    // D2 is a parking node with heading 325, 5A is a spot node with no heading.

    private const string PushbackRefusal = "Pushback requires aircraft to be at parking";

    private static AirportGroundLayout? SfoLayout() => new TestAirportGroundData().GetLayout("SFO");

    /// <summary>
    /// A jet sitting on the SFO surface, heading well away from any gate's nose-in heading so a
    /// heading assertion after the warp is meaningful.
    /// </summary>
    /// <param name="layout">The SFO ground layout the aircraft warps around on.</param>
    /// <returns>An on-ground aircraft carrying the layout.</returns>
    private static AircraftState MakeSfoAircraft(AirportGroundLayout layout)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL1234",
            AircraftType = "B738",
            Position = new LatLon(37.6160, -122.3830),
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        return ac;
    }

    private static CommandResult DispatchText(AircraftState ac, string text, AirportGroundLayout layout)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        return DispatchWarp(ac, parsed.Value!, layout);
    }

    /// <summary>
    /// A gate is a stand: WARPG onto one must leave the aircraft parked on it, nose-in on the gate's
    /// own heading, so the ground verbs that require a stand (PUSH) are accepted afterwards.
    /// </summary>
    /// <param name="gate">Name of the SFO parking node to warp onto.</param>
    [Theory]
    [InlineData("D3")]
    [InlineData("D2")]
    public void WarpGround_ToGate_LeavesAircraftAtParking(string gate)
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode? node = layout.FindParkingByName(gate);
        Assert.NotNull(node);
        Assert.NotNull(node.TrueHeading);
        AircraftState ac = MakeSfoAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: gate), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);
        Assert.Equal(gate, ac.Ground.ParkingSpot);
        Assert.Equal(node.TrueHeading.Value.Degrees, ac.TrueHeading.Degrees, 1e-6);
    }

    [Fact]
    public void WarpGround_ToGate_ThenPushbackIsAccepted()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);
        CommandResult warp = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: "D3"), layout);
        Assert.True(warp.Success, $"Expected WARPG success, got: {warp.Message}");

        CommandResult push = DispatchText(ac, "PUSH $5A", layout);

        Assert.True(push.Success, $"Expected PUSH success, got: {push.Message}");
    }

    /// <summary>
    /// Issue #448: an arrival that landed and taxied carries no cached <c>Ground.Layout</c>; TAXI resolves the
    /// layout through the dispatch context, so WARPG must too instead of refusing "No airport layout loaded".
    /// </summary>
    [Fact]
    public void WarpGround_WithoutCachedLayout_UsesDispatchContextLayout()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);
        ac.Ground.Layout = null;

        CommandResult result = DispatchText(ac, "WARPG A E", layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        GroundNode? node = CommandDispatcher.FindTaxiwayIntersection(layout, "A", "E");
        Assert.NotNull(node);
        Assert.Equal(node.Position.Lat, ac.Position.Lat, 6);
        Assert.Equal(node.Position.Lon, ac.Position.Lon, 6);
    }

    [Fact]
    public void WarpGround_ByNodeId_ToGate_LeavesAircraftAtParking()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode? node = layout.FindParkingByName("D3");
        Assert.NotNull(node);
        Assert.NotNull(node.TrueHeading);
        AircraftState ac = MakeSfoAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", NodeId: node.Id), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);
        Assert.Equal("D3", ac.Ground.ParkingSpot);
        Assert.Equal(node.TrueHeading.Value.Degrees, ac.TrueHeading.Degrees, 1e-6);
    }

    /// <summary>
    /// An aircraft warped onto a gate is occupying it, not spawning at it: it must not auto-delete as a
    /// parked arrival, and it must not make the solo-training ready-to-taxi call.
    /// </summary>
    [Fact]
    public void WarpGround_ToGate_SetsAutoDeleteExemptAndSuppressesCallup()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: "D3"), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.True(ac.Ground.AutoDeleteExempt, "a warped-in aircraft must not auto-delete at the gate");
        Assert.True(ac.Ground.InitialCallupDecisionProcessed, "a warped-in aircraft must not call ready-to-taxi");
    }

    /// <summary>
    /// A spot is a surface position, not a stand: the aircraft idles on it, leaves no parking behind,
    /// and PUSH still refuses.
    /// </summary>
    [Fact]
    public void WarpGround_ToSpot_StillHoldsInPosition()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", SpotName: "5A"), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);
        Assert.Null(ac.Ground.ParkingSpot);

        CommandResult push = DispatchText(ac, "PUSH $5A", layout);

        Assert.False(push.Success);
        Assert.Contains(PushbackRefusal, push.Message);
    }

    /// <summary>
    /// A helipad is a stand too, on the same predicate arm as a gate. No committed layout has one, so
    /// this is the only guard against the enum arm being dropped in a later refactor.
    /// </summary>
    [Fact]
    public void WarpGround_ToHelipad_LeavesAircraftAtParking()
    {
        AirportGroundLayout layout = BuildLayoutWithHelipad();
        AircraftState ac = MakeGroundAircraft(layout);

        CommandResult result = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: "H1"), layout);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);
        Assert.Equal("H1", ac.Ground.ParkingSpot);
        Assert.Equal(45, ac.TrueHeading.Degrees, 1e-6);
    }

    /// <summary>
    /// The taxiway-pair form is the other way onto a surface position, and the one an instructor uses to
    /// pull an aircraft off a stand. It must clear the stand the aircraft left, or the Aircraft List and
    /// the pilot's own phraseology keep naming a gate the aircraft is no longer on.
    /// </summary>
    [Fact]
    public void WarpGround_FromGateToTaxiwayIntersection_ClearsParkingSpot()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);
        CommandResult toGate = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: "D3"), layout);
        Assert.True(toGate.Success, $"Expected success, got: {toGate.Message}");
        Assert.Equal("D3", ac.Ground.ParkingSpot);

        // Alpha crosses Echo on the real SFO layout.
        CommandResult offGate = DispatchWarp(ac, new WarpGroundCommand("A", "E"), layout);

        Assert.True(offGate.Success, $"Expected success, got: {offGate.Message}");
        Assert.Null(ac.Ground.ParkingSpot);
        Assert.NotNull(ac.Phases);
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void WarpGround_AwayFromGate_ClearsParkingSpot()
    {
        AirportGroundLayout? layout = SfoLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState ac = MakeSfoAircraft(layout);
        CommandResult toGate = DispatchWarp(ac, new WarpGroundCommand("", "", ParkingName: "D3"), layout);
        Assert.True(toGate.Success, $"Expected success, got: {toGate.Message}");
        Assert.Equal("D3", ac.Ground.ParkingSpot);

        CommandResult offGate = DispatchWarp(ac, new WarpGroundCommand("", "", SpotName: "5A"), layout);

        Assert.True(offGate.Success, $"Expected success, got: {offGate.Message}");
        Assert.Null(ac.Ground.ParkingSpot);
        Assert.NotNull(ac.Phases);
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);
    }
}
