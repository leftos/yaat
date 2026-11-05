using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using OakesHarness = Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness;

namespace Yaat.Sim.Tests;

/// <summary>
/// A constrained direct-to a fix on the active STAR keeps the STAR from that fix (7110.65 4-5-7.h; AIM 5-4-1.a.2):
/// "descend via, proceed direct GRUDG, cross GRUDG at or above FL220" in one transmission, either order, leaves the
/// STAR active, the via in force and the STAR's fixes past GRUDG on the route, with the crossing on GRUDG. The crossing
/// replaces only what the controller sets; the rest of the arrival is flown as published (7110.65 4-5-7.h.9 NOTE;
/// 5-7-2.e NOTE 1; AIM 5-4-1.a.2(a) NOTE 1). In its own transmission the direct ends the via and keeps the STAR, as a
/// plain direct onto the STAR does; to fixes off the route, out of order or not consecutive on it, it replaces the
/// route. Real KOAK navdata and a generated heavy jet on the real OAKES3 RW12 transition.
/// </summary>
public class ConstrainedDctfKeepsStarTests
{
    private const string Grudg = "GRUDG";
    private const string Lohgn = "LOHGN";
    private const string Bannd = "BANND";
    private const int BanndCrossingKts = 230;
    private const int CrossingFloorFt = 22000;
    private const int CrossingSpeedKts = 250;
    private const int LohgnFloorFt = 11000;
    private const int GrudgPublishedKts = 260;
    private const int InsideOakesWindowFt = 26000;
    private const int BelowMachHandoverFt = 16000;
    private const double CrossingToleranceKts = 10.0;
    private const double SnapKts = 2.0;

    private readonly ITestOutputHelper _output;

    public ConstrainedDctfKeepsStarTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void DviaThenConstrainedDctfOnTheStar_KeepsTheStarAndTheVia()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        PublishedState before = ReadPublished();

        DispatchOk(aircraft, new DescendViaCommand(null), GrudgCrossing(aircraft, CrossingFloorFt, speedKts: null));

        AssertStarKeptWithTheCrossing(aircraft, before);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    [Fact]
    public void ConstrainedDctfThenDviaOnTheStar_KeepsTheStarAndTheCrossing()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        PublishedState before = ReadPublished();

        DispatchOk(aircraft, GrudgCrossing(aircraft, CrossingFloorFt, speedKts: null), new DescendViaCommand(null));

        AssertStarKeptWithTheCrossing(aircraft, before);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    /// <summary>
    /// After a descend via, a constrained direct in its own transmission ends the via and keeps the STAR; past GRUDG the
    /// aircraft keeps the crossing altitude assigned (AIM 4-4-10.e, 4-4-10.g), not the ended via's target.
    /// </summary>
    [Fact]
    public void ConstrainedDctfOnTheStar_OwnTransmission_EndsTheViaKeepsTheStar()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        PublishedState before = ReadPublished();
        SendOk(harness, "DVIA");
        Assert.True(aircraft.Procedure.StarViaMode);

        DispatchOk(aircraft, GrudgCrossing(aircraft, CrossingFloorFt, speedKts: null));

        AssertStarKeptWithTheCrossing(aircraft, before);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Procedure.StarViaTransmissionId);

        TickUntilPassed(harness, Grudg);
        _output.WriteLine($"Past GRUDG: assigned {aircraft.Targets.AssignedAltitude}, target {aircraft.Targets.TargetAltitude}");
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Equal(CrossingFloorFt, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A climb via of the same transmission is no descend via: the constrained direct onto the STAR ends it, and the
    /// crossing takes its reverts and assigned altitude as any direct that ends a via does. An arrival has no SID to
    /// climb via, so the state a same-transmission CVIA leaves is set directly.
    /// </summary>
    [Fact]
    public void CviaThenConstrainedDctfOnTheStar_EndsTheViaAndCapturesTheReverts()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 280");
        int transmissionId = aircraft.Queue.LastTransmissionId + 1;
        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.ClimbViaTargetFt = InsideOakesWindowFt;
        aircraft.Procedure.ClimbViaTransmissionId = transmissionId;
        aircraft.Targets.AssignedAltitude = null;

        DispatchOk(aircraft, GrudgCrossing(aircraft, CrossingFloorFt, CrossingSpeedKts));

        Assert.Equal(transmissionId, aircraft.Queue.LastTransmissionId);
        Assert.Equal("OAKES3", aircraft.Procedure.ActiveStarId);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.False(aircraft.Procedure.SidViaMode);
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        // Ending a via with no assigned altitude, the crossing altitude stays assigned past the fix (AIM 4-4-10.e, 4-4-10.g).
        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Null(grudg.RevertAltitude);
        Assert.Equal(CrossingFloorFt, aircraft.Targets.AssignedAltitude);
        Assert.Equal(280, grudg.RevertAssignedSpeed);
    }

    /// <summary>A speed-only constrained direct onto GRUDG keeps GRUDG's published altitude window.</summary>
    [Fact]
    public void DviaThenSpeedOnlyConstrainedDctfOnTheStar_KeepsThePublishedAltitudes()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        PublishedState before = ReadPublished();

        DispatchOk(aircraft, new DescendViaCommand(null), GrudgCrossing(aircraft, altitudeFt: null, CrossingSpeedKts));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Equal(before.GrudgAltitude, grudg.AltitudeRestriction);
        Assert.Equal(new CifpSpeedRestriction(CrossingSpeedKts, CifpSpeedRestrictionType.AtOrBelow), grudg.SpeedRestriction);
        Assert.Equal(aircraft.Queue.LastTransmissionId, grudg.SpeedTransmissionId);
        AssertLaterPublishedAltitudesKept(aircraft, before);
    }

    [Fact]
    public void ConstrainedDctfOffTheStar_ReplacesTheRoute()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name.Equals("SUNOL", StringComparison.OrdinalIgnoreCase));
        (double lat, double lon) = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition("SUNOL"));
        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix("SUNOL", lat, lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(CrossingFloorFt, CrossFixAltitudeType.AtOrAbove) },
            null,
            null
        );

        DispatchOk(aircraft, cmd);

        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.Equal(["SUNOL"], aircraft.Targets.NavigationRoute.Select(f => f.Name));
    }

    /// <summary>
    /// The STAR is kept only for fixes consecutive on the route, in order, names matched case-insensitively: fixes that
    /// skip STAR fixes between them, or come out of order, replace the route.
    /// </summary>
    [Theory]
    [InlineData(new[] { "GRUDG", "LOHGN" }, false)]
    [InlineData(new[] { "SULRR", "GRUDG" }, false)]
    [InlineData(new[] { "grudg" }, true)]
    public void ConstrainedDctfFixList_KeepsTheStarOnlyForConsecutiveFixesInOrder(string[] fixNames, bool keepsStar)
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        string? starId = aircraft.Procedure.ActiveStarId;
        List<string> tail = TailAfter(aircraft, Grudg);
        List<ResolvedFix> fixes = [.. fixNames.Select(name => ResolvedOnRoute(aircraft, name))];
        var cmd = new ConstrainedForceDirectToCommand(
            fixes,
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(CrossingFloorFt, CrossFixAltitudeType.AtOrAbove) },
            null,
            null
        );

        DispatchOk(aircraft, new DescendViaCommand(null), cmd);

        if (keepsStar)
        {
            Assert.Equal(starId, aircraft.Procedure.ActiveStarId);
            Assert.Equal(tail, TailAfter(aircraft, Grudg));
        }
        else
        {
            Assert.Null(aircraft.Procedure.ActiveStarId);
            Assert.Equal(fixNames, aircraft.Targets.NavigationRoute.Select(f => f.Name));
        }
    }

    /// <summary>A constrained direct to LOHGN with GRUDG next drops the fixes before LOHGN and keeps the STAR past it.</summary>
    [Fact]
    public void DviaThenConstrainedDctfToALaterStarFix_DropsTheFixesBeforeIt()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        string? starId = aircraft.Procedure.ActiveStarId;
        List<string> tail = TailAfter(aircraft, Lohgn);

        DispatchOk(aircraft, new DescendViaCommand(null), Crossing(aircraft, Lohgn, LohgnFloorFt, speedKts: null));

        Assert.Equal(starId, aircraft.Procedure.ActiveStarId);
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Equal(Lohgn, aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal(tail, TailAfter(aircraft, Lohgn));
        NavigationTarget lohgn = RouteFix(aircraft, Lohgn);
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, LohgnFloorFt) { Source = CifpAltitudeRestrictionSource.Atc },
            lohgn.AltitudeRestriction
        );
        Assert.Equal(aircraft.Queue.LastTransmissionId, lohgn.CrossingTransmissionId);
    }

    /// <summary>A skipped fix's pending speed revert applies when the direct drops it, as other route changes apply it.</summary>
    [Fact]
    public void ConstrainedDctfPastACrossingWithARevert_AppliesTheRevert()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 300");
        SendOk(harness, "CFIX GRUDG 220 250");
        Assert.Equal(300, RouteFix(aircraft, Grudg).RevertAssignedSpeed);

        DispatchOk(aircraft, Crossing(aircraft, Lohgn, LohgnFloorFt, speedKts: null));

        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name.Equals(Grudg, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(300, aircraft.Targets.AssignedSpeed);
    }

    /// <summary>
    /// An RNS given with an earlier crossing on GRUDG is carried past the new crossing speed on GRUDG, so the new
    /// crossing's ceiling does not hold for good.
    /// </summary>
    [Fact]
    public void CfixWithResume_ThenConstrainedDctfWithSpeedOnTheSameFix_CarriesTheResume()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "CFIX GRUDG 220 250, RNS");
        Assert.Equal(SpeedResumeKind.Normal, RouteFix(aircraft, Grudg).RevertResume);

        DispatchOk(aircraft, GrudgCrossing(aircraft, CrossingFloorFt, 240));

        Assert.Equal("OAKES3", aircraft.Procedure.ActiveStarId);
        Assert.Equal(SpeedResumeKind.Normal, RouteFix(aircraft, Grudg).RevertResume);
    }

    /// <summary>
    /// A Mach nobody assigned, pending past an earlier crossing on GRUDG below the Mach handover altitude, is carried as a
    /// Mach revert past the new crossing speed on GRUDG, not as a plain speed.
    /// </summary>
    [Fact]
    public void PendingUnassignedMach_ThenConstrainedDctfWithSpeedOnTheSameFix_CarriesTheMach()
    {
        OakesHarness harness = BuildOakesRw12Harness(BelowMachHandoverFt);
        AircraftState aircraft = harness.Aircraft;
        aircraft.Targets.TargetMach = 0.62;
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        SendOk(harness, "CFIX GRUDG 160 250");
        Assert.Equal((double?)0.62, RouteFix(aircraft, Grudg).RevertMach);

        DispatchOk(aircraft, GrudgCrossing(aircraft, BelowMachHandoverFt, 240));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Equal((double?)0.62, grudg.RevertMach);
        Assert.Equal((bool?)false, grudg.RevertMachExplicit);
        Assert.Null(grudg.RevertSpeed);
    }

    /// <summary>
    /// After "SPD 300", "DVIA" with a constrained direct GRUDG at or above FL220 at 250: the via cancels the 300 (7110.65
    /// 5-7-1.d), GRUDG is crossed at the 250, and past it the STAR's published speeds apply again, so LOHGN is crossed at
    /// its published speed.
    /// </summary>
    [Fact]
    public void ConstrainedDctfOnTheStarWithSpeed_PlacesTheRevert()
    {
        SpeedFlight flight = FlyTheSpeedCrossing(dviaFirst: true);
        AssertFliesTheSpeedCrossing(flight);
    }

    /// <summary>The same as <see cref="ConstrainedDctfOnTheStarWithSpeed_PlacesTheRevert"/>, the direct given before the via.</summary>
    [Fact]
    public void ConstrainedDctfWithSpeedThenDvia_FliesAsDviaThenConstrainedDctf()
    {
        SpeedFlight dctfFirst = FlyTheSpeedCrossing(dviaFirst: false);
        SpeedFlight dviaFirst = FlyTheSpeedCrossing(dviaFirst: true);
        _output.WriteLine($"DCTF first {dctfFirst}; DVIA first {dviaFirst}");

        AssertFliesTheSpeedCrossing(dctfFirst);
        Assert.Equal(dviaFirst.CeilingPastGrudg, dctfFirst.CeilingPastGrudg);
        Assert.Equal(dviaFirst.AssignedPastGrudg, dctfFirst.AssignedPastGrudg);
        Assert.InRange(dctfFirst.LohgnIas, dviaFirst.LohgnIas - SnapKts, dviaFirst.LohgnIas + SnapKts);
    }

    /// <summary>
    /// An altitude-only direct onto GRUDG sets no speed, so the speed revert an earlier "CFIX GRUDG 220 250" left there
    /// stays: the 250 holds to GRUDG and the 300 comes back past it.
    /// </summary>
    [Fact]
    public void AltitudeOnlyDctfOntoAFixWithACrossingSpeed_KeepsItsSpeedRevert()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 300");
        SendOk(harness, "CFIX GRUDG 220 250");
        NavigationTarget cfixed = RouteFix(aircraft, Grudg);
        (double? revertSpeed, double? revertAssigned) = (cfixed.RevertSpeed, cfixed.RevertAssignedSpeed);
        Assert.Equal(300, revertAssigned);

        DispatchOk(aircraft, GrudgCrossing(aircraft, CrossingFloorFt, speedKts: null));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Equal(new CifpSpeedRestriction(CrossingSpeedKts, CifpSpeedRestrictionType.AtOrBelow), grudg.SpeedRestriction);
        Assert.Equal(revertSpeed, grudg.RevertSpeed);
        Assert.Equal(revertAssigned, grudg.RevertAssignedSpeed);
        Assert.Equal(CrossingSpeedKts, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, Grudg);
        Assert.Equal(300, aircraft.Targets.AssignedSpeed);
    }

    /// <summary>An altitude-only direct onto GRUDG leaves the Mach revert an earlier crossing speed there left pending.</summary>
    [Fact]
    public void AltitudeOnlyDctfOntoAFixWithAMachRevert_KeepsTheMachRevert()
    {
        OakesHarness harness = BuildOakesRw12Harness(BelowMachHandoverFt);
        AircraftState aircraft = harness.Aircraft;
        aircraft.Targets.TargetMach = 0.62;
        SendOk(harness, "CFIX GRUDG 160 250");
        Assert.Equal((double?)0.62, RouteFix(aircraft, Grudg).RevertMach);

        DispatchOk(aircraft, GrudgCrossing(aircraft, BelowMachHandoverFt, speedKts: null));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Equal((double?)0.62, grudg.RevertMach);
        Assert.Equal((bool?)false, grudg.RevertMachExplicit);
        // YAAT-1839: the CFIX speed leaves the Mach live for the speed planner to end, below FL240 on the next tick
        Assert.Equal((double?)0.62, aircraft.Targets.TargetMach);
    }

    /// <summary>
    /// YAAT-1839: "MACH .70, DVIA, DCTF GRUDG A220 250" in one block leaves the Mach live for the speed planner to end,
    /// as a CFIX restated with the via does, so the IAS keeps following the Mach down the descent rather than freezing at
    /// its figure at dispatch. On this harness the descent handover or the GRUDG window ends the Mach within the first
    /// 2,000 ft, so the IAS is read where the Mach ends: it has followed the Mach up from the dispatch figure.
    /// </summary>
    [Fact]
    public void MachThenDviaThenDctfWithSpeed_SameBlock_LeavesNoFrozenIas()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        ParsedCommand mach = Assert.IsAssignableFrom<ParsedCommand>(CommandParser.Parse("MACH .70").Value);
        double dispatchIas = aircraft.IndicatedAirspeed;
        Assert.True(WindInterpolator.MachToIas(0.70, aircraft.Altitude) > dispatchIas + 4, "premise: the Mach's IAS is above the dispatch IAS");

        DispatchOk(aircraft, mach, new DescendViaCommand(null), GrudgCrossing(aircraft, CrossingFloorFt, CrossingSpeedKts));

        Assert.Equal((double?)0.70, aircraft.Targets.TargetMach);
        double startFt = aircraft.Altitude;
        (double Ias, double MachIas) lastUnderMach = (aircraft.IndicatedAirspeed, WindInterpolator.MachToIas(0.70, aircraft.Altitude));
        for (int t = 0; (t < 1800) && (aircraft.Targets.TargetMach is not null); t++)
        {
            lastUnderMach = (aircraft.IndicatedAirspeed, WindInterpolator.MachToIas(0.70, aircraft.Altitude));
            Tick(harness, 1);
        }

        // On the last tick the Mach was held, the IAS was the Mach's own.
        _output.WriteLine($"Last tick under the Mach: {lastUnderMach.Ias:F1} KIAS, Mach IAS {lastUnderMach.MachIas:F1}");
        Assert.InRange(lastUnderMach.Ias, lastUnderMach.MachIas - SnapKts, lastUnderMach.MachIas + SnapKts);

        _output.WriteLine(
            $"Dispatched at {startFt:F0} ft, {dispatchIas:F0} KIAS; Mach ended at {aircraft.Altitude:F0} ft, {aircraft.IndicatedAirspeed:F0} KIAS"
        );
        Assert.Null(aircraft.Targets.TargetMach);
        Assert.True(
            aircraft.IndicatedAirspeed > dispatchIas + 4,
            $"the IAS stayed at {aircraft.IndicatedAirspeed:F0} KIAS from {dispatchIas:F0} at dispatch"
        );
    }

    /// <summary>
    /// YAAT-1839: a direct that keeps the STAR with a crossing speed on BANND, after a CFIX GRUDG speed under a Mach whose
    /// revert GRUDG still holds, leaves one Mach revert: the Mach still held is the one GRUDG's crossing captured.
    /// </summary>
    [Fact]
    public void StarKeptDctf_WithAMachRevertOnAKeptFix_KeepsOneMachRevert()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "MACH .78");
        SendOk(harness, "CFIX GRUDG 160 250");
        Assert.Equal((double?)0.78, RouteFix(aircraft, Grudg).RevertMach);
        Assert.Equal((double?)0.78, aircraft.Targets.TargetMach);
        var direct = new ConstrainedForceDirectToCommand(
            [ResolvedOnRoute(aircraft, Grudg), ResolvedOnRoute(aircraft, Bannd)],
            [],
            new Dictionary<int, int> { [1] = BanndCrossingKts },
            null
        );

        DispatchOk(aircraft, direct);

        Assert.Equal("OAKES3", aircraft.Procedure.ActiveStarId);
        NavigationTarget holder = Assert.Single(aircraft.Targets.NavigationRoute, fix => fix.RevertMach is not null);
        _output.WriteLine($"Mach revert on {holder.Name}");
        Assert.Equal((double?)0.78, holder.RevertMach);
    }

    /// <summary>
    /// A speed-only direct onto GRUDG sets no altitude, so the crossing altitude an earlier CFIX put there keeps its
    /// stamp and the altitude it reverts to.
    /// </summary>
    [Fact]
    public void SpeedOnlyDctfOntoAFixWithACrossingAltitude_KeepsItsAltitudeRevert()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        // A descent cleared below the crossing, so the revert past GRUDG continues the descent and is captured.
        SendOk(harness, "DM 200");
        SendOk(harness, "CFIX GRUDG 220");
        NavigationTarget cfixed = RouteFix(aircraft, Grudg);
        (CifpAltitudeRestriction?, int?, double?, double?) before = (
            cfixed.AltitudeRestriction,
            cfixed.CrossingTransmissionId,
            cfixed.RevertAltitude,
            cfixed.RevertAssignedAltitude
        );
        Assert.NotNull(before.Item3);

        DispatchOk(aircraft, GrudgCrossing(aircraft, altitudeFt: null, CrossingSpeedKts));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        Assert.Equal(before, (grudg.AltitudeRestriction, grudg.CrossingTransmissionId, grudg.RevertAltitude, grudg.RevertAssignedAltitude));
    }

    /// <summary>
    /// "DVIA, DCTF GRUDG A220 250" and "DCTF GRUDG A220 250, DVIA", each in one transmission, leave the same state right
    /// after dispatch: the crossing restated with the via, no reverts, and the crossing speed assigned.
    /// </summary>
    [Fact]
    public void DviaThenDctfWithSpeed_StateAtDispatchEqualsTheOtherOrder()
    {
        AircraftState dviaFirst = BuildOakesRw12Harness().Aircraft;
        DispatchOk(dviaFirst, new DescendViaCommand(null), GrudgCrossing(dviaFirst, CrossingFloorFt, CrossingSpeedKts));
        AircraftState dctfFirst = BuildOakesRw12Harness().Aircraft;
        DispatchOk(dctfFirst, GrudgCrossing(dctfFirst, CrossingFloorFt, CrossingSpeedKts), new DescendViaCommand(null));

        DispatchState viaFirstState = ReadDispatchState(dviaFirst);
        DispatchState directFirstState = ReadDispatchState(dctfFirst);
        _output.WriteLine($"DVIA first {viaFirstState}; DCTF first {directFirstState}");

        Assert.Equal(CrossingSpeedKts, viaFirstState.AssignedSpeed);
        Assert.Null(viaFirstState.RevertAltitude);
        Assert.Null(viaFirstState.RevertAssignedAltitude);
        Assert.True(viaFirstState.StarViaMode);
        Assert.Equal(viaFirstState, directFirstState);
    }

    private sealed record DispatchState(
        double? AssignedAltitude,
        double? AssignedSpeed,
        CifpAltitudeRestriction? AltitudeRestriction,
        CifpSpeedRestriction? SpeedRestriction,
        double? RevertAltitude,
        double? RevertAssignedAltitude,
        double? RevertSpeed,
        double? RevertAssignedSpeed,
        double? RevertMach,
        bool StarViaMode,
        int? CrossingTransmissionId,
        int? SpeedTransmissionId,
        double? TargetSpeed
    );

    private static DispatchState ReadDispatchState(AircraftState aircraft)
    {
        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        return new DispatchState(
            aircraft.Targets.AssignedAltitude,
            aircraft.Targets.AssignedSpeed,
            grudg.AltitudeRestriction,
            grudg.SpeedRestriction,
            grudg.RevertAltitude,
            grudg.RevertAssignedAltitude,
            grudg.RevertSpeed,
            grudg.RevertAssignedSpeed,
            grudg.RevertMach,
            aircraft.Procedure.StarViaMode,
            grudg.CrossingTransmissionId,
            grudg.SpeedTransmissionId,
            aircraft.Targets.TargetSpeed
        );
    }

    /// <summary>
    /// A direct GRUDG BANND (BANND is next after GRUDG) with a crossing speed on BANND alone moves the plain revert an
    /// earlier "CFIX GRUDG 220 250" left on GRUDG past the new crossing: BANND goes back to the 300, and GRUDG reverts
    /// nothing on the way.
    /// </summary>
    [Fact]
    public void DctfWithSpeedOnTheSecondFix_MovesAKeptFixsPendingRevertPastTheCrossing()
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 300");
        SendOk(harness, "CFIX GRUDG 220 250");
        Assert.Equal(300, RouteFix(aircraft, Grudg).RevertAssignedSpeed);
        var direct = new ConstrainedForceDirectToCommand(
            [ResolvedOnRoute(aircraft, Grudg), ResolvedOnRoute(aircraft, Bannd)],
            [],
            new Dictionary<int, int> { [1] = BanndCrossingKts },
            null
        );

        DispatchOk(aircraft, direct);

        Assert.Equal("OAKES3", aircraft.Procedure.ActiveStarId);
        Assert.False(NavigationCommandHandler.HasPlainRevert(RouteFix(aircraft, Grudg)));
        Assert.Equal(300, RouteFix(aircraft, Bannd).RevertAssignedSpeed);

        TickUntilPassed(harness, Grudg);
        Assert.Contains(aircraft.Targets.NavigationRoute, f => f.Name.Equals(Bannd, StringComparison.OrdinalIgnoreCase));
        // GRUDG reverts nothing, so the speed the CFIX assigned stands until BANND's crossing is flown.
        Assert.Equal(CrossingSpeedKts, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, Bannd);
        Assert.Equal(300, aircraft.Targets.AssignedSpeed);
    }

    private sealed record SpeedFlight(
        CifpSpeedRestriction? GrudgSpeed,
        int? GrudgSpeedTransmissionId,
        int TransmissionId,
        double GrudgIas,
        double? CeilingPastGrudg,
        double? AssignedPastGrudg,
        CifpSpeedRestriction LohgnPublished,
        double LohgnIas
    );

    private SpeedFlight FlyTheSpeedCrossing(bool dviaFirst)
    {
        OakesHarness harness = BuildOakesRw12Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 300");
        Tick(harness, 5);
        ConstrainedForceDirectToCommand direct = GrudgCrossing(aircraft, CrossingFloorFt, CrossingSpeedKts);
        var via = new DescendViaCommand(null);
        if (dviaFirst)
        {
            DispatchOk(aircraft, via, direct);
        }
        else
        {
            DispatchOk(aircraft, direct, via);
        }

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        CifpSpeedRestriction lohgn = Assert.IsType<CifpSpeedRestriction>(RouteFix(aircraft, Lohgn).SpeedRestriction);
        (CifpSpeedRestriction? grudgSpeed, int? grudgStamp) = (grudg.SpeedRestriction, grudg.SpeedTransmissionId);
        (double grudgIas, _) = TickUntilPassed(harness, Grudg);
        (double? ceiling, double? assigned) = (aircraft.Targets.SpeedCeiling, aircraft.Targets.AssignedSpeed);
        (double lohgnIas, _) = TickUntilPassed(harness, Lohgn);
        return new SpeedFlight(grudgSpeed, grudgStamp, aircraft.Queue.LastTransmissionId, grudgIas, ceiling, assigned, lohgn, lohgnIas);
    }

    private static void AssertFliesTheSpeedCrossing(SpeedFlight flight)
    {
        Assert.Equal(new CifpSpeedRestriction(CrossingSpeedKts, CifpSpeedRestrictionType.AtOrBelow), flight.GrudgSpeed);
        Assert.Equal(flight.TransmissionId, flight.GrudgSpeedTransmissionId);
        Assert.InRange(flight.GrudgIas, CrossingSpeedKts - CrossingToleranceKts, CrossingSpeedKts + CrossingToleranceKts);
        Assert.NotEqual(CrossingSpeedKts, flight.CeilingPastGrudg);
        Assert.NotEqual(300, flight.AssignedPastGrudg);
        double published = flight.LohgnPublished.SpeedKts;
        Assert.InRange(flight.LohgnIas, published - CrossingToleranceKts, published + CrossingToleranceKts);
    }

    /// <summary>
    /// The STAR, the route past GRUDG and the published restrictions a descend via puts on GRUDG and on the fixes after it
    /// (a level spawn carries the published speeds only), read from an identical aircraft cleared "DVIA" alone.
    /// </summary>
    private sealed record PublishedState(
        string? StarId,
        List<string> TailAfterGrudg,
        CifpAltitudeRestriction? GrudgAltitude,
        CifpSpeedRestriction? GrudgSpeed,
        Dictionary<string, CifpAltitudeRestriction?> TailAltitudes
    );

    private PublishedState ReadPublished()
    {
        OakesHarness reference = BuildOakesRw12Harness();
        AircraftState aircraft = reference.Aircraft;
        SendOk(reference, "DVIA");
        List<string> tail = TailAfter(aircraft, Grudg);
        var state = new PublishedState(
            aircraft.Procedure.ActiveStarId,
            tail,
            RouteFix(aircraft, Grudg).AltitudeRestriction,
            RouteFix(aircraft, Grudg).SpeedRestriction,
            tail.ToDictionary(name => name, name => RouteFix(aircraft, name).AltitudeRestriction)
        );
        _output.WriteLine($"Published: GRUDG {state.GrudgAltitude} {state.GrudgSpeed}; {string.Join(" ", state.TailAltitudes)}");
        Assert.NotNull(state.StarId);
        Assert.NotNull(state.GrudgAltitude);
        Assert.Equal(GrudgPublishedKts, state.GrudgSpeed?.SpeedKts);
        Assert.Contains(state.TailAltitudes.Values, altitude => altitude is not null);
        return state;
    }

    private static void AssertStarKeptWithTheCrossing(AircraftState aircraft, PublishedState before)
    {
        Assert.Equal(before.StarId, aircraft.Procedure.ActiveStarId);
        Assert.Equal(before.TailAfterGrudg, TailAfter(aircraft, Grudg));

        NavigationTarget grudg = RouteFix(aircraft, Grudg);
        CifpAltitudeRestriction crossing = Assert.IsType<CifpAltitudeRestriction>(grudg.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, crossing.Type);
        Assert.Equal(CrossingFloorFt, crossing.Altitude1Ft);
        Assert.NotEqual(0, aircraft.Queue.LastTransmissionId);
        Assert.Equal(aircraft.Queue.LastTransmissionId, grudg.CrossingTransmissionId);
        Assert.Equal(before.GrudgSpeed, grudg.SpeedRestriction);
        AssertLaterPublishedAltitudesKept(aircraft, before);
    }

    private static void AssertLaterPublishedAltitudesKept(AircraftState aircraft, PublishedState before)
    {
        foreach ((string name, CifpAltitudeRestriction? published) in before.TailAltitudes)
        {
            Assert.Equal(published, RouteFix(aircraft, name).AltitudeRestriction);
        }
    }

    /// <summary>The fix names on the route after <paramref name="fixName"/>, which must be on it.</summary>
    private static List<string> TailAfter(AircraftState aircraft, string fixName)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int index = route.FindIndex(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"{fixName} is not on the route");
        return [.. route.Skip(index + 1).Select(f => f.Name)];
    }

    /// <summary><paramref name="name"/> as a command fix, at the position of the route fix of that name (any case).</summary>
    private static ResolvedFix ResolvedOnRoute(AircraftState aircraft, string name)
    {
        NavigationTarget fix = RouteFix(aircraft, name);
        return new ResolvedFix(name, fix.Position.Lat, fix.Position.Lon);
    }

    private static ConstrainedForceDirectToCommand GrudgCrossing(AircraftState aircraft, int? altitudeFt, int? speedKts) =>
        Crossing(aircraft, Grudg, altitudeFt, speedKts);

    /// <summary>
    /// "Proceed direct <paramref name="fixName"/>, cross it at or above <paramref name="altitudeFt"/>", at
    /// <paramref name="speedKts"/>.
    /// </summary>
    private static ConstrainedForceDirectToCommand Crossing(AircraftState aircraft, string fixName, int? altitudeFt, int? speedKts)
    {
        var altitudes = new Dictionary<int, ConstrainedFixAltitude>();
        if (altitudeFt is { } feet)
        {
            altitudes[0] = new ConstrainedFixAltitude(feet, CrossFixAltitudeType.AtOrAbove);
        }

        return new ConstrainedForceDirectToCommand(
            [ResolvedOnRoute(aircraft, fixName)],
            altitudes,
            speedKts is { } kts ? new Dictionary<int, int> { [0] = kts } : null,
            null
        );
    }

    /// <summary>Dispatches <paramref name="commands"/> as one block, so they share one transmission.</summary>
    private void DispatchOk(AircraftState aircraft, params ParsedCommand[] commands)
    {
        var compound = new CompoundCommand([new ParsedBlock(null, [.. commands])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        string route = string.Join(" ", aircraft.Targets.NavigationRoute.Select(t => t.Name));
        _output.WriteLine($"{string.Join(", ", commands.Select(c => c.GetType().Name))} -> {result.Message}; route {route}");
        Assert.True(result.Success, result.Message);
    }

    private static NavigationTarget RouteFix(AircraftState aircraft, string fixName) =>
        Assert.Single(aircraft.Targets.NavigationRoute, f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A generated heavy jet level at <paramref name="altitudeFt"/> (by default inside OAKES's FL240-FL320 window) on
    /// KOAK's OAKES3 RW12 transition, just past OAKES, GRUDG next.
    /// </summary>
    private OakesHarness BuildOakesRw12Harness(int altitudeFt = InsideOakesWindowFt)
    {
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = "OAKES",
            StarId = "OAKES3",
            StarRunway = "12",
            DescendVia = false,
            StarAltitude = altitudeFt,
            DestinationAirportId = "KOAK",
        };
        (AircraftState? aircraft, string? error) = AircraftGenerator.Generate(
            request,
            "KOAK",
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(aircraft);

        SimulationEngine engine = new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "constrained-dctf-keeps-star",
                ScenarioName = "A constrained direct onto the active STAR keeps it",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.AddAircraft(aircraft);
        var harness = new OakesHarness(engine, aircraft);
        TickUntilPassed(harness, "OAKES");
        Assert.Equal(Grudg, aircraft.Targets.NavigationRoute[0].Name);
        IEnumerable<string> fixes = aircraft.Targets.NavigationRoute.Select(t => $"{t.Name}[{t.AltitudeRestriction}|{t.SpeedRestriction}]");
        string route = string.Join(" ", fixes);
        _output.WriteLine($"Past OAKES at {aircraft.Altitude:F0} ft: route {route}");
        return harness;
    }

    private void SendOk(OakesHarness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void Tick(OakesHarness harness, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            harness.Engine.TickOneSecond();
        }
    }

    /// <summary>Ticks until <paramref name="fixName"/> leaves the route; returns the speed on that tick and the highest on the way.</summary>
    private (double CrossingIas, double MaxIas) TickUntilPassed(OakesHarness harness, string fixName)
    {
        AircraftState aircraft = harness.Aircraft;
        double maxIas = aircraft.IndicatedAirspeed;
        for (int t = 1; t <= 1800; t++)
        {
            harness.Engine.TickOneSecond();
            maxIas = Math.Max(maxIas, aircraft.IndicatedAirspeed);
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                ControlTargets targets = aircraft.Targets;
                _output.WriteLine(
                    $"Passed {fixName} after {t} s at {aircraft.IndicatedAirspeed:F0} kt, {aircraft.Altitude:F0} ft, "
                        + $"ceiling {targets.SpeedCeiling}, assigned {targets.AssignedSpeed}"
                );
                return (aircraft.IndicatedAirspeed, maxIas);
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
        return (0, 0);
    }
}
