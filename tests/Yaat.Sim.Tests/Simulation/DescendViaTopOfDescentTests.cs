using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2717 (GitHub #1395): a descend via issued well before top of descent started the aircraft down at once on an
/// extremely shallow path, because the descend-via step planner spread the whole drop over the whole distance to the
/// restriction with no top of descent. A descend via is a pilot's-discretion descent (AIM 5-4-1.a.2(a)1): the pilot stays at
/// cruise until the top of descent, then descends on a normal profile to meet the restriction. The top of descent pinned
/// here is the 3:1 rule, 300 ft per NM to lose, plus 1 NM per 10 kt of deceleration the descent needs (250 kt by 10,000 ft
/// and the published STAR speeds), taken over every in-force ceiling ahead; a controller's crossing ceiling ("cross X at
/// Y", "at or below Y") uses the same top of descent, an "at or above" never triggers one, and a plain descend and maintain
/// stays prompt. Real KOAK navdata: a B738 at FL350 on the real OAKES3 STAR, whose first restriction is OAKES between FL320
/// and FL240, which a descend via targets at its bottom (FL240).
/// </summary>
public class DescendViaTopOfDescentTests
{
    private const double CruiseFt = 35000;
    private const string FirstFix = "OAKES";
    private const double OakesTargetFt = 24000;
    private const double OakesTopFt = 32000;

    /// <summary>The 3:1 rule: 300 ft to lose per NM.</summary>
    private const double TodFtPerNm = 300;

    /// <summary>One NM of top of descent per 10 kt of deceleration.</summary>
    private const double DecelKtsPerNm = 10;

    /// <summary>The 14 CFR 91.117(a) limit below 10,000 ft.</summary>
    private const double SpeedLimitBelow10kKts = 250;

    /// <summary>Slack over the top of descent for the sequencing threshold and a planning buffer before the descent.</summary>
    private const double TodSlackFactor = 1.25;

    /// <summary>The shallowest average path a normal idle descent from cruise flies, in degrees.</summary>
    private const double MinNormalPathDeg = 2.5;

    /// <summary>How far the IAS may drift from the speed it holds.</summary>
    private const double HoldToleranceKts = 1.0;

    private readonly ITestOutputHelper _output;

    public DescendViaTopOfDescentTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// DVIA 150 NM before OAKES: the aircraft stays at FL350 until within 1.25 times its top of descent (the latest over the
    /// in-force ceilings ahead, with the deceleration allowance), then descends at 2.5° or steeper on average and crosses
    /// OAKES inside its window.
    /// </summary>
    [Fact]
    public void DescendVia_IssuedWellBeforeTopOfDescent_StaysLevelUntilTopOfDescent()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DVIA");
        double todNm = TopOfDescentBeforeFixNm(aircraft, FirstFix, ceilingsOnly: null);
        Flight flight = Fly(engine, aircraft, FirstFix);
        DescentProfile profile = flight.ProfileTo(FirstFix);

        Assert.True(
            profile.DescentStartNm <= todNm * TodSlackFactor,
            $"Descent began {profile.DescentStartNm:F1} NM before {FirstFix}; the top of descent is {todNm:F1} NM "
                + $"(at most {todNm * TodSlackFactor:F1} with slack). Average path {profile.AveragePathDeg:F2}°, "
                + $"average {profile.AverageVsFpm:F0} fpm."
        );
        Assert.True(
            profile.AveragePathDeg >= MinNormalPathDeg,
            $"The descent to {FirstFix} averaged {profile.AveragePathDeg:F2}° ({profile.AverageVsFpm:F0} fpm), shallower "
                + $"than a normal {MinNormalPathDeg}° profile"
        );
        Assert.InRange(flight.AltitudeAtSequencing(FirstFix), OakesTargetFt - FlightPhysics.AltitudeSnapFt, OakesTopFt);
    }

    /// <summary>
    /// DVIA just outside the 3:1 top of descent (38 NM before OAKES, top of descent 33 NM): the aircraft descends on a
    /// normal profile and crosses OAKES inside its window. Guards the fix against delaying the descent past the point
    /// where the restriction can still be met.
    /// </summary>
    [Fact]
    public void DescendVia_IssuedNearTopOfDescent_MeetsTheFirstRestriction()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(38);
        SendOk(engine, aircraft, "DVIA");
        Flight flight = Fly(engine, aircraft, FirstFix);
        DescentProfile profile = flight.ProfileTo(FirstFix);

        Assert.True(profile.AveragePathDeg >= MinNormalPathDeg, $"The descent averaged {profile.AveragePathDeg:F2}°");
        Assert.InRange(flight.AltitudeAtSequencing(FirstFix), OakesTargetFt - FlightPhysics.AltitudeSnapFt, OakesTopFt);
    }

    /// <summary>
    /// DVIA 150 NM before OAKES, flown down the STAR: once the descent begins there is no level segment at FL320, the top
    /// of the OAKES window (the planner looks past the window to the controlling ceiling), the aircraft never levels above
    /// the window's floor before OAKES, every later ceiling is met where its fix sequences, and the descent itself is flown
    /// at the 3:1 gradient or steeper.
    /// </summary>
    [Fact]
    public void DescendVia_IssuedWellBeforeTopOfDescent_FliesTheGradientPastTheWindowTop()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DVIA");
        List<RouteCeiling> ceilings = Ceilings(aircraft);
        Flight flight = Fly(engine, aircraft, stopAfterFix: ceilings[^1].Name);
        DescentProfile profile = flight.ProfileTo(FirstFix);

        int start = flight.DescentStartIndex;
        int oakes = flight.SequencedIndex(FirstFix);
        int levelAtTop = flight.Samples.Skip(start).Take(oakes - start).Count(s => (s.VsFpm == 0) && (Math.Abs(s.AltFt - OakesTopFt) < 300));
        Assert.True(levelAtTop == 0, $"Level for {levelAtTop} s at FL320, the top of the {FirstFix} window");
        int longestLevel = flight.LongestLevelRun(start, oakes, aboveFt: OakesTargetFt + FlightPhysics.AltitudeSnapFt);
        Assert.True(longestLevel < 20, $"Level for {longestLevel} s above {FirstFix}'s floor before {FirstFix}");

        foreach (RouteCeiling ceiling in ceilings.Where(c => c.CeilingFt < CruiseFt))
        {
            double alt = flight.AltitudeAtSequencing(ceiling.Name);
            Assert.True(
                alt <= ceiling.CeilingFt + FlightPhysics.AltitudeSnapFt,
                $"{ceiling.Name} sequenced at {alt:F0}, above its {ceiling.CeilingFt:F0} ceiling"
            );
        }

        double gradient = flight.DescendingGradientFtPerNm(start, flight.Samples.Count);
        _output.WriteLine($"Descent began {profile.DescentStartNm:F1} NM before {FirstFix}; descending gradient {gradient:F0} ft/NM");
        Assert.True(gradient >= 0.9 * TodFtPerNm, $"The descent was flown at {gradient:F0} ft/NM, shallower than 3:1");
    }

    /// <summary>
    /// "Cross OAKES at FL240" 150 NM out, off a via: the crossing ceiling uses the descend-via top of descent. The aircraft
    /// holds FL350 until about 11,000 / 300 NM plus the deceleration allowance before OAKES, then descends at about
    /// 300 ft per NM and crosses OAKES at FL240.
    /// </summary>
    [Fact]
    public void CrossFixAt_IssuedWellBeforeTopOfDescent_StaysLevelUntilTopOfDescent()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "CFIX OAKES 240");
        double todNm = TopOfDescentBeforeFixNm(aircraft, FirstFix, ceilingsOnly: FirstFix);
        Flight flight = Fly(engine, aircraft, FirstFix);
        DescentProfile profile = flight.ProfileTo(FirstFix);
        double gradient = flight.DescendingGradientFtPerNm(flight.DescentStartIndex, flight.SequencedIndex(FirstFix));
        _output.WriteLine($"Top of descent {todNm:F1} NM; descent began {profile.DescentStartNm:F1} NM; gradient {gradient:F0} ft/NM");

        Assert.InRange(profile.DescentStartNm, todNm * 0.9, todNm * TodSlackFactor);
        Assert.InRange(gradient, 0.9 * TodFtPerNm, 1.1 * TodFtPerNm);
        Assert.InRange(flight.AltitudeAtSequencing(FirstFix), OakesTargetFt - FlightPhysics.AltitudeSnapFt, OakesTargetFt + 100);
    }

    /// <summary>
    /// "Cross OAKES at FL240" 150 NM out, level at FL280 at 305 KIAS (above OAKES's 280, so no speed-up waits): level in the
    /// crossing's top-of-descent hold the descent schedule does not move the speed (YAAT-2727), and no 250 kt lead fires
    /// before the descent.
    /// </summary>
    [Fact]
    public void CrossFixAt_LevelBeforeTopOfDescent_HoldsThePresentSpeed()
    {
        const double heldKts = 305;
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, descendVia: false, starAltitude: 28000);
        aircraft.IndicatedAirspeed = heldKts;
        aircraft.Targets.TargetSpeed = null;
        SendOk(engine, aircraft, "CFIX OAKES 240");
        engine.TickOneSecond();
        Assert.Equal(OakesTargetFt, aircraft.Targets.TargetAltitude);
        Assert.Equal(0, aircraft.Targets.PlannedVerticalRate);
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double scheduleKts = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, aircraft.Altitude, aircraft.Targets.TargetAltitude);
        Assert.True(Math.Abs(scheduleKts - heldKts) > 10, $"setup: the schedule's {scheduleKts:F1} is the held speed");
        int level = 1;
        for (int t = 2; (t <= 3600) && (level == t - 1); t++)
        {
            engine.TickOneSecond();
            if (aircraft.VerticalSpeed != 0)
            {
                break;
            }

            level = t;
            Assert.InRange(aircraft.IndicatedAirspeed, heldKts - HoldToleranceKts, heldKts + HoldToleranceKts);
            Assert.True(aircraft.Targets.TargetSpeed is null or heldKts, $"+{t} s: target {aircraft.Targets.TargetSpeed} while level");
        }

        Assert.True(level > 120, $"setup: level for {level} s");
    }

    /// <summary>
    /// "Cross OAKES at or above FL240" 150 NM out, off a via: an at-or-above bounds a descent but never triggers one, so the
    /// aircraft is still at FL350 when OAKES sequences, and never below FL240 before it. A floor below the altitude held is
    /// not an altitude to maintain: no assigned altitude is written, and the aircraft keeps FL350 past OAKES.
    /// </summary>
    [Fact]
    public void CrossFixAtOrAbove_NeverTriggersADescentBeforeTheFix()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        Assert.Null(aircraft.Targets.AssignedAltitude);
        SendOk(engine, aircraft, "CFIX OAKES A240");
        // The floor lies below FL350, so it is a bound only: the spawn's absent assigned altitude stays absent
        Assert.Null(aircraft.Targets.AssignedAltitude);
        Flight flight = Fly(engine, aircraft, FirstFix);
        int oakes = flight.SequencedIndex(FirstFix);
        double lowest = flight.Samples.Take(oakes).Min(s => s.AltFt);

        Assert.True(lowest >= CruiseFt - FlightPhysics.AltitudeSnapFt, $"Descended to {lowest:F0} before {FirstFix} on an at-or-above");
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            Assert.True(
                aircraft.Altitude >= CruiseFt - FlightPhysics.AltitudeSnapFt,
                $"+{t} s past {FirstFix}: left FL350 for {aircraft.Altitude:F0}"
            );
        }
    }

    /// <summary>A plain descend and maintain 150 NM out still leaves FL350 at once at the type's profile rate.</summary>
    [Fact]
    public void DescendAndMaintain_LeavesAtOnceAtTheProfileRate()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DM 240");
        AssertLeavesAtProfileRate(engine, aircraft, "DM 240");
    }

    /// <summary>
    /// DVIA 20 NM before OAKES, already inside the top of descent: the aircraft descends at once, never faster than twice
    /// the standard rate, and its vertical speed never reads negative zero.
    /// </summary>
    [Fact]
    public void DescendVia_IssuedInsideTopOfDescent_DescendsAtOnceCappedAtTwiceStandard()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(20);
        SendOk(engine, aircraft, "DVIA");
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        Flight flight = Fly(engine, aircraft, FirstFix);

        Assert.True(flight.DescentStartIndex < 5, $"The descent began {flight.DescentStartIndex} s after the clearance");
        double plannedAtFt = CruiseFt;
        foreach (Sample s in flight.Samples)
        {
            // The planner reads the standard rate at the altitude the tick starts from
            double cap = 2 * AircraftPerformance.DescentRate(aircraft.AircraftType, cat, plannedAtFt);
            Assert.True(-s.VsFpm <= cap + 1, $"t={s.T}: {s.VsFpm:F0} fpm, past twice standard ({cap:F0})");
            plannedAtFt = s.AltFt;
        }
    }

    /// <summary>
    /// An OAKES3 arrival the generator spawns on its descent profile under a descend via (its altitude projected up the
    /// 3:1 gradient the planner flies) starts down at once and does not level off before its first ceiling.
    /// </summary>
    [Fact]
    public void SpawnedOnProfileUnderDescendVia_DoesNotLevelOff()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(distanceBeforeFixNm: null, descendVia: true, starAltitude: null);
        List<RouteCeiling> ceilings = [.. Ceilings(aircraft).Where(c => c.CeilingFt < aircraft.Altitude - FlightPhysics.AltitudeSnapFt)];
        Assert.NotEmpty(ceilings);
        _output.WriteLine(
            $"Spawned at {aircraft.Altitude:F0}, {aircraft.IndicatedAirspeed:F0} KIAS; first ceiling {ceilings[0].Name} {ceilings[0].CeilingFt:F0}"
        );
        Flight flight = Fly(engine, aircraft, ceilings[0].Name);

        Assert.True(flight.DescentStartIndex < 30, $"The descent began {flight.DescentStartIndex} s after spawn");
        int sequenced = flight.SequencedIndex(ceilings[0].Name);
        int longestLevel = flight.LongestLevelRun(flight.DescentStartIndex, sequenced, aboveFt: ceilings[0].CeilingFt + FlightPhysics.AltitudeSnapFt);
        Assert.True(longestLevel < 20, $"Level for {longestLevel} s above {ceilings[0].Name}'s {ceilings[0].CeilingFt:F0} ceiling");
    }

    /// <summary>
    /// A DVIA followed by "descend and maintain 12,000": the maintain takes the STAR's altitudes out of force, so no
    /// ceiling is left to plan a top of descent for and the descent is prompt at the profile rate.
    /// </summary>
    [Fact]
    public void DescendViaThenDescendAndMaintain_IsPrompt()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DVIA");
        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        SendOk(engine, aircraft, "DM 120");
        AssertLeavesAtProfileRate(engine, aircraft, "DVIA then DM 120");
    }

    /// <summary>
    /// "Descend and maintain FL280, cross OAKES at or above FL240" 150 NM out: the cleared altitude is above the crossing's
    /// floor and no ceiling lies below the aircraft, so the descent to FL280 is a plain prompt one at the profile rate.
    /// </summary>
    [Fact]
    public void DescendAndMaintainAboveAnAtOrAboveCrossing_LeavesAtOnceAtTheProfileRate()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DM 280");
        SendOk(engine, aircraft, "CFIX OAKES A240");
        Assert.Equal(28000, aircraft.Targets.AssignedAltitude);
        AssertLeavesAtProfileRate(engine, aircraft, "DM 280, CFIX OAKES A240");
    }

    /// <summary>
    /// "Descend and maintain FL240, cross OAKES at or above FL240" 150 NM out: the floor is the cleared altitude, so the
    /// crossing assigns nothing new and the descent to FL240 is a plain prompt one at the profile rate.
    /// </summary>
    [Fact]
    public void DescendAndMaintainAtAnAtOrAboveCrossing_LeavesAtOnceAtTheProfileRate()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DM 240");
        SendOk(engine, aircraft, "CFIX OAKES A240");
        Assert.Equal(OakesTargetFt, aircraft.Targets.AssignedAltitude);
        AssertLeavesAtProfileRate(engine, aircraft, "DM 240, CFIX OAKES A240");
    }

    /// <summary>
    /// "Cross OAKES at or above FL240", then "descend and maintain FL240": the reverse order ends as the first does, with
    /// FL240 assigned and a prompt descent at the profile rate.
    /// </summary>
    [Fact]
    public void AtOrAboveCrossingThenDescendAndMaintainToItsFloor_LeavesAtOnceAtTheProfileRate()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "CFIX OAKES A240");
        SendOk(engine, aircraft, "DM 240");
        Assert.Equal(OakesTargetFt, aircraft.Targets.AssignedAltitude);
        AssertLeavesAtProfileRate(engine, aircraft, "CFIX OAKES A240, DM 240");
    }

    /// <summary>
    /// An aircraft level at its cleared 10,000 ft told "cross OAKES at or above 14,000": the floor lies above the cleared
    /// altitude, so the crossing assigns 14,000 and the aircraft climbs to it.
    /// </summary>
    [Fact]
    public void AtOrAboveCrossingAboveTheClearedAltitude_AssignsTheFloorAndClimbs() => AssertAtOrAboveAssignsTheFloorAndClimbs(clearedFt: 10000);

    /// <summary>
    /// An aircraft level at 10,000 ft with no altitude cleared told "cross OAKES at or above 14,000": the floor lies above
    /// the altitude it holds, so the crossing assigns 14,000 and the aircraft climbs to it.
    /// </summary>
    [Fact]
    public void AtOrAboveCrossingAboveTheHeldAltitude_NothingCleared_AssignsTheFloorAndClimbs() =>
        AssertAtOrAboveAssignsTheFloorAndClimbs(clearedFt: null);

    private void AssertAtOrAboveAssignsTheFloorAndClimbs(double? clearedFt)
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        aircraft.Altitude = 10000;
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = clearedFt;
        Assert.Equal(clearedFt, NavigationCommandHandler.ClearedAltitude(aircraft));
        aircraft.IndicatedAirspeed = SpeedLimitBelow10kKts;
        aircraft.Targets.TargetSpeed = null;
        SendOk(engine, aircraft, "CFIX OAKES A140");
        Assert.Equal(14000, aircraft.Targets.AssignedAltitude);
        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(aircraft.VerticalSpeed > 0, $"VS {aircraft.VerticalSpeed:F0} fpm at {aircraft.Altitude:F0}: not climbing to 14,000");
        Assert.True(aircraft.Altitude > 10000 + FlightPhysics.AltitudeSnapFt, $"Still at {aircraft.Altitude:F0}");
    }

    /// <summary>
    /// "Descend and maintain FL240, cross OAKES at or above FL260": the floor lies above the cleared altitude and binds, so
    /// it is assigned (FL260). A descend and maintain is a prompt clearance (AIM 4-4-10.d); inside the pilot's-discretion
    /// window AIM 4-4-10.e allows for the binding floor the sim chooses the prompt descent, so the aircraft leaves FL350 at
    /// once, holds no lower than FL260 until OAKES, and once OAKES sequences goes back to FL240 and descends to it.
    /// </summary>
    [Fact]
    public void DescendAndMaintainBelowAnAtOrAboveCrossing_DescendsPromptlyToTheFloorThenToTheClearedAltitude()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DM 240");
        SendOk(engine, aircraft, "CFIX OAKES A260");
        Assert.Equal(26000, aircraft.Targets.AssignedAltitude);
        AssertLeavesAtProfileRate(engine, aircraft, "DM 240, CFIX OAKES A260");
        Assert.True(aircraft.Altitude < CruiseFt - FlightPhysics.AltitudeSnapFt, $"Still at {aircraft.Altitude:F0} after the clearance");

        Flight flight = Fly(engine, aircraft, FirstFix);
        int oakes = flight.SequencedIndex(FirstFix);
        double lowest = flight.Samples.Take(oakes).Min(s => s.AltFt);
        _output.WriteLine($"OAKES at {oakes} s; lowest before it {lowest:F0}");
        Assert.True(lowest >= 26000 - FlightPhysics.AltitudeSnapFt, $"Descended to {lowest:F0} before {FirstFix}, below its FL260 floor");

        Assert.Equal(OakesTargetFt, aircraft.Targets.AssignedAltitude);
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(
            aircraft.Altitude < 26000 - FlightPhysics.AltitudeSnapFt,
            $"{aircraft.Altitude:F0} ft 60 s past {FirstFix}: not descending to FL240"
        );
    }

    /// <summary>
    /// The whole OAKES3 descend via from 150 NM out: once the descent has begun, the aircraft never levels for 20 s or more
    /// except within 100 ft of a published altitude (a ceiling or a floor it holds until the fix) or of 10,000 ft (the
    /// 250 kt slow-down).
    /// </summary>
    [Fact]
    public void DescendVia_WholeStar_LevelsOnlyAtPublishedAltitudes()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DVIA");
        List<double> allowedFt = [10000];
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            if (fix.AltitudeRestriction is { } r)
            {
                allowedFt.Add(r.Altitude1Ft);
                allowedFt.AddRange(r.Altitude2Ft is { } lower ? [lower] : []);
            }
        }

        List<RouteCeiling> ceilings = Ceilings(aircraft);
        Flight flight = Fly(engine, aircraft, stopAfterFix: ceilings[^1].Name);
        int longest = 0;
        int run = 0;
        double runAltFt = 0;
        double longestAltFt = 0;
        foreach (Sample s in flight.Samples.Skip(flight.DescentStartIndex))
        {
            bool unexplainedLevel = (s.VsFpm == 0) && !allowedFt.Exists(a => Math.Abs(s.AltFt - a) <= 100);
            run = unexplainedLevel ? run + 1 : 0;
            runAltFt = s.AltFt;
            if (run > longest)
            {
                longest = run;
                longestAltFt = runAltFt;
            }
        }

        _output.WriteLine($"Longest level run away from a published altitude: {longest} s at {longestAltFt:F0}");
        Assert.True(longest < 20, $"Level for {longest} s at {longestAltFt:F0}, away from every published altitude");
    }

    /// <summary>
    /// The level hold and the descent from top of descent are derived from the aircraft's state each tick, so a snapshot
    /// round trip taken while the aircraft holds FL350, and one taken just inside the top of descent (descending, within the
    /// hold band), fly on tick for tick as the uninterrupted run does.
    /// </summary>
    [Fact]
    public void DescendVia_LevelHoldAndTopOfDescent_SurviveASnapshotRoundTrip()
    {
        const int Ticks = 1100;
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150);
        SendOk(engine, aircraft, "DVIA");
        List<(double AltFt, double VsFpm)> uninterrupted = [];
        for (int t = 0; t < Ticks; t++)
        {
            engine.TickOneSecond();
            uninterrupted.Add((aircraft.Altitude, aircraft.VerticalSpeed));
        }

        int descentStart = uninterrupted.FindIndex(s => s.VsFpm < 0);
        Assert.InRange(descentStart, 300, Ticks - 100);
        foreach (int snapAfter in new[] { 200, descentStart + 5 })
        {
            (SimulationEngine before, AircraftState original) = SpawnOnOakes(150);
            SendOk(before, original, "DVIA");
            for (int t = 0; t < snapAfter; t++)
            {
                before.TickOneSecond();
            }

            Assert.True(
                (snapAfter == 200) ? (original.VerticalSpeed == 0) : (original.VerticalSpeed < 0),
                $"setup: VS {original.VerticalSpeed} at {snapAfter}"
            );
            var restored = AircraftState.FromSnapshot(original.ToSnapshot(), original.Ground.Layout);
            SimulationEngine after = EngineWith(restored, new TestAirportGroundData());
            for (int t = snapAfter; t < Ticks; t++)
            {
                after.TickOneSecond();
                Assert.True(
                    (restored.Altitude == uninterrupted[t].AltFt) && (restored.VerticalSpeed == uninterrupted[t].VsFpm),
                    $"Restored at {snapAfter} s: t={t + 1} alt {restored.Altitude} VS {restored.VerticalSpeed}, uninterrupted alt "
                        + $"{uninterrupted[t].AltFt} VS {uninterrupted[t].VsFpm}"
                );
            }
        }
    }

    private void AssertLeavesAtProfileRate(SimulationEngine engine, AircraftState aircraft, string what)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        for (int t = 1; t <= 15; t++)
        {
            engine.TickOneSecond();
        }

        double profileRate = AircraftPerformance.DescentRate(aircraft.AircraftType, cat, aircraft.Altitude);
        _output.WriteLine($"{what}: VS {aircraft.VerticalSpeed:F0} fpm at {aircraft.Altitude:F0}; profile {profileRate:F0} fpm");
        Assert.InRange(-aircraft.VerticalSpeed, 0.9 * profileRate, 1.1 * profileRate);
    }

    private static void SendOk(SimulationEngine engine, AircraftState aircraft, string command)
    {
        CommandResult result = engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, result.Message);
    }

    /// <summary>
    /// How far before <paramref name="fixName"/> the top of descent lies for the aircraft as it is now: over every ceiling
    /// ahead (or the one at <paramref name="ceilingsOnly"/>), the drop at 300 ft per NM plus 1 NM per 10 kt the aircraft
    /// must slow by that ceiling's fix (250 kt by 10,000 ft and the published speeds up to it), less the ceiling's distance
    /// past <paramref name="fixName"/>, the latest one winning.
    /// </summary>
    private double TopOfDescentBeforeFixNm(AircraftState aircraft, string fixName, string? ceilingsOnly)
    {
        List<RouteCeiling> ceilings = Ceilings(aircraft);
        double toFix = ceilings.Concat(AllFixes(aircraft)).First(c => c.Name == fixName).AlongNm;
        double best = double.MinValue;
        foreach (RouteCeiling c in ceilings.Where(c => (ceilingsOnly is null) || (c.Name == ceilingsOnly)))
        {
            double lose = aircraft.Altitude - c.CeilingFt;
            if (lose <= FlightPhysics.AltitudeSnapFt)
            {
                continue;
            }

            double bySpeed = (c.CeilingFt < 10000) && (aircraft.Altitude > 10000) ? Math.Min(SpeedLimitBelow10kKts, c.SpeedByKts) : c.SpeedByKts;
            double decelNm = Math.Max(0, aircraft.IndicatedAirspeed - bySpeed) / DecelKtsPerNm;
            double tod = (lose / TodFtPerNm) + decelNm - (c.AlongNm - toFix);
            _output.WriteLine(
                $"Ceiling {c.Name} {c.CeilingFt:F0} at {c.AlongNm:F1} NM, by {bySpeed:F0} kt: top of descent {tod:F1} NM before {fixName}"
            );
            best = Math.Max(best, tod);
        }

        _output.WriteLine($"IAS {aircraft.IndicatedAirspeed:F0}, GS {aircraft.GroundSpeed:F0}: top of descent {best:F1} NM before {fixName}");
        return best;
    }

    /// <summary>Every in-force altitude ceiling on the route (an at, an at-or-below, a window's top), in route order.</summary>
    private static List<RouteCeiling> Ceilings(AircraftState aircraft) =>
        [.. AllFixes(aircraft).Where(c => !double.IsNaN(c.CeilingFt) && AltitudeRestrictionInForce.IsInForce(aircraft, c.Fix.AltitudeRestriction!))];

    private static List<RouteCeiling> AllFixes(AircraftState aircraft)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        var fixes = new List<RouteCeiling>();
        double along = 0;
        double speedBy = double.MaxValue;
        for (int i = 0; i < route.Count; i++)
        {
            along += GeoMath.DistanceNm((i == 0) ? aircraft.Position : route[i - 1].Position, route[i].Position);
            if (route[i].SpeedRestriction is { Type: not CifpSpeedRestrictionType.AtOrAbove } speed)
            {
                speedBy = Math.Min(speedBy, speed.SpeedKts);
            }

            double ceiling = route[i].AltitudeRestriction is { Type: not CifpAltitudeRestrictionType.AtOrAbove } alt ? alt.Altitude1Ft : double.NaN;
            fixes.Add(new RouteCeiling(route[i].Name, route[i], ceiling, along, speedBy));
        }

        return fixes;
    }

    /// <summary>
    /// Ticks the engine one second at a time until <paramref name="stopAfterFix"/> sequences, recording each tick, and fails
    /// the moment the vertical speed reads negative zero.
    /// </summary>
    private Flight Fly(SimulationEngine engine, AircraftState aircraft, string stopAfterFix)
    {
        foreach (NavigationTarget fix in aircraft.Targets.NavigationRoute)
        {
            _output.WriteLine(
                $"  {fix.Name}: alt {Describe(fix.AltitudeRestriction)} speed {fix.SpeedRestriction?.Type} {fix.SpeedRestriction?.SpeedKts}"
            );
        }

        var flight = new Flight(aircraft.Altitude);
        LatLon previous = aircraft.Position;
        for (int t = 1; t <= 3600; t++)
        {
            HashSet<string> before = [.. aircraft.Targets.NavigationRoute.Select(f => f.Name)];
            double? toFix = AlongRouteTo(aircraft, FirstFix);
            engine.TickOneSecond();
            Assert.False((aircraft.VerticalSpeed == 0) && double.IsNegative(aircraft.VerticalSpeed), $"t={t}: vertical speed reads negative zero");
            double flown = GeoMath.DistanceNm(previous, aircraft.Position);
            previous = aircraft.Position;
            flight.Samples.Add(new Sample(t, aircraft.Altitude, aircraft.VerticalSpeed, flown, toFix));
            foreach (string name in before.Where(n => !aircraft.Targets.NavigationRoute.Exists(f => f.Name == n)))
            {
                flight.Sequenced[name] = (flight.Samples.Count - 1, aircraft.Altitude);
                _output.WriteLine($"Sequenced {name} at t={t}, alt {aircraft.Altitude:F0}, IAS {aircraft.IndicatedAirspeed:F0}");
            }

            if (flight.Sequenced.ContainsKey(stopAfterFix))
            {
                return flight;
            }
        }

        Assert.Fail($"{stopAfterFix} did not sequence within 3600 s");
        return flight;
    }

    private static string Describe(CifpAltitudeRestriction? r) => r is null ? "-" : $"{r.Type} {r.Altitude1Ft} {r.Altitude2Ft} ({r.Source})";

    private static double? AlongRouteTo(AircraftState aircraft, string fixName)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int k = route.FindIndex(f => f.Name == fixName);
        if (k < 0)
        {
            return null;
        }

        double d = GeoMath.DistanceNm(aircraft.Position, route[0].Position);
        for (int i = 1; i <= k; i++)
        {
            d += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
        }

        return d;
    }

    /// <summary>
    /// Puts the aircraft on the extended inbound course to the STAR's first fix, <paramref name="distanceBeforeFixNm"/> flying miles before
    /// OAKES.
    /// </summary>
    private static void PlaceBeforeFix(AircraftState aircraft, double distanceBeforeFixNm)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int k = route.FindIndex(f => f.Name == FirstFix);
        Assert.True(k >= 0, $"{FirstFix} is not on the route");
        double along = 0;
        for (int i = 1; i <= k; i++)
        {
            along += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
        }

        double inbound = GeoMath.BearingTo(route[0].Position, route[1].Position);
        aircraft.Position = GeoMath.ProjectPoint(route[0].Position, new TrueHeading(inbound + 180), distanceBeforeFixNm - along);
        var heading = new TrueHeading(GeoMath.BearingTo(aircraft.Position, route[0].Position));
        aircraft.TrueHeading = heading;
        aircraft.TrueTrack = heading;
    }

    private (SimulationEngine Engine, AircraftState Aircraft) SpawnOnOakes(double distanceBeforeFixNm) =>
        SpawnOnOakes(distanceBeforeFixNm, descendVia: false, starAltitude: CruiseFt);

    /// <summary>
    /// A B738 on Oakland's OAKES3 STAR, with the STAR in its filed route: placed <paramref name="distanceBeforeFixNm"/>
    /// before OAKES, or where the generator spawns it when null.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) SpawnOnOakes(double? distanceBeforeFixNm, bool descendVia, double? starAltitude)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        string entryFix = DepartureClearanceHandler.ResolveLegsToTargets([.. star.CommonLegs])[0].Name;
        string runwayKey = star.RunwayTransitions.Keys.First();
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Large,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = entryFix,
            StarId = "OAKES3",
            StarRunway = runwayKey.StartsWith("RW", StringComparison.Ordinal) ? runwayKey[2..] : runwayKey,
            DescendVia = descendVia,
            StarAltitude = starAltitude,
            DestinationAirportId = "KOAK",
            ExplicitType = "B738",
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
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is AirportGroundLayout layout)
        {
            aircraft.Ground.Layout = layout;
        }

        if (distanceBeforeFixNm is { } distance)
        {
            PlaceBeforeFix(aircraft, distance);
        }

        return (EngineWith(aircraft, groundData), aircraft);
    }

    /// <summary>A fresh engine on <paramref name="groundData"/> holding <paramref name="aircraft"/> alone.</summary>
    private static SimulationEngine EngineWith(AircraftState aircraft, TestAirportGroundData groundData)
    {
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "dvia-top-of-descent-oakes",
                ScenarioName = "DVIA top of descent on OAKES3",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.AddAircraft(aircraft);
        return engine;
    }

    private static double PathDeg(double lostFt, double flownNm) => (flownNm > 0) ? Math.Atan(lostFt / (flownNm * 6076.12)) * 180.0 / Math.PI : 0;

    /// <summary>A route fix with its ceiling (NaN for none), its distance along the route and the lowest published speed up to it.</summary>
    private readonly record struct RouteCeiling(string Name, NavigationTarget Fix, double CeilingFt, double AlongNm, double SpeedByKts);

    /// <summary>One tick: altitude, vertical speed, the NM flown in it and the along-route NM to OAKES before it.</summary>
    private readonly record struct Sample(int T, double AltFt, double VsFpm, double FlownNm, double? ToOakesNm);

    private readonly record struct DescentProfile(double DescentStartNm, double AveragePathDeg, double AverageVsFpm);

    private sealed class Flight(double startAltFt)
    {
        public List<Sample> Samples { get; } = [];

        public Dictionary<string, (int Index, double AltFt)> Sequenced { get; } = [];

        /// <summary>The first tick below the starting altitude, or the tick count when the aircraft never left it.</summary>
        public int DescentStartIndex
        {
            get
            {
                int i = Samples.FindIndex(s => s.AltFt < startAltFt - FlightPhysics.AltitudeSnapFt);
                return (i < 0) ? Samples.Count : i;
            }
        }

        public int SequencedIndex(string fix) => Assert.Contains(fix, (IDictionary<string, (int Index, double AltFt)>)Sequenced).Index;

        public double AltitudeAtSequencing(string fix) => Assert.Contains(fix, (IDictionary<string, (int Index, double AltFt)>)Sequenced).AltFt;

        /// <summary>Altitude lost per NM flown on the ticks the aircraft descended, from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public double DescendingGradientFtPerNm(int from, int to)
        {
            double lost = 0;
            double flown = 0;
            for (int i = Math.Max(from, 1); i < to; i++)
            {
                if (Samples[i].VsFpm < 0)
                {
                    lost += Samples[i - 1].AltFt - Samples[i].AltFt;
                    flown += Samples[i].FlownNm;
                }
            }

            return (flown > 0) ? lost / flown : 0;
        }

        /// <summary>The longest run of level ticks above <paramref name="aboveFt"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public int LongestLevelRun(int from, int to, double aboveFt)
        {
            int longest = 0;
            int run = 0;
            for (int i = from; i < Math.Min(to, Samples.Count); i++)
            {
                run = ((Samples[i].VsFpm == 0) && (Samples[i].AltFt > aboveFt)) ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            return longest;
        }

        /// <summary>The descent to <paramref name="fix"/>: where it began before the fix, and its average path and rate.</summary>
        public DescentProfile ProfileTo(string fix)
        {
            int start = DescentStartIndex;
            int end = SequencedIndex(fix);
            if (start >= end)
            {
                return new DescentProfile(0, 0, 0);
            }

            double startNm = Samples[Math.Max(start - 1, 0)].ToOakesNm ?? 0;
            double flown = Samples.Skip(start).Take(end - start + 1).Sum(s => s.FlownNm);
            double lost = Samples[Math.Max(start - 1, 0)].AltFt - Samples[end].AltFt;
            double avgVs = -Samples.Skip(start).Take(end - start + 1).Average(s => s.VsFpm);
            return new DescentProfile(startNm, PathDeg(lost, flown), avgVs);
        }
    }
}
