using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="AutoClearedToLandSync.ApplyToAircraft"/>, the helper that keeps every
/// <see cref="AircraftModel"/>'s <see cref="AircraftModel.IsAutoClearedToLand"/> flag in sync with
/// the live "Auto Cleared-to-Land" session setting. That flag drives the radar / tower-cab
/// "NoLndgClnc" datablock suppression. The Info-column status itself is computed server-side
/// (<see cref="Yaat.Sim.AircraftStatusDescriber"/>, which honours the setting), so this helper only
/// propagates the client-only datablock flag.
///
/// Repro context (S2-OAK-4 bundle, t=939 toggled on, t=1009 N80ZU spawned): when the user toggled
/// the in-session checkbox after aircraft were already loaded, the client never pushed the new value
/// to existing models, so their datablocks still flashed the red no-clearance warning.
/// </summary>
public class AutoClearedToLandSyncTests
{
    private static AircraftModel OnFinalApproach(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            CurrentPhase = "FinalApproach",
            LandingClearance = "",
            ActiveApproachId = "ILS28R",
        };

    [Fact]
    public void ApplyToAircraft_True_SetsFlagOnEveryModel()
    {
        var n80zu = OnFinalApproach("N80ZU");
        var n2bp = OnFinalApproach("N2BP");
        Assert.False(n80zu.IsAutoClearedToLand);
        Assert.False(n2bp.IsAutoClearedToLand);

        AutoClearedToLandSync.ApplyToAircraft([n80zu, n2bp], true);

        Assert.True(n80zu.IsAutoClearedToLand);
        Assert.True(n2bp.IsAutoClearedToLand);
    }

    [Fact]
    public void ApplyToAircraft_False_ClearsFlag()
    {
        var ac = OnFinalApproach("N80ZU");
        ac.IsAutoClearedToLand = true;

        AutoClearedToLandSync.ApplyToAircraft([ac], false);

        Assert.False(ac.IsAutoClearedToLand);
    }

    [Fact]
    public void ApplyToAircraft_EmptyCollection_DoesNotThrow()
    {
        AutoClearedToLandSync.ApplyToAircraft([], true);
    }

    [Fact]
    public void ApplyToAircraft_LeavesUnrelatedFieldsAlone()
    {
        var ac = new AircraftModel
        {
            Callsign = "N80ZU",
            AircraftType = "C172",
            CurrentPhase = "Downwind",
            AssignedRunway = "28R",
            LandingClearance = "",
        };

        AutoClearedToLandSync.ApplyToAircraft([ac], true);

        Assert.True(ac.IsAutoClearedToLand);
        Assert.Equal("Downwind", ac.CurrentPhase);
        Assert.Equal("28R", ac.AssignedRunway);
    }
}
