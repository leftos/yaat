using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using OakesHarness = Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness;

namespace Yaat.Sim.Tests;

/// <summary>
/// DVXM, "descend via the STAR, except maintain {speed} until {fix}" (7110.65 5-7-1.e, 5-7-2.e, 5-7-1.g): the assigned speed
/// replaces every published speed up to and including the fix, the published altitudes are still met, and past the fix
/// only the next published speed beyond it governs. Real KOAK navdata and a generated heavy jet on the real OAKES3 STAR
/// (OAKES 280, GRUDG 260, BANND none; RW12: SULRR 250, KEENR, LOHGN 200, WHYEE, HIRMO; RW30: TOOOL, FFIST 200).
/// </summary>
public class DescendViaExceptSpeedTests
{
    private const double SnapKts = 2.0;
    private const double CrossingToleranceKts = 10.0;

    private readonly ITestOutputHelper _output;

    public DescendViaExceptSpeedTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Dvxm_PublishedOnlyBeyondFix_NextPublishedGoverns()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Null(RouteFix(aircraft, "BANND").SpeedRestriction);
        Assert.Equal("SULRR", NextSpeedRestrictedFixAfter(aircraft, "BANND").Name);
        Assert.Equal(250, RouteFix(aircraft, "SULRR").SpeedRestriction?.SpeedKts);

        SendOk(harness, "DVXM 240 BANND");
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "BANND").RevertResume);

        TickUntilPassed(harness, "GRUDG");
        Assert.Equal(240, aircraft.Targets.AssignedSpeed);
        Assert.Equal(260, aircraft.Procedure.LastCrossedPublishedSpeedKts);

        TickUntilPassed(harness, "BANND");
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
        Assert.NotEqual(260, aircraft.Targets.SpeedCeiling);

        (double crossingIas, double maxIas) = TickUntilPassed(harness, "SULRR");
        Assert.True(maxIas <= 250 + SnapKts, $"Past BANND the aircraft reached {maxIas:F0} kt before SULRR, above its published 250");
        Assert.InRange(crossingIas, 240 - CrossingToleranceKts, 250 + SnapKts);
    }

    [Fact]
    public void Dvxm_PublishedSpeedAtPassedFix_DoesNotComeBack()
    {
        // RW12: GRUDG's 260 is passed during the exception, SULRR's 250 is the named fix's own, LOHGN's 200 lies beyond.
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(260, RouteFix(aircraft, "GRUDG").SpeedRestriction?.SpeedKts);
        Assert.Equal("LOHGN", NextSpeedRestrictedFixAfter(aircraft, "SULRR").Name);
        Assert.Equal(200, RouteFix(aircraft, "LOHGN").SpeedRestriction?.SpeedKts);

        SendOk(harness, "DVXM 230 SULRR");
        CifpAltitudeRestriction keenr = Assert.IsType<CifpAltitudeRestriction>(RouteFix(aircraft, "KEENR").AltitudeRestriction);
        CifpAltitudeRestriction lohgn = Assert.IsType<CifpAltitudeRestriction>(RouteFix(aircraft, "LOHGN").AltitudeRestriction);
        Assert.Equal((CifpAltitudeRestrictionType.At, 7000), (keenr.Type, keenr.Altitude1Ft));
        Assert.Equal((CifpAltitudeRestrictionType.At, 6000), (lohgn.Type, lohgn.Altitude1Ft));

        TickUntilPassed(harness, "GRUDG");
        Assert.Equal(230, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);

        (double crossingIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(crossingIas, 230 - CrossingToleranceKts, 230 + SnapKts);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.NotEqual(260, aircraft.Targets.SpeedCeiling);
        Assert.NotEqual(250, aircraft.Targets.SpeedCeiling);

        // 14 CFR 91.117 bounds the acceleration past SULRR; KEENR (at 7,000, not crossed above it) and LOHGN (6,000) are still met.
        double maxIasBelow10000 = 0;
        double? keenrAltitude = null;
        for (int t = 1; aircraft.Targets.NavigationRoute.Exists(f => f.Name == "LOHGN"); t++)
        {
            Assert.True(t <= 1800, "The aircraft did not pass LOHGN within 1800 s");
            harness.Engine.TickOneSecond();
            if (aircraft.Altitude < 10000)
            {
                maxIasBelow10000 = Math.Max(maxIasBelow10000, aircraft.IndicatedAirspeed);
            }

            if ((keenrAltitude is null) && !aircraft.Targets.NavigationRoute.Exists(f => f.Name == "KEENR"))
            {
                keenrAltitude = aircraft.Altitude;
            }
        }

        double lohgnIas = aircraft.IndicatedAirspeed;
        _output.WriteLine(
            $"Below 10,000 max {maxIasBelow10000:F0} kt; KEENR at {keenrAltitude:F0} ft; LOHGN at {aircraft.Altitude:F0} ft, {lohgnIas:F0} kt"
        );
        Assert.True(maxIasBelow10000 <= 250 + SnapKts, $"Below 10,000 ft the aircraft reached {maxIasBelow10000:F0} kt");
        Assert.NotNull(keenrAltitude);
        Assert.True(keenrAltitude.Value <= 7000 + FlightPhysics.AltitudeSnapFt, $"Crossed KEENR at {keenrAltitude:F0} ft, above 7,000");
        Assert.InRange(aircraft.Altitude, 6000 - FlightPhysics.AltitudeSnapFt, 6000 + FlightPhysics.AltitudeSnapFt);
        Assert.InRange(lohgnIas, 200 - CrossingToleranceKts, 200 + SnapKts);
    }

    [Fact]
    public void Dvxm_NamedFixOwnPublishedSpeed_Replaced()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(260, RouteFix(aircraft, "GRUDG").SpeedRestriction?.SpeedKts);

        SendOk(harness, "DVXM 280 GRUDG");

        (double crossingIas, _) = TickUntilPassed(harness, "GRUDG");
        Assert.True(crossingIas >= 280 - CrossingToleranceKts, $"Crossed GRUDG at {crossingIas:F0} kt, not at the assigned 280");
        Assert.NotEqual(260, aircraft.Targets.SpeedCeiling);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_NoPublishedAfterFix_ResumesDefaultSchedule()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        string last = aircraft.Targets.NavigationRoute[^1].Name;
        Assert.Equal("HIRMO", last);
        Assert.Null(RouteFix(aircraft, last).SpeedRestriction);

        SendOk(harness, $"DVXM 230 {last}");

        TickUntilPassed(harness, "LOHGN");
        Assert.Equal(230, aircraft.Targets.AssignedSpeed);
        Assert.Equal(200, aircraft.Procedure.LastCrossedPublishedSpeedKts);

        TickUntilPassed(harness, last);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(aircraft.Targets.SpeedCeiling);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    [Fact]
    public void Dvxm_Assigned280Below10000_CappedAt250()
    {
        OakesHarness harness = SpawnOnStar("BANND", "12", 9000);
        AircraftState aircraft = harness.Aircraft;
        Assert.True(aircraft.Altitude < 10000);

        SendOk(harness, "DVXM 280 LOHGN");

        (double crossingIas, double maxIas) = TickUntilPassed(harness, "SULRR");
        Assert.True(maxIas <= 250 + SnapKts, $"Below 10,000 ft the aircraft reached {maxIas:F0} kt under DVXM 280");
        Assert.True(crossingIas >= 240, $"Crossed SULRR at {crossingIas:F0} kt, not held near the 250 cap");
        Assert.Equal(280, aircraft.Targets.AssignedSpeed);
    }

    [Fact]
    public void Dvxm_MachAboveFl240_HeldToFix()
    {
        OakesHarness harness = SpawnOnStar("OAKES", "12", 26000);
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal("OAKES", aircraft.Targets.NavigationRoute[0].Name);

        SendOk(harness, "DVXM M.78 GRUDG");
        Assert.Equal(0.78, aircraft.Targets.TargetMach);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "GRUDG").RevertResume);
        Assert.True(aircraft.Procedure.StarViaMode);

        for (int t = 1; aircraft.Targets.NavigationRoute.Exists(f => f.Name == "GRUDG"); t++)
        {
            Assert.True(aircraft.Targets.HasExplicitSpeedCommand, $"{t} s on the Mach assignment ended before GRUDG");
            Assert.True(t <= 1800, "The aircraft did not pass GRUDG within 1800 s");
            harness.Engine.TickOneSecond();
        }

        Assert.Null(aircraft.Targets.TargetMach);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_MachBelowFl240_Refused()
    {
        OakesHarness harness = SpawnOnStar("OAKES", "12", 20000);
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 270");

        CommandResult result = Send(harness, "DVXM M.78 GRUDG");

        Assert.False(result.Success);
        Assert.Equal("Unable, Mach speeds are assigned at or above FL240", result.Message);
        AssertNothingChanged(aircraft, 270);
    }

    [Fact]
    public void Dvxm_FixOffRoute_RefusedBeforeAnythingChanges()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name == "SUNOL");
        SendOk(harness, "SPD 210");

        CommandResult result = Send(harness, "DVXM 250 SUNOL");

        Assert.False(result.Success);
        Assert.Equal("Fix SUNOL not found in current route", result.Message);
        AssertNothingChanged(aircraft, 210);
    }

    [Fact]
    public void Dvxm_FixAlreadyPassed_Refused()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");

        CommandResult result = Send(harness, "DVXM 250 OAKES");

        Assert.False(result.Success);
        Assert.Equal("Fix OAKES not found in current route", result.Message);
        AssertNothingChanged(aircraft, 210);
    }

    [Fact]
    public void Dvxm_LaterSpdBeforeFix_CancelsEnd()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 SULRR");
        SendOk(harness, "SPD 220");
        Assert.Null(RouteFix(aircraft, "SULRR").RevertResume);

        TickUntilPassed(harness, "SULRR");
        Tick(harness, 30);

        Assert.Equal(220, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_LaterDviaBeforeFix_EndsException()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 SULRR");
        SendOk(harness, "DVIA");

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.RevertResume is not null);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    [Fact]
    public void Dvxm_VectorWhileEndPending_KeepsAssignedSpeed()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 SULRR");

        SendOk(harness, "FH 270");
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);

        Tick(harness, 30);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_NoFixForm_HoldsToStarEnd()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal("HIRMO", aircraft.Targets.NavigationRoute[^1].Name);
        SendOk(harness, "DVXM 250");
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.RevertResume is not null);

        TickUntilPassed(harness, "LOHGN");
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, "HIRMO");
        Tick(harness, 30);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_ImpossibleAltitude_NoPilotCall()
    {
        OakesHarness harness = SpawnOnStar("BANND", "12", 25000);
        AircraftState aircraft = harness.Aircraft;
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(harness.Engine.Scenario);
        scenario.SoloTrainingMode = true;
        scenario.StudentPositionType = "APP";
        Assert.True(scenario.PilotContacts.AnyAnswering, "Precondition: nobody answers pilots, so no radio call could be seen");
        // The arrival's check-in is not a reply to the clearance: the aircraft is already talking to the controller.
        aircraft.HasMadeInitialContact = true;
        aircraft.HasControllerAcknowledgedInitialContact = true;
        var calls = new List<string>();
        harness.Engine.WarningEmitted += (cs, text) => AddIfOurs(calls, aircraft, cs, $"warning: {text}");
        harness.Engine.PilotSpeechEmitted += (cs, text) => AddIfOurs(calls, aircraft, cs, $"speech: {text.TerminalForRpo}");
        harness.Engine.TerminalEntryEmitted += entry => AddIfOurs(calls, aircraft, entry.Callsign, $"{entry.Kind}: {entry.Message}");

        // The channel is live: an altitude clearance's readback reaches it in solo.
        SendOk(harness, "DM 240");
        Tick(harness, 2);
        _output.WriteLine($"After DM 240: {string.Join(" | ", calls)}");
        Assert.Contains(calls, c => c.Contains("240", StringComparison.Ordinal) || c.Contains("two four zero", StringComparison.OrdinalIgnoreCase));

        SendOk(harness, "DVXM 320 SULRR");
        Tick(harness, 2);
        _output.WriteLine($"After DVXM: {string.Join(" | ", calls)}");
        calls.Clear();
        CifpAltitudeRestriction window = Assert.IsType<CifpAltitudeRestriction>(RouteFix(aircraft, "SULRR").AltitudeRestriction);
        int topFt = Math.Max(window.Altitude1Ft, window.Altitude2Ft ?? window.Altitude1Ft);

        TickUntilPassed(harness, "SULRR");
        Assert.True(
            aircraft.Altitude > topFt + FlightPhysics.AltitudeSnapFt,
            $"Precondition: the aircraft crossed SULRR at {aircraft.Altitude:F0} ft, inside its window; the altitude was not missed"
        );
        Assert.Empty(calls);
    }

    private static void AddIfOurs(List<string> calls, AircraftState aircraft, string callsign, string line)
    {
        if (callsign == aircraft.Callsign)
        {
            calls.Add(line);
        }
    }

    [Fact]
    public void Dvxm_SnapshotRoundTrip_KeepsPendingEnd()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 SULRR");

        StateSnapshotDto snapshot = harness.Engine.CaptureSnapshot();
        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(engine.FindAircraft(harness.Aircraft.Callsign));

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(restored, "SULRR").RevertResume);
        Assert.Equal(250, restored.Targets.AssignedSpeed);
        Assert.Equal(harness.Aircraft.Targets.AssignedSpeedTransmissionId, restored.Targets.AssignedSpeedTransmissionId);
    }

    [Fact]
    public void Dvxm_NoStar_RefusedBeforeAnythingChanges()
    {
        (OakesHarness harness, CrossingState before) = OffTheStarWithDsrAndCrossing();

        CommandResult result = Send(harness, "DVXM 250 GRUDG");

        Assert.False(result.Success);
        Assert.Equal("No active STAR — descend via requires a STAR in the filed route", result.Message);
        Assert.Equal(before, CrossingState.Of(harness.Aircraft));
    }

    [Fact]
    public void Dvxm_NoStar_Deferred_RefusedBeforeAnythingChanges()
    {
        (OakesHarness harness, CrossingState before) = OffTheStarWithDsrAndCrossing();

        SendOk(harness, "AT SUNOL DVXM 250 GRUDG");
        TickUntilPassed(harness, "SUNOL");
        Tick(harness, 2);

        Assert.Equal(before, CrossingState.Of(harness.Aircraft));
        Assert.False(harness.Aircraft.Procedure.StarViaMode);
    }

    [Fact]
    public void Dvxm_FixBeforeTheStar_Refused()
    {
        OakesHarness harness = WithEnrouteFixesAhead();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");

        CommandResult result = Send(harness, "DVXM 250 OAK");

        Assert.False(result.Success);
        Assert.Equal("Fix OAK not found in current route", result.Message);
        AssertNothingChanged(aircraft, 210);
    }

    [Fact]
    public void Dvxm_FixBeforeTheStar_Deferred_Refused()
    {
        OakesHarness harness = WithEnrouteFixesAhead();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");

        SendOk(harness, "AT SUNOL DVXM 250 OAK");
        TickUntilPassed(harness, "SUNOL");
        Tick(harness, 2);

        AssertNothingChanged(aircraft, 210);
    }

    [Fact]
    public void Dvxm_FixOnAnotherTransitionAheadOfTheEntry_Refused()
    {
        // OAKES3 filed by its MRLET transition (MRLET POYSN OAKES), with RUSME, the first fix of its RUSME transition, on the
        // route ahead of MRLET: RUSME is not on the STAR part of this aircraft's route.
        OakesHarness harness = SpawnOnStar("MRLET", "12", 16000);
        AircraftState aircraft = harness.Aircraft;
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        Assert.Equal("RUSME", star.EnrouteTransitions.Values.Select(t => t.Legs[0].FixIdentifier).Single(f => f == "RUSME"));
        Assert.Equal("MRLET", aircraft.Targets.NavigationRoute[0].Name);
        CifpTransition mrlet = star.EnrouteTransitions.Single(pair => pair.Key == "MRLET").Value;
        Assert.DoesNotContain("RUSME", mrlet.Legs.Concat(star.CommonLegs).Select(l => l.FixIdentifier));
        aircraft.FlightPlan.Route = "RUSME MRLET OAKES3";
        aircraft.Targets.NavigationRoute.Insert(0, RealFix("RUSME"));
        SendOk(harness, "SPD 210");

        CommandResult result = Send(harness, "DVXM 250 RUSME");

        Assert.False(result.Success);
        Assert.Equal("Fix RUSME not found in current route", result.Message);
        AssertNothingChanged(aircraft, 210);
    }

    [Fact]
    public void Dvxm_AfterJarrOntoAnotherTransition_AcceptsAFixOnIt()
    {
        // OAKES3 filed by MRLET, then cleared by JARR onto its RUSME transition: RUSME is on the cleared STAR now.
        OakesHarness harness = SpawnOnStar("MRLET", "12", 16000);
        AircraftState aircraft = harness.Aircraft;
        aircraft.FlightPlan.Route = "MRLET OAKES3";
        SendOk(harness, "JARR OAKES3 RUSME");
        Assert.Equal("RUSME", aircraft.Targets.NavigationRoute[0].Name);

        SendOk(harness, "DVXM 250 RUSME");

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "RUSME").RevertResume);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
    }

    /// <summary>
    /// KDCA FRDMM7 publishes no common legs: its BUCKO transition runs straight into the runway transitions at ALWYZ. The
    /// descend via puts HONNR's published window (FL240 to FL280) on the route, and DVXM accepts HONNR, a fix of the
    /// transition flown.
    /// </summary>
    [Fact]
    public void Dvxm_StarWithNoCommonLegs_ViaOverlaysTheTransitionFlown_AndAcceptsItsFix()
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KDCA", "FRDMM7"));
        Assert.Empty(star.CommonLegs);
        Assert.Contains("HONNR", star.EnrouteTransitions["BUCKO"].Legs.Select(l => l.FixIdentifier));
        OakesHarness harness = SpawnOnStar("KDCA", "FRDMM7", "BUCKO", "01", 31000);
        AircraftState aircraft = harness.Aircraft;
        Assert.Null(RouteFix(aircraft, "HONNR").AltitudeRestriction);

        SendOk(harness, "DVIA");

        CifpAltitudeRestriction honnr = Assert.IsType<CifpAltitudeRestriction>(RouteFix(aircraft, "HONNR").AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.Between, honnr.Type);
        Assert.Equal([24000, 28000], new[] { honnr.Altitude1Ft, honnr.Altitude2Ft ?? 0 }.Order());

        SendOk(harness, "DVXM 280 HONNR");

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "HONNR").RevertResume);
        Assert.Equal(280, aircraft.Targets.AssignedSpeed);
    }

    /// <summary>
    /// OAKES3 publishes common legs (OAKES GRUDG BANND); past OAKES the route starts inside them, at GRUDG. DVXM accepts
    /// BANND, a common fix, and refuses POYSN, a fix of the MRLET transition the aircraft did not fly.
    /// </summary>
    [Fact]
    public void Dvxm_RouteStartingInsideTheCommonLegs_AcceptsACommonFix_RefusesAnUnflownTransitionFix()
    {
        CifpStarProcedure star = OakesStar();
        List<string> common = [.. star.CommonLegs.Select(l => l.FixIdentifier).OfType<string>()];
        Assert.Contains("GRUDG", common);
        Assert.Contains("BANND", common);
        Assert.Contains("POYSN", star.EnrouteTransitions["MRLET"].Legs.Select(l => l.FixIdentifier));
        Assert.DoesNotContain("POYSN", common);
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal("GRUDG", aircraft.Targets.NavigationRoute[0].Name);
        SendOk(harness, "SPD 210");

        CommandResult refused = Send(harness, "DVXM 250 POYSN");

        Assert.False(refused.Success);
        Assert.Equal("Fix POYSN not found in current route", refused.Message);
        AssertNothingChanged(aircraft, 210);

        SendOk(harness, "DVXM 250 BANND");

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "BANND").RevertResume);
    }

    /// <summary>Past BANND, OAKES3's last common fix, only RW12's runway-transition fixes are left: DVXM accepts LOHGN.</summary>
    [Fact]
    public void Dvxm_OnlyRunwayTransitionFixesLeft_AcceptsARunwayTransitionFix()
    {
        CifpStarProcedure star = OakesStar();
        HashSet<string> rw12 = [.. star.RunwayTransitions["RW12"].Legs.Select(l => l.FixIdentifier).OfType<string>()];
        Assert.Contains("LOHGN", rw12);
        OakesHarness harness = PastOakes("12");
        TickUntilPassed(harness, "BANND");
        AircraftState aircraft = harness.Aircraft;
        Assert.NotEmpty(aircraft.Targets.NavigationRoute);
        Assert.All(aircraft.Targets.NavigationRoute, f => Assert.Contains(f.Name, rw12));
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => star.CommonLegs.Any(l => l.FixIdentifier == f.Name));

        SendOk(harness, "DVXM 250 LOHGN");

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "LOHGN").RevertResume);
    }

    /// <summary>KLAX OLAAA2 publishes no enroute transitions, only common legs from AMMOR: DVXM accepts CYNDE, a common fix.</summary>
    [Fact]
    public void Dvxm_StarWithNoEnrouteTransitions_AcceptsACommonFix()
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KLAX", "OLAAA2"));
        Assert.Empty(star.EnrouteTransitions);
        Assert.Contains("CYNDE", star.CommonLegs.Select(l => l.FixIdentifier));
        OakesHarness harness = SpawnOnStar("KLAX", "OLAAA2", "AMMOR", null, 26000);
        Assert.Contains(harness.Aircraft.Targets.NavigationRoute, f => f.Name == "CYNDE");

        SendOk(harness, "DVXM 250 CYNDE");

        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(harness.Aircraft, "CYNDE").RevertResume);
    }

    private static CifpStarProcedure OakesStar() => Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));

    [Fact]
    public void Dvxm_LaterCfixBeyondTheNamedFix_DropsTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 BANND");
        SendOk(harness, "CFIX LOHGN 60 200");

        AssertNoExceptSpeedEnd(harness.Aircraft);
    }

    [Fact]
    public void Dvxm_LaterCfixBeforeTheNamedFix_DropsTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 SULRR");
        SendOk(harness, "CFIX BANND 170 240");

        AssertNoExceptSpeedEnd(harness.Aircraft);
    }

    [Fact]
    public void CfixThenDvxm_OneTransmission_250FromTheCrossingToTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "CFIX SULRR A110 220, DVXM 250 LOHGN");

        AssertCrossingAt220Then250UntilLohgn(harness);
    }

    [Fact]
    public void DvxmThenCfix_OneTransmission_FliesLikeTheCfixFirstForm()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 LOHGN, CFIX SULRR A110 220");

        AssertCrossingAt220Then250UntilLohgn(harness);
    }

    [Fact]
    public void EndBeforeTheCrossing_250UntilTheEnd_ThenTheCrossing()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "CFIX SULRR A110 220, DVXM 250 BANND");

        Assert250UntilBanndThenCrossingAt220(harness);
    }

    [Fact]
    public void EndBeforeTheCrossing_DvxmFirst_250UntilTheEnd_ThenTheCrossing()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 BANND, CFIX SULRR A110 220");

        Assert250UntilBanndThenCrossingAt220(harness);
    }

    [Fact]
    public void EndAtTheCrossingFix_CrossesAtTheCrossingSpeed()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "CFIX SULRR A110 220, DVXM 250 SULRR");

        AssertCrossesTheEndFixAt220ThenPublished(harness);
    }

    [Fact]
    public void EndAtTheCrossingFix_DvxmFirst_FliesLikeTheCfixFirstForm()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 SULRR, CFIX SULRR A110 220");

        AssertCrossesTheEndFixAt220ThenPublished(harness);
    }

    [Fact]
    public void TwoCrossingsBeforeTheEnd_DvxmFirst_250PastTheLastCrossing()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 LOHGN, CFIX SULRR A110 220, CFIX KEENR A070 230");

        AssertTwoCrossingsThen250UntilLohgn(harness);
    }

    [Fact]
    public void TwoCrossingsBeforeTheEnd_DvxmLast_250PastTheLastCrossing()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "CFIX SULRR A110 220, CFIX KEENR A070 230, DVXM 250 LOHGN");

        AssertTwoCrossingsThen250UntilLohgn(harness);
    }

    [Fact]
    public void CrossingBeforeAnEndThatIsACrossing_FliesEachCrossingAtItsSpeed()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 SULRR, CFIX SULRR A110 220, CFIX GRUDG A120 240");
        Assert.Equal(240, aircraft.Targets.AssignedSpeed);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "SULRR").RevertResume);

        (double grudgIas, _) = TickUntilPassed(harness, "GRUDG");
        Assert.InRange(grudgIas, 240 - CrossingToleranceKts, 240 + SnapKts);
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);

        (double sulrrIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(sulrrIas, 220 - CrossingToleranceKts, 220 + SnapKts);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_SnapshotRoundTrip_KeepsThePlainRevertBeforeTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "CFIX SULRR A110 220, DVXM 250 LOHGN");
        Assert.Equal(250, RouteFix(harness.Aircraft, "SULRR").RevertAssignedSpeed);

        StateSnapshotDto snapshot = harness.Engine.CaptureSnapshot();
        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(engine.FindAircraft(harness.Aircraft.Callsign));

        Assert.Equal(250, RouteFix(restored, "SULRR").RevertSpeed);
        Assert.Equal(250, RouteFix(restored, "SULRR").RevertAssignedSpeed);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(restored, "LOHGN").RevertResume);
        Assert.Equal(220, restored.Targets.AssignedSpeed);
    }

    [Fact]
    public void SeparateTransmissions_KeepTodaysBehaviour()
    {
        // CFIX, then DVXM in a later transmission: the DVXM's speed cancels the crossing speed (SULRR back to its published
        // 250, no revert), and its end stays on LOHGN.
        OakesHarness cfixFirst = PastOakes("12");
        AircraftState first = cfixFirst.Aircraft;
        SendOk(cfixFirst, "CFIX SULRR A110 220");
        SendOk(cfixFirst, "DVXM 250 LOHGN");
        Assert.Equal(250, first.Targets.AssignedSpeed);
        Assert.Equal(250, first.Targets.TargetSpeed);
        Assert.Equal(250, RouteFix(first, "SULRR").SpeedRestriction?.SpeedKts);
        Assert.Null(RouteFix(first, "SULRR").RevertSpeed);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(first, "LOHGN").RevertResume);
        TickUntilPassed(cfixFirst, "SULRR");
        Assert.Equal(250, first.Targets.AssignedSpeed);
        Assert.True(first.Targets.HasExplicitSpeedCommand);
        TickUntilPassed(cfixFirst, "LOHGN");
        Assert.Null(first.Targets.AssignedSpeed);
        Assert.False(first.Targets.HasExplicitSpeedCommand);
        Assert.Null(first.Procedure.LastCrossedPublishedSpeedKts);

        // DVXM, then CFIX in a later transmission: the crossing drops the end, 220 is flown to SULRR, the DVXM's 250 comes
        // back past it and holds past LOHGN, whose published 200 is the last crossed.
        OakesHarness dvxmFirst = PastOakes("12");
        AircraftState second = dvxmFirst.Aircraft;
        SendOk(dvxmFirst, "DVXM 250 LOHGN");
        SendOk(dvxmFirst, "CFIX SULRR A110 220");
        Assert.Equal(220, second.Targets.AssignedSpeed);
        Assert.Equal(250, second.Targets.TargetSpeed);
        Assert.Equal(220, RouteFix(second, "SULRR").SpeedRestriction?.SpeedKts);
        Assert.Equal(250, RouteFix(second, "SULRR").RevertSpeed);
        Assert.Equal(250, RouteFix(second, "SULRR").RevertAssignedSpeed);
        AssertNoExceptSpeedEnd(second);
        TickUntilPassed(dvxmFirst, "SULRR");
        Assert.Equal(250, second.Targets.AssignedSpeed);
        Assert.Equal(250, second.Targets.TargetSpeed);
        Assert.True(second.Targets.HasExplicitSpeedCommand);
        TickUntilPassed(dvxmFirst, "LOHGN");
        Assert.Equal(250, second.Targets.AssignedSpeed);
        Assert.True(second.Targets.HasExplicitSpeedCommand);
        Assert.Equal(200, second.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>The end fix is the crossing fix: 220 to SULRR, then the published speeds.</summary>
    private void AssertCrossesTheEndFixAt220ThenPublished(OakesHarness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "SULRR").RevertResume);
        Assert.Equal(220, RouteFix(aircraft, "SULRR").SpeedRestriction?.SpeedKts);

        TickUntilPassed(harness, "BANND");
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);

        (double sulrrIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(sulrrIas, 220 - CrossingToleranceKts, 220 + SnapKts);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);

        (double lohgnIas, _) = TickUntilPassed(harness, "LOHGN");
        Assert.InRange(lohgnIas, 200 - CrossingToleranceKts, 200 + SnapKts);
    }

    /// <summary>Two crossings before the DVXM's end: 220 to SULRR, 230 to KEENR, 250 from KEENR to LOHGN, then published.</summary>
    private void AssertTwoCrossingsThen250UntilLohgn(OakesHarness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "LOHGN").RevertResume);
        Assert.Equal(250, RouteFix(aircraft, "KEENR").RevertSpeed);
        Assert.Equal(250, RouteFix(aircraft, "KEENR").RevertAssignedSpeed);
        Assert.Null(RouteFix(aircraft, "SULRR").RevertSpeed);
        Assert.Null(RouteFix(aircraft, "SULRR").RevertAssignedSpeed);

        (double sulrrIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(sulrrIas, 220 - CrossingToleranceKts, 220 + SnapKts);
        Assert.Equal(230, aircraft.Targets.AssignedSpeed);

        (double keenrIas, _) = TickUntilPassed(harness, "KEENR");
        Assert.InRange(keenrIas, 230 - CrossingToleranceKts, 230 + SnapKts);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, "LOHGN");
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// Both orders of a crossing before the DVXM's end: 220 to SULRR, 250 from SULRR to LOHGN, then the published speeds, LOHGN's own
    /// not held.
    /// </summary>
    private void AssertCrossingAt220Then250UntilLohgn(OakesHarness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);
        Assert.Equal(220, RouteFix(aircraft, "SULRR").SpeedRestriction?.SpeedKts);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "LOHGN").RevertResume);

        TickUntilPassed(harness, "BANND");
        Assert.Equal(220, aircraft.Targets.AssignedSpeed);

        (double sulrrIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(sulrrIas, 220 - CrossingToleranceKts, 220 + SnapKts);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.Equal(250, aircraft.Targets.TargetSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);

        TickUntilPassed(harness, "KEENR");
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, "LOHGN");
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// Both orders of a DVXM end before the crossing: 250 until BANND, then slow to cross SULRR at or below 220, then the published
    /// speeds.
    /// </summary>
    private void Assert250UntilBanndThenCrossingAt220(OakesHarness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.Equal(220, RouteFix(aircraft, "SULRR").SpeedRestriction?.SpeedKts);
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(aircraft, "BANND").RevertResume);

        TickUntilPassed(harness, "GRUDG");
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);

        Assert.Equal(16.51, Math.Round(GeoMath.DistanceNm(RouteFix(aircraft, "BANND").Position, RouteFix(aircraft, "SULRR").Position), 2));
        Assert.InRange(IasOnReaching(harness, "BANND"), 250 - 1, 250 + 1);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);

        (double sulrrIas, _) = TickUntilPassed(harness, "SULRR");
        Assert.InRange(sulrrIas, 220 - CrossingToleranceKts, 220 + SnapKts);
        Assert.Null(aircraft.Targets.AssignedSpeed);

        (double lohgnIas, _) = TickUntilPassed(harness, "LOHGN");
        Assert.InRange(lohgnIas, 200 - CrossingToleranceKts, 200 + SnapKts);
    }

    [Fact]
    public void Dvxm_DirectToAFixShortOfTheNamedFix_KeepsTheSpeed_DropsTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 SULRR");

        SendOk(harness, "DCT BANND");
        AssertNoExceptSpeedEnd(aircraft);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);

        TickUntilPassed(harness, "BANND");
        Tick(harness, 60);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Dvxm_ConstrainedDirectKeepingTheStar_DoesNotMoveTheEnd()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 LOHGN");
        NavigationTarget bannd = RouteFix(aircraft, "BANND");
        NavigationTarget sulrr = RouteFix(aircraft, "SULRR");
        var direct = new ConstrainedForceDirectToCommand(
            [
                new ResolvedFix(bannd.Name, bannd.Position.Lat, bannd.Position.Lon),
                new ResolvedFix(sulrr.Name, sulrr.Position.Lat, sulrr.Position.Lon),
            ],
            new Dictionary<int, ConstrainedFixAltitude> { [1] = new(13000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [1] = 230 },
            null
        );

        CommandResult result = CommandDispatcher.Dispatch(direct, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
        AssertNoExceptSpeedEnd(aircraft);
    }

    [Fact]
    public void Dvxm_RnsBeforeTheFix_RestoresTheLastPublishedSpeedCrossed()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        Assert.Equal(260, RouteFix(aircraft, "GRUDG").SpeedRestriction?.SpeedKts);
        SendOk(harness, "DVXM 250 SULRR");
        TickUntilPassed(harness, "GRUDG");

        SendOk(harness, "RNS");

        Assert.Equal(260, aircraft.Procedure.LastCrossedPublishedSpeedKts);
        Assert.Equal(260, aircraft.Targets.SpeedCeiling);
    }

    [Fact]
    public void Dvxm_PublishedAltitudesStillMet()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVXM 250 LOHGN");
        CifpAltitudeRestriction window = Assert.IsType<CifpAltitudeRestriction>(RouteFix(aircraft, "SULRR").AltitudeRestriction);
        int topFt = Math.Max(window.Altitude1Ft, window.Altitude2Ft ?? window.Altitude1Ft);
        int bottomFt = Math.Min(window.Altitude1Ft, window.Altitude2Ft ?? window.Altitude1Ft);
        Assert.True(aircraft.Altitude > topFt, "Precondition: the aircraft starts above SULRR's window");
        TickUntilPassed(harness, "SULRR");

        Assert.InRange(aircraft.Altitude, bottomFt - FlightPhysics.AltitudeSnapFt, topFt + FlightPhysics.AltitudeSnapFt);
    }

    [Theory]
    [InlineData("RNS")]
    [InlineData("RPS")]
    [InlineData("DSR")]
    [InlineData("MACH .60")]
    [InlineData("CAPP I30")]
    public void Dvxm_LaterRnsRpsDsrMachApproach_CancelTheEnd(string later)
    {
        OakesHarness harness = PastOakes("12");
        SendOk(harness, "DVXM 250 LOHGN");
        Assert.Equal(SpeedResumeKind.ExceptSpeed, RouteFix(harness.Aircraft, "LOHGN").RevertResume);

        SendOk(harness, later);

        AssertNoExceptSpeedEnd(harness.Aircraft);
    }

    private static void AssertNoExceptSpeedEnd(AircraftState aircraft) =>
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.RevertResume == SpeedResumeKind.ExceptSpeed);

    /// <summary>The speed state a refused DVXM must leave as it was: DSR, the assigned speed and GRUDG's crossing speed.</summary>
    private sealed record CrossingState(bool Deleted, double? AssignedSpeed, CifpSpeedRestriction? GrudgSpeed, int? GrudgStamp, double? GrudgRevert)
    {
        internal static CrossingState Of(AircraftState aircraft)
        {
            NavigationTarget grudg = RouteFix(aircraft, "GRUDG");
            return new CrossingState(
                aircraft.Procedure.SpeedRestrictionsDeleted,
                aircraft.Targets.AssignedSpeed,
                grudg.SpeedRestriction,
                grudg.SpeedTransmissionId,
                grudg.RevertSpeed
            );
        }
    }

    /// <summary>
    /// An OAKES3 jet vectored off the STAR with no STAR left in its filed route, under DSR, with a route of SUNOL then a
    /// CFIX GRUDG crossing speed: neither an active nor a filed STAR is there for a descend via.
    /// </summary>
    private (OakesHarness Harness, CrossingState Before) OffTheStarWithDsrAndCrossing()
    {
        OakesHarness harness = PastOakes("12");
        AircraftState aircraft = harness.Aircraft;
        aircraft.FlightPlan.Route = "";
        SendOk(harness, "FH 270");
        SendOk(harness, "DSR");
        SendOk(harness, "CFIX SUNOL 120");
        SendOk(harness, "CFIX GRUDG 160 230");
        Assert.Null(aircraft.Procedure.ActiveStarId);
        Assert.True(aircraft.Procedure.SpeedRestrictionsDeleted);
        Assert.Equal(230, RouteFix(aircraft, "GRUDG").SpeedRestriction?.SpeedKts);
        return (harness, CrossingState.Of(aircraft));
    }

    /// <summary>An OAKES3 jet at 16,000 before OAKES with two enroute fixes, SUNOL then OAK, ahead of the STAR on its route.</summary>
    private OakesHarness WithEnrouteFixesAhead()
    {
        OakesHarness harness = SpawnOnStar("OAKES", "12", 16000);
        List<NavigationTarget> route = harness.Aircraft.Targets.NavigationRoute;
        Assert.Equal("OAKES", route[0].Name);
        route.Insert(0, RealFix("OAK"));
        route.Insert(0, RealFix("SUNOL"));
        return harness;
    }

    private static NavigationTarget RealFix(string name)
    {
        (double lat, double lon) = Assert.IsType<(double, double)>(NavigationDatabase.Instance.GetFixPosition(name));
        return new NavigationTarget { Name = name, Position = new LatLon(lat, lon) };
    }

    [Fact]
    public void Dvxm_ReadbackAndEcho()
    {
        Assert.Equal("DVXM 250 SUNOL", CommandDescriber.DescribeCommand(Parse("DVXM 250 SUNOL")));
        Assert.Equal("DVXM 250", CommandDescriber.DescribeCommand(Parse("DVXM 250")));
        Assert.Equal("DVXM M.78 SUNOL", CommandDescriber.DescribeCommand(Parse("DVXM M.78 SUNOL")));
        Assert.Equal("Descend via STAR, except maintain 250 knots until SUNOL", CommandDescriber.DescribeNatural(Parse("DVXM 250 SUNOL")));
        Assert.Equal("Descend via STAR, except maintain 250 knots", CommandDescriber.DescribeNatural(Parse("DVXM 250")));
        Assert.Equal("Descend via STAR, except maintain Mach 0.78 until SUNOL", CommandDescriber.DescribeNatural(Parse("DVXM M.78 SUNOL")));

        OakesHarness harness = PastOakes("12");
        CommandResult result = Send(harness, "DVXM 250 SULRR");
        Assert.True(result.Success, result.Message);
        Assert.Equal("Descend via STAR, except maintain 250 knots until SULRR", result.Message);
    }

    private static ParsedCommand Parse(string text) => Assert.IsAssignableFrom<ParsedCommand>(CommandParser.Parse(text).Value);

    private static void AssertNothingChanged(AircraftState aircraft, int assignedKts)
    {
        Assert.Equal(assignedKts, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(aircraft.Targets.TargetMach);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.RevertResume is not null);
    }

    private static NavigationTarget RouteFix(AircraftState aircraft, string fixName) =>
        Assert.Single(aircraft.Targets.NavigationRoute, f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase));

    private static NavigationTarget NextSpeedRestrictedFixAfter(AircraftState aircraft, string fixName)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int index = route.FindIndex(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase));
        return route.Skip(index + 1).First(f => f.SpeedRestriction is not null);
    }

    private static CommandResult Send(OakesHarness harness, string command) => harness.Engine.SendCommand(harness.Aircraft.Callsign, command);

    private void SendOk(OakesHarness harness, string command)
    {
        CommandResult result = Send(harness, command);
        _output.WriteLine($"{command} -> {result.Message} (IAS {harness.Aircraft.IndicatedAirspeed:F0})");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void Tick(OakesHarness harness, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            harness.Engine.TickOneSecond();
        }
    }

    /// <summary>Ticks until <paramref name="fixName"/> is next to leave the route; returns the speed on the last tick it is still on it.</summary>
    private static double IasOnReaching(OakesHarness harness, string fixName)
    {
        AircraftState aircraft = harness.Aircraft;
        double ias = aircraft.IndicatedAirspeed;
        for (int t = 1; t <= 1800; t++)
        {
            ias = aircraft.IndicatedAirspeed;
            harness.Engine.TickOneSecond();
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                return ias;
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
        return 0;
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
                _output.WriteLine(
                    $"Passed {fixName} after {t} s at {aircraft.IndicatedAirspeed:F0} kt, {aircraft.Altitude:F0} ft, max {maxIas:F0} kt"
                );
                return (aircraft.IndicatedAirspeed, maxIas);
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
        return (0, 0);
    }

    /// <summary>A generated heavy jet level at 16,000 on OAKES3 for <paramref name="runway"/>, just past OAKES.</summary>
    private OakesHarness PastOakes(string runway)
    {
        OakesHarness harness = SpawnOnStar("OAKES", runway, 16000);
        TickUntilPassed(harness, "OAKES");
        return harness;
    }

    /// <summary>A generated heavy jet level at <paramref name="altitudeFt"/> at <paramref name="entryFix"/> on KOAK OAKES3, filed.</summary>
    private OakesHarness SpawnOnStar(string entryFix, string runway, int altitudeFt) => SpawnOnStar("KOAK", "OAKES3", entryFix, runway, altitudeFt);

    /// <summary>A generated heavy jet level at <paramref name="altitudeFt"/> at <paramref name="entryFix"/> on a STAR, filed.</summary>
    private OakesHarness SpawnOnStar(string airport, string starId, string entryFix, string? runway, int altitudeFt)
    {
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = entryFix,
            StarId = starId,
            StarRunway = runway,
            DescendVia = false,
            StarAltitude = altitudeFt,
            DestinationAirportId = airport,
        };
        (AircraftState? aircraft, string? error) = AircraftGenerator.Generate(
            request,
            airport,
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(aircraft);

        SimulationEngine engine = NewEngine();
        engine.World.AddAircraft(aircraft);
        string route = string.Join(" ", aircraft.Targets.NavigationRoute.Select(t => $"{t.Name}({t.SpeedRestriction?.SpeedKts})"));
        _output.WriteLine(
            $"Spawned {aircraft.Callsign} {aircraft.AircraftType} at {aircraft.Altitude:F0}, IAS {aircraft.IndicatedAirspeed:F0}, route {route}"
        );
        return new OakesHarness(engine, aircraft);
    }

    private static SimulationEngine NewEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "dvxm",
                ScenarioName = "Descend via, except maintain a speed until a fix",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
}

/// <summary>DVXM's forms: {speed} {fix}, {speed}, and a Mach number at or above FL240, with or without a fix.</summary>
public class DvxmParserTests
{
    [Fact]
    public void SpeedAndFix()
    {
        DescendViaExceptSpeedCommand cmd = ParseOk("DVXM 250 SUNOL");
        Assert.Equal(250, cmd.Speed);
        Assert.Null(cmd.Mach);
        Assert.Equal("SUNOL", cmd.FixName);
        Assert.NotNull(cmd.FixLat);
        Assert.NotNull(cmd.FixLon);
    }

    [Fact]
    public void SpeedOnly()
    {
        DescendViaExceptSpeedCommand cmd = ParseOk("DVXM 250");
        Assert.Equal(250, cmd.Speed);
        Assert.Null(cmd.FixName);
        Assert.Null(cmd.FixLat);
    }

    [Theory]
    [InlineData("DVXM M.78 SUNOL")]
    [InlineData("DVXM .78 SUNOL")]
    public void MachAndFix(string text)
    {
        DescendViaExceptSpeedCommand cmd = ParseOk(text);
        Assert.Null(cmd.Speed);
        Assert.NotNull(cmd.Mach);
        Assert.Equal(0.78, cmd.Mach.Value, 3);
        Assert.Equal("SUNOL", cmd.FixName);
    }

    [Fact]
    public void MachOnly()
    {
        DescendViaExceptSpeedCommand cmd = ParseOk("DVXM M.78");
        Assert.NotNull(cmd.Mach);
        Assert.Equal(0.78, cmd.Mach.Value, 3);
        Assert.Null(cmd.FixName);
    }

    [Theory]
    [InlineData("DVXM")]
    [InlineData("DVXM ABC SUNOL")]
    [InlineData("DVXM 0 SUNOL")]
    [InlineData("DVXM 250 ZZZZZQ")]
    [InlineData("DVXM 250 SUNOL EXTRA")]
    public void Rejected(string text) => Assert.False(CommandParser.Parse(text).IsSuccess, text);

    private static DescendViaExceptSpeedCommand ParseOk(string text)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(text);
        Assert.True(result.IsSuccess, text);
        return Assert.IsType<DescendViaExceptSpeedCommand>(result.Value);
    }
}
