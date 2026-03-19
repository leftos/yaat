using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class VectoringLevelOffTests
{
    private static AircraftState CreateAircraft(double altitude = 15000)
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
            Targets = { TargetAltitude = 5000, AssignedAltitude = 5000 },
        };
    }

    private static AircraftState CreateStarAircraft()
    {
        var ac = CreateAircraft(altitude: 15000);
        ac.ActiveStarId = "BDEGA3";
        ac.StarViaMode = true;
        ac.Targets.TargetAltitude = 5000;
        ac.Targets.AssignedAltitude = 5000;
        // Build a procedure route: fixes A, B, C
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIXAA",
                Latitude = 37.5,
                Longitude = -122.5,
            }
        );
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIXBB",
                Latitude = 37.6,
                Longitude = -122.6,
                AltitudeRestriction = new Data.Vnas.CifpAltitudeRestriction(Data.Vnas.CifpAltitudeRestrictionType.AtOrAbove, 8000),
            }
        );
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIXCC",
                Latitude = 37.7,
                Longitude = -122.7,
                AltitudeRestriction = new Data.Vnas.CifpAltitudeRestriction(Data.Vnas.CifpAltitudeRestrictionType.At, 5000),
            }
        );
        return ac;
    }

    private static AircraftState CreateSidAircraft()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.ActiveSidId = "PORTE3";
        ac.SidViaMode = true;
        ac.Targets.TargetAltitude = 10000;
        ac.Targets.AssignedAltitude = 10000;
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SIDAA",
                Latitude = 37.5,
                Longitude = -122.5,
            }
        );
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SIDBB",
                Latitude = 37.6,
                Longitude = -122.6,
            }
        );
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "SIDCC",
                Latitude = 37.7,
                Longitude = -122.7,
            }
        );
        return ac;
    }

    // =================================================================
    // Part 1: Level off when vectored off procedure
    // =================================================================

    [Fact]
    public void Fh_WithoutAltitude_LevelsOff()
    {
        var ac = CreateStarAircraft();

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);
        Assert.False(ac.IsExpediting);
        Assert.Single(ac.PendingWarnings);
        Assert.Contains("without an altitude", ac.PendingWarnings[0]);
    }

    [Fact]
    public void Fh_WithAltitude_InCompound_KeepsTarget()
    {
        var ac = CreateStarAircraft();

        var compound = new CompoundCommand([
            new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(270)), new DescendMaintainCommand(5000)]),
        ]);
        CommandDispatcher.DispatchCompound(compound, ac, null, Random.Shared, true);

        Assert.Equal(5000.0, ac.Targets.TargetAltitude);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Dct_OffProcedure_WithoutAltitude_LevelsOff()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("ZZZZZ", 38.0, -123.0) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.Null(ac.ActiveStarId);
        Assert.Null(ac.Targets.TargetAltitude);
        Assert.Single(ac.PendingWarnings);
    }

    [Fact]
    public void Dct_OffProcedure_WithAltitude_KeepsTarget()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("ZZZZZ", 38.0, -123.0) };

        var compound = new CompoundCommand([new ParsedBlock(null, [new DirectToCommand(fixes, []), new DescendMaintainCommand(5000)])]);
        CommandDispatcher.DispatchCompound(compound, ac, null, Random.Shared, false);

        Assert.Equal(5000.0, ac.Targets.TargetAltitude);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void PresentHeading_LevelsOff()
    {
        var ac = CreateStarAircraft();

        CommandDispatcher.Dispatch(new FlyPresentHeadingCommand(), ac, null, Random.Shared, true);

        Assert.Null(ac.Targets.TargetAltitude);
        Assert.Single(ac.PendingWarnings);
    }

    [Fact]
    public void NoProcedure_Fh_NoChange()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetAltitude = 10000;
        ac.Targets.AssignedAltitude = 10000;

        CommandDispatcher.Dispatch(new FlyHeadingCommand(new MagneticHeading(270)), ac, null, Random.Shared, true);

        // No procedure was active — altitude should not change
        Assert.Equal(10000.0, ac.Targets.TargetAltitude);
        Assert.Empty(ac.PendingWarnings);
    }

    // =================================================================
    // Part 2: Auto-preserve procedure on DCT to on-procedure fix
    // =================================================================

    [Fact]
    public void Dct_OnProcedureFix_PreservesStar()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.Equal("BDEGA3", ac.ActiveStarId);
    }

    [Fact]
    public void Dct_OnProcedureFix_DisablesViaMode()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.False(ac.StarViaMode);
    }

    [Fact]
    public void Dct_OnProcedureFix_TruncatesRoute()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        // Route should be [FIXBB, FIXCC] — FIXAA removed
        Assert.Equal(2, ac.Targets.NavigationRoute.Count);
        Assert.Equal("FIXBB", ac.Targets.NavigationRoute[0].Name);
        Assert.Equal("FIXCC", ac.Targets.NavigationRoute[1].Name);
        // Constraints preserved
        Assert.NotNull(ac.Targets.NavigationRoute[0].AltitudeRestriction);
        Assert.NotNull(ac.Targets.NavigationRoute[1].AltitudeRestriction);
    }

    [Fact]
    public void Dct_OnProcedureFix_NoAlt_LevelsOff()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        // Via-mode disabled without altitude/DVIA → level off
        Assert.Null(ac.Targets.TargetAltitude);
        Assert.Single(ac.PendingWarnings);
    }

    [Fact]
    public void Dct_OnProcedureFix_WithDvia_NoLevelOff()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        // DCT FIXBB, DVIA — parallel block
        var compound = new CompoundCommand([new ParsedBlock(null, [new DirectToCommand(fixes, []), new DescendViaCommand(null)])]);
        CommandDispatcher.DispatchCompound(compound, ac, null, Random.Shared, false);

        Assert.True(ac.StarViaMode);
        Assert.Equal("BDEGA3", ac.ActiveStarId);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Dct_OnProcedureFix_WithAltitude_NoLevelOff()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        var compound = new CompoundCommand([new ParsedBlock(null, [new DirectToCommand(fixes, []), new DescendMaintainCommand(8000)])]);
        CommandDispatcher.DispatchCompound(compound, ac, null, Random.Shared, false);

        Assert.Equal(8000.0, ac.Targets.TargetAltitude);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void Dct_OffProcedureFix_ClearsProcedure()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("ZZZZZ", 38.0, -123.0) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.Null(ac.ActiveStarId);
        Assert.False(ac.StarViaMode);
    }

    [Fact]
    public void ForceDct_OnProcedureFix_PreservesStar()
    {
        var ac = CreateStarAircraft();
        var fixes = new List<ResolvedFix> { new("FIXBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new ForceDirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.Equal("BDEGA3", ac.ActiveStarId);
        Assert.False(ac.StarViaMode);
        Assert.Equal(2, ac.Targets.NavigationRoute.Count);
        Assert.Equal("FIXBB", ac.Targets.NavigationRoute[0].Name);
    }

    [Fact]
    public void Dct_OnSidFix_PreservesSid()
    {
        var ac = CreateSidAircraft();
        var fixes = new List<ResolvedFix> { new("SIDBB", 37.6, -122.6) };

        CommandDispatcher.Dispatch(new DirectToCommand(fixes, []), ac, null, Random.Shared, false);

        Assert.Equal("PORTE3", ac.ActiveSidId);
        Assert.False(ac.SidViaMode);
        Assert.Equal(2, ac.Targets.NavigationRoute.Count);
        Assert.Equal("SIDBB", ac.Targets.NavigationRoute[0].Name);
        Assert.Equal("SIDCC", ac.Targets.NavigationRoute[1].Name);
    }
}
