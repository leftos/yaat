using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// YAAT-2043: a VFR aircraft flying closed traffic is classified <see cref="AircraftSituation.Final"/> from the turn to final
/// (<c>SituationClassifier</c> maps <c>FinalApproachPhase</c> to it on every circuit), and on a pattern final the server sets
/// <see cref="SituationFlags.InsideFinalApproachFix"/> from the first second and sends no exits ahead. The aircraft's state is
/// the one measured on the sim (C172, KOAK 28R, CTO MRT then TG): on final the option clearances must still be offered, as
/// the sim accepts <c>COPT</c> and <c>TG</c> there.
/// </summary>
public class QuickCommandFinalOptionTests
{
    private static MenuContext Context() =>
        new(
            new MenuClick("N152SP", null, null, []),
            new MenuSession(
                "XX",
                false,
                VfrCommandsForIfr.None,
                QuickCommandDefaults.For,
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true
            )
        );

    /// <summary>The second circuit's final as the sim leaves it: the touch-and-go terminator rebuilt, no clearance on it.</summary>
    private static AircraftModel OnPatternFinal(string flightRules, string landingClearance) =>
        new()
        {
            Callsign = "N152SP",
            AircraftType = "C172",
            CurrentPhase = "FinalApproach",
            PhaseSequence = "FinalApproach > TouchAndGo",
            IsOnGround = false,
            FlightRules = flightRules,
            AssignedRunway = "28R",
            LandingClearance = landingClearance,
            Situation = AircraftSituation.Final,
            SituationFlags = SituationFlags.InsideFinalApproachFix,
            Altitude = 320,
        };

    private static List<string> Shown(AircraftModel ac)
    {
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(ac, Context(), _ => true);
        return [.. resolution.Strip.Select(item => item.Entry.Id), .. resolution.Text.Select(entry => entry.Id)];
    }

    [Theory]
    [InlineData("")]
    [InlineData("ClearedTouchAndGo")]
    public void VfrPatternAircraftOnFinal_QuickList_OffersTheOptionClearances(string landingClearance)
    {
        List<string> shown = Shown(OnPatternFinal("VFR", landingClearance));

        Assert.Contains(MenuIds.TowerClearedOption, shown);
        Assert.Contains(MenuIds.TowerTouchAndGo, shown);
        Assert.Contains(MenuIds.TowerStopAndGo, shown);
        Assert.Contains(MenuIds.TowerLowApproach, shown);
        Assert.Contains(MenuIds.TowerGoAround, shown);
    }

    /// <summary>A VFR aircraft on the downwind leg is in the Pattern situation and carries the option clearances there too.</summary>
    [Fact]
    public void VfrPatternAircraftOnDownwind_QuickList_OffersStopAndGoAndLowApproach()
    {
        AircraftModel aircraft = OnPatternFinal("VFR", "");
        aircraft.CurrentPhase = "Downwind";
        aircraft.PhaseSequence = "Downwind > Base > FinalApproach > TouchAndGo";
        aircraft.Situation = AircraftSituation.Pattern;
        aircraft.SituationFlags = SituationFlags.None;

        List<string> shown = Shown(aircraft);

        Assert.Contains(MenuIds.TowerStopAndGo, shown);
        Assert.Contains(MenuIds.TowerLowApproach, shown);
    }

    /// <summary>The option clearances stay VFR operations: an IFR aircraft on final is not offered them under the default setting.</summary>
    [Fact]
    public void IfrAircraftOnFinal_QuickList_LeavesOutTheOptionClearances()
    {
        List<string> shown = Shown(OnPatternFinal("IFR", ""));

        Assert.DoesNotContain(MenuIds.TowerClearedOption, shown);
        Assert.DoesNotContain(MenuIds.TowerTouchAndGo, shown);
        Assert.DoesNotContain(MenuIds.TowerStopAndGo, shown);
        Assert.DoesNotContain(MenuIds.TowerLowApproach, shown);
        Assert.Contains(MenuIds.TowerClearedToLand, shown);
    }
}
