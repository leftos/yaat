using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests;

/// <summary>
/// The AP/1B military route clearances (FAA JO 7110.65 §9-2-6): CMTR, MTRA, XMTR and SAYEXIT,
/// plus the altitude-block, speed-waiver and squawk behaviour they drive.
/// </summary>
[Collection("NavDbMutator")]
public sealed class MilitaryRouteCommandTests
{
    public MilitaryRouteCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>An aircraft positioned just before IR-149's entry point A, heading down the route.</summary>
    private static AircraftState AircraftOnIr149()
    {
        var route = NavigationDatabase.Instance.GetMilitaryRoute("IR149")!;
        var entry = route.Points[0].Position;
        var next = route.Points[1].Position;
        return new AircraftState
        {
            Callsign = "TREND21",
            AircraftType = "F16",
            Position = new LatLon(entry.Lat - 0.2, entry.Lon),
            TrueHeading = new TrueHeading(GeoMath.BearingTo(entry, next)),
            Altitude = 8000,
            IndicatedAirspeed = 300,
        };
    }

    private static CommandResult Apply(AircraftState aircraft, string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.Dispatch(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
    }

    [Fact]
    public void Cmtr_ClearsTheAircraftIntoTheRouteAndLoadsIt()
    {
        var aircraft = AircraftOnIr149();

        var result = Apply(aircraft, "CMTR IR149");

        Assert.True(result.Success, result.Message);
        Assert.Equal("IR149", aircraft.MilitaryRoute.Designator);
        Assert.Equal(MilitaryRouteStatus.ClearedIn, aircraft.MilitaryRoute.Status);
        Assert.Equal(MilitaryRouteAltitudeSource.RouteAltitudes, aircraft.MilitaryRoute.AltitudeSource);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains(aircraft.Phases!.Phases, p => p is MilitaryRoutePhase);
    }

    [Fact]
    public void Cmtr_UnknownRoute_IsRejected()
    {
        var result = Apply(AircraftOnIr149(), "CMTR IR999999");

        Assert.False(result.Success);
        Assert.Contains("Unknown military route", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cmtr_WithAltitude_AssignsItInsteadOfThePublishedBlock()
    {
        var aircraft = AircraftOnIr149();

        var result = Apply(aircraft, "CMTR IR149 50");

        Assert.True(result.Success, result.Message);
        Assert.Equal(MilitaryRouteAltitudeSource.AssignedAltitude, aircraft.MilitaryRoute.AltitudeSource);
        Assert.Equal(5000, aircraft.MilitaryRoute.AssignedOverrideFt);
        Assert.Equal(5000, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Cmtr_WithBPrefix_IsAnAtOrBelowRestriction()
    {
        var aircraft = AircraftOnIr149();

        var result = Apply(aircraft, "CMTR IR149 B50");

        Assert.True(result.Success, result.Message);
        Assert.Equal(MilitaryRouteAltitudeSource.AtOrBelow, aircraft.MilitaryRoute.AltitudeSource);
        Assert.Equal(5000, aircraft.Targets.AltitudeCeiling);
        Assert.Contains("at or below", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mtra_RestoresThePublishedBlockAfterAnAssignedAltitude()
    {
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149 50");

        var result = Apply(aircraft, "MTRA");

        Assert.True(result.Success, result.Message);
        Assert.Equal(MilitaryRouteAltitudeSource.RouteAltitudes, aircraft.MilitaryRoute.AltitudeSource);
        Assert.Null(aircraft.MilitaryRoute.AssignedOverrideFt);
        Assert.Null(aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Mtra_WhenNotOnARoute_IsRejected()
    {
        var result = Apply(AircraftOnIr149(), "MTRA");

        Assert.False(result.Success);
    }

    [Fact]
    public void Xmtr_EndsTheClearanceAndClearsTheBlock()
    {
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        aircraft.Targets.AltitudeFloor = 500;
        aircraft.Targets.AltitudeCeiling = 6000;

        var result = Apply(aircraft, "XMTR KLRD");

        Assert.True(result.Success, result.Message);
        Assert.Equal(MilitaryRouteStatus.Exited, aircraft.MilitaryRoute.Status);
        Assert.Null(aircraft.Targets.AltitudeFloor);
        Assert.Null(aircraft.Targets.AltitudeCeiling);
        Assert.Contains("KLRD", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xmtr_WithViaRoute_LoadsTheRouteOfFlight()
    {
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");

        var result = Apply(aircraft, "XMTR KLRD VIA LRD");

        Assert.True(result.Success, result.Message);
        Assert.Contains("via", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Xmtr_WhenNotOnARoute_IsRejected()
    {
        Assert.False(Apply(AircraftOnIr149(), "XMTR KLRD").Success);
    }

    [Fact]
    public void Cmtr_ParserRejectsAnInvalidAltitude()
    {
        var parsed = CommandParser.Parse("CMTR IR149 banana");

        Assert.False(parsed.IsSuccess);
        Assert.Contains("altitude", parsed.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MtraAndSayExit_RejectStrayArguments()
    {
        Assert.False(CommandParser.Parse("MTRA 50").IsSuccess);
        Assert.False(CommandParser.Parse("SAYEXIT now").IsSuccess);
    }

    [Fact]
    public void SpeedLimitWaiver_AppliesOnRouteButNotToSlowRoutes()
    {
        var aircraft = AircraftOnIr149();
        aircraft.MilitaryRoute.Designator = "IR149";
        aircraft.MilitaryRoute.Status = MilitaryRouteStatus.Established;

        aircraft.MilitaryRoute.Kind = MilitaryRouteType.Ir;
        Assert.True(aircraft.MilitaryRoute.SpeedLimitWaived);

        // AP/1B chapter 4 §V.C defines an SR as 250 KIAS or less, so the 91.117(a) exemption must
        // not extend to one.
        aircraft.MilitaryRoute.Kind = MilitaryRouteType.Sr;
        Assert.False(aircraft.MilitaryRoute.SpeedLimitWaived);

        // Merely cleared in is not established; the exemption applies within the route's confines.
        aircraft.MilitaryRoute.Kind = MilitaryRouteType.Ir;
        aircraft.MilitaryRoute.Status = MilitaryRouteStatus.ClearedIn;
        Assert.False(aircraft.MilitaryRoute.SpeedLimitWaived);
    }

    [Fact]
    public void SpellMilitaryRoute_UsesTheGroupFormFromParagraph2_5_1()
    {
        // The two examples FAA JO 7110.65 §2-5-1.f gives verbatim.
        Assert.Equal("i-r five thirty one", PhraseologyVerbalizer.SpellMilitaryRoute("IR531"));
        Assert.Equal("v-r fifty two", PhraseologyVerbalizer.SpellMilitaryRoute("VR52"));
    }

    [Fact]
    public void SpellMilitaryRoute_HandlesHyphensAndLetterSuffixes()
    {
        Assert.Equal("i-r one forty nine", PhraseologyVerbalizer.SpellMilitaryRoute("IR-149"));
        Assert.Equal("i-r eight alpha", PhraseologyVerbalizer.SpellMilitaryRoute("IR008A"));
        Assert.Equal("s-r nine hundred", PhraseologyVerbalizer.SpellMilitaryRoute("SR900"));
    }

    [Fact]
    public void BuildExitFixEstimate_ReportsTheExitPointOrDeclines()
    {
        var aircraft = AircraftOnIr149();
        Assert.Contains("not on a military", PilotSayBuilder.BuildExitFixEstimate(aircraft), StringComparison.OrdinalIgnoreCase);

        Apply(aircraft, "CMTR IR149");
        aircraft.MilitaryRoute.ExitPointId = "I";
        var spoken = PilotSayBuilder.BuildExitFixEstimate(aircraft);

        Assert.Contains("IR149", spoken, StringComparison.Ordinal);
        Assert.Contains("I", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Describer_RoundTripsEveryMilitaryRouteCommand()
    {
        Assert.Equal("CMTR IR149", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", null, false)));
        Assert.Equal("CMTR IR149 5000", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", 5000, false)));
        Assert.Equal("CMTR IR149 B5000", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", 5000, true)));
        Assert.Equal("MTRA", CommandDescriber.DescribeCommand(new MaintainMilitaryRouteAltitudesCommand()));
        Assert.Equal("XMTR KTCM VIA V495 SEA", CommandDescriber.DescribeCommand(new ClearedOutOfMilitaryRouteCommand("KTCM", "V495 SEA")));
        Assert.Equal("SAYEXIT", CommandDescriber.DescribeCommand(new SayExitFixEstimateCommand()));
    }
}
