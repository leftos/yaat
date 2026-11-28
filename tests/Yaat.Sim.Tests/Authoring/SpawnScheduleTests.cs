using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="SpawnSchedule"/> matches the aircraft a spawn filter takes, turns them into slots by a band, walk or
/// fill-time schedule, keeps same-origin spawns apart from each other and from the aircraft the run does not schedule,
/// and reports a spawn later than the scenario's length.
/// </summary>
public class SpawnScheduleTests
{
    // ── B: time bands ────────────────────────────────────────────────────────

    [Fact]
    public void BandSlots_WithoutJitter_EndsInclusive()
    {
        IReadOnlyList<int> slots = SpawnSchedule.BandSlots([new SpawnBand(0, 2400, 120, 15, 15)], null);

        Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 120).ToArray(), slots.ToArray());
    }

    [Fact]
    public void BandSlots_SeededJitter_AccumulatesTheOffset()
    {
        SpawnBand band = new(0, 2400, 120, 15, 15);

        IReadOnlyList<int> first = SpawnSchedule.BandSlots([band], new Random(42));
        IReadOnlyList<int> second = SpawnSchedule.BandSlots([band], new Random(42));
        IReadOnlyList<int> other = SpawnSchedule.BandSlots([band], new Random(43));

        Assert.Equal(21, first.Count);
        Assert.InRange(first[0], 0, 15);
        for (int i = 1; i < first.Count; i++)
        {
            Assert.InRange(first[i] - first[i - 1], 105, 135);
        }

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void BandSlots_ZeroJitter_DrawsNothing()
    {
        IReadOnlyList<int> slots = SpawnSchedule.BandSlots([new SpawnBand(0, 2400, 120, 0, 0)], new Random(9));
        var fresh = new Random(9);
        var used = new Random(9);

        Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 120).ToArray(), slots.ToArray());
        SpawnSchedule.BandSlots([new SpawnBand(0, 2400, 120, 0, 0)], used);
        Assert.Equal(fresh.Next(), used.Next());
    }

    [Fact]
    public void BandSlots_TwoBands_ConcatenateInBandOrder()
    {
        IReadOnlyList<int> slots = SpawnSchedule.BandSlots([new SpawnBand(0, 600, 300, 0, 0), new SpawnBand(1200, 1800, 300, 0, 0)], null);

        Assert.Equal([0, 300, 600, 1200, 1500, 1800], [.. slots]);
    }

    [Fact]
    public void BandSlots_InvalidInterval_SkipsTheBand()
    {
        IReadOnlyList<int> slots = SpawnSchedule.BandSlots([new SpawnBand(0, 2400, 0, 15, 15), new SpawnBand(0, 2400, -60, 15, 15)], null);

        Assert.Empty(slots);
    }

    // ── W: workload walk ─────────────────────────────────────────────────────

    [Fact]
    public void WalkSlots_FlatHalf_StepsAtTheMeanInterval()
    {
        var curve = new WorkloadCurve(3600, 50, 50, []);
        IReadOnlyList<int> slots = SpawnSchedule.WalkSlots(curve, new WorkloadSettings(60, 300, 0), null);

        Assert.Equal(Enumerable.Range(0, 20).Select(i => i * 180).ToArray(), slots.ToArray());
    }

    [Fact]
    public void WalkSlots_FlatEnds_UseTheMinAndMaxIntervals()
    {
        var settings = new WorkloadSettings(60, 300, 0);

        IReadOnlyList<int> busy = SpawnSchedule.WalkSlots(new WorkloadCurve(3600, 100, 100, []), settings, null);
        IReadOnlyList<int> idle = SpawnSchedule.WalkSlots(new WorkloadCurve(3600, 0, 0, []), settings, null);

        Assert.Equal(Enumerable.Range(0, 60).Select(i => i * 60).ToArray(), busy.ToArray());
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i * 300).ToArray(), idle.ToArray());
    }

    [Fact]
    public void WalkSlots_IntervalFloor_KeepsSlotsFiveSecondsApart()
    {
        IReadOnlyList<int> slots = SpawnSchedule.WalkSlots(new WorkloadCurve(3600, 100, 100, []), new WorkloadSettings(1, 1, 0), null);

        Assert.Equal(720, slots.Count);
        Assert.Equal(Enumerable.Range(0, 720).Select(i => i * 5).ToArray(), slots.ToArray());
    }

    [Fact]
    public void WalkSlots_Jitter_MovesEachSlotWithinTheJitter()
    {
        var curve = new WorkloadCurve(3600, 50, 50, []);
        IReadOnlyList<int> plain = SpawnSchedule.WalkSlots(curve, new WorkloadSettings(60, 300, 0), null);
        IReadOnlyList<int> jittered = SpawnSchedule.WalkSlots(curve, new WorkloadSettings(60, 300, 15), new Random(7));

        var fresh = new Random(7);
        int[] offsets = [.. plain.Select(_ => fresh.Next(-15, 16))];
        Assert.Equal(plain.Zip(offsets, (slot, offset) => Math.Max(0, slot + offset)).ToArray(), jittered.ToArray());
    }

    // ── F: fill time ─────────────────────────────────────────────────────────

    [Fact]
    public void FillSlots_FlatCurve_PlacesTheQuantiles()
    {
        IReadOnlyList<int> slots = SpawnSchedule.FillSlots(new WorkloadCurve(3600, 50, 50, []), 5, new WorkloadSettings(60, 300, 0), null);

        Assert.Equal([0, 895, 1796, 2698, 3600], [.. slots]);
    }

    [Fact]
    public void FillSlots_FlatCurve_SpacesEvenly()
    {
        var curve = new WorkloadCurve(3600, 50, 50, []);
        var settings = new WorkloadSettings(60, 300, 0);

        IReadOnlyList<int> five = SpawnSchedule.FillSlots(curve, 5, settings, null);
        for (int i = 1; i < five.Count; i++)
        {
            Assert.InRange(five[i] - five[i - 1], 900 - 8, 900 + 8);
        }

        IReadOnlyList<int> sixtyOne = SpawnSchedule.FillSlots(curve, 61, settings, null);
        Assert.Equal(61, sixtyOne.Count);
        for (int i = 1; i < sixtyOne.Count; i++)
        {
            Assert.InRange(sixtyOne[i] - sixtyOne[i - 1], 60 - 8, 60 + 8);
        }
    }

    [Fact]
    public void FillSlots_PlacesExactlyTheCount()
    {
        var curve = new WorkloadCurve(3600, 50, 50, [new WorkloadPoint(1800, 100)]);
        var settings = new WorkloadSettings(60, 300, 0);

        Assert.Empty(SpawnSchedule.FillSlots(curve, 0, settings, null));
        Assert.Equal([0], [.. SpawnSchedule.FillSlots(curve, 1, settings, null)]);
        Assert.Equal(2, SpawnSchedule.FillSlots(curve, 2, settings, null).Count);
        Assert.Equal(7, SpawnSchedule.FillSlots(curve, 7, settings, null).Count);
        Assert.Equal(100, SpawnSchedule.FillSlots(curve, 100, settings, null).Count);

        var fresh = new Random(7);
        IReadOnlyList<int> jittered = SpawnSchedule.FillSlots(curve, 1, new WorkloadSettings(60, 300, 15), new Random(7));
        Assert.Equal([Math.Clamp(fresh.Next(-15, 16), 0, 3600)], [.. jittered]);
    }

    [Fact]
    public void FillSlots_FollowsTheCurve()
    {
        var curve = new WorkloadCurve(3600, 50, 50, [new WorkloadPoint(1800, 100)]);
        IReadOnlyList<int> slots = SpawnSchedule.FillSlots(curve, 20, new WorkloadSettings(5, 300, 0), null);

        Assert.Equal(20, slots.Count);
        Assert.Equal(slots.OrderBy(s => s).ToArray(), slots.ToArray());
        int outer = slots.Count(s => s < 1200);
        int middle = slots.Count(s => (s >= 1200) && (s < 2400));
        int far = slots.Count(s => s >= 2400);
        Assert.True((middle > outer) && (middle > far), $"thirds were {outer}/{middle}/{far}");
    }

    [Fact]
    public void FillSlots_MinInterval_PushesForward()
    {
        IReadOnlyList<int> slots = SpawnSchedule.FillSlots(new WorkloadCurve(60, 50, 50, []), 3, new WorkloadSettings(60, 300, 0), null);

        Assert.Equal([0, 60, 120], [.. slots]);

        var scenario = new Scenario { Aircraft = [Airborne("AAL1", "SUNOL"), Airborne("AAL2", "SUNAP"), Airborne("AAL3", "SUNIP")] };
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], slots, 60, null);

        Assert.Equal([0, 60, 120], Delays(result));
        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(60, overrun.LengthSec);
        Assert.Equal(120, overrun.LastSpawnSec);
        Assert.Equal(60, overrun.OverrunSec);
    }

    [Fact]
    public void FillSlots_Jitter_MovesEachSlotWithinTheJitter()
    {
        var curve = new WorkloadCurve(3600, 50, 50, []);
        IReadOnlyList<int> plain = SpawnSchedule.FillSlots(curve, 5, new WorkloadSettings(60, 300, 0), null);
        IReadOnlyList<int> jittered = SpawnSchedule.FillSlots(curve, 5, new WorkloadSettings(60, 300, 15), new Random(7));

        var fresh = new Random(7);
        int[] offsets = [.. Enumerable.Range(0, 5).Select(_ => fresh.Next(-15, 16))];
        Assert.Equal(plain.Zip(offsets, (slot, offset) => Math.Max(0, slot + offset)).ToArray(), jittered.ToArray());
    }

    // ── D: same-origin deconfliction ─────────────────────────────────────────

    [Fact]
    public void Assign_SameOrigin_PushesToTheMinGap()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"), Ground("UAL3", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 30, 45], 600, new SpawnDeconfliction(60));

        Assert.Equal([0, 60, 120], Delays(result));
    }

    [Fact]
    public void Assign_DifferentOrigins_AreUntouched()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KOAK"), Ground("UAL3", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 10, 30], 600, new SpawnDeconfliction(60));

        Assert.Equal([0, 10, 60], Delays(result));
    }

    [Fact]
    public void Assign_AirborneOrigin_IsTheFix_CaseInsensitive()
    {
        var scenario = new Scenario { Aircraft = [Airborne("AAL1", "SUNOL"), Airborne("AAL2", "sunol")] };

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [100, 110], 600, new SpawnDeconfliction(60));

        Assert.Equal([100, 160], Delays(result));
        Assert.Equal("SUNOL", SpawnSchedule.OriginKey(scenario, scenario.Aircraft[1]));
    }

    [Fact]
    public void Assign_GroundAndAirborneStarts_ShareTheTextKey()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Airborne("UAL2", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 10], 600, new SpawnDeconfliction(60));

        Assert.Equal("KSFO", SpawnSchedule.OriginKey(scenario, scenario.Aircraft[0]));
        Assert.Equal("KSFO", SpawnSchedule.OriginKey(scenario, scenario.Aircraft[1]));
        Assert.Equal([0, 60], Delays(result));
    }

    [Fact]
    public void Assign_NoOriginKey_LeavesTheSlot()
    {
        var coordinates = new ScenarioAircraft
        {
            AircraftId = "AAL1",
            SpawnDelay = 5,
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Coordinates, Coordinates = new ScenarioCoordinates() },
        };
        var noFix = new ScenarioAircraft
        {
            AircraftId = "UAL2",
            SpawnDelay = 6,
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.FixOrFrd },
        };
        var scenario = new Scenario { Aircraft = [coordinates, noFix] };

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [5, 6], 600, new SpawnDeconfliction(60));

        Assert.Equal("", SpawnSchedule.OriginKey(scenario, coordinates));
        Assert.Equal("", SpawnSchedule.OriginKey(scenario, noFix));
        Assert.Equal([5, 6], Delays(result));
    }

    [Fact]
    public void Assign_AlreadySeparated_IsUntouched()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"), Ground("UAL3", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 60, 200], 600, new SpawnDeconfliction(60));

        Assert.Equal([0, 60, 200], Delays(result));
    }

    [Fact]
    public void Assign_SortsBySlot_WhateverTheInputOrder()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [30, 0], 600, new SpawnDeconfliction(60));

        Assert.Equal(60, DelayOf(result, "AAL1"));
        Assert.Equal(0, DelayOf(result, "AAL2"));
    }

    [Fact]
    public void Assign_CascadesThePush()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"), Ground("UAL3", "KSFO"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 0, 0], 600, new SpawnDeconfliction(60));

        Assert.Equal([0, 60, 120], Delays(result));
    }

    [Fact]
    public void OriginKey_GroundOrigin_FallsBackToTheAircraftThenPrimaryAirport()
    {
        var withAirport = new ScenarioAircraft
        {
            AircraftId = "AAL1",
            AirportId = "KOAK",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "" },
        };
        var withoutAirport = new ScenarioAircraft
        {
            AircraftId = "UAL2",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnRunway, Runway = "28R" },
        };
        var withBoth = new ScenarioAircraft
        {
            AircraftId = "SWA3",
            AirportId = "KOAK",
            FlightPlan = new ScenarioFlightPlan { Departure = "KSFO" },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "A1" },
        };
        var scenario = new Scenario { PrimaryAirportId = "KSFO", Aircraft = [withAirport, withoutAirport, withBoth] };

        Assert.Equal("KOAK", SpawnSchedule.OriginKey(scenario, withAirport));
        Assert.Equal("KSFO", SpawnSchedule.OriginKey(scenario, withoutAirport));
        Assert.Equal("KSFO", SpawnSchedule.OriginKey(scenario, withBoth));
    }

    [Fact]
    public void OriginKey_OnFinal_IsTheDestinationAndRunway()
    {
        var scenario = new Scenario { PrimaryAirportId = "KSFO" };
        var final = new ScenarioAircraft
        {
            AircraftId = "AAL1",
            FlightPlan = new ScenarioFlightPlan { Destination = "ksfo" },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal, Runway = "28r" },
        };
        var byAirport = new ScenarioAircraft
        {
            AircraftId = "UAL2",
            AirportId = "KOAK",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal, Runway = "10L" },
        };
        var byPrimary = new ScenarioAircraft
        {
            AircraftId = "SWA3",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal, Runway = "1R" },
        };
        var noRunway = new ScenarioAircraft
        {
            AircraftId = "N45",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal },
        };

        Assert.Equal("KSFO/28R", SpawnSchedule.OriginKey(scenario, final));
        Assert.Equal("KOAK/10L", SpawnSchedule.OriginKey(scenario, byAirport));
        Assert.Equal("KSFO/1R", SpawnSchedule.OriginKey(scenario, byPrimary));
        Assert.Equal("", SpawnSchedule.OriginKey(scenario, noRunway));
    }

    [Fact]
    public void Assign_OnFinalToTheSameRunway_IsSpaced()
    {
        Scenario scenario = GroundScenario(Final("AAL1", "KSFO", "28R"), Final("UAL2", "KSFO", "28R"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [100, 110], 600, new SpawnDeconfliction(60));

        Assert.Equal([100, 160], Delays(result));
    }

    [Fact]
    public void Assign_OnFinalToDifferentRunways_IsUntouched()
    {
        Scenario scenario = GroundScenario(Final("AAL1", "KSFO", "28R"), Final("UAL2", "KSFO", "28L"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [100, 110], 600, new SpawnDeconfliction(60));

        Assert.Equal([100, 110], Delays(result));
    }

    [Fact]
    public void Assign_UnscheduledAircraft_ClearsOnlyTheSpawnInsideTheGap()
    {
        ScenarioAircraft first = Airborne("AAL1", "SUNOL", 400);
        ScenarioAircraft second = Airborne("UAL2", "SUNOL", 470);
        ScenarioAircraft waiting = Airborne("SWA3", "SUNOL", 500);
        var scenario = new Scenario { Aircraft = [first, second, waiting] };

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [first, second], [400, 470], 600, new SpawnDeconfliction(60));

        Assert.Equal([400, 560], Delays(result));
        Assert.Equal(500, waiting.SpawnDelay);
        Assert.Empty(result.Unassigned);
    }

    [Fact]
    public void Assign_TwoUnscheduledAircraft_CascadeTheClearance()
    {
        ScenarioAircraft scheduled = Airborne("AAL1", "SUNOL", 530);
        ScenarioAircraft near = Airborne("UAL2", "SUNOL", 500);
        ScenarioAircraft far = Airborne("SWA3", "SUNOL", 600);
        var scenario = new Scenario { Aircraft = [scheduled, near, far] };

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [scheduled], [530], 600, new SpawnDeconfliction(60));

        Assert.Equal([660], Delays(result));
        Assert.Equal(500, near.SpawnDelay);
        Assert.Equal(600, far.SpawnDelay);
    }

    [Fact]
    public void Assign_DeconflictionOff_LeavesTheSlots()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"), Ground("UAL3", "KSFO"));
        ScenarioAircraft waiting = Airborne("N45", "SUNOL", 500);
        scenario.Aircraft.Add(waiting);

        SpawnScheduleResult off = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 10, 20, 400], 600, null);
        SpawnScheduleResult zero = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 10, 20, 400], 600, new SpawnDeconfliction(0));

        Assert.Equal([0, 10, 20, 400], Delays(off));
        Assert.Equal([0, 10, 20, 400], Delays(zero));
    }

    // ── O: overrun and its remedies ──────────────────────────────────────────

    [Fact]
    public void Assign_ReportsTheOverrunAfterDeconfliction()
    {
        (_, IReadOnlyList<int> slots, SpawnScheduleResult result) = OverrunFixture();

        Assert.Equal([0, 30, 60, 90], Delays(result));
        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(60, overrun.LengthSec);
        Assert.Equal(90, overrun.LastSpawnSec);
        Assert.Equal(30, overrun.OverrunSec);
        Assert.Equal(90, overrun.ExtendedLengthSec);
        Assert.Equal(60 / 90.0, overrun.CompressFactor, 6);
        Assert.Equal([0, 20, 40, 60], [.. overrun.CompressedSpawnSec]);
        Assert.Equal(20, overrun.CompressedMinGapSec);
        Assert.True(overrun.CompressBreaksGap);
        Assert.Equal(slots.Count, result.Assignments.Count);
    }

    [Fact]
    public void Assign_NoOverrun_WhenEverySpawnFits()
    {
        (Scenario scenario, _, _) = OverrunFixture();

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 10, 20, 30], 90, new SpawnDeconfliction(30));

        Assert.Equal([0, 30, 60, 90], Delays(result));
        Assert.Null(result.Overrun);
    }

    [Fact]
    public void Assign_NonPositiveLength_Throws()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"));

        Assert.Throws<ArgumentOutOfRangeException>(() => SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0], 0, null));
    }

    [Fact]
    public void ApplyRemedy_Extend_KeepsTheSlots()
    {
        (_, _, SpawnScheduleResult result) = OverrunFixture();

        SpawnScheduleResult extended = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.ExtendScenario, out int newLengthSec);

        Assert.Equal(90, newLengthSec);
        Assert.Equal(90, extended.LengthSec);
        Assert.Equal([0, 30, 60, 90], Delays(extended));
        Assert.Null(extended.Overrun);
        Assert.False(extended.GapWarning);
    }

    [Fact]
    public void ApplyRemedy_Compress_ScalesEverySlot_AndReportsTheShrunkenGap()
    {
        (_, _, SpawnScheduleResult result) = OverrunFixture();

        SpawnScheduleResult compressed = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.CompressIntervals, out int newLengthSec);

        Assert.Equal(60, newLengthSec);
        Assert.Equal([0, 20, 40, 60], Delays(compressed));
        Assert.Null(compressed.Overrun);
        Assert.True(compressed.GapWarning);
        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(20, overrun.CompressedMinGapSec);
    }

    [Fact]
    public void ApplyRemedy_Compress_ThatKeepsTheGap_WarnsNothing()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"));
        IReadOnlyList<int> slots = [0, 61];
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], slots, 60, new SpawnDeconfliction(5));

        Assert.Equal([0, 61], Delays(result));
        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(60, overrun.CompressedMinGapSec);
        Assert.False(overrun.CompressBreaksGap);

        SpawnScheduleResult compressed = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.CompressIntervals, out int newLengthSec);

        Assert.Equal(60, newLengthSec);
        Assert.Equal([0, 60], Delays(compressed));
        Assert.False(compressed.GapWarning);
    }

    [Fact]
    public void ApplyRemedy_Compress_MeasuresTheGapItLeaves()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"));
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 600], 540, new SpawnDeconfliction(60));

        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(600, overrun.LastSpawnSec);
        Assert.Equal(540, overrun.CompressedMinGapSec);
        Assert.False(overrun.CompressBreaksGap);
    }

    [Fact]
    public void ApplyRemedy_Compress_IgnoresPairsOfUnscheduledAircraft()
    {
        Scenario scenario = GroundScenario(
            Ground("AAL1", "KSFO", 0),
            Ground("AAL2", "KSFO", 0),
            Ground("UAL3", "KSFO", 300),
            Ground("SWA4", "KSFO", 900)
        );
        SpawnScheduleResult result = SpawnSchedule.Assign(
            scenario,
            [scenario.Aircraft[2], scenario.Aircraft[3]],
            [300, 900],
            600,
            new SpawnDeconfliction(60)
        );

        Assert.Equal([300, 900], Delays(result));
        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(900, overrun.LastSpawnSec);
        Assert.Equal(200, overrun.CompressedMinGapSec);
        Assert.False(overrun.CompressBreaksGap);
    }

    [Fact]
    public void ApplyRemedy_CompressOntoAnUnscheduledAircraft_Warns()
    {
        ScenarioAircraft scheduled = Airborne("AAL1", "SUNOL", 530);
        ScenarioAircraft near = Airborne("UAL2", "SUNOL", 500);
        ScenarioAircraft far = Airborne("SWA3", "SUNOL", 600);
        var scenario = new Scenario { Aircraft = [scheduled, near, far] };
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [scheduled], [530], 500, new SpawnDeconfliction(60));

        SpawnOverrun? overrun = result.Overrun;
        Assert.NotNull(overrun);
        Assert.Equal(660, overrun.LastSpawnSec);
        Assert.Equal(0, overrun.CompressedMinGapSec);
        Assert.True(overrun.CompressBreaksGap);

        SpawnScheduleResult compressed = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.CompressIntervals, out int newLengthSec);

        Assert.Equal(500, newLengthSec);
        Assert.Equal([500], Delays(compressed));
        Assert.True(compressed.GapWarning);
    }

    [Fact]
    public void ApplyRemedy_None_KeepsTheOverrunReported()
    {
        (_, _, SpawnScheduleResult result) = OverrunFixture();

        SpawnScheduleResult none = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.None, out int newLengthSec);

        Assert.Equal(60, newLengthSec);
        Assert.Same(result, none);
        Assert.NotNull(none.Overrun);
    }

    [Fact]
    public void ApplyRemedy_WithoutAnOverrun_ChangesNothing()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"));
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0], 600, new SpawnDeconfliction(60));

        SpawnScheduleResult extended = SpawnSchedule.ApplyRemedy(result, SpawnOverrunRemedy.ExtendScenario, out int newLengthSec);

        Assert.Equal(600, newLengthSec);
        Assert.Same(result, extended);
        Assert.Equal([0], Delays(extended));
    }

    [Fact]
    public void ApplyRemedy_UnknownRemedy_ThrowsBeforeTheOverrunCheck()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"));
        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0], 600, new SpawnDeconfliction(60));

        Assert.Throws<ArgumentOutOfRangeException>(() => SpawnSchedule.ApplyRemedy(result, (SpawnOverrunRemedy)99, out _));
    }

    [Fact]
    public void Assign_NeverLowersASlot()
    {
        (Scenario scenario, IReadOnlyList<int> slots, SpawnScheduleResult result) = OverrunFixture();

        for (int i = 0; i < result.Assignments.Count; i++)
        {
            Assert.True(result.Assignments[i].SpawnDelaySec >= slots[i], $"slot {i} was lowered");
        }

        SpawnSchedule.Apply(result);
        Assert.Equal(Delays(result), scenario.Aircraft.Select(a => a.SpawnDelay).ToArray());
    }

    // ── E: edges ─────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_EmptyMatch_DoesNothing()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"));
        IReadOnlyList<ScenarioAircraft> matched = SpawnSchedule.Match(scenario, new SpawnFilter(Departure: "KJFK"));

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, matched, [], 60, new SpawnDeconfliction(60));

        Assert.Empty(matched);
        Assert.Empty(result.Assignments);
        Assert.Empty(result.Unassigned);
        Assert.Equal(0, result.UnusedSlots);
        Assert.Null(result.Overrun);
        SpawnSchedule.Apply(result);
        Assert.Equal(0, scenario.Aircraft[0].SpawnDelay);
        Assert.Empty(SpawnSchedule.FillSlots(new WorkloadCurve(3600, 50, 50, []), 0, new WorkloadSettings(60, 300, 0), null));
    }

    [Fact]
    public void Assign_SurplusAircraftStayUnassigned()
    {
        var scenario = new Scenario { Aircraft = [.. Enumerable.Range(0, 25).Select(i => Airborne($"AAL{i:00}", $"FIX{i:00}", i))] };
        IReadOnlyList<int> slots = SpawnSchedule.BandSlots([new SpawnBand(0, 2400, 120, 0, 0)], null);

        SpawnScheduleResult result = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], slots, 3000, null);

        Assert.Equal(21, result.Assignments.Count);
        Assert.Equal(0, result.UnusedSlots);
        Assert.Equal(Enumerable.Range(21, 4).ToArray(), result.Unassigned.Select(a => a.SpawnDelay).ToArray());

        SpawnScheduleResult reverse = SpawnSchedule.Assign(
            scenario,
            [.. scenario.Aircraft.Take(21)],
            [.. Enumerable.Range(0, 25).Select(i => i * 10)],
            3000,
            null
        );

        Assert.Equal(4, reverse.UnusedSlots);
        Assert.Empty(reverse.Unassigned);
    }

    [Fact]
    public void Match_AppliesEveryFilterMember()
    {
        var departure = new ScenarioAircraft
        {
            AircraftId = "AAL1",
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = "KSFO",
                Destination = "KLAX",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "A1" },
        };
        var arrival = new ScenarioAircraft
        {
            AircraftId = "UAL2",
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "VFR",
                Departure = "KOAK",
                Destination = "KSFO",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.FixOrFrd, Fix = "SUNOL" },
            ExpectedApproach = "ILS 28R",
        };
        var onFinal = new ScenarioAircraft
        {
            AircraftId = "SWA3",
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = "KOAK",
                Destination = "KSFO",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal, Runway = "28R" },
        };
        var overflight = new ScenarioAircraft
        {
            AircraftId = "N45",
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "VFR",
                Departure = "KSEA",
                Destination = "KPDX",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.FixOrFrd, Fix = "SUNAP" },
        };
        var scenario = new Scenario { Aircraft = [departure, arrival, onFinal, overflight] };

        Assert.Equal(["AAL1"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Kind: SpawnKind.Departure))));
        Assert.Equal(["UAL2", "SWA3"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Kind: SpawnKind.Arrival))));
        Assert.Equal(["N45"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Kind: SpawnKind.Overflight))));
        Assert.Equal(["AAL1"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Departure: "kSfO"))));
        Assert.Equal(["UAL2", "SWA3"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Destination: "KSFO"))));
        Assert.Equal(["UAL2"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(Approach: "ils 28r"))));
        Assert.Equal(["AAL1", "SWA3"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(FlightRules: "IFR"))));
        Assert.Equal(["AAL1", "N45"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(ExcludeDeparture: "KOAK"))));
        Assert.Equal(["AAL1", "N45"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(ExcludeDestination: "KSFO"))));
        Assert.Equal(["UAL2", "SWA3", "N45"], Ids(SpawnSchedule.Match(scenario, new SpawnFilter(ExcludeCallsigns: new HashSet<string> { "aal1" }))));
        Assert.Equal(4, SpawnSchedule.Match(scenario, new SpawnFilter()).Count);
    }

    [Fact]
    public void Order_RandomGivesTheSamePermutationPerSeed()
    {
        var scenario = new Scenario
        {
            Aircraft = [Airborne("AAL1", "SUNOL", 30), Airborne("UAL2", "SUNAP", 10), Airborne("N45", "SUNOP", 10), Airborne("SWA3", "SUNIP", 20)],
        };
        IReadOnlyList<ScenarioAircraft> matched = [.. scenario.Aircraft];
        var fresh = new Random(1);
        string[] expected = [.. matched.OrderBy(_ => fresh.Next()).Select(aircraft => aircraft.AircraftId)];

        string[] first = Ids(SpawnSchedule.Order(matched, true, new Random(1)));
        string[] second = Ids(SpawnSchedule.Order(matched, true, new Random(1)));
        string[] byDelay = Ids(SpawnSchedule.Order(matched, false, new Random(1)));

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.NotEqual(byDelay, first);
        Assert.Equal(["UAL2", "N45", "SWA3", "AAL1"], byDelay);
    }

    [Fact]
    public void Assign_LeavesTheScenarioAlone_UntilApply()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO", 0), Ground("AAL2", "KSFO", 5));
        int[] before = [.. scenario.Aircraft.Select(a => a.SpawnDelay)];

        SpawnScheduleResult first = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 5], 600, new SpawnDeconfliction(60));
        SpawnScheduleResult second = SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], [0, 5], 600, new SpawnDeconfliction(60));

        Assert.Equal(before, scenario.Aircraft.Select(a => a.SpawnDelay).ToArray());
        Assert.Equal(Delays(first), Delays(second));
        Assert.Equal([0, 60], Delays(first));

        SpawnSchedule.Apply(first);
        Assert.Equal([0, 60], [.. scenario.Aircraft.Select(a => a.SpawnDelay)]);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static (Scenario Scenario, IReadOnlyList<int> Slots, SpawnScheduleResult Result) OverrunFixture()
    {
        Scenario scenario = GroundScenario(Ground("AAL1", "KSFO"), Ground("AAL2", "KSFO"), Ground("UAL3", "KSFO"), Ground("SWA4", "KSFO"));
        IReadOnlyList<int> slots = [0, 10, 20, 30];
        return (scenario, slots, SpawnSchedule.Assign(scenario, [.. scenario.Aircraft], slots, 60, new SpawnDeconfliction(30)));
    }

    private static Scenario GroundScenario(params ScenarioAircraft[] aircraft) => new() { PrimaryAirportId = "KSFO", Aircraft = [.. aircraft] };

    private static ScenarioAircraft Ground(string callsign, string departure, int spawnDelay = 0) =>
        new()
        {
            AircraftId = callsign,
            SpawnDelay = spawnDelay,
            FlightPlan = new ScenarioFlightPlan { Departure = departure },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "A1" },
        };

    private static ScenarioAircraft Airborne(string callsign, string fix, int spawnDelay = 0) =>
        new()
        {
            AircraftId = callsign,
            SpawnDelay = spawnDelay,
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.FixOrFrd, Fix = fix },
        };

    private static ScenarioAircraft Final(string callsign, string destination, string runway, int spawnDelay = 0) =>
        new()
        {
            AircraftId = callsign,
            SpawnDelay = spawnDelay,
            FlightPlan = new ScenarioFlightPlan { Destination = destination },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.OnFinal, Runway = runway },
        };

    private static int[] Delays(SpawnScheduleResult result) => [.. result.Assignments.Select(a => a.SpawnDelaySec)];

    private static int DelayOf(SpawnScheduleResult result, string callsign) =>
        result.Assignments.Single(a => a.Aircraft.AircraftId == callsign).SpawnDelaySec;

    private static string[] Ids(IReadOnlyList<ScenarioAircraft> aircraft) => [.. aircraft.Select(a => a.AircraftId)];
}
