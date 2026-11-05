using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A crossing bound the cleared altitude already satisfies does not move the aircraft (AIM 4-4-10.a; 7110.65 4-5-7): an
/// at-or-above bound below the cleared altitude, or an at-or-below bound above it, keeps the cleared altitude
/// assigned and the aircraft flies to it. Real KOAK navdata: a generated jet on the OAKES3 STAR and a CNDEL5 departure.
/// </summary>
public class CrossingBoundMetByClearanceTests
{
    /// <summary>The OAKES3 fix fourth on the harness's route at spawn.</summary>
    private const string StarFix = "TOOOL";

    private readonly ITestOutputHelper _output;

    public CrossingBoundMetByClearanceTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>"DM 120", level at 12,000, then "CFIX X at or above 110": 12,000 meets the bound, so the aircraft stays at 12,000.</summary>
    [Fact]
    public void DescendThenCrossAtOrAboveBelowClearance_KeepsClearedAltitude()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        SendOk(harness, "DM 120");
        for (int t = 1; Math.Abs(aircraft.Altitude - 12000) > FlightPhysics.AltitudeSnapFt; t++)
        {
            Assert.True(t <= 900, $"The aircraft did not level at 12,000 within 900 s, at {aircraft.Altitude:F0}");
            harness.Engine.TickOneSecond();
        }

        Assert.Contains(aircraft.Targets.NavigationRoute, f => f.Name == fixName);
        SendOk(harness, $"CFIX {fixName} A110");
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);

        TickUntilPassed(harness, fixName, t => AssertNotBelow(aircraft, 12000, t));
        for (int t = 1; t <= 120; t++)
        {
            harness.Engine.TickOneSecond();
            AssertNotBelow(aircraft, 12000, t);
        }

        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>"CM 100; CFIX PORTE at or below 120" from 3,000: 10,000 meets the bound, so the aircraft levels at 10,000.</summary>
    [Fact]
    public void ClimbThenCrossAtOrBelowAboveClearance_KeepsClearedAltitude()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 100", ctx);
        DispatchLine(ac, "CFIX PORTE B120", ctx);
        Assert.Equal(10000, ac.Targets.AssignedAltitude);

        FlyPastFix(
            ac,
            "PORTE",
            t =>
                Assert.True(
                    ac.Altitude <= 10000 + FlightPhysics.AltitudeSnapFt,
                    $"At {t} s the aircraft climbed to {ac.Altitude:F0} ft, above its cleared 10,000"
                )
        );
        Assert.Equal(10000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "CM 140; CFIX PORTE at or above 120" from 3,000: 14,000 meets the bound, so 14,000 stays assigned and the target, and
    /// the aircraft climbs through 12,000 before PORTE.
    /// </summary>
    [Fact]
    public void ClimbFromBelowAtOrAboveWithHigherClearance_ClimbsToClearance()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 140", ctx);
        DispatchLine(ac, "CFIX PORTE A120", ctx);
        Assert.Equal(14000, ac.Targets.AssignedAltitude);

        FlyPastFix(
            ac,
            "PORTE",
            t =>
            {
                Assert.True(ac.Targets.AssignedAltitude == 14000, $"At {t} s AssignedAltitude became {ac.Targets.AssignedAltitude}");
                Assert.True(ac.Targets.TargetAltitude is null or 14000, $"At {t} s TargetAltitude became {ac.Targets.TargetAltitude}");
            }
        );
        Assert.True(ac.Altitude >= 12000, $"The aircraft crossed PORTE at {ac.Altitude:F0} ft, below 12,000");
    }

    /// <summary>
    /// "DM 120; DCTF X/at or above 110" from 16,000: 12,000 meets the last constraint's bound, so 12,000 stays assigned and the
    /// aircraft levels there through X.
    /// </summary>
    [Fact]
    public void DescendThenConstrainedDirectAtOrAboveBelowClearance_KeepsClearedAltitude()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        SendOk(harness, "DM 120");
        SendOk(harness, $"DCTF {fixName}/A110");
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);

        TickUntilPassed(harness, fixName, t => AssertNotBelow(aircraft, 12000, t));
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "DM 100; CFIX TOOOL at or below 120" from 16,000 issued 8 nm before TOOOL: 10,000 meets the bound, but the profile
    /// descent rate alone would still be above 12,000 at TOOOL, so the aircraft descends fast enough to cross it at or below
    /// 12,000 (AIM 4-4-10.e).
    /// </summary>
    [Fact]
    public void DescendThroughAtOrBelowAtFixEightNmAhead_CrossesAtOrBelowTheBound()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        LatLon fix = FixPosition(fixName);
        for (int t = 1; GeoMath.DistanceNm(aircraft.Position, fix) > 8.0; t++)
        {
            Assert.True(t <= 1800, $"The aircraft did not come within 8 nm of {fixName} within 1800 s");
            harness.Engine.TickOneSecond();
        }

        _output.WriteLine($"{GeoMath.DistanceNm(aircraft.Position, fix):F1} nm from {fixName} at {aircraft.Altitude:F0} ft");
        SendOk(harness, "DM 100");
        SendOk(harness, $"CFIX {fixName} B120");
        Assert.Equal(10000, aircraft.Targets.AssignedAltitude);

        var overFix = new ClosestApproach(fix);
        TickUntilPassed(harness, fixName, _ => overFix.Observe(aircraft));
        for (int t = 0; t < 60; t++)
        {
            harness.Engine.TickOneSecond();
            overFix.Observe(aircraft);
        }

        _output.WriteLine($"Closest to {fixName}: {overFix.DistanceNm:F2} nm at {overFix.AltitudeFt:F0} ft");
        Assert.True(overFix.DistanceNm < 1.0, $"The aircraft came no closer than {overFix.DistanceNm:F2} nm to {fixName}");
        Assert.True(overFix.AltitudeFt <= 12000 + FlightPhysics.AltitudeSnapFt, $"The aircraft crossed {fixName} at {overFix.AltitudeFt:F0} ft");
        Assert.Equal(10000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "CM 140; CFIX PORTE at or above 120" from 3,000 issued 12 nm before PORTE: 14,000 meets the bound and stays assigned,
    /// and while below 12,000 the aircraft climbs at the planned bound-crossing rate rather than the profile rate, which
    /// alone reaches only about 10,400 ft at PORTE (AIM 4-4-10.e).
    /// </summary>
    [Fact]
    public void ClimbThroughAtOrAboveAtFixTwelveNmAhead_UsesBoundCrossingRate()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        LatLon porte = FixPosition("PORTE");
        for (int t = 1; GeoMath.DistanceNm(ac.Position, porte) > 12.0; t++)
        {
            Assert.True(t <= 1800, "The aircraft did not come within 12 nm of PORTE within 1800 s");
            FlightPhysics.Update(ac, 1.0);
        }

        _output.WriteLine($"{GeoMath.DistanceNm(ac.Position, porte):F1} nm from PORTE at {ac.Altitude:F0} ft");
        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(ac, "CM 140", ctx);
        DispatchLine(ac, "CFIX PORTE A120", ctx);
        Assert.Equal(14000, ac.Targets.AssignedAltitude);

        var overFix = new ClosestApproach(porte);
        FlyPastFix(
            ac,
            "PORTE",
            t =>
            {
                overFix.Observe(ac);
                Assert.True(ac.Targets.AssignedAltitude == 14000, $"At {t} s AssignedAltitude became {ac.Targets.AssignedAltitude}");
            }
        );
        for (int t = 0; t < 60; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            overFix.Observe(ac);
        }

        _output.WriteLine($"Closest to PORTE: {overFix.DistanceNm:F2} nm at {overFix.AltitudeFt:F0} ft");
        Assert.True(overFix.DistanceNm < 1.0, $"The aircraft came no closer than {overFix.DistanceNm:F2} nm to PORTE");
        Assert.True(overFix.AltitudeFt >= 12000, $"The aircraft crossed PORTE at {overFix.AltitudeFt:F0} ft");
        Assert.Equal(14000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "CM 140; CFIX PORTE at or above 120" from 3,000 issued 12 nm before PORTE, a fly-by fix: the climb needs more than the
    /// standard rate, and the planner aims it at the point YAAT sequences PORTE, its turn anticipation before the fix, so the
    /// aircraft is through 12,000 when PORTE leaves the route (AIM 4-4-10.e, 1-2-1.b.1).
    /// </summary>
    [Fact]
    public void ClimbThroughAtOrAboveAtFlyByFix_ReachesTheBoundBySequencing()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        LatLon porte = FixPosition("PORTE");
        FlyPhysicsWithin(ac, porte, 12.0);

        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(ac, "CM 140", ctx);
        DispatchLine(ac, "CFIX PORTE A120", ctx);
        Assert.Equal(14000, ac.Targets.AssignedAltitude);
        AssertNeedsMoreThanStandardRate(ac, porte, 12000, descending: false);

        FlyPastFix(ac, "PORTE", _ => { });

        _output.WriteLine($"Sequenced PORTE {GeoMath.DistanceNm(ac.Position, porte):F2} nm from it at {ac.Altitude:F0} ft");
        Assert.True(ac.Altitude >= 12000, $"The aircraft sequenced PORTE at {ac.Altitude:F0} ft, below 12,000");
    }

    /// <summary>
    /// "DM 100; CFIX TOOOL at or below 120" from 16,000 issued 8 nm before TOOOL, a fly-by fix: the descent needs more than the
    /// standard rate, and the planner aims it at the point YAAT sequences TOOOL, so the aircraft is through 12,000 when TOOOL
    /// leaves the route (AIM 4-4-10.e, 1-2-1.b.1).
    /// </summary>
    [Fact]
    public void DescendThroughAtOrBelowAtFlyByFix_ReachesTheBoundBySequencing()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        LatLon fix = FixPosition(fixName);
        for (int t = 1; GeoMath.DistanceNm(aircraft.Position, fix) > 8.0; t++)
        {
            Assert.True(t <= 1800, $"The aircraft did not come within 8 nm of {fixName} within 1800 s");
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "DM 100");
        SendOk(harness, $"CFIX {fixName} B120");
        Assert.Equal(10000, aircraft.Targets.AssignedAltitude);
        AssertNeedsMoreThanStandardRate(aircraft, fix, 12000, descending: true);

        TickUntilPassed(harness, fixName, _ => { });

        _output.WriteLine($"Sequenced {fixName} {GeoMath.DistanceNm(aircraft.Position, fix):F2} nm from it at {aircraft.Altitude:F0} ft");
        Assert.True(aircraft.Altitude <= 12000, $"The aircraft sequenced {fixName} at {aircraft.Altitude:F0} ft, above 12,000");
    }

    /// <summary>
    /// "CM 100; DCTF PORTE/at or above 80" from 3,000 issued 6 nm before PORTE: PORTE is the only, so the last, route fix and
    /// sequences 0.5 nm before it; the climb needs more than the standard rate and the planner aims it at that point, so the
    /// aircraft is through 8,000 when PORTE leaves the route (AIM 4-4-10.e).
    /// </summary>
    [Fact]
    public void ConstrainedDirectToLastFix_ReachesTheBoundBySequencing()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        LatLon porte = FixPosition("PORTE");
        FlyPhysicsWithin(ac, porte, 6.0);

        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(ac, "CM 100", ctx);
        DispatchLine(ac, "DCTF PORTE/A080", ctx);
        Assert.Equal(10000, ac.Targets.AssignedAltitude);
        Assert.Equal("PORTE", Assert.Single(ac.Targets.NavigationRoute).Name);
        AssertNeedsMoreThanStandardRate(ac, porte, 8000, descending: false);

        FlyPastFix(ac, "PORTE", _ => { });

        _output.WriteLine($"Sequenced PORTE {GeoMath.DistanceNm(ac.Position, porte):F2} nm from it at {ac.Altitude:F0} ft");
        Assert.True(ac.Altitude >= 8000, $"The aircraft sequenced PORTE at {ac.Altitude:F0} ft, below 8,000");
    }

    /// <summary>
    /// "DCTF PORTE/at 80" from 3,000 issued 8 nm before PORTE, the last route fix: no cleared altitude meets an "at", so the
    /// climb planner flies it inline. It shallows into the level-off rather than steepening in the last seconds, and is at
    /// 8,000 when PORTE sequences.
    /// </summary>
    [Fact]
    public void ConstrainedDirectAtLastFix_ClimbNeverSteepensBeforeSequencing()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        LatLon porte = FixPosition("PORTE");
        FlyPhysicsWithin(ac, porte, 8.0);

        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(ac, "DCTF PORTE/080", ctx);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);
        Assert.NotNull(fix.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.At, fix.AltitudeRestriction.Type);
        Assert.False(NavigationCommandHandler.CrossingBoundMetByClearance(ac, fix.AltitudeRestriction));

        var rates = new List<double>();
        FlyPastFix(
            ac,
            "PORTE",
            _ =>
            {
                if (ac.Targets.NavigationRoute.Count > 0)
                {
                    rates.Add(Math.Abs(ac.Targets.PlannedVerticalRate ?? 0));
                }
            }
        );

        List<double> last = [.. rates.TakeLast(10)];
        _output.WriteLine(
            $"Sequenced PORTE at {ac.Altitude:F0} ft; planned |rate| over the last {last.Count} s: {string.Join(", ", last.Select(r => r.ToString("F0")))}"
        );
        for (int k = 1; k < last.Count; k++)
        {
            Assert.True(last[k] <= last[k - 1] + 50, $"The planned climb stepped up from {last[k - 1]:F0} to {last[k]:F0} fpm before PORTE");
        }

        Assert.True(ac.Altitude >= 8000 - FlightPhysics.AltitudeSnapFt, $"The aircraft sequenced PORTE at {ac.Altitude:F0} ft, below 8,000");
    }

    /// <summary>
    /// Under a descend via from an earlier transmission, "CFIX X at or above 110" and then "CFIX Y at or above 100" each
    /// assign their crossing altitude: a via clearance flies to the depicted altitudes, so the bound is never met by the
    /// assigned altitude, and the handler agrees with the planner.
    /// </summary>
    [Fact]
    public void DescendVia_LaterCrossFixes_AssignTheLastCrossingAltitude()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        string first = StarFixOnRoute(aircraft);
        string second = aircraft.Targets.NavigationRoute[aircraft.Targets.NavigationRoute.FindIndex(f => f.Name == first) + 1].Name;

        SendOk(harness, $"CFIX {first} A110");
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Equal(11000, aircraft.Targets.AssignedAltitude);

        SendOk(harness, $"CFIX {second} A100");
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Equal(10000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A plain "CFIX TOOOL at or below 120" from 16,000 with the fix far ahead assigns 12,000, which then equals the bound:
    /// that is not a clearance beyond the bound, so the planner keeps planning the crossing itself and descends at the
    /// gentle rate that arrives at the fix, well below the standard descent rate.
    /// </summary>
    [Fact]
    public void PlainAtOrBelowFromAbove_FarFix_KeepsTheGentlePlannedRate()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        _output.WriteLine($"{GeoMath.DistanceNm(aircraft.Position, FixPosition(fixName)):F1} nm from {fixName} at {aircraft.Altitude:F0} ft");
        SendOk(harness, $"CFIX {fixName} B120");
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);

        for (int t = 0; t < 10; t++)
        {
            harness.Engine.TickOneSecond();
        }

        double standardFpm = AircraftPerformance.DescentRate(
            aircraft.AircraftType,
            AircraftCategorization.Categorize(aircraft.AircraftType),
            aircraft.Altitude
        );
        _output.WriteLine($"Vertical speed {aircraft.VerticalSpeed:F0} fpm, standard descent {standardFpm:F0} fpm");
        Assert.True(aircraft.VerticalSpeed < 0, $"The aircraft is not descending: {aircraft.VerticalSpeed:F0} fpm");
        // 4,000 ft over the ~47 nm to TOOOL plans a few hundred fpm, well under half the standard rate
        Assert.True(
            -aircraft.VerticalSpeed < standardFpm / 2,
            $"Descending at {-aircraft.VerticalSpeed:F0} fpm, not the gentle planned rate (standard {standardFpm:F0})"
        );
    }

    /// <summary>
    /// On OAKES3 joined by JARR with no descend via, "DM 120" with a published ceiling below 12,000 ahead: the published
    /// altitude is not in force, so the aircraft descends to and levels at 12,000 through that fix (AIM 5-4-1.a.2 and its
    /// Example 2 NOTE; 7110.65 4-5-7.h.2).
    /// </summary>
    [Fact]
    public void MaintainWithoutDescendVia_PublishedAtOrBelowAhead_LevelsAtAssigned()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        NavigationTarget ceilingFix = aircraft.Targets.NavigationRoute.First(f =>
            (f.AltitudeRestriction is { } r)
            && (r.Type is CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrBelow or CifpAltitudeRestrictionType.Between)
            && (r.Altitude1Ft < 12000)
        );
        _output.WriteLine($"{ceilingFix.Name} publishes {ceilingFix.AltitudeRestriction!.Type} {ceilingFix.AltitudeRestriction.Altitude1Ft}");

        SendOk(harness, "DM 120");
        TickUntilPassed(
            harness,
            ceilingFix.Name,
            t => Assert.True(aircraft.Altitude >= 12000 - 20, $"At {t} s the aircraft descended to {aircraft.Altitude:F0} ft, below its 12,000")
        );

        Assert.InRange(aircraft.Altitude, 12000 - 20, 12000 + 20);
        Assert.Equal(12000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A CNDEL5 departure in SID mode cleared "CTO 050" (no climb via), then "CFIX PORTE at or above 30": SID mode is not a
    /// via clearance, so the cleared 5,000 meets the bound and stays assigned (AIM 4-4-10.a; 7110.65 4-5-7).
    /// </summary>
    [Fact]
    public void CtoFifty_ThenCfixAtOrAboveAboveIt_KeepsTheAssignedAltitude()
    {
        AircraftState aircraft = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: true, out _);
        // The state "CTO 050" leaves a SID departure in: SID mode, 5,000 assigned, no climb via
        aircraft.Targets.AssignedAltitude = 5000;
        aircraft.Targets.TargetAltitude = 5000;
        Assert.True(aircraft.Procedure.SidViaMode);
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("CFIX PORTE A030");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);

        Assert.Equal(5000, aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "DM 120", then "CFIX TOOOL at or below 100": the controller's crossing is in force, so the aircraft crosses TOOOL at
    /// or below 10,000 and holds the altitude it crossed at past it, never climbing back toward 12,000.
    /// </summary>
    [Fact]
    public void CrossFixAtOrBelowAfterMaintain_AimsAtTheBoundAndHolds()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = StarFixOnRoute(aircraft);
        SendOk(harness, "DM 120");
        SendOk(harness, $"CFIX {fixName} B100");

        TickUntilPassed(harness, fixName, _ => { });
        double crossed = aircraft.Altitude;
        _output.WriteLine($"Crossed {fixName} at {crossed:F0} ft");
        Assert.True(crossed <= 10000 + 20, $"Crossed {fixName} at {crossed:F0} ft, above its 10,000 bound");

        for (int t = 1; t <= 300; t++)
        {
            harness.Engine.TickOneSecond();
            Assert.True(
                aircraft.Altitude <= crossed + 20,
                $"{t} s past {fixName} the aircraft climbed back to {aircraft.Altitude:F0} ft from {crossed:F0}"
            );
        }
    }

    [Theory]
    [InlineData(CifpAltitudeRestrictionType.AtOrAbove, 12000, 12000.0, false)]
    [InlineData(CifpAltitudeRestrictionType.AtOrAbove, 12000, 14000.0, true)]
    [InlineData(CifpAltitudeRestrictionType.AtOrAbove, 12000, 11000.0, false)]
    [InlineData(CifpAltitudeRestrictionType.AtOrAbove, 12000, null, false)]
    [InlineData(CifpAltitudeRestrictionType.AtOrBelow, 12000, 12000.0, false)]
    [InlineData(CifpAltitudeRestrictionType.AtOrBelow, 12000, 10000.0, true)]
    [InlineData(CifpAltitudeRestrictionType.AtOrBelow, 12000, 13000.0, false)]
    [InlineData(CifpAltitudeRestrictionType.AtOrBelow, 12000, null, false)]
    [InlineData(CifpAltitudeRestrictionType.At, 12000, 12000.0, false)]
    public void CrossingBoundMetByClearance_SingleBound(CifpAltitudeRestrictionType type, int bound, double? cleared, bool met)
    {
        AircraftState aircraft = AircraftClearedTo(cleared);
        Assert.Equal(met, NavigationCommandHandler.CrossingBoundMetByClearance(aircraft, new CifpAltitudeRestriction(type, bound)));
    }

    [Theory]
    [InlineData(12000.0, true)]
    [InlineData(14000.0, false)]
    [InlineData(10000.0, false)]
    [InlineData(15000.0, false)]
    [InlineData(9000.0, false)]
    [InlineData(null, false)]
    public void CrossingBoundMetByClearance_Window(double? cleared, bool met)
    {
        AircraftState aircraft = AircraftClearedTo(cleared);
        var window = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 14000, 10000);
        Assert.Equal(met, NavigationCommandHandler.CrossingBoundMetByClearance(aircraft, window));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossingBoundMetByClearance_UnderAVia_IsNeverMet(bool descendVia)
    {
        AircraftState aircraft = AircraftClearedTo(12000);
        aircraft.Procedure.StarViaMode = descendVia;
        aircraft.Procedure.SidViaMode = !descendVia;
        aircraft.Procedure.ClimbViaTargetFt = descendVia ? null : 14000;
        var bound = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 11000);
        Assert.False(NavigationCommandHandler.CrossingBoundMetByClearance(aircraft, bound));
    }

    private static AircraftState AircraftClearedTo(double? cleared)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Altitude = 13000,
            Position = new LatLon(37.7, -122.2),
        };
        aircraft.Targets.AssignedAltitude = cleared;
        return aircraft;
    }

    /// <summary>An OAKES3 fix the harness's aircraft has ahead of it, found by name.</summary>
    private static string StarFixOnRoute(AircraftState aircraft)
    {
        Assert.Contains(aircraft.Targets.NavigationRoute, f => f.Name == StarFix);
        return StarFix;
    }

    /// <summary>The aircraft's altitude where it came closest to a fix's position, sampled once a second.</summary>
    private sealed class ClosestApproach(LatLon fix)
    {
        public double DistanceNm { get; private set; } = double.MaxValue;

        public double AltitudeFt { get; private set; }

        public void Observe(AircraftState aircraft)
        {
            double distanceNm = GeoMath.DistanceNm(aircraft.Position, fix);
            if (distanceNm < DistanceNm)
            {
                DistanceNm = distanceNm;
                AltitudeFt = aircraft.Altitude;
            }
        }
    }

    private static LatLon FixPosition(string fix)
    {
        (double Lat, double Lon) position = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition(fix));
        return new LatLon(position.Lat, position.Lon);
    }

    private static void AssertNotBelow(AircraftState aircraft, double clearedFt, int t) =>
        Assert.True(
            aircraft.Altitude >= clearedFt - FlightPhysics.AltitudeSnapFt,
            $"At {t} s the aircraft descended to {aircraft.Altitude:F0} ft, below its cleared {clearedFt:F0}"
        );

    private static void FlyPhysicsWithin(AircraftState aircraft, LatLon fix, double distanceNm)
    {
        for (int t = 1; GeoMath.DistanceNm(aircraft.Position, fix) > distanceNm; t++)
        {
            Assert.True(t <= 1800, $"The aircraft did not come within {distanceNm:F0} nm of the fix within 1800 s");
            FlightPhysics.Update(aircraft, 1.0);
        }
    }

    /// <summary>The staging precondition: reaching <paramref name="boundFt"/> by the fix itself already takes more than the standard rate.</summary>
    private void AssertNeedsMoreThanStandardRate(AircraftState aircraft, LatLon fix, double boundFt, bool descending)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double standardFpm = descending
            ? AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude)
            : AircraftPerformance.ClimbRate(aircraft.AircraftType, cat, aircraft.Altitude);
        double minutesToFix = GeoMath.DistanceNm(aircraft.Position, fix) / (aircraft.GroundSpeed / 60.0);
        double requiredFpm = Math.Abs(aircraft.Altitude - boundFt) / minutesToFix;
        _output.WriteLine(
            $"{GeoMath.DistanceNm(aircraft.Position, fix):F1} nm from the fix at {aircraft.Altitude:F0} ft, {aircraft.GroundSpeed:F0} kt: "
                + $"{requiredFpm:F0} fpm needed, standard {standardFpm:F0}"
        );
        Assert.True(requiredFpm > standardFpm, $"The bound needs {requiredFpm:F0} fpm, no more than the standard {standardFpm:F0}");
    }

    private static void FlyPastFix(AircraftState aircraft, string fixName, Action<int> eachSecond)
    {
        for (int t = 1; t <= 1800; t++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            eachSecond(t);
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name == fixName))
            {
                return;
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
    }

    private void TickUntilPassed(DescendViaSupersedesEarlierAltitudeTests.Harness harness, string fixName, Action<int> eachSecond)
    {
        for (int t = 1; t <= 1800; t++)
        {
            harness.Engine.TickOneSecond();
            eachSecond(t);
            if (!harness.Aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                _output.WriteLine($"Passed {fixName} after {t} s at {harness.Aircraft.Altitude:F0} ft");
                return;
            }
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
    }

    private void SendOk(DescendViaSupersedesEarlierAltitudeTests.Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private void DispatchLine(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), aircraft, ctx);
        _output.WriteLine($"{text} -> {result.Message}");
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
