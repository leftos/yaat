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
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL").Value);
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
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Null(cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetSecondArg_SkipsHeadingAndFillsAltitude()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_HeadingAndShorthandAltitude_LeavesSpeedUnset()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 50").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Null(cmd.Speed);
    }

    [Fact]
    public void Warp_FullFeetThenSpeed_SkipsHeading()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 5000 220").Value);
        Assert.Null(cmd.MagneticHeading);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    [Fact]
    public void Warp_AllFourArgs_SetsEverything()
    {
        using var _ = WithFix();
        var cmd = Assert.IsType<WarpCommand>(CommandParser.Parse("WARP SUNOL 270 5000 220").Value);
        Assert.Equal(270, cmd.MagneticHeading?.Degrees);
        Assert.Equal(5000, cmd.Altitude);
        Assert.Equal(220, cmd.Speed);
    }

    // ----- parser: failure cases -----------------------------------------

    [Fact]
    public void Warp_NoArg_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_TooManyArgs_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL 270 5000 220 99");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_GarbageToken_Fails()
    {
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL abc");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Warp_NegativeHeadingFollowedByAltitude_FillsAltitudeOnly()
    {
        // -50: heading rejects (out of range); altitude rejects (resolver returns null for <=0); speed rejects (<=0).
        // Whole command should fail because no slot accepts the token.
        using var _ = WithFix();
        var result = CommandParser.Parse("WARP SUNOL -50");
        Assert.False(result.IsSuccess);
    }

    // ----- ApplyWarp: nulls fall back to current state -------------------

    [Fact]
    public void ApplyWarp_NullHeading_KeepsCurrentHeading()
    {
        var ac = MakeAircraft(heading: 123, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, MagneticHeading: null, Altitude: 6000, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(123, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullAltitude_KeepsCurrentAltitude()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: null, Speed: 250);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(4500, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_NullSpeed_KeepsCurrentSpeed()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
        var cmd = new WarpCommand("X", FixLat, FixLon, new MagneticHeading(270), Altitude: 6000, Speed: null);
        FlightCommandHandler.ApplyWarp(cmd, ac);
        Assert.Equal(270, ac.MagneticHeading.Degrees, 6);
        Assert.Equal(6000, ac.Altitude);
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    [Fact]
    public void ApplyWarp_AllNull_KeepsAllExceptPosition()
    {
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
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
        var ac = MakeAircraft(heading: 90, altitude: 4500, ias: 210);
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
        var ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);
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
        var layout = BuildSimpleLayout();
        var ac = MakeGroundAircraft(layout);
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingInPositionPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        Assert.IsType<HoldingInPositionPhase>(ac.Phases.CurrentPhase);

        var result = DispatchWarp(ac, new WarpGroundCommand("", "", NodeId: 1), layout);

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
        var layout = BuildSimpleLayout();
        var ac = MakeGroundAircraft(layout);
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);

        var result = DispatchWarp(ac, new WarpGroundCommand("", "", NodeId: 1), layout);

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
        var rwy = TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 100);
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
        var result = DispatchWarp(ac, cmd, layout: null);

        Assert.True(result.Success, $"Expected success, got: {result.Message}");
        Assert.Equal(FixLat, ac.Position.Lat, 6);
        Assert.Equal(FixLon, ac.Position.Lon, 6);
        Assert.Equal(8000, ac.Altitude);
        Assert.Equal(250, ac.IndicatedAirspeed);
        Assert.False(ac.IsOnGround);
        Assert.Null(ac.Phases);
    }
}
