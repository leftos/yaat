using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A descend via clearance supersedes the altitude and the crossing restrictions issued before it and not restated
/// with it (YAAT-956; 7110.65 4-5-7.h.9, h.11 NOTES 1-2; AIM 5-2-9.h.8 Example 8). A CFIX in the same transmission
/// as the DVIA is restated with it and keeps its restriction; no CFIX revert survives the DVIA. Real KOAK navdata and
/// a generated jet on the real OAKES3 STAR.
/// </summary>
public class DescendViaSupersedesEarlierAltitudeTests
{
    private const string Airport = "OAK";

    /// <summary>The band around an assigned altitude the aircraft counts as level at.</summary>
    private const double LevelToleranceFt = 20.0;

    private readonly ITestOutputHelper _output;

    public DescendViaSupersedesEarlierAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void DescendVia_AfterCrossFix_DropsTheRevert()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.AltitudeRestriction is not null);
        CifpAltitudeRestriction published = Assert.IsType<CifpAltitudeRestriction>(fix.AltitudeRestriction);
        _output.WriteLine($"{fix.Name} publishes {published.Type} {published.Altitude1Ft}");

        SendOk(harness, "DM 080");
        SendOk(harness, $"CFIX {fix.Name} A110");
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 11000) { Source = CifpAltitudeRestrictionSource.Atc },
            fix.AltitudeRestriction
        );
        Assert.Equal(8000, fix.RevertAssignedAltitude);

        SendOk(harness, "DVIA");

        Assert.Equal(published, fix.AltitudeRestriction);
        AssertNoReverts(fix);
        Assert.Null(fix.CrossingTransmissionId);
        Assert.Null(aircraft.Targets.AssignedAltitude);

        TickUntilPassed(harness, fix.Name, t => Assert.True(aircraft.Targets.AssignedAltitude is null, $"At {t} s AssignedAltitude came back"));
        Assert.NotEqual(8000, aircraft.Targets.TargetAltitude);
    }

    /// <summary>
    /// "DM 150; CFIX X 110" from above 15,000: going back up to 15,000 past X would reverse the descent, which needs a new
    /// clearance (7110.65 4-5-7.h.8 NOTE 2; 14 CFR 91.123), and the crossing overrides the earlier 15,000 (AIM 4-4-10.g).
    /// The CFIX captures no altitude revert, and the aircraft stays at 11,000 past X.
    /// </summary>
    [Fact]
    public void CrossFixBelowEarlierDescent_NoRevert_StaysAtTheCrossingAltitude()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, "DM 150");
        SendOk(harness, $"CFIX {fixName} 110");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Null(fix.RevertAltitude);
        Assert.Null(fix.RevertAssignedAltitude);

        TickUntilPassed(harness, fixName, _ => { });
        double lowest = aircraft.Altitude;
        for (int t = 1; t <= 300; t++)
        {
            harness.Engine.TickOneSecond();
            lowest = Math.Min(lowest, aircraft.Altitude);
            Assert.True(
                aircraft.Altitude <= lowest + FlightPhysics.AltitudeSnapFt,
                $"{t} s past {fixName} the aircraft climbed back to {aircraft.Altitude:F0} ft from {lowest:F0}"
            );
        }

        Assert.Equal(11000, aircraft.Targets.AssignedAltitude);
        Assert.InRange(aircraft.Altitude, 11000 - FlightPhysics.AltitudeSnapFt, 11000 + FlightPhysics.AltitudeSnapFt);
    }

    /// <summary>
    /// "DM 080; CFIX X at or above 110" from above 11,000: the revert to 8,000 continues the descent (AIM 4-4-10.e), so the
    /// CFIX captures it and the aircraft carries on down to 8,000 past X.
    /// </summary>
    [Fact]
    public void CrossFixAboveLaterDescent_KeepsTheRevert_ContinuesDown()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, "DM 080");
        SendOk(harness, $"CFIX {fixName} A110");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(8000, fix.RevertAltitude);
        Assert.Equal(8000, fix.RevertAssignedAltitude);

        TickUntilPassed(harness, fixName, _ => { });
        for (int t = 0; (t < 600) && (aircraft.Altitude > 8000 + FlightPhysics.AltitudeSnapFt); t++)
        {
            harness.Engine.TickOneSecond();
        }

        Assert.Equal(8000, aircraft.Targets.AssignedAltitude);
        Assert.InRange(aircraft.Altitude, 8000 - FlightPhysics.AltitudeSnapFt, 8000 + FlightPhysics.AltitudeSnapFt);
    }

    /// <summary>
    /// On OAKES3 joined by JARR, "CFIX GRUDG 110 230" from 16,000 is a descent to cross GRUDG at 11,000 (AIM 4-4-10.g).
    /// The STAR's higher window past GRUDG (BANND between 17,000 and 19,000) is not a climb before it: the aircraft never
    /// targets above 11,000 nor climbs before GRUDG (YAAT-1402).
    /// </summary>
    [Fact]
    public void Cfix_BelowTheAircraft_WithAHigherWindowBeyondIt_DoesNotClimbBeforeTheFix()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        SendOk(harness, "SPD 250");
        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "CFIX GRUDG 110 230");
        double cfixAltitude = aircraft.Altitude;
        SendOk(harness, "SPD 240, CAPP I30");

        (double highestTarget, int highestTargetSecond, double highestAltitude) = HighestUntilGrudg(harness);

        Assert.True(highestTarget <= 11000, $"Before GRUDG the target rose to {highestTarget:F0} ft, {highestTargetSecond} s after the CFIX");
        Assert.True(
            highestAltitude <= cfixAltitude + 50,
            $"Before GRUDG the aircraft climbed to {highestAltitude:F0} ft from {cfixAltitude:F0} ft at the CFIX"
        );
    }

    /// <summary>
    /// Level at 11,000 (or within the altitude snap above it), "CFIX GRUDG 110 230" on OAKES3 joined by JARR is already met:
    /// the met "at" crossing still ends the climb scan, so BANND's higher window past GRUDG is no climb before it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Cfix_AlreadyMetAt_WithAHigherWindowBeyondIt_DoesNotClimbBeforeTheFix(int feetAbove)
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        LevelAt11000(harness);
        aircraft.Altitude = 11000 + feetAbove;
        SendOk(harness, "JARR OAKES3");
        SendOk(harness, "CFIX GRUDG 110 230");

        (double highestTarget, int highestTargetSecond, _) = HighestUntilGrudg(harness);

        Assert.True(highestTarget <= 11000, $"Before GRUDG the target rose to {highestTarget:F0} ft, {highestTargetSecond} s after the CFIX");
    }

    /// <summary>
    /// "JARR" puts both the STAR's published altitudes and speeds on the route; the later descend via overlays the same
    /// published values again, so the route it hands to via mode is the one JARR built.
    /// </summary>
    [Fact]
    public void Jarr_ThenDescendVia_LeavesTheRestrictionsJarrPutOnTheRoute()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        string before = RouteRestrictions(aircraft);

        SendOk(harness, "DVIA");

        Assert.Equal(before, RouteRestrictions(aircraft));
    }

    /// <summary>
    /// A level STAR spawn carries the STAR's published speeds only, so "CFIX X A110, DVIA" must still load its other
    /// published altitudes: the crossing this transmission restates at X keeps 11,000, and the next published fix takes
    /// the altitude the STAR publishes.
    /// </summary>
    [Fact]
    public void LevelSpawn_SameTransmissionCfixThenDvia_LoadsTheOtherPublishedAltitudes()
    {
        if ((BuildOakesHarness() is not { } harness) || (BuildOakesHarness() is not { } reference))
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        SendOk(reference, "DVIA");
        (string first, string second) = FirstTwoPublishedAltitudeFixes(reference.Aircraft);

        SendOk(harness, $"CFIX {first} A110, DVIA");

        Assert.Equal(11000, FixNamed(harness.Aircraft, first).AltitudeRestriction?.Altitude1Ft);
        Assert.Equal(FixNamed(reference.Aircraft, second).AltitudeRestriction, FixNamed(harness.Aircraft, second).AltitudeRestriction);
    }

    /// <summary>
    /// The same crossing and clearance in separate transmissions: the clearance supersedes the earlier crossing at X and
    /// still loads the STAR's other published altitudes, so the next published fix carries the altitude the STAR publishes.
    /// </summary>
    [Fact]
    public void LevelSpawn_CfixThenLaterDvia_LoadsTheOtherPublishedAltitudes()
    {
        if ((BuildOakesHarness() is not { } harness) || (BuildOakesHarness() is not { } reference))
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        SendOk(reference, "DVIA");
        (string first, string second) = FirstTwoPublishedAltitudeFixes(reference.Aircraft);

        SendOk(harness, $"CFIX {first} A110");
        SendOk(harness, "DVIA");

        Assert.Equal(FixNamed(reference.Aircraft, second).AltitudeRestriction, FixNamed(harness.Aircraft, second).AltitudeRestriction);
    }

    /// <summary>
    /// "JARR OAKES3" with no descend via puts the STAR's published altitudes on the route but not in force: the aircraft
    /// holds its assigned 16,000 through GRUDG (between 20,000 and 23,000) and still flies the STAR's published speeds
    /// (YAAT-1644; AIM 5-4-1.a.2 and its Example 2 NOTE; 7110.65 4-5-7.h.2).
    /// </summary>
    [Fact]
    public void JarrWithoutDescendVia_HoldsAltitudeThroughGrudg()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        NavigationTarget grudg = FixNamed(aircraft, "GRUDG");
        CifpSpeedRestriction speed = Assert.IsType<CifpSpeedRestriction>(grudg.SpeedRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.Between, grudg.AltitudeRestriction?.Type);

        TickUntilPassed(
            harness,
            "GRUDG",
            t =>
                Assert.True(
                    Math.Abs(aircraft.Altitude - 16000) <= LevelToleranceFt,
                    $"{t} s after JARR the aircraft is at {aircraft.Altitude:F0} ft, off its assigned 16,000"
                )
        );

        Assert.Equal(speed.SpeedKts, aircraft.Procedure.LastProcedureSpeedKts);
    }

    /// <summary>
    /// From FL260, "JARR OAKES3" then "DVIA": the published altitudes are in force, so GRUDG is crossed inside its
    /// 20,000-23,000 window. Staged above the window, since a descend via never climbs to a floor. The planner retargets
    /// BANND on the sub-tick GRUDG sequences, so the passing second's reading carries up to one second of BANND's descent.
    /// </summary>
    [Fact]
    public void JarrThenDescendVia_CrossesGrudgInsideItsWindow()
    {
        if (BuildOakesHarness(_output, starAltitude: 26000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        SendOk(harness, "DVIA");

        double lastAhead = aircraft.Altitude;
        TickUntilPassed(
            harness,
            "GRUDG",
            _ =>
            {
                if (aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals("GRUDG", StringComparison.OrdinalIgnoreCase)))
                {
                    lastAhead = aircraft.Altitude;
                }
            }
        );

        double oneSecondPast = Math.Abs(aircraft.VerticalSpeed) / 60.0;
        _output.WriteLine($"Last second with GRUDG ahead at {lastAhead:F0} ft; one second past it {oneSecondPast:F1} ft of descent");
        Assert.InRange(lastAhead, 20000 - FlightPhysics.AltitudeSnapFt, 23000 + LevelToleranceFt);
        Assert.InRange(aircraft.Altitude, 20000 - FlightPhysics.AltitudeSnapFt - oneSecondPast, 23000 + LevelToleranceFt);
    }

    /// <summary>
    /// At FL260 under a descend via, a later transmission's "DCTF GRUDG/A220" is the controller's crossing: the aircraft
    /// crosses GRUDG at or above FL220 and levels there past it, never carrying on down toward 16,000.
    /// </summary>
    [Fact]
    public void DescendViaThenOwnTransmissionDctf_LevelsAtTheCrossingPastGrudg()
    {
        if (BuildOakesHarness(_output, starAltitude: 26000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, aircraft.Targets.NavigationRoute[0].Name, _ => { });
        SendOk(harness, "JARR OAKES3");
        SendOk(harness, "DVIA");
        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "DCTF GRUDG/A220");
        TickUntilPassed(harness, "GRUDG", _ => { });

        double lowest = aircraft.Altitude;
        for (int t = 0; t < 180; t++)
        {
            harness.Engine.TickOneSecond();
            lowest = Math.Min(lowest, aircraft.Altitude);
        }

        _output.WriteLine($"180 s past GRUDG: altitude {aircraft.Altitude:F0}, lowest {lowest:F0}, target {aircraft.Targets.TargetAltitude}");
        Assert.True(lowest >= 22000 - LevelToleranceFt, $"Past GRUDG the aircraft descended to {lowest:F0} ft, below its FL220 crossing");
    }

    /// <summary>Each route fix with the restrictions in force, in order, for comparing a route across a clearance.</summary>
    private static string RouteRestrictions(AircraftState aircraft) => string.Join(" ", aircraft.Targets.NavigationRoute.Select(DescribeFix));

    private static string DescribeFix(NavigationTarget fix)
    {
        CifpAltitudeRestriction? altitude = fix.AltitudeRestriction;
        return $"{fix.Name}({altitude?.Type} {altitude?.Altitude1Ft}/{altitude?.Altitude2Ft},{fix.SpeedRestriction?.SpeedKts})";
    }

    private static NavigationTarget FixNamed(AircraftState aircraft, string name) => aircraft.Targets.NavigationRoute.First(t => t.Name == name);

    /// <summary>"DM 110" from the harness's 16,000, ticked until level at 11,000.</summary>
    private void LevelAt11000(Harness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DM 110");
        for (int t = 0; (t < 600) && (Math.Abs(aircraft.Altitude - 11000) > FlightPhysics.AltitudeSnapFt); t++)
        {
            harness.Engine.TickOneSecond();
        }

        Assert.InRange(aircraft.Altitude, 11000 - FlightPhysics.AltitudeSnapFt, 11000 + FlightPhysics.AltitudeSnapFt);
    }

    /// <summary>
    /// Ticks until GRUDG sequences; returns the highest target altitude, the second it was first set, and the highest
    /// altitude. Tracked from the first tick, while GRUDG is still ahead: the target standing before the first tick is the
    /// planner's from before the last command, and on the tick GRUDG sequences the planners already fly the fixes past it.
    /// </summary>
    private (double HighestTarget, int HighestTargetSecond, double HighestAltitude) HighestUntilGrudg(Harness harness)
    {
        AircraftState aircraft = harness.Aircraft;
        double highestTarget = 0;
        int highestTargetSecond = 0;
        double highestAltitude = aircraft.Altitude;
        TickUntilPassed(
            harness,
            "GRUDG",
            t =>
            {
                if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals("GRUDG", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if ((aircraft.Targets.TargetAltitude ?? 0) > highestTarget)
                {
                    highestTarget = aircraft.Targets.TargetAltitude ?? 0;
                    highestTargetSecond = t;
                }

                highestAltitude = Math.Max(highestAltitude, aircraft.Altitude);
            }
        );

        return (highestTarget, highestTargetSecond, highestAltitude);
    }

    /// <summary>
    /// "CFIX X 110, DVIA" restates the crossing with the via clearance. The overlay runs either way — with an earlier
    /// descend via it rewrites the STAR's published values, without one it is what puts them there — and the crossing this
    /// transmission restated survives both the overlay's and the supersede pass's stamp checks.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescendVia_SameTransmissionCrossFix_KeepsTheRestriction_NoRevert(bool earlierDescendVia)
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        if (earlierDescendVia)
        {
            SendOk(harness, "DVIA");
            SendOk(harness, "DM 150");
        }

        string fixName = FirstFixWithPublishedAltitude(aircraft);
        SendOk(harness, $"CFIX {fixName} 110, DVIA");

        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000) { Source = CifpAltitudeRestrictionSource.Atc },
            fix.AltitudeRestriction
        );
        AssertNoReverts(fix);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    /// <summary>
    /// "DVIA, CFIX X A160" restates the crossing with the via clearance just as the reverse order does (owner ruling,
    /// YAAT-956: a same-line CFIX, either order, keeps its restriction and gets no reverts).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescendVia_ThenSameTransmissionCrossFix_KeepsTheRestriction_NoRevert(bool starAlreadyActive)
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        if (starAlreadyActive)
        {
            SendOk(harness, "DVIA");
            SendOk(harness, "DM 150");
        }

        string fixName = FirstFixWithPublishedAltitude(aircraft);
        SendOk(harness, $"DVIA, CFIX {fixName} A160");

        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 16000) { Source = CifpAltitudeRestrictionSource.Atc },
            fix.AltitudeRestriction
        );
        AssertNoReverts(fix);
        Assert.True(aircraft.Procedure.StarViaMode);
        Assert.Null(aircraft.Targets.AssignedAltitude);
    }

    /// <summary>
    /// "Proceed direct X, cross X at or above FL200, then descend via" (7110.65 4-5-7.h.5; AIM 5-4-1.a.2 Example 5):
    /// the constrained direct-to restates the crossing with the via clearance, on a fix the STAR itself leaves
    /// unconstrained, so the DVIA neither drops it nor leaves a revert on it.
    /// </summary>
    [Fact]
    public void DescendVia_SameTransmissionConstrainedDirect_KeepsTheCrossing_NoRevert()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string fixName = FirstStarFixWithoutPublishedAltitude();
        SendOk(harness, "DM 150");
        SendOk(harness, $"DCTF {fixName}/A200, DVIA");

        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 20000) { Source = CifpAltitudeRestrictionSource.Atc },
            fix.AltitudeRestriction
        );
        AssertNoReverts(fix);
        Assert.True(aircraft.Procedure.StarViaMode);
    }

    /// <summary>
    /// A queued "AT A CFIX X 110, DVIA" restored from a snapshot before it fires still runs as one transmission: the
    /// rebuilt block stamps and reads the id it was queued with, so X keeps the restated crossing with no reverts.
    /// </summary>
    [Fact]
    public void QueuedSameTransmissionCrossFix_RestoredFromSnapshot_KeepsTheRestriction_NoRevert()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        string triggerFix = harness.Aircraft.Targets.NavigationRoute[1].Name;
        string fixName = harness.Aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, $"AT {triggerFix} CFIX {fixName} 110, DVIA");
        Assert.False(harness.Aircraft.Procedure.StarViaMode);

        StateSnapshotDto snapshot = harness.Engine.CaptureSnapshot();
        SimulationEngine restored = NewEngine();
        restored.RestoreFromSnapshot(snapshot);
        AircraftState aircraft = Assert.IsType<AircraftState>(restored.FindAircraft(harness.Aircraft.Callsign));

        for (int t = 0; (t < 1800) && !aircraft.Procedure.StarViaMode; t++)
        {
            restored.TickOneSecond();
        }

        Assert.True(aircraft.Procedure.StarViaMode, $"The queued block at {triggerFix} never fired");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000) { Source = CifpAltitudeRestrictionSource.Atc },
            fix.AltitudeRestriction
        );
        AssertNoReverts(fix);
    }

    /// <summary>
    /// A DVIA that supersedes a CFIX speed hands the speed back to the procedure and the default schedule
    /// (7110.65 5-7-1.d; AIM 4-4-12.f.5): the CFIX's assigned speed no longer stands.
    /// </summary>
    [Fact]
    public void DescendVia_AfterCrossFixSpeed_ClearsTheAssignedSpeed()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.AltitudeRestriction is not null);
        CifpSpeedRestriction? publishedSpeed = fix.SpeedRestriction;

        SendOk(harness, "DM 150");
        SendOk(harness, $"CFIX {fix.Name} 110 230");
        Assert.Equal(230, aircraft.Targets.AssignedSpeed);

        SendOk(harness, "DVIA");

        Assert.Equal(publishedSpeed, fix.SpeedRestriction);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.NotEqual(230, aircraft.Targets.TargetSpeed);
    }

    /// <summary>
    /// A speed still required is restated with the descend via (7110.65 5-7-1.c): "SPD 250, DVIA" after an earlier
    /// "CFIX X 110 250" keeps 250 assigned, though the DVIA supersedes the crossing speed at X.
    /// </summary>
    [Fact]
    public void DescendVia_SameTransmissionSpeed_KeepsTheRestatedSpeed()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.AltitudeRestriction is not null);
        var crossingSpeed = new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow);
        Assert.True(fix.SpeedRestriction != crossingSpeed, $"{fix.Name} publishes 250 knots, so the DVIA would not supersede the crossing speed");

        SendOk(harness, "DM 150");
        SendOk(harness, $"CFIX {fix.Name} 110 250");
        SendOk(harness, "SPD 250, DVIA");

        Assert.NotEqual(crossingSpeed, fix.SpeedRestriction);
        Assert.Equal(250, aircraft.Targets.AssignedSpeed);
    }

    /// <summary>
    /// A lateral command that drops the queued DVIA out of "AT A DM 080, CFIX X 110, DVIA" leaves a lone CFIX behind: it
    /// no longer restates the crossing with a via clearance, so it fires with its reverts, as "AT A DM 080, CFIX X 110"
    /// would. The DM rides in the queued block, so the aircraft is still level above the crossing when A fires.
    /// </summary>
    [Fact]
    public void QueuedSameTransmissionCrossFix_DescendViaSplitOut_KeepsTheReverts()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string triggerFix = aircraft.Targets.NavigationRoute[1].Name;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, $"AT {triggerFix} DM 080, CFIX {fixName} 110, DVIA");
        SendOk(harness, $"DCTF {triggerFix}");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesDescendVia);

        NavigationTarget fix = TickUntilCrossingSet(harness.Engine, aircraft, fixName, 11000);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Equal(8000, fix.RevertAssignedAltitude);
    }

    /// <summary>
    /// "AT A DM 080, CFIX X 110; DVIA" queues the CFIX and the DVIA as two blocks of one transmission. A later command that
    /// drops the DVIA block (a lateral DCTF, or DELAT) leaves the CFIX block a lone CFIX: it fires with its reverts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedSameTransmissionCrossFix_DescendViaBlockDropped_KeepsTheReverts(bool deleteConditional)
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string triggerFix = aircraft.Targets.NavigationRoute[1].Name;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, $"AT {triggerFix} DM 080, CFIX {fixName} 110; DVIA");
        Assert.Contains(aircraft.Queue.Blocks, CarriesDescendVia);
        SendOk(harness, deleteConditional ? "DELAT 2" : $"DCTF {triggerFix}");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesDescendVia);

        NavigationTarget fix = TickUntilCrossingSet(harness.Engine, aircraft, fixName, 11000);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Equal(8000, fix.RevertAssignedAltitude);
    }

    /// <summary>
    /// "CFIX X A110; AT A DVIA" applies the CFIX at once and queues the DVIA. Until that DVIA fires the crossing is a lone
    /// CFIX with its reverts, so dropping the DVIA (DELAT) leaves the reverts the CFIX captured in place (YAAT-984).
    /// </summary>
    [Fact]
    public void AppliedCrossFix_ThenQueuedDescendViaDropped_KeepsTheReverts()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        string triggerFix = aircraft.Targets.NavigationRoute[1].Name;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, "DM 080");
        SendOk(harness, $"CFIX {fixName} A110; AT {triggerFix} DVIA");
        Assert.Contains(aircraft.Queue.Blocks, CarriesDescendVia);
        SendOk(harness, "DELAT 2");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesDescendVia);

        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.False(aircraft.Procedure.StarViaMode);
        Assert.Equal(8000, fix.RevertAssignedAltitude);
    }

    /// <summary>
    /// "CFIX X A110; AT A DVIA" on an aircraft with no active or filed STAR: the DVIA is refused when it fires at A, before
    /// X, so the CFIX was never restated with a via clearance and keeps its reverts.
    /// </summary>
    [Fact]
    public void AppliedCrossFix_ThenDescendViaRefusedAtFireTime_KeepsTheReverts()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        aircraft.Procedure.ActiveStarId = null;
        aircraft.FlightPlan.Route = string.Join(
            ' ',
            aircraft
                .FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.Contains("OAKES", StringComparison.OrdinalIgnoreCase))
        );
        Assert.Null(FiledProcedureLookup.FindStar(NavigationDatabase.Instance, aircraft.FlightPlan.Destination, aircraft.FlightPlan.Route));

        string triggerFix = aircraft.Targets.NavigationRoute[1].Name;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, "DM 080");
        SendOk(harness, $"CFIX {fixName} A110; AT {triggerFix} DVIA");
        CommandBlock descendVia = Assert.Single(aircraft.Queue.Blocks, CarriesDescendVia);
        for (int t = 0; (t < 1800) && !descendVia.Commands.TrueForAll(c => c.IsComplete); t++)
        {
            harness.Engine.TickOneSecond();
        }

        Assert.True(descendVia.Commands.TrueForAll(c => c.IsComplete), $"The DVIA at {triggerFix} never fired");
        Assert.False(aircraft.Procedure.StarViaMode);
        NavigationTarget fix = Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == fixName);
        Assert.Equal(8000, fix.RevertAssignedAltitude);
    }

    /// <summary>
    /// A DVIA from an earlier transmission is in force when "CFIX X 110; AT A DVIA" is issued: this transmission's DVIA
    /// has not fired, so the CFIX assigns 11,000 and keeps the reverts (here the speed assigned before it), and they
    /// survive that DVIA being dropped.
    /// </summary>
    [Fact]
    public void AppliedCrossFix_UnderEarlierDescendVia_ThenQueuedDescendViaDropped_KeepsTheReverts()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DVIA");
        SendOk(harness, "SPD 250");
        Assert.True(aircraft.Procedure.StarViaMode);

        string triggerFix = aircraft.Targets.NavigationRoute[1].Name;
        string fixName = aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, $"CFIX {fixName} 110 230; AT {triggerFix} DVIA");
        Assert.Equal(11000, aircraft.Targets.AssignedAltitude);
        SendOk(harness, "DELAT 1");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesDescendVia);

        NavigationTarget fix = aircraft.Targets.NavigationRoute.First(t => t.Name == fixName);
        Assert.Equal(250, fix.RevertAssignedSpeed);
    }

    /// <summary>
    /// A queued CFIX restored from a snapshot written before blocks carried a transmission id fires with none, so the
    /// fix it stamps carries no crossing transmission id rather than id 0.
    /// </summary>
    [Fact]
    public void QueuedCrossFix_RestoredWithoutTransmissionId_StampsNoTransmission()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        string triggerFix = harness.Aircraft.Targets.NavigationRoute[1].Name;
        string fixName = harness.Aircraft.Targets.NavigationRoute[3].Name;
        SendOk(harness, $"AT {triggerFix} CFIX {fixName} 110");

        StateSnapshotDto snapshot = harness.Engine.CaptureSnapshot();
        List<CommandBlockDto> blocks = snapshot.Aircraft.Single(a => a.Callsign == harness.Aircraft.Callsign).Queue.Blocks;
        Assert.Contains(blocks, b => b.TransmissionId != 0);
        for (int i = 0; i < blocks.Count; i++)
        {
            JsonObject block = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(blocks[i]));
            block.Remove(nameof(CommandBlockDto.TransmissionId));
            blocks[i] = Assert.IsType<CommandBlockDto>(block.Deserialize<CommandBlockDto>());
        }

        SimulationEngine restored = NewEngine();
        restored.RestoreFromSnapshot(snapshot);
        AircraftState aircraft = Assert.IsType<AircraftState>(restored.FindAircraft(harness.Aircraft.Callsign));

        NavigationTarget fix = TickUntilCrossingSet(restored, aircraft, fixName, 11000);
        Assert.Null(fix.CrossingTransmissionId);
    }

    [Fact]
    public void DescendVia_AfterDescendMaintain_NeverClimbsBack()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DM 110");
        for (int t = 0; (t < 600) && (aircraft.Altitude > 11000 + FlightPhysics.AltitudeSnapFt); t++)
        {
            harness.Engine.TickOneSecond();
        }

        Assert.InRange(aircraft.Altitude, 0, 11000 + FlightPhysics.AltitudeSnapFt);

        SendOk(harness, "DVIA");
        for (int t = 0; t < 300; t++)
        {
            harness.Engine.TickOneSecond();
            Assert.True(
                aircraft.Altitude <= 11000 + FlightPhysics.AltitudeSnapFt,
                $"{t + 1} s after DVIA the aircraft is at {aircraft.Altitude:F0} ft, above the 11,000 it was cleared down to"
            );
        }

        _output.WriteLine($"300 s after DVIA: altitude {aircraft.Altitude:F0}, target {aircraft.Targets.TargetAltitude}");
    }

    /// <summary>
    /// A DVIA ends a climb via still in force (its top altitude and "except maintain"), so the STAR's restrictions drive
    /// the descent instead of the climb via holding the aircraft at its top altitude.
    /// </summary>
    [Fact]
    public void DescendVia_AfterClimbVia_ClearsTheClimbViaAndDescendsViaTheStar()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        aircraft.Procedure.ClimbViaTargetFt = 16000;
        aircraft.Procedure.SidViaCeiling = 16000;

        SendOk(harness, "DVIA");
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        Assert.Null(aircraft.Procedure.SidViaCeiling);

        for (int t = 0; (t < 900) && (aircraft.Altitude > 15000); t++)
        {
            harness.Engine.TickOneSecond();
        }

        _output.WriteLine($"After DVIA: altitude {aircraft.Altitude:F0}, target {aircraft.Targets.TargetAltitude}");
        Assert.True(aircraft.Altitude <= 15000, $"the leftover climb via held the aircraft at {aircraft.Altitude:F0} ft");
    }

    /// <summary>
    /// A DVIA ends a SID via still in force too: with <see cref="AircraftProcedure.SidViaMode"/> left on, descent planning
    /// stays off (FlightPhysics.UpdateDescentPlanning) and the aircraft never follows the STAR down (YAAT-980).
    /// </summary>
    [Fact]
    public void DescendVia_InSidViaMode_ClearsSidViaModeAndPlansTheDescent()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        aircraft.Procedure.SidViaMode = true;
        double start = aircraft.Altitude;

        SendOk(harness, "DVIA");

        Assert.False(aircraft.Procedure.SidViaMode, "the DVIA left the SID via mode on, which switches descent planning off");
        NavigationTarget next = aircraft.Targets.NavigationRoute.First(t => t.AltitudeRestriction is not null);
        CifpAltitudeRestriction published = Assert.IsType<CifpAltitudeRestriction>(next.AltitudeRestriction);
        Assert.True(
            published.Type is CifpAltitudeRestrictionType.AtOrBelow or CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.Between,
            $"setup: {next.Name} publishes {published.Type} {published.Altitude1Ft}, not a ceiling to descend to"
        );
        int bound =
            published.Type == CifpAltitudeRestrictionType.Between
                ? Math.Min(published.Altitude1Ft, published.Altitude2Ft ?? published.Altitude1Ft)
                : published.Altitude1Ft;

        double? target = null;
        for (int t = 0; t <= 30; t++)
        {
            if (t > 0)
            {
                harness.Engine.TickOneSecond();
            }

            target = aircraft.Targets.TargetAltitude;
            if (target is { } v && (v <= bound))
            {
                break;
            }
        }

        _output.WriteLine(
            $"after DVIA {next.Name} publishes {published.Type} {bound}; target {target}, altitude {aircraft.Altitude:F0} from {start:F0}"
        );
        Assert.NotNull(target);
        Assert.True(target <= bound, $"the target altitude is {target}, above the {bound} ft published at {next.Name}");
        for (int t = 0; (t < 120) && (aircraft.Altitude >= start); t++)
        {
            harness.Engine.TickOneSecond();
        }

        Assert.True(aircraft.Altitude < start, $"the aircraft is still at {aircraft.Altitude:F0} ft, level with the {start:F0} ft it held");
    }

    [Fact]
    public void DescendViaExceptMaintain_DescentStopsAtTheFloor()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DM 150");
        SendOk(harness, "DVIA 140");
        Assert.Null(aircraft.Targets.AssignedAltitude);
        Assert.Equal(14000, aircraft.Procedure.StarViaFloor);
        int lowestPublished = aircraft
            .Targets.NavigationRoute.Where(t => t.AltitudeRestriction is not null)
            .Min(t => t.AltitudeRestriction!.Altitude1Ft);
        Assert.True(lowestPublished < 14000, $"OAKES3 publishes nothing below 14,000 ({lowestPublished}), so the floor is untested");

        double lowest = aircraft.Altitude;
        for (int t = 0; (t < 1500) && (aircraft.Targets.NavigationRoute.Count > 0); t++)
        {
            harness.Engine.TickOneSecond();
            lowest = Math.Min(lowest, aircraft.Altitude);
        }

        _output.WriteLine($"After DVIA 140: altitude {aircraft.Altitude:F0}, lowest {lowest:F0}, lowest published {lowestPublished}");
        Assert.True(lowest >= 14000 - FlightPhysics.AltitudeSnapFt, $"The descent went to {lowest:F0} ft, below the 14,000 floor");
        Assert.Null(aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void TransmissionId_SurvivesSnapshotRoundTrip()
    {
        if (BuildOakesHarness() is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        NavigationTarget fix = aircraft.Targets.NavigationRoute[1];
        SendOk(harness, "DM 150");
        SendOk(harness, $"CFIX {fix.Name} 110");
        int stamped = Assert.IsType<int>(fix.CrossingTransmissionId);
        int counter = aircraft.Queue.LastTransmissionId;
        Assert.Equal(counter, stamped);
        Assert.Equal(2, counter); // every transmission takes an id: the DM took 1, the CFIX 2

        NavigationTargetDto targetDto = RoundTrip(fix.ToSnapshot());
        CommandQueueDto queueDto = RoundTrip(aircraft.Queue.ToSnapshot());

        Assert.Equal(stamped, NavigationTarget.FromSnapshot(targetDto).CrossingTransmissionId);
        Assert.Equal(counter, CommandQueue.FromSnapshot(queueDto).LastTransmissionId);

        Assert.DoesNotContain(nameof(AircraftProcedureDto.StarViaTransmissionId), JsonSerializer.Serialize(aircraft.Procedure.ToSnapshot()));
        SendOk(harness, $"CFIX {fix.Name} 110, DVIA");
        Assert.Equal(3, aircraft.Procedure.StarViaTransmissionId);
        aircraft.Procedure.ClimbViaTransmissionId = 1;

        var procedure = AircraftProcedure.FromSnapshot(RoundTrip(aircraft.Procedure.ToSnapshot()));
        Assert.Equal(3, procedure.StarViaTransmissionId);
        Assert.Equal(1, procedure.ClimbViaTransmissionId);
    }

    private static T RoundTrip<T>(T dto) => Assert.IsType<T>(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(dto)));

    private static bool CarriesDescendVia(CommandBlock block) => block.ParsedCommands?.Exists(c => c is DescendViaCommand) is true;

    /// <summary>Ticks until a queued CFIX stamps At <paramref name="altitudeFt"/> on <paramref name="fixName"/> and returns that fix.</summary>
    private static NavigationTarget TickUntilCrossingSet(SimulationEngine engine, AircraftState aircraft, string fixName, int altitudeFt)
    {
        var crossing = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, altitudeFt) { Source = CifpAltitudeRestrictionSource.Atc };
        for (int t = 0; t < 1800; t++)
        {
            if (aircraft.Targets.NavigationRoute.Find(f => f.Name == fixName) is { } fix && (fix.AltitudeRestriction == crossing))
            {
                return fix;
            }

            engine.TickOneSecond();
        }

        throw new InvalidOperationException($"The queued CFIX never set At {altitudeFt:N0} on {fixName} within 1800 s");
    }

    private static void AssertNoReverts(NavigationTarget fix)
    {
        Assert.Null(fix.RevertAltitude);
        Assert.Null(fix.RevertAssignedAltitude);
        Assert.Null(fix.RevertSpeed);
        Assert.Null(fix.RevertAssignedSpeed);
    }

    /// <summary>The OAKES3 fixes that publish an altitude restriction, read from the CIFP legs.</summary>
    private static HashSet<string> PublishedAltitudeFixNames()
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        List<CifpLeg> legs =
        [
            .. star.EnrouteTransitions.Values.SelectMany(t => t.Legs),
            .. star.CommonLegs,
            .. star.RunwayTransitions.Values.SelectMany(t => t.Legs),
        ];
        return DepartureClearanceHandler
            .ResolveLegsToTargets(legs)
            .Where(t => t.AltitudeRestriction is not null)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The first route fix OAKES3 publishes an altitude restriction for.</summary>
    private static string FirstFixWithPublishedAltitude(AircraftState aircraft)
    {
        HashSet<string> published = PublishedAltitudeFixNames();
        return aircraft.Targets.NavigationRoute.First(t => published.Contains(t.Name)).Name;
    }

    /// <summary>The first two route fixes OAKES3 publishes an altitude restriction for, in route order.</summary>
    private static (string First, string Second) FirstTwoPublishedAltitudeFixes(AircraftState aircraft)
    {
        HashSet<string> published = PublishedAltitudeFixNames();
        List<string> names = [.. aircraft.Targets.NavigationRoute.Select(t => t.Name).Where(published.Contains).Take(2)];
        Assert.Equal(2, names.Count);
        return (names[0], names[1]);
    }

    /// <summary>The first OAKES3 fix, in CIFP leg order, on which no leg publishes an altitude restriction.</summary>
    private static string FirstStarFixWithoutPublishedAltitude()
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        List<CifpLeg> legs =
        [
            .. star.EnrouteTransitions.Values.SelectMany(t => t.Legs),
            .. star.CommonLegs,
            .. star.RunwayTransitions.Values.SelectMany(t => t.Legs),
        ];
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);
        var constrained = targets.Where(t => t.AltitudeRestriction is not null).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return targets.First(t => !constrained.Contains(t.Name)).Name;
    }

    private static SimulationEngine NewEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "dvia-supersedes-oakes",
                ScenarioName = "DVIA supersedes earlier altitude on OAKES3",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = Airport,
            },
        };

    private void TickUntilPassed(Harness harness, string fixName, Action<int> eachSecond)
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

    private void SendOk(Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private Harness? BuildOakesHarness() => BuildOakesHarness(_output, 16000);

    /// <summary>A generated jet established on Oakland's OAKES3 STAR, level at 16,000 with the STAR in its filed route.</summary>
    internal static Harness? BuildOakesHarness(ITestOutputHelper output, int starAltitude)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(Airport);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        string entryFix = DepartureClearanceHandler.ResolveLegsToTargets([.. star.CommonLegs])[0].Name;
        string runwayKey = star.RunwayTransitions.Keys.First();
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = entryFix,
            StarId = "OAKES3",
            StarRunway = runwayKey.StartsWith("RW", StringComparison.Ordinal) ? runwayKey[2..] : runwayKey,
            DescendVia = false,
            StarAltitude = starAltitude,
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
        aircraft.Ground.Layout = layout;

        SimulationEngine engine = NewEngine();
        engine.World.AddAircraft(aircraft);

        string route = string.Join(
            " ",
            aircraft.Targets.NavigationRoute.Select(t => $"{t.Name}{(t.AltitudeRestriction is { } r ? $"({r.Type} {r.Altitude1Ft})" : "")}")
        );
        output.WriteLine($"Spawned {aircraft.Callsign} {aircraft.AircraftType} at {aircraft.Altitude:F0}, route {route}");
        return new Harness(engine, aircraft);
    }

    internal sealed record Harness(SimulationEngine Engine, AircraftState Aircraft);
}
