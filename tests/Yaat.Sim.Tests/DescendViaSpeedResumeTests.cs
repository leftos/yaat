using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation;
using OakesHarness = Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness;

namespace Yaat.Sim.Tests;

/// <summary>
/// Speeds around a via clearance and "resume normal speed" (7110.65 5-7-1; AIM 4-4-12.f, 5-4-1 NOTE 2): a climb via or
/// descend via cancels any speed assigned before it unless restated with it, and "resume normal speed" hands the speed
/// back to the procedure's published restrictions. Real KOAK navdata and a generated jet on the real OAKES3 STAR
/// (OAKES 280, GRUDG 260, then SULRR 250 / LOHGN 200 or FFIST 200), and an airborne jet on the real KSFO WESLA5 SID
/// (WESLA 230).
/// </summary>
public class DescendViaSpeedResumeTests
{
    /// <summary>Speed snap the physics settles within (<c>FlightPhysics.SpeedSnapKts</c>).</summary>
    private const double SnapKts = 2.0;

    /// <summary>How far under an at-or-below published speed still counts as flying that speed rather than an assigned one.</summary>
    private const double ResumedMarginKts = 10.0;

    /// <summary>How far over a crossing speed a crossing may still be, 7110.65 5-7-1.g NOTE 1's ±10 knots.</summary>
    private const double CrossingToleranceKts = 10.0;

    private readonly ITestOutputHelper _output;

    public DescendViaSpeedResumeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // --- RNS (verification) ---

    [Fact]
    public void ResumeNormalSpeed_AfterSlowSpeed_CrossesTheNextPublishedSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        SendOk(harness, "SPD 210");
        Tick(harness, 30);
        SendOk(harness, "RNS");

        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        AssertFliesPublished(fix.Name, published, crossingIas);
    }

    /// <summary>
    /// AIM 5-4-1 NOTE 2: past a fix with a published "at" or "at or below" speed the pilot keeps that speed until ATC intervenes
    /// or the next restriction. "SPD lower, RNS" on the leg after it must not take the aircraft above the speed it crossed.
    /// </summary>
    [Fact]
    public void ResumeNormalSpeed_AfterCrossedPublishedSpeed_DoesNotExceedItBeforeTheNextRestriction()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        NavigationTarget crossed = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction crossedSpeed = crossed.SpeedRestriction!;
        Assert.NotEqual(CifpSpeedRestrictionType.AtOrAbove, crossedSpeed.Type);
        TickUntilPassed(harness, crossed.Name);
        _output.WriteLine(
            $"Crossed {crossed.Name} ({crossedSpeed.SpeedKts}) at {aircraft.IndicatedAirspeed:F0}, ceiling {aircraft.Targets.SpeedCeiling}"
        );

        SendOk(harness, $"SPD {crossedSpeed.SpeedKts - 30}");
        Tick(harness, 20);
        SendOk(harness, "RNS");

        NavigationTarget next = NextSpeedRestrictedFix(aircraft);
        (_, double maxIas) = TickUntilPassed(harness, next.Name);

        Assert.True(
            maxIas <= crossedSpeed.SpeedKts + SnapKts,
            $"After RNS the aircraft reached {maxIas:F0} kt before {next.Name}, above the {crossedSpeed.SpeedKts} kt it crossed {crossed.Name} at"
        );
    }

    /// <summary>
    /// The pinned rule: RNS ("resume normal speed") clears the ATC speed fields but leaves a DSR in place
    /// (<see cref="AircraftProcedure.SpeedRestrictionsDeleted"/>), so after "DSR", "RNS" the published speeds stay deleted
    /// and the aircraft flies the default schedule past the next restriction. RPS is the command that undoes a DSR.
    /// </summary>
    [Fact]
    public void DeleteSpeedRestrictions_ThenResumeNormalSpeed_StaysDeleted()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        SendOk(harness, "DSR");
        Tick(harness, 5);
        SendOk(harness, "RNS");

        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);

        Assert.True(aircraft.Procedure.SpeedRestrictionsDeleted);
        Assert.True(
            crossingIas > published.SpeedKts + SnapKts,
            $"Crossed {fix.Name} at {crossingIas:F0} kt, within its published {published.SpeedKts}: DSR, RNS resumed the published speeds"
        );
    }

    // --- RPS: resume published speed ---

    [Fact]
    public void ResumePublishedSpeed_AfterDeleteSpeedRestrictions_FliesThePublishedSpeedsAgain()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        SendOk(harness, "DSR");
        SendOk(harness, "RPS");

        Assert.False(aircraft.Procedure.SpeedRestrictionsDeleted);
        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        AssertFliesPublished(fix.Name, published, crossingIas);
    }

    /// <summary>As <see cref="ResumeNormalSpeed_AfterCrossedPublishedSpeed_DoesNotExceedItBeforeTheNextRestriction"/>, with RPS.</summary>
    [Fact]
    public void ResumePublishedSpeed_AfterCrossedPublishedSpeed_DoesNotExceedItBeforeTheNextRestriction()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        NavigationTarget crossed = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction crossedSpeed = crossed.SpeedRestriction!;
        Assert.NotEqual(CifpSpeedRestrictionType.AtOrAbove, crossedSpeed.Type);
        TickUntilPassed(harness, crossed.Name);

        SendOk(harness, $"SPD {crossedSpeed.SpeedKts - 30}");
        Tick(harness, 20);
        SendOk(harness, "RPS");

        NavigationTarget next = NextSpeedRestrictedFix(aircraft);
        (_, double maxIas) = TickUntilPassed(harness, next.Name);

        Assert.True(
            maxIas <= crossedSpeed.SpeedKts + SnapKts,
            $"After RPS the aircraft reached {maxIas:F0} kt before {next.Name}, above the {crossedSpeed.SpeedKts} kt it crossed {crossed.Name} at"
        );
    }

    // --- DVIA / CVIA cancel an earlier speed (7110.65 5-7-1) ---

    [Fact]
    public void DescendVia_AfterEarlierSpeed_CancelsItAndFliesThePublishedSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");
        Tick(harness, 5);
        SendOk(harness, "DVIA");

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);

        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        AssertFliesPublished(fix.Name, published, crossingIas);
    }

    [Fact]
    public void ClimbVia_AfterEarlierSpeed_CancelsItAndFliesThePublishedSpeed()
    {
        SimulationEngine engine = NewEngine("SFO");
        AircraftState aircraft = BuildWeslaDeparture();
        engine.World.AddAircraft(aircraft);
        var harness = new OakesHarness(engine, aircraft);

        SendOk(harness, "SPD 210");
        Tick(harness, 5);
        SendOk(harness, "CVIA");

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);

        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        AssertFliesPublished(fix.Name, published, crossingIas);
    }

    /// <summary>A climb via after "delete speed restrictions" puts the SID's published speeds back in force, as a descend via does.</summary>
    [Fact]
    public void DeleteSpeedRestrictions_ThenClimbVia_ResumesPublishedSpeeds()
    {
        SimulationEngine engine = NewEngine("SFO");
        AircraftState aircraft = BuildWeslaDeparture();
        engine.World.AddAircraft(aircraft);
        var harness = new OakesHarness(engine, aircraft);

        SendOk(harness, "DSR");
        SendOk(harness, "CVIA");

        Assert.False(aircraft.Procedure.SpeedRestrictionsDeleted);
        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        AssertFliesPublished(fix.Name, published, crossingIas);
    }

    /// <summary>A speed restated with the via clearance stands (7110.65 5-7-1.c).</summary>
    [Fact]
    public void DescendVia_SameTransmissionSpeed_KeepsIt()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210, DVIA");

        Assert.Equal(210, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);

        NavigationTarget fix = NextSpeedRestrictedFix(aircraft);
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        Assert.InRange(crossingIas, 210 - SnapKts, 210 + SnapKts);
    }

    [Fact]
    public void DescendViaWithFixSpeed_SetsTheFixSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        // Past OAKES the route is GRUDG (260 once the STAR's speeds apply), then BANND, which publishes none.
        NavigationTarget fix = aircraft.Targets.NavigationRoute[1];
        SendOk(harness, $"DVIA SPD 230 {fix.Name}");

        Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), fix.SpeedRestriction);
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        Assert.True(crossingIas <= 230 + SnapKts, $"Crossed {fix.Name} at {crossingIas:F0} kt, above the 230 assigned there");
    }

    /// <summary>A "DVIA SPD n FIX" naming a fix off the route is refused before it cancels anything.</summary>
    [Fact]
    public void DescendViaWithFixSpeed_UnknownFix_FailsBeforeCancellingTheSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");
        // OAKES is the STAR's entry fix, already behind the aircraft.
        CommandResult result = Send(harness, "DVIA SPD 230 OAKES");
        _output.WriteLine($"DVIA SPD 230 OAKES -> {result.Message}");

        Assert.False(result.Success);
        Assert.Contains("not found in current route", result.Message);
        Assert.Equal(210, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Procedure.StarViaMode);
    }

    /// <summary>
    /// As <see cref="DescendViaWithFixSpeed_UnknownFix_FailsBeforeCancellingTheSpeed"/>, from a conditional block, which the
    /// dispatch dry run does not check: the "DVIA SPD n FIX" fires at its trigger and is refused there before it cancels
    /// the speed.
    /// </summary>
    [Fact]
    public void DeferredDescendViaWithFixSpeed_UnknownFix_FailsBeforeCancellingTheSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");
        string trigger = aircraft.Targets.NavigationRoute[0].Name;
        // OAKES is the STAR's entry fix, already behind the aircraft.
        SendOk(harness, $"AT {trigger} DVIA SPD 230 OAKES");
        TickUntilPassed(harness, trigger);
        Tick(harness, 5);

        Assert.Equal(210, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Procedure.StarViaMode);
    }

    /// <summary>
    /// Decision 9: a "DVIA SPD n FIX" speed is the controller's, not a published one, so crossing it leaves the
    /// last-crossed published speed alone and a later RNS does not hold it as the ceiling.
    /// </summary>
    [Fact]
    public void DescendViaWithFixSpeed_ThenResumeNormalSpeed_DoesNotHoldTheControllerSpeedAsPublished()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        NavigationTarget fix = aircraft.Targets.NavigationRoute[1];
        SendOk(harness, $"DVIA SPD 230 {fix.Name}");
        TickUntilPassed(harness, fix.Name);
        SendOk(harness, "RNS");

        Assert.NotEqual(230, aircraft.Procedure.LastCrossedPublishedSpeedKts);
        Assert.NotEqual(230, aircraft.Targets.SpeedCeiling);
    }

    // --- A re-issued DVIA keeps the crossed published ceiling (decision 5) ---

    [Fact]
    public void DescendVia_ReissuedPastACrossedPublishedSpeed_NeverExceedsItBeforeTheNextRestriction()
    {
        if (PastFirstPublishedSpeed() is not var (harness, crossed))
        {
            return;
        }

        SendOk(harness, "DVIA");
        Assert.Equal(crossed.SpeedKts, harness.Aircraft.Targets.SpeedCeiling);
        NavigationTarget next = NextSpeedRestrictedFix(harness.Aircraft);
        (_, double maxIas) = TickUntilPassed(harness, next.Name);

        Assert.True(
            maxIas <= crossed.SpeedKts + CrossingToleranceKts,
            $"After the second DVIA the aircraft reached {maxIas:F0} kt before {next.Name}, above the {crossed.SpeedKts} kt it crossed"
        );
    }

    // --- Controller-set crossing speeds apply under an assigned speed (decision 6) ---

    [Fact]
    public void AssignedSpeed_ThenCfixWithSpeedTwoFixesAhead_CrossesAtTheCfixSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 250");
        Tick(harness, 5);
        NavigationTarget fix = aircraft.Targets.NavigationRoute[1];
        SendOk(harness, $"CFIX {fix.Name} 160 210");

        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        Assert.InRange(crossingIas, 210 - CrossingToleranceKts, 210 + CrossingToleranceKts);
    }

    [Fact]
    public void AssignedSpeed_ThenConstrainedDctfWithSpeedTwoFixesAhead_CrossesAtTheDctfSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 250");
        Tick(harness, 5);
        NavigationTarget first = aircraft.Targets.NavigationRoute[0];
        NavigationTarget second = aircraft.Targets.NavigationRoute[1];
        CommandResult result = CommandDispatcher.Dispatch(
            ConstrainedDirect(first, second, 16000, 210),
            aircraft,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(result.Success, result.Message);

        (double crossingIas, _) = TickUntilPassed(harness, second.Name);
        Assert.InRange(crossingIas, 210 - CrossingToleranceKts, 210 + CrossingToleranceKts);
    }

    // --- A speed restated in the same block (decision 7) ---

    [Fact]
    public void Mach_ThenDvia_SameBlock_KeepsTheMach()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        SendOk(harness, "MACH 78, DVIA");

        Assert.Equal(0.78, harness.Aircraft.Targets.TargetMach);
    }

    [Fact]
    public void CfixWithSpeed_AndDvia_EitherOrder_EndTheSame()
    {
        if ((BuildHarness() is not { } cfixFirst) || (BuildHarness() is not { } dviaFirst))
        {
            return;
        }

        string fixName = cfixFirst.Aircraft.Targets.NavigationRoute[0].Name;
        SendOk(cfixFirst, $"CFIX {fixName} 110 230, DVIA");
        SendOk(dviaFirst, $"DVIA, CFIX {fixName} 110 230");

        ControlTargets a = cfixFirst.Aircraft.Targets;
        ControlTargets b = dviaFirst.Aircraft.Targets;
        Assert.Equal(230, a.AssignedSpeed);
        Assert.Equal(a.AssignedSpeed, b.AssignedSpeed);
        Assert.Equal(a.TargetSpeed, b.TargetSpeed);
        Assert.Equal(a.SpeedCeiling, b.SpeedCeiling);
        Assert.Equal(a.SpeedFloor, b.SpeedFloor);
        Assert.Equal(a.TargetMach, b.TargetMach);
        Assert.Equal(a.HasExplicitSpeedCommand, b.HasExplicitSpeedCommand);
        Assert.Equal(a.SpeedCommandIsControllerIssued, b.SpeedCommandIsControllerIssued);
        Assert.Equal(cfixFirst.Aircraft.Procedure.LastProcedureSpeedKts, dviaFirst.Aircraft.Procedure.LastProcedureSpeedKts);
        Assert.Equal(cfixFirst.Aircraft.Procedure.SpeedRestrictionsDeleted, dviaFirst.Aircraft.Procedure.SpeedRestrictionsDeleted);
    }

    /// <summary>
    /// A crossing speed in the via clearance's own block restates only that crossing, not a speed assigned before the
    /// block (7110.65 5-7-1.c, d): after "SPD 250", either order of "CFIX X 110 230, DVIA" cancels the 250, keeps the 230
    /// at X and flies the published speed at the next speed fix after X.
    /// </summary>
    [Fact]
    public void EarlierSpeed_ThenCfixWithSpeedAndDvia_EitherOrder_CancelsTheEarlierSpeed()
    {
        if ((BuildHarness() is not { } cfixFirst) || (BuildHarness() is not { } dviaFirst))
        {
            return;
        }

        string fixName = cfixFirst.Aircraft.Targets.NavigationRoute[0].Name;
        OakesHarness[] harnesses = [cfixFirst, dviaFirst];
        foreach (OakesHarness harness in harnesses)
        {
            SendOk(harness, "SPD 250");
            Tick(harness, 5);
        }

        SendOk(cfixFirst, $"CFIX {fixName} 110 230, DVIA");
        SendOk(dviaFirst, $"DVIA, CFIX {fixName} 110 230");
        AssertSameSpeedState(cfixFirst.Aircraft, dviaFirst.Aircraft);

        foreach (OakesHarness harness in harnesses)
        {
            ControlTargets targets = harness.Aircraft.Targets;
            NavigationTarget fix = targets.NavigationRoute[0];
            Assert.False(targets.HasExplicitSpeedCommand);
            Assert.False(targets.SpeedCommandIsControllerIssued);
            Assert.Equal(230, targets.AssignedSpeed);
            Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), fix.SpeedRestriction);
            Assert.Null(fix.RevertSpeed);

            TickUntilPassed(harness, fixName);
            NavigationTarget next = NextSpeedRestrictedFix(harness.Aircraft);
            CifpSpeedRestriction published = next.SpeedRestriction!;
            (double crossingIas, _) = TickUntilPassed(harness, next.Name);
            AssertFliesPublished(next.Name, published, crossingIas);
        }
    }

    /// <summary>
    /// After "SPD 210", "CFIX X 110 230, CAPP I30" restates only the crossing at X: the approach clearance still cancels the
    /// 210 (7110.65 5-7-1.c, d) and the 230 at X stands, with no revert to the 210 once X is passed.
    /// </summary>
    [Fact]
    public void EarlierSpeed_ThenCfixWithSpeedAndApproachClearance_CancelsTheEarlierSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");
        Tick(harness, 5);
        NavigationTarget fix = aircraft.Targets.NavigationRoute[0];
        SendOk(harness, $"CFIX {fix.Name} 110 230, CAPP I30");

        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
        Assert.Equal(230, aircraft.Targets.AssignedSpeed);
        Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), fix.SpeedRestriction);
        Assert.NotNull(fix.CrossingTransmissionId);
        Assert.Null(fix.RevertSpeed);

        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        Assert.True(crossingIas <= 230 + CrossingToleranceKts, $"Crossed {fix.Name} at {crossingIas:F0} kt, above the 230 assigned there");
        Assert.NotEqual(210, aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    // --- RNS leaves slowdowns to the planner (decision 8) ---

    /// <summary>
    /// RNS just past GRUDG (260) holds 250-260, not FFIST's 200, until FFIST becomes the active fix or the aircraft is
    /// inside the planner's deceleration window, whichever comes first. The slowdown via-mode sequencing starts once FFIST
    /// is the active fix is pre-existing and booked as YAAT-1367 (leave published-speed slowdowns to the planner).
    /// </summary>
    [Fact]
    public void ResumeNormalSpeed_PastGrudg_HoldsTheCrossedSpeedUntilNearTheNextRestriction()
    {
        if (PastFirstPublishedSpeed() is not var (harness, crossed))
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "RNS");
        NavigationTarget next = NextSpeedRestrictedFix(aircraft);
        int nextKts = next.SpeedRestriction!.SpeedKts;
        Assert.True(
            nextKts < crossed.SpeedKts - 30,
            $"{next.Name} publishes {nextKts} kt, too close to {crossed.SpeedKts} to tell holding from slowing"
        );

        double decelRate = AircraftPerformance.DecelRate(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType));
        for (int t = 1; t <= 1800; t++)
        {
            harness.Engine.TickOneSecond();
            double windowNm = (aircraft.IndicatedAirspeed - nextKts) / decelRate * 1.1 * (aircraft.GroundSpeed / 3600.0);
            double distanceNm = RouteDistanceNm(aircraft, next.Name);
            // Via-mode sequencing applies a fix's restriction once it becomes the active fix (FlightPhysics.ApplyFixConstraints),
            // so the hold is measured on the legs before it.
            bool nextIsActive = aircraft.Targets.NavigationRoute[0].Name.Equals(next.Name, StringComparison.OrdinalIgnoreCase);
            if (nextIsActive || (distanceNm <= windowNm + 1.0))
            {
                _output.WriteLine($"Within {distanceNm:F1} nm of {next.Name} after {t} s at {aircraft.IndicatedAirspeed:F0} kt");
                return;
            }

            // Below 10,000 ft 14 CFR 91.117 caps the held speed at 250 knots.
            double ias = aircraft.IndicatedAirspeed;
            Assert.True(
                (ias >= Math.Min(crossed.SpeedKts, 250) - ResumedMarginKts) && (ias <= crossed.SpeedKts + CrossingToleranceKts),
                $"{ias:F0} kt after {t} s, {distanceNm:F1} nm from {next.Name} (decel window {windowNm:F1} nm), at {aircraft.Altitude:F0} ft, "
                    + $"target {aircraft.Targets.TargetSpeed}, ceiling {aircraft.Targets.SpeedCeiling}, "
                    + $"route {aircraft.Targets.NavigationRoute[0].Name}"
            );
        }

        Assert.Fail($"The aircraft did not near {next.Name} within 1800 s");
    }

    // --- The last-crossed memory ends with the procedure (decision 10) ---

    [Fact]
    public void LastCrossedPublishedSpeed_ClearedByVector()
    {
        if (PastFirstPublishedSpeed() is not var (harness, _))
        {
            return;
        }

        SendOk(harness, "FH 270");
        Assert.Null(harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    [Fact]
    public void LastCrossedPublishedSpeed_ClearedByJarr()
    {
        if (PastFirstPublishedSpeed() is not var (harness, _))
        {
            return;
        }

        SendOk(harness, "JARR OAKES3");
        Assert.Null(harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    [Fact]
    public void LastCrossedPublishedSpeed_ClearedByApproachClearance()
    {
        if (PastFirstPublishedSpeed() is not var (harness, _))
        {
            return;
        }

        SendOk(harness, "CAPP I30");
        Assert.Null(harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    [Fact]
    public void LastCrossedPublishedSpeed_KeptByDctToTheSameProcedure()
    {
        if (PastFirstPublishedSpeed() is not var (harness, crossed))
        {
            return;
        }

        SendOk(harness, $"DCT {NextSpeedRestrictedFix(harness.Aircraft).Name}");
        Assert.Equal(crossed.SpeedKts, harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    // --- An approach clearance cancels an earlier speed (decision 11; 7110.65 5-7-1.d) ---

    [Fact]
    public void ApproachClearance_AfterSpeed_CancelsIt()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210");
        Tick(harness, 5);
        SendOk(harness, "CAPP I30");

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        NavigationTarget? fix = aircraft.Targets.NavigationRoute.FirstOrDefault(t => t.SpeedRestriction is not null);
        Assert.True(fix is not null, "No fix with a speed restriction left on the route after the approach clearance");
        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        AssertFliesPublished(fix.Name, fix.SpeedRestriction!, crossingIas);
    }

    /// <summary>"Maintain 210 until …, cleared ILS": a speed restated with the approach clearance stands (7110.65 5-7-1.c).</summary>
    [Fact]
    public void SpeedAndApproachClearance_SameBlock_KeepsTheSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 210, CAPP I30");

        Assert.Equal(210, aircraft.Targets.AssignedSpeed);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Tick(harness, 60);
        Assert.InRange(aircraft.IndicatedAirspeed, 210 - SnapKts, 210 + SnapKts);
    }

    private void AssertFliesPublished(string fixName, CifpSpeedRestriction published, double crossingIas)
    {
        _output.WriteLine($"Crossed {fixName} at {crossingIas:F0} kt; published {published.Type} {published.SpeedKts}");
        Assert.NotEqual(CifpSpeedRestrictionType.AtOrAbove, published.Type);
        Assert.InRange(crossingIas, published.SpeedKts - ResumedMarginKts, published.SpeedKts + CrossingToleranceKts);
    }

    /// <summary>
    /// An approach clearance cancels every speed assigned before it (7110.65 5-7-1.d), the speed an earlier CFIX would
    /// revert to past its fix included: after "SPD 250", "CFIX X 110 230" and then "CAPP I30", each its own transmission,
    /// passing X does not put the 250 back. The clearance cancels the earlier transmission's 230 at X too, and X takes its
    /// published speed back: none here, with no STAR active and no speed the approach publishes at X.
    /// </summary>
    [Fact]
    public void EarlierSpeed_ThenCfixWithSpeed_ThenApproachClearance_DoesNotRevertToTheEarlierSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "SPD 250");
        Tick(harness, 5);
        NavigationTarget fix = aircraft.Targets.NavigationRoute[0];
        SendOk(harness, $"CFIX {fix.Name} 110 230");
        Assert.Equal(250, fix.RevertAssignedSpeed);
        SendOk(harness, "CAPP I30");

        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(fix.SpeedRestriction);
        Assert.Null(fix.RevertSpeed);
        Assert.Null(fix.RevertAssignedSpeed);

        TickUntilPassed(harness, fix.Name);
        Assert.NotEqual(250, aircraft.Targets.AssignedSpeed);
        Assert.NotEqual(250, aircraft.Targets.TargetSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    // --- An altitude-only crossing leaves the fix's published speed the procedure's ---

    /// <summary>
    /// A CFIX carrying only an altitude restates only the crossing altitude: in "CFIX X 110, DVIA" the fix's published
    /// speed stays the procedure's, so the descend via neither assigns it nor holds it as the controller's.
    /// </summary>
    [Fact]
    public void AltitudeOnlyCfix_OnPublishedSpeedFix_ThenDvia_LeavesNoAssignedSpeed()
    {
        if (JoinedThen(fix => $"CFIX {fix} 110, DVIA") is not var (harness, fixName, published))
        {
            return;
        }

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
    }

    /// <summary>"DVIA, CFIX X 110" ends where "CFIX X 110, DVIA" does, with no speed assigned.</summary>
    [Fact]
    public void Dvia_ThenAltitudeOnlyCfix_OnPublishedSpeedFix_LeavesNoAssignedSpeed()
    {
        if (
            (JoinedThen(fix => $"DVIA, CFIX {fix} 110") is not var (harness, fixName, published))
            || (JoinedThen(fix => $"CFIX {fix} 110, DVIA") is not var (cfixFirst, _, _))
        )
        {
            return;
        }

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
        AssertSameEndState(cfixFirst.Aircraft, harness.Aircraft, fixName);
    }

    /// <summary>As <see cref="AltitudeOnlyCfix_OnPublishedSpeedFix_ThenDvia_LeavesNoAssignedSpeed"/>: "CFIX X 110, CAPP I30".</summary>
    [Fact]
    public void AltitudeOnlyCfix_OnPublishedSpeedFix_ThenApproachClearance_LeavesNoAssignedSpeed()
    {
        if (JoinedThen(fix => $"CFIX {fix} 110, CAPP I30") is not var (harness, fixName, published))
        {
            return;
        }

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
    }

    /// <summary>"CAPP I30, CFIX X 110" ends where "CFIX X 110, CAPP I30" does, with no speed assigned.</summary>
    [Fact]
    public void ApproachClearance_ThenAltitudeOnlyCfix_OnPublishedSpeedFix_LeavesNoAssignedSpeed()
    {
        if (
            (JoinedThen(fix => $"CAPP I30, CFIX {fix} 110") is not var (harness, fixName, published))
            || (JoinedThen(fix => $"CFIX {fix} 110, CAPP I30") is not var (cfixFirst, _, _))
        )
        {
            return;
        }

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
        AssertSameEndState(cfixFirst.Aircraft, harness.Aircraft, fixName);
    }

    /// <summary>
    /// An altitude-only CFIX in a transmission after the descend via leaves the fix's speed the procedure's: crossing the
    /// fix records its published speed as the last one crossed (AIM 5-4-1 NOTE 2).
    /// </summary>
    [Fact]
    public void Dvia_ThenAltitudeOnlyCfixLater_CrossesTheFixAtItsPublishedSpeed()
    {
        if (JoinedThen(_ => "DVIA") is not var (harness, fixName, published))
        {
            return;
        }

        SendOk(harness, $"CFIX {fixName} 110");

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
        TickUntilPassed(harness, fixName);
        Assert.Equal(published.SpeedKts, harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>As <see cref="Dvia_ThenAltitudeOnlyCfixLater_CrossesTheFixAtItsPublishedSpeed"/>, after an approach clearance.</summary>
    [Fact]
    public void ApproachClearance_ThenAltitudeOnlyCfixLater_CrossesTheFixAtItsPublishedSpeed()
    {
        if (JoinedThen(_ => "CAPP I30") is not var (harness, fixName, published))
        {
            return;
        }

        SendOk(harness, $"CFIX {fixName} 110");

        AssertPublishedSpeedNotAssigned(harness.Aircraft, fixName, published);
        TickUntilPassed(harness, fixName);
        Assert.Equal(published.SpeedKts, harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// With no STAR active, "CFIX X 110, DVIA" activates the filed STAR: X keeps the crossing altitude restated with the
    /// descend via and takes the STAR's published speed, which the CFIX did not restate.
    /// </summary>
    [Fact]
    public void AltitudeOnlyCfixAndDvia_ActivatingTheStar_TakesThePublishedSpeedAndKeepsTheCrossingAltitude()
    {
        if ((BuildHarness() is not { } harness) || (BuildHarness() is not { } reference))
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        Assert.Null(aircraft.Procedure.ActiveStarId);
        string fixName = aircraft.Targets.NavigationRoute[0].Name;
        SendOk(reference, "DVIA");
        CifpSpeedRestriction published = Assert.IsType<CifpSpeedRestriction>(RouteFix(reference.Aircraft, fixName).SpeedRestriction);

        SendOk(harness, $"CFIX {fixName} 110, DVIA");

        NavigationTarget fix = RouteFix(aircraft, fixName);
        Assert.Equal(published, fix.SpeedRestriction);
        Assert.Equal(11000, fix.AltitudeRestriction?.Altitude1Ft);
        Assert.Null(aircraft.Targets.AssignedSpeed);
    }

    // --- An approach clearance cancels a crossing speed of an earlier transmission (7110.65 5-7-1.d) ---

    /// <summary>
    /// "CFIX X 110 230" and then "CAPP I30", each its own transmission: the approach clearance cancels the crossing speed
    /// at X, which carries its published speed again with no speed stamp, and leaves the crossing altitude and its altitude
    /// reverts alone. The crossing's speed reverts go.
    /// </summary>
    [Fact]
    public void EarlierCfixSpeed_ThenApproachClearance_RestoresThePublishedSpeedAtTheFix()
    {
        // A descent to 8,000 continues the one the crossing at 11,000 starts, and a slowdown to 250 is still under way when
        // the CFIX is issued, so the crossing keeps both an altitude and a speed to revert to past the fix.
        if (JoinedThen(_ => "DM 80, SPD 250") is not var (harness, fixName, published))
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, $"CFIX {fixName} 110 {published.SpeedKts - 30}");
        NavigationTarget crossing = RouteFix(aircraft, fixName);
        CifpAltitudeRestriction? crossingAltitude = crossing.AltitudeRestriction;
        double? revertAltitude = crossing.RevertAltitude;
        double? revertAssignedAltitude = crossing.RevertAssignedAltitude;
        Assert.NotNull(revertAltitude);
        Assert.NotNull(crossing.RevertSpeed);
        Assert.Equal(250, crossing.RevertAssignedSpeed);

        SendOk(harness, "CAPP I30");
        NavigationTarget fix = RouteFix(aircraft, fixName);
        _output.WriteLine($"After CAPP: {fixName} speed {fix.SpeedRestriction?.SpeedKts}, target speed {aircraft.Targets.TargetSpeed}");

        Assert.Equal(published, fix.SpeedRestriction);
        Assert.Null(fix.SpeedTransmissionId);
        Assert.Equal(crossingAltitude, fix.AltitudeRestriction);
        Assert.Equal(revertAltitude, fix.RevertAltitude);
        Assert.Equal(revertAssignedAltitude, fix.RevertAssignedAltitude);
        Assert.Null(fix.RevertSpeed);
        Assert.Null(fix.RevertAssignedSpeed);
    }

    /// <summary>
    /// After "SPD 250" and "CFIX X 110 230" on the fix after the active one, a speed restated with the approach clearance
    /// ("SPD 240, CAPP I30") governs over that older crossing (7110.65 5-7-1.c, d): when fix sequencing makes X the next fix
    /// it does not apply the 230, so the aircraft crosses X at the 240 and holds it past X, with neither the 250 nor the 230
    /// back.
    /// </summary>
    [Fact]
    public void EarlierSpeed_ThenCfixWithSpeed_ThenRestatedSpeedWithApproach_PassingTheFixKeepsTheRestatedSpeed()
    {
        if (JoinedThen(_ => "SPD 250") is not var (harness, _, _))
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        Tick(harness, 5);
        NavigationTarget fix = aircraft.Targets.NavigationRoute[1];
        SendOk(harness, $"CFIX {fix.Name} 110 230");
        SendOk(harness, "SPD 240, CAPP I30");

        (double crossingIas, _) = TickUntilPassed(harness, fix.Name);
        double? targetSpeed = aircraft.Targets.TargetSpeed;
        _output.WriteLine($"Past {fix.Name}: target speed {targetSpeed}, assigned {aircraft.Targets.AssignedSpeed}");
        Assert.InRange(crossingIas, 240 - SnapKts, 240 + SnapKts);
        Assert.Equal(240, aircraft.Targets.AssignedSpeed);
        // A reached speed leaves no target behind, so a null target is the 240 held.
        Assert.True(
            (targetSpeed is null) || (Math.Abs(targetSpeed.Value - 240) <= SnapKts),
            $"Past {fix.Name} the speed target is {targetSpeed}, not the restated 240"
        );
    }

    /// <summary>
    /// A deferred approach clearance ("CAPP I30" on OAKES3) appends the approach's fixes after the STAR's connecting fix,
    /// each with its published speed. A descend via in a later transmission cancels only the speeds ATC assigned
    /// (7110.65 5-7-1.d), and a pilot keeps a published speed (AIM 5-4-1 NOTE), so every appended fix keeps the speed it
    /// carried. The pending clearance records the transition selected when it was issued, before its fixes joined the route.
    /// </summary>
    [Fact]
    public void DeferredApproachClearance_ThenDvia_KeepsTheApproachFixSpeeds()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "JARR OAKES3");
        var starFixes = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        string? selected = ApproachCommandHandler.SelectBestTransition(I30(), aircraft)?.Name;
        Assert.NotNull(selected);
        SendOk(harness, "CAPP I30");
        Assert.Equal(selected, aircraft.Approach.PendingClearance!.Clearance.TransitionName);
        var appendedSpeeds = aircraft
            .Targets.NavigationRoute.Where(f => !starFixes.Contains(f.Name) && (f.SpeedRestriction is not null))
            .ToDictionary(f => f.Name, f => f.SpeedRestriction!, StringComparer.OrdinalIgnoreCase);
        _output.WriteLine($"Appended approach fix speeds: {string.Join(" ", appendedSpeeds.Select(p => $"{p.Key}({p.Value.SpeedKts})"))}");
        Assert.NotEmpty(appendedSpeeds);

        SendOk(harness, "DVIA");

        foreach ((string name, CifpSpeedRestriction speed) in appendedSpeeds)
        {
            Assert.Equal(speed, RouteFix(aircraft, name).SpeedRestriction);
        }
    }

    /// <summary>
    /// A join approach ("JAPP I30" on OAKES3) records on its active clearance the transition it selected when it was issued.
    /// </summary>
    [Fact]
    public void JoinApproach_RecordsTheSelectedTransition()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "JARR OAKES3");
        string? selected = ApproachCommandHandler.SelectBestTransition(I30(), aircraft)?.Name;
        Assert.NotNull(selected);
        SendOk(harness, "JAPP I30");
        Assert.Equal(selected, aircraft.Phases!.ActiveApproach!.TransitionName);
    }

    private static CifpApproachProcedure I30() => Assert.IsType<CifpApproachProcedure>(NavigationDatabase.Instance.GetApproach("KOAK", "I30"));

    /// <summary>
    /// An approach clearance's published speeds come from the transition it cleared and the common legs only. On KOAK's
    /// H12-Z, FREEM carries 210 on the HIRMO transition, which neither the NRTHM transition nor the common legs name: a
    /// clearance on HIRMO has the 210 at FREEM, and one on NRTHM has no speed there.
    /// </summary>
    [Fact]
    public void ClearedApproach_ReadsOnlyTheClearedTransitionsSpeeds()
    {
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(NavigationDatabase.Instance.GetApproach("KOAK", "H12-Z"));
        Assert.Equal(210, procedure.Transitions["HIRMO"].Legs.First(l => l.FixIdentifier == "FREEM").Speed?.SpeedKts);

        Assert.Equal(new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow), ClearedSpeeds(procedure, "HIRMO").GetValueOrDefault("FREEM"));
        Assert.Null(ClearedSpeeds(procedure, "NRTHM").GetValueOrDefault("FREEM"));
    }

    /// <summary>
    /// The published speeds <see cref="NavigationCommandHandler.ClearedApproachSpeeds"/> reads for an aircraft cleared on
    /// <paramref name="procedure"/>'s <paramref name="transitionName"/>.
    /// </summary>
    private static Dictionary<string, CifpSpeedRestriction> ClearedSpeeds(CifpApproachProcedure procedure, string transitionName)
    {
        var aircraft = new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            Phases = new PhaseList
            {
                ActiveApproach = new ApproachClearance
                {
                    TransitionName = transitionName,
                    ApproachId = procedure.ApproachId,
                    AirportCode = "KOAK",
                    RunwayId = procedure.Runway!,
                    FinalApproachCourse = new TrueHeading(0),
                    Procedure = procedure,
                },
            },
        };
        return NavigationCommandHandler.ClearedApproachSpeeds(aircraft);
    }

    /// <summary>
    /// A fresh harness that has joined OAKES3 (<c>JARR OAKES3</c>), so its fixes carry the STAR's published speeds, then sent
    /// the transmission <paramref name="block"/> builds from the name of the first fix with a published speed; returns the
    /// harness, that fix's name and its published speed.
    /// </summary>
    private (OakesHarness Harness, string FixName, CifpSpeedRestriction Published)? JoinedThen(Func<string, string> block)
    {
        if (BuildHarness() is not { } harness)
        {
            return null;
        }

        SendOk(harness, "JARR OAKES3");
        NavigationTarget fix = NextSpeedRestrictedFix(harness.Aircraft);
        CifpSpeedRestriction published = fix.SpeedRestriction!;
        SendOk(harness, block(fix.Name));
        return (harness, fix.Name, published);
    }

    /// <summary>No speed is assigned, and <paramref name="fixName"/>, still on the route, carries its published speed.</summary>
    private static void AssertPublishedSpeedNotAssigned(AircraftState aircraft, string fixName, CifpSpeedRestriction published)
    {
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.Equal(published, RouteFix(aircraft, fixName).SpeedRestriction);
    }

    /// <summary>The two aircraft end with the same speed state and the same restrictions on <paramref name="fixName"/>.</summary>
    private static void AssertSameEndState(AircraftState a, AircraftState b, string fixName)
    {
        AssertSameSpeedState(a, b);
        NavigationTarget fixA = RouteFix(a, fixName);
        NavigationTarget fixB = RouteFix(b, fixName);
        Assert.Equal(fixA.AltitudeRestriction, fixB.AltitudeRestriction);
        Assert.Equal(fixA.SpeedRestriction, fixB.SpeedRestriction);
    }

    private static NavigationTarget RouteFix(AircraftState aircraft, string fixName) =>
        Assert.Single(aircraft.Targets.NavigationRoute, f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase));

    private static void AssertSameSpeedState(AircraftState a, AircraftState b)
    {
        Assert.Equal(a.Targets.AssignedSpeed, b.Targets.AssignedSpeed);
        Assert.Equal(a.Targets.TargetSpeed, b.Targets.TargetSpeed);
        Assert.Equal(a.Targets.SpeedCeiling, b.Targets.SpeedCeiling);
        Assert.Equal(a.Targets.SpeedFloor, b.Targets.SpeedFloor);
        Assert.Equal(a.Targets.TargetMach, b.Targets.TargetMach);
        Assert.Equal(a.Targets.HasExplicitSpeedCommand, b.Targets.HasExplicitSpeedCommand);
        Assert.Equal(a.Targets.SpeedCommandIsControllerIssued, b.Targets.SpeedCommandIsControllerIssued);
        Assert.Equal(a.Procedure.LastProcedureSpeedKts, b.Procedure.LastProcedureSpeedKts);
        Assert.Equal(a.Procedure.SpeedRestrictionsDeleted, b.Procedure.SpeedRestrictionsDeleted);
    }

    /// <summary>Spawns on OAKES3, descends via and crosses the first fix with a published speed; returns that speed.</summary>
    private (OakesHarness Harness, CifpSpeedRestriction Crossed)? PastFirstPublishedSpeed()
    {
        if (BuildHarness() is not { } harness)
        {
            return null;
        }

        SendOk(harness, "DVIA");
        NavigationTarget crossed = NextSpeedRestrictedFix(harness.Aircraft);
        CifpSpeedRestriction crossedSpeed = crossed.SpeedRestriction!;
        Assert.NotEqual(CifpSpeedRestrictionType.AtOrAbove, crossedSpeed.Type);
        TickUntilPassed(harness, crossed.Name);
        Assert.Equal(crossedSpeed.SpeedKts, harness.Aircraft.Procedure.LastCrossedPublishedSpeedKts);
        return (harness, crossedSpeed);
    }

    private static CommandResult Send(OakesHarness harness, string command) => harness.Engine.SendCommand(harness.Aircraft.Callsign, command);

    private static ConstrainedForceDirectToCommand ConstrainedDirect(NavigationTarget first, NavigationTarget second, int altitudeFt, int speedKts) =>
        new(
            [
                new ResolvedFix(first.Name, first.Position.Lat, first.Position.Lon),
                new ResolvedFix(second.Name, second.Position.Lat, second.Position.Lon),
            ],
            new Dictionary<int, ConstrainedFixAltitude> { [1] = new(altitudeFt, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [1] = speedKts },
            null
        );

    /// <summary>Along-route distance (nm) to <paramref name="fixName"/>, measured as the speed planner measures it.</summary>
    private static double RouteDistanceNm(AircraftState aircraft, string fixName)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        double distanceNm = GeoMath.DistanceNm(aircraft.Position, route[0].Position);
        for (int i = 0; !route[i].Name.Equals(fixName, StringComparison.OrdinalIgnoreCase); i++)
        {
            distanceNm += GeoMath.DistanceNm(route[i].Position, route[i + 1].Position);
        }

        return distanceNm;
    }

    private static NavigationTarget NextSpeedRestrictedFix(AircraftState aircraft) =>
        aircraft.Targets.NavigationRoute.First(t => t.SpeedRestriction is not null);

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
                _output.WriteLine(
                    $"Passed {fixName} after {t} s at {aircraft.IndicatedAirspeed:F0} kt, {aircraft.Altitude:F0} ft, max {maxIas:F0} kt"
                );
                return (aircraft.IndicatedAirspeed, maxIas);
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
        return (0, 0);
    }

    private void SendOk(OakesHarness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message} (IAS {harness.Aircraft.IndicatedAirspeed:F0})");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private OakesHarness? BuildHarness()
    {
        OakesHarness? harness = DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output);
        if (harness is null)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return null;
        }

        // The harness spawns at OAKES, the STAR's entry fix; start on the STAR past it so the next fix is ahead.
        TickUntilPassed(harness, harness.Aircraft.Targets.NavigationRoute[0].Name);
        string route = string.Join(" ", harness.Aircraft.Targets.NavigationRoute.Select(t => $"{t.Name}({t.SpeedRestriction?.SpeedKts})"));
        _output.WriteLine($"IAS {harness.Aircraft.IndicatedAirspeed:F0}, speeds {route}");
        return harness;
    }

    /// <summary>
    /// A B738 airborne off KSFO 28R at 1,500 ft and 200 kt, filed on WESLA5 with no SID active yet, flying direct WESLA:
    /// the CVIA activates the filed SID and overlays its published WESLA restriction (at or above 2,000, 230 kt).
    /// </summary>
    private static AircraftState BuildWeslaDeparture()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("WESLA") ?? throw new InvalidOperationException("WESLA not in navdata");
        var aircraft = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.625, -122.40),
            TrueHeading = new TrueHeading(284),
            TrueTrack = new TrueHeading(284),
            Altitude = 1500,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KLAX",
                Route = "WESLA5",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        aircraft.Procedure.DepartureRunway = "28R";
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WESLA", Position = new LatLon(lat, lon) });
        return aircraft;
    }

    private static SimulationEngine NewEngine(string airport) =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "dvia-speed-resume",
                ScenarioName = "Via clearances and RNS resume published speeds",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
            },
        };
}
