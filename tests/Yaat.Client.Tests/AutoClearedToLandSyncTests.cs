using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="AutoClearedToLandSync.ApplyToAircraft"/>, the helper that
/// keeps every <see cref="AircraftModel"/> in sync with the live "Auto Cleared-to-Land"
/// session setting.
///
/// Repro context (S2-OAK-4 bundle, t=939 toggled on, t=1009 N80ZU spawned): when the user
/// toggled the in-session checkbox after aircraft were already loaded, the client never
/// pushed the new value to existing models, so any aircraft reaching FinalApproach
/// rendered a red "No landing clnc" alert (<see cref="AircraftModel.CheckAlerts"/>) even
/// though the server was correctly auto-clearing them.
/// </summary>
public class AutoClearedToLandSyncTests
{
    private static AircraftModel FinalApproachWithoutClearance(string callsign)
    {
        var ac = new AircraftModel { Callsign = callsign, AircraftType = "C172" };
        ac.CurrentPhase = "FinalApproach";
        ac.LandingClearance = "";
        ac.ActiveApproachId = "ILS28R";
        ac.ComputeSmartStatus();
        return ac;
    }

    [Fact]
    public void ApplyToAircraft_True_ClearsRedAlertOnFinalApproach()
    {
        var n80zu = FinalApproachWithoutClearance("N80ZU");
        var n2bp = FinalApproachWithoutClearance("N2BP");
        Assert.Equal("No landing clnc", n80zu.SmartStatus);
        Assert.Equal("No landing clnc", n2bp.SmartStatus);

        AutoClearedToLandSync.ApplyToAircraft([n80zu, n2bp], true);

        Assert.True(n80zu.IsAutoClearedToLand);
        Assert.True(n2bp.IsAutoClearedToLand);
        Assert.Equal(SmartStatusSeverity.Normal, n80zu.SmartStatusSeverity);
        Assert.Equal(SmartStatusSeverity.Normal, n2bp.SmartStatusSeverity);
        Assert.NotEqual("No landing clnc", n80zu.SmartStatus);
        Assert.NotEqual("No landing clnc", n2bp.SmartStatus);
    }

    [Fact]
    public void ApplyToAircraft_False_RestoresRedAlertOnFinalApproach()
    {
        var ac = FinalApproachWithoutClearance("N80ZU");
        ac.IsAutoClearedToLand = true;
        ac.ComputeSmartStatus();
        Assert.Equal(SmartStatusSeverity.Normal, ac.SmartStatusSeverity);

        AutoClearedToLandSync.ApplyToAircraft([ac], false);

        Assert.False(ac.IsAutoClearedToLand);
        Assert.Equal("No landing clnc", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Critical, ac.SmartStatusSeverity);
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
        ac.ComputeSmartStatus();
        var phaseBefore = ac.CurrentPhase;
        var rwyBefore = ac.AssignedRunway;

        AutoClearedToLandSync.ApplyToAircraft([ac], true);

        Assert.True(ac.IsAutoClearedToLand);
        Assert.Equal(phaseBefore, ac.CurrentPhase);
        Assert.Equal(rwyBefore, ac.AssignedRunway);
    }
}
