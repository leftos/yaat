using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class ProcedureCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 5000)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = 250,
        };
    }

    // --- CVIA ---

    [Fact]
    public void Cvia_EnablesSidViaMode()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = false;

        CommandResult result = CommandDispatcher.Dispatch(new ClimbViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(aircraft.Procedure.SidViaMode);
        Assert.Null(aircraft.Procedure.SidViaCeiling);
    }

    [Fact]
    public void Cvia_WithAltitude_SetsCeiling()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";

        CommandResult result = CommandDispatcher.Dispatch(new ClimbViaCommand(19000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(aircraft.Procedure.SidViaMode);
        Assert.Equal(19000, aircraft.Procedure.SidViaCeiling);
    }

    [Fact]
    public void Cvia_WithoutActiveSid_Rejected()
    {
        AircraftState aircraft = CreateAircraft();

        CommandResult result = CommandDispatcher.Dispatch(new ClimbViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("No active SID", result.Message);
    }

    // --- DVIA ---

    [Fact]
    public void Dvia_EnablesStarViaMode()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";

        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.StarViaFloor);
    }

    [Fact]
    public void Dvia_WithAltitude_SetsFloor()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";

        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(10000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Equal(10000, aircraft.Procedure.StarViaFloor);
    }

    // --- CM/DM cancel any via clearance, whichever direction (7110.65 §4-5-7.h.11 NOTE 1) ---

    [Fact]
    public void Cm_DuringDescendVia_CancelsStarVia()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaFloor = 10000;

        CommandResult result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(17000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.StarViaFloor);
        Assert.Equal(17000.0, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Dm_DuringClimbVia_CancelsSidVia()
    {
        AircraftState aircraft = CreateAircraft(altitude: 9000);
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.SidViaCeiling = 19000;

        CommandResult result = CommandDispatcher.Dispatch(new DescendMaintainCommand(7000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(aircraft.Procedure.SidViaMode);
        Assert.Null(aircraft.Procedure.SidViaCeiling);
        Assert.Equal(7000.0, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void CmOrAbove_DuringDescendVia_CancelsStarVia()
    {
        AircraftState aircraft = CreateAircraft(altitude: 7500);
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaFloor = 5000;

        CommandResult result = CommandDispatcher.Dispatch(
            new ClimbMaintainCommand(5500, AltitudeAssignmentModifier.AtOrAbove),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.StarViaFloor);
        Assert.Equal(5500.0, aircraft.Targets.AltitudeFloor);
    }

    // --- DVIA supersedes the stored assigned altitude (YAAT-956) ---

    [Fact]
    public void DescendVia_ClearsTheAssignedAltitude()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        CommandDispatcher.Dispatch(new DescendMaintainCommand(12000), aircraft, TestDispatch.Context(Random.Shared));
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);

        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Null(aircraft.Targets.AssignedAltitude);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    [Fact]
    public void DescendViaExceptMaintain_ClearsTheAssignedAltitude()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        CommandDispatcher.Dispatch(new DescendMaintainCommand(12000), aircraft, TestDispatch.Context(Random.Shared));
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);

        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(10000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Null(aircraft.Targets.AssignedAltitude);
        Assert.Equal(10000, aircraft.Procedure.StarViaFloor);
    }

    [Fact]
    public void DescendVia_Refused_KeepsTheAssignedAltitude()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        CommandDispatcher.Dispatch(new DescendMaintainCommand(12000), aircraft, TestDispatch.Context(Random.Shared));

        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A refused transmission carrying a CFIX hands back the transmission id it took, so the aircraft's queue
    /// snapshot (which carries the counter) is what it was before the refusal.
    /// </summary>
    [Fact]
    public void RefusedCrossFixTransmission_LeavesTheQueueSnapshotUnchanged()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        string before = JsonSerializer.Serialize(aircraft.Queue.ToSnapshot());
        CompoundCommand compound = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound("CFIX SUNOL 40, DVIA", null).Value);

        CommandResult result = CommandDispatcher.DispatchCompound(compound, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Equal(0, aircraft.Queue.LastTransmissionId);
        Assert.Equal(before, JsonSerializer.Serialize(aircraft.Queue.ToSnapshot()));
    }

    /// <summary>
    /// DVIA restores a superseded crossing from the runway transition the aircraft flies, not the first transition
    /// that names the fix. CHINS5 at KSEA publishes RADDY differently on its runway 16 and runway 34 transitions.
    /// </summary>
    [Fact]
    public void DescendVia_RestoresTheRestrictionOfTheFlownRunwayTransition()
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KSEA", "CHINS5"));
        NavigationTarget ForRunway(string key) =>
            DepartureClearanceHandler.ResolveLegsToTargets([.. star.RunwayTransitions[key].Legs]).First(t => t.Name == "RADDY");
        NavigationTarget north = ForRunway("RW34B");
        NavigationTarget south = ForRunway("RW16B");
        Assert.NotEqual(north.AltitudeRestriction, south.AltitudeRestriction);

        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.FlightPlan.Destination = "KSEA";
        aircraft.Procedure.ActiveStarId = "CHINS5";
        aircraft.Procedure.DestinationRunway = "34R";
        (double Lat, double Lon) raddy = NavigationDatabase.Instance.GetFixPosition("RADDY")!.Value;
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "RADDY", Position = new LatLon(raddy.Lat, raddy.Lon) });

        CommandDispatcher.Dispatch(
            new CrossFixCommand("RADDY", raddy.Lat, raddy.Lon, 11000, CrossFixAltitudeType.At, null),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );
        CommandResult result = CommandDispatcher.Dispatch(new DescendViaCommand(null), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        NavigationTarget fix = Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal(north.AltitudeRestriction, fix.AltitudeRestriction);
        Assert.Equal(north.SpeedRestriction, fix.SpeedRestriction);
    }

    // --- CM disables SidViaMode ---

    [Fact]
    public void Cm_DisablesSidViaMode_PreservesLateralPath()
    {
        AircraftState aircraft = CreateAircraft(altitude: 5000);
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.SidViaCeiling = 10000;

        // Set up a navigation route to verify it's preserved
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "FIX1", Position = new LatLon(37.5, -122.0) });

        CommandResult result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(35000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(aircraft.Procedure.SidViaMode);
        Assert.Null(aircraft.Procedure.SidViaCeiling);
        Assert.Equal(35000.0, aircraft.Targets.TargetAltitude);
        // Lateral path preserved
        Assert.Single(aircraft.Targets.NavigationRoute);
        // ActiveSidId preserved — procedure is still active laterally
        Assert.Equal("PORTE3", aircraft.Procedure.ActiveSidId);
    }

    // --- DM disables StarViaMode ---

    [Fact]
    public void Dm_DisablesStarViaMode_PreservesLateralPath()
    {
        AircraftState aircraft = CreateAircraft(altitude: 15000);
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaFloor = 10000;

        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "FIX1", Position = new LatLon(37.5, -122.0) });

        CommandResult result = CommandDispatcher.Dispatch(new DescendMaintainCommand(10000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.StarViaFloor);
        Assert.Equal(10000.0, aircraft.Targets.TargetAltitude);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("BDEGA3", aircraft.Procedure.ActiveStarId);
    }

    // --- FH clears entire procedure ---

    [Fact]
    public void Fh_ClearsEntireProcedure()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;

        CommandResult result = CommandDispatcher.Dispatch(
            new FlyHeadingCommand(new MagneticHeading(270)),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Null(aircraft.Procedure.ActiveSidId);
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.False(aircraft.Procedure.SidViaMode);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.SidViaCeiling);
        Assert.Null(aircraft.Procedure.StarViaFloor);
    }

    // --- DCT clears entire procedure ---

    [Fact]
    public void Dct_ClearsEntireProcedure()
    {
        TestVnasData.EnsureInitialized();

        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;

        var fixes = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        CommandResult result = CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Null(aircraft.Procedure.ActiveSidId);
        Assert.False(aircraft.Procedure.SidViaMode);
    }

    // --- TL/TR clear entire procedure ---

    [Fact]
    public void TurnLeft_ClearsEntireProcedure()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;

        CommandResult result = CommandDispatcher.Dispatch(
            new TurnLeftCommand(new MagneticHeading(180)),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.False(aircraft.Procedure.StarViaMode);
    }

    [Fact]
    public void TurnRight_ClearsEntireProcedure()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;

        CommandResult result = CommandDispatcher.Dispatch(
            new TurnRightCommand(new MagneticHeading(90)),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Null(aircraft.Procedure.ActiveSidId);
        Assert.False(aircraft.Procedure.SidViaMode);
    }

    // --- Vectoring warning ---

    [Fact]
    public void Fh_WithoutAltitude_WarnsWhenClearingProcedure()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;
        aircraft.Targets.TargetAltitude = 10000;

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), aircraft, TestDispatch.Context(Random.Shared));

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("without an altitude", aircraft.PendingWarnings[0]);
        Assert.Null(aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Fh_WithAltitude_InCompound_NoWarning()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;

        // FH 070, DM 050 — parallel block with heading + altitude
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(70)), new DescendMaintainCommand(5000)]),
        ]);

        CommandDispatcher.DispatchCompound(compound, aircraft, TestDispatch.Context(Random.Shared));

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void TurnLeft_WithoutAltitude_WarnsWhenClearingProcedure()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveStarId = "BDEGA3";
        aircraft.Procedure.StarViaMode = true;
        aircraft.Targets.TargetAltitude = 5000;

        CommandDispatcher.Dispatch(new TurnLeftCommand(new MagneticHeading(270)), aircraft, TestDispatch.Context(Random.Shared));

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("without an altitude", aircraft.PendingWarnings[0]);
        Assert.Null(aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Fh_NoProcedureActive_NoWarning()
    {
        AircraftState aircraft = CreateAircraft();
        // No active procedure

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), aircraft, TestDispatch.Context(Random.Shared));

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void Cm_ClearingViaMode_NoWarning()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Procedure.ActiveSidId = "PORTE3";
        aircraft.Procedure.SidViaMode = true;

        // CM only disables via mode, doesn't clear the procedure
        CommandDispatcher.Dispatch(new ClimbMaintainCommand(35000), aircraft, TestDispatch.Context(Random.Shared));

        Assert.Empty(aircraft.PendingWarnings);
        // SidId still active — only via mode was disabled
        Assert.Equal("PORTE3", aircraft.Procedure.ActiveSidId);
    }
}
