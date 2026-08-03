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

    /// <summary>
    /// Runs the installed phase so OnStart/OnTick arm the segment block, marking it Active the way
    /// the real tick loop does — PhaseList.Clear only calls OnEnd on an Active phase.
    /// </summary>
    private static void TickPhase(AircraftState aircraft)
    {
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        foreach (var phase in aircraft.Phases!.Phases)
        {
            phase.Status = PhaseStatus.Active;
            phase.OnTick(ctx);
        }
    }

    /// <summary>
    /// Sequences past the route's first point so the armed block is a real one. IR-149 publishes
    /// "As assigned to" for the segment into point A — the entry altitude is ATC's, not the route's
    /// — so the first segment legitimately arms no block.
    /// </summary>
    private static void SequenceToSecondPoint(AircraftState aircraft)
    {
        aircraft.Targets.NavigationRoute.RemoveAt(0);
        TickPhase(aircraft);
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
    public void EstablishedOnARoute_CommandsTheTrainingSpeedForItsCategory()
    {
        // >250 kt is the defining characteristic of the MTR program (P/CG "Military Training
        // Routes", AIM 3-5-2.c). The 91.117(a) waiver lifts the cap but supplies no speed, so
        // without a commanded one the aircraft transits at whatever it arrived with.
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        Assert.Null(aircraft.Targets.TargetSpeed);

        SequenceToSecondPoint(aircraft);

        Assert.Equal(MilitaryRouteStatus.Established, aircraft.MilitaryRoute.Status);
        Assert.Equal(AircraftPerformance.MilitaryRouteSpeedKts(AircraftCategory.Jet), aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void RouteSpeed_IsOverriddenByAnExplicitAssignment()
    {
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        SequenceToSecondPoint(aircraft);

        Apply(aircraft, "SPD 300");
        TickPhase(aircraft);

        Assert.Equal(300, aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void JoinIndex_DefaultsToThePublishedEntryPoint()
    {
        // §9-2-6 hangs its structure on the published entry fix, so an aircraft positioned before
        // the route should join there rather than at whichever point happens to be nearest.
        var route = NavigationDatabase.Instance.GetMilitaryRoute("IR149")!;
        var aircraft = AircraftOnIr149();

        Apply(aircraft, "CMTR IR149");

        Assert.Equal(route.EntryPoints[0], aircraft.MilitaryRoute.EntryPointId);
    }

    [Fact]
    public void DirectTo_APointAlreadyPassed_IsRejected()
    {
        // AP/1B routes are one-way and course reversals are prohibited (chapter 1 §V.B.1). DCT
        // clears the phase before it can object, so the guard has to sit in the DCT path itself.
        var aircraft = AircraftOnIr149();
        var route = NavigationDatabase.Instance.GetMilitaryRoute("IR149")!;
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        SequenceToSecondPoint(aircraft);
        SequenceToSecondPoint(aircraft);

        var result = Apply(aircraft, $"DCTF {route.Points[0].Name}");

        Assert.False(result.Success);
        Assert.Contains("one-way", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectTo_APointStillAhead_IsAllowed()
    {
        var aircraft = AircraftOnIr149();
        var route = NavigationDatabase.Instance.GetMilitaryRoute("IR149")!;
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        SequenceToSecondPoint(aircraft);

        var result = Apply(aircraft, $"DCTF {route.Points[^1].Name}");

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Cmtr_AltitudeOutsideThePublishedBlock_WarnsTheInstructor()
    {
        // IR-149's segments are published as an AGL floor under a 3,000 ft MSL ceiling, so 10,000
        // is outside the block the aircraft is being cleared into.
        var aircraft = AircraftOnIr149();

        var result = Apply(aircraft, "CMTR IR149 100");

        Assert.True(result.Success, result.Message);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("above", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cmtr_AltitudeInsideThePublishedBlock_RaisesNoWarning()
    {
        var aircraft = AircraftOnIr149();

        Apply(aircraft, "CMTR IR149 25");

        Assert.DoesNotContain(aircraft.PendingWarnings, w => w.Contains("published", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cmtr_SecondAircraftIntoAnOccupiedRoute_WarnsTheInstructor()
    {
        // §9-2-6.a leaves separation on the controller for aircraft sharing a route.
        var first = AircraftOnIr149();
        first.Callsign = "TREND21";
        Apply(first, "CMTR IR149");

        var second = AircraftOnIr149();
        second.Callsign = "TREND22";
        var parsed = CommandParser.Parse("CMTR IR149");
        var ctx = TestDispatch.Context(Random.Shared) with { ListAircraft = () => [first, second] };
        var result = CommandDispatcher.Dispatch(parsed.Value!, second, ctx);

        Assert.True(result.Success, result.Message);
        Assert.Contains(second.PendingWarnings, w => w.Contains("TREND21", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RouteOverlay_ProjectsThePublishedProtectedCorridor()
    {
        // §9-2-6.d makes the corridor the controller's continuing responsibility, and AP/1B
        // publishes it asymmetrically about the centerline, so it cannot be drawn as a buffer.
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);

        var shapes = NavRouteOverlayProjector.BuildShapes(aircraft);

        var corridor = Assert.Single(shapes);
        Assert.Equal(NavRouteShapeKind.MilitaryRouteCorridor, corridor.Kind);

        // A closed polygon: both edges plus the point that closes it back onto the first.
        int centerlineCount = aircraft.Targets.NavigationRoute.Count;
        Assert.Equal((centerlineCount * 2) + 1, corridor.Points.Count);
        Assert.Equal(corridor.Points[0], corridor.Points[^1]);

        // Every vertex sits off the centerline by roughly the published half-width.
        var width = NavigationDatabase.Instance.GetMilitaryRoute("IR149")!.WidthAt("B")!;
        double offset = GeoMath.DistanceNm(aircraft.Targets.NavigationRoute[0].Position, new LatLon(corridor.Points[0][0], corridor.Points[0][1]));
        Assert.InRange(offset, Math.Min(width.LeftNm, width.RightNm) * 0.5, Math.Max(width.LeftNm, width.RightNm) * 1.5);
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
    public void AtOrBelow_StillArmsThePublishedFloor()
    {
        // §9-2-6.a offers "MAINTAIN AT OR BELOW (altitude)" as an alternative ceiling, not as a
        // release from the route's published profile — the floors are the segment's minimum IFR
        // altitudes. Regression: ReArmBlock once gated on RouteAltitudes alone, which made the
        // at-or-below branch unreachable and let the aircraft descend below the floor unopposed.
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149 B120");
        TickPhase(aircraft);
        SequenceToSecondPoint(aircraft);

        Assert.Equal(MilitaryRouteAltitudeSource.AtOrBelow, aircraft.MilitaryRoute.AltitudeSource);
        Assert.NotNull(aircraft.Targets.AltitudeFloor);
        Assert.NotNull(aircraft.Targets.AltitudeCeiling);
        Assert.True(aircraft.Targets.AltitudeCeiling <= 12000, $"ceiling was {aircraft.Targets.AltitudeCeiling}");
    }

    [Fact]
    public void Xmtr_RestoresTheBeaconCodeByEndingThePhase()
    {
        // Regression: XMTR used to null aircraft.Phases outright, which skips OnEnd — so a VR
        // aircraft kept squawking 4000 forever and PreRouteSquawk was lost on the next clearance.
        var aircraft = AircraftOnIr149();
        aircraft.Transponder.Code = 1234;
        Apply(aircraft, "CMTR VR1257");
        TickPhase(aircraft);
        Assert.Equal(4000u, aircraft.Transponder.Code);

        Apply(aircraft, "XMTR KLRD");

        Assert.Equal(1234u, aircraft.Transponder.Code);
        Assert.Null(aircraft.MilitaryRoute.PreRouteSquawk);
    }

    [Fact]
    public void ClearedIntoAVfrRoute_DoesNotUseClearancePhraseology()
    {
        // §9-2-6 is IFR-only; ATC issues no clearance into a VR. The aircraft can still be placed
        // on one as traffic, but the readback must not say "cleared into".
        var aircraft = AircraftOnIr149();

        var result = Apply(aircraft, "CMTR VR1257");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("cleared into", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("no clearance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EstablishedAircraft_FliesInsideTheBlockRatherThanParkingOnABound()
    {
        // AIM 3-5-2: MTRs exist for low level tactical training, and on a scope the traffic is
        // recognisable by Mode C working through the block. Holding on whichever bound the aircraft
        // happened to reach is the opposite of that.
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        SequenceToSecondPoint(aircraft);

        var floor = aircraft.Targets.AltitudeFloor;
        var ceiling = aircraft.Targets.AltitudeCeiling;
        Assert.NotNull(floor);
        Assert.NotNull(ceiling);
        Assert.NotNull(aircraft.Targets.TargetAltitude);
        Assert.InRange(aircraft.Targets.TargetAltitude!.Value, floor!.Value, ceiling!.Value);
    }

    [Fact]
    public void MarsaRoute_AcceptsAnAmendmentAndVoidsMarsa()
    {
        // §9-2-13.e: "Altitude or course changes issued will automatically void MARSA." The
        // amendment is accepted rather than refused; MARSA drops and the instructor is told.
        var aircraft = AircraftOnIr149();
        Apply(aircraft, "CMTR IR149");
        TickPhase(aircraft);
        aircraft.MilitaryRoute.Marsa = true;

        var phase = aircraft.Phases!.Phases.OfType<MilitaryRoutePhase>().Single();
        Assert.Equal(CommandAcceptanceStatus.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain).Status);

        phase.OnCommandAccepted(CanonicalCommandType.ClimbMaintain, CommandDispatcher.BuildMinimalContext(aircraft));

        Assert.False(aircraft.MilitaryRoute.Marsa);
        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("MARSA voided", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Describer_RoundTripsEveryMilitaryRouteCommand()
    {
        Assert.Equal("CMTR IR149", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", null, false)));
        Assert.Equal("CMTR IR149 5000", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", 5000, false)));
        Assert.Equal("CMTR IR149 B5000", CommandDescriber.DescribeCommand(new ClearedIntoMilitaryRouteCommand("IR149", 5000, true)));
        Assert.Equal("MTRA", CommandDescriber.DescribeCommand(new MaintainMilitaryRouteAltitudesCommand()));
        Assert.Equal("XMTR KTCM VIA V495 SEA", CommandDescriber.DescribeCommand(new ClearedOutOfMilitaryRouteCommand("KTCM", "V495 SEA", null)));
        Assert.Equal("XMTR KTCM 24000", CommandDescriber.DescribeCommand(new ClearedOutOfMilitaryRouteCommand("KTCM", null, 24000)));
        Assert.Equal(
            "XMTR KTCM 24000 VIA V495 SEA",
            CommandDescriber.DescribeCommand(new ClearedOutOfMilitaryRouteCommand("KTCM", "V495 SEA", 24000))
        );
        Assert.Equal("SAYEXIT", CommandDescriber.DescribeCommand(new SayExitFixEstimateCommand()));
    }
}
