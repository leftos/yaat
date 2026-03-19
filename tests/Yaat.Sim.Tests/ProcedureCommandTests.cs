using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class ProcedureCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 5000)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.0,
            Longitude = -122.0,
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
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = false;

        var result = CommandDispatcher.Dispatch(new ClimbViaCommand(null), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.SidViaMode);
        Assert.Null(aircraft.SidViaCeiling);
    }

    [Fact]
    public void Cvia_WithAltitude_SetsCeiling()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";

        var result = CommandDispatcher.Dispatch(new ClimbViaCommand(19000), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.SidViaMode);
        Assert.Equal(19000, aircraft.SidViaCeiling);
    }

    [Fact]
    public void Cvia_WithoutActiveSid_Rejected()
    {
        var aircraft = CreateAircraft();

        var result = CommandDispatcher.Dispatch(new ClimbViaCommand(null), aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("No active SID", result.Message);
    }

    // --- DVIA ---

    [Fact]
    public void Dvia_EnablesStarViaMode()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.ActiveStarId = "BDEGA3";

        var result = CommandDispatcher.Dispatch(new DescendViaCommand(null), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.StarViaMode);
        Assert.Null(aircraft.StarViaFloor);
    }

    [Fact]
    public void Dvia_WithAltitude_SetsFloor()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.ActiveStarId = "BDEGA3";

        var result = CommandDispatcher.Dispatch(new DescendViaCommand(10000), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.StarViaMode);
        Assert.Equal(10000, aircraft.StarViaFloor);
    }

    // --- CM disables SidViaMode ---

    [Fact]
    public void Cm_DisablesSidViaMode_PreservesLateralPath()
    {
        var aircraft = CreateAircraft(altitude: 5000);
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;
        aircraft.SidViaCeiling = 10000;

        // Set up a navigation route to verify it's preserved
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.5,
                Longitude = -122.0,
            }
        );

        var result = CommandDispatcher.Dispatch(new ClimbMaintainCommand(35000), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.False(aircraft.SidViaMode);
        Assert.Null(aircraft.SidViaCeiling);
        Assert.Equal(35000.0, aircraft.Targets.TargetAltitude);
        // Lateral path preserved
        Assert.Single(aircraft.Targets.NavigationRoute);
        // ActiveSidId preserved — procedure is still active laterally
        Assert.Equal("PORTE3", aircraft.ActiveSidId);
    }

    // --- DM disables StarViaMode ---

    [Fact]
    public void Dm_DisablesStarViaMode_PreservesLateralPath()
    {
        var aircraft = CreateAircraft(altitude: 15000);
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;
        aircraft.StarViaFloor = 10000;

        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.5,
                Longitude = -122.0,
            }
        );

        var result = CommandDispatcher.Dispatch(new DescendMaintainCommand(10000), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.False(aircraft.StarViaMode);
        Assert.Null(aircraft.StarViaFloor);
        Assert.Equal(10000.0, aircraft.Targets.TargetAltitude);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("BDEGA3", aircraft.ActiveStarId);
    }

    // --- FH clears entire procedure ---

    [Fact]
    public void Fh_ClearsEntireProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;

        var result = CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(aircraft.ActiveSidId);
        Assert.Null(aircraft.ActiveStarId);
        Assert.False(aircraft.SidViaMode);
        Assert.False(aircraft.StarViaMode);
        Assert.Null(aircraft.SidViaCeiling);
        Assert.Null(aircraft.StarViaFloor);
    }

    // --- DCT clears entire procedure ---

    [Fact]
    public void Dct_ClearsEntireProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;

        var fixes = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var result = CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(aircraft.ActiveSidId);
        Assert.False(aircraft.SidViaMode);
    }

    // --- TL/TR clear entire procedure ---

    [Fact]
    public void TurnLeft_ClearsEntireProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;

        var result = CommandDispatcher.Dispatch(new TurnLeftCommand(new MagneticHeading(180)), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(aircraft.ActiveStarId);
        Assert.False(aircraft.StarViaMode);
    }

    [Fact]
    public void TurnRight_ClearsEntireProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;

        var result = CommandDispatcher.Dispatch(new TurnRightCommand(new MagneticHeading(90)), aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(aircraft.ActiveSidId);
        Assert.False(aircraft.SidViaMode);
    }

    // --- Vectoring warning ---

    [Fact]
    public void Fh_WithoutAltitude_WarnsWhenClearingProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), aircraft, null, Random.Shared, true);

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("without an altitude", aircraft.PendingWarnings[0]);
    }

    [Fact]
    public void Fh_WithAltitude_InCompound_NoWarning()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;

        // FH 070, DM 050 — parallel block with heading + altitude
        var compound = new CompoundCommand([new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(70)), new DescendMaintainCommand(5000)])]);

        CommandDispatcher.DispatchCompound(compound, aircraft, null, Random.Shared, true);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void TurnLeft_WithoutAltitude_WarnsWhenClearingProcedure()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveStarId = "BDEGA3";
        aircraft.StarViaMode = true;

        CommandDispatcher.Dispatch(new TurnLeftCommand(new MagneticHeading(270)), aircraft, null, Random.Shared, true);

        Assert.Single(aircraft.PendingWarnings);
        Assert.Contains("without an altitude", aircraft.PendingWarnings[0]);
    }

    [Fact]
    public void Fh_NoProcedureActive_NoWarning()
    {
        var aircraft = CreateAircraft();
        // No active procedure

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), aircraft, null, Random.Shared, true);

        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void Cm_ClearingViaMode_NoWarning()
    {
        var aircraft = CreateAircraft();
        aircraft.ActiveSidId = "PORTE3";
        aircraft.SidViaMode = true;

        // CM only disables via mode, doesn't clear the procedure
        CommandDispatcher.Dispatch(new ClimbMaintainCommand(35000), aircraft, null, Random.Shared, true);

        Assert.Empty(aircraft.PendingWarnings);
        // SidId still active — only via mode was disabled
        Assert.Equal("PORTE3", aircraft.ActiveSidId);
    }
}
