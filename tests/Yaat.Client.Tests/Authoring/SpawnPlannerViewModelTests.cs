using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The spawn planner's view model previews a band or workload schedule over the matched aircraft, applies it as one undo
/// step recomputed against the session's current document, and offers the overrun's remedies with their warnings.
/// </summary>
public class SpawnPlannerViewModelTests
{
    public SpawnPlannerViewModelTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Preview_Bands_0to600s_Interval120_NoJitter_GivesSlots0_120_240_360_480_600()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(6));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        SpawnPlannerScenario.UseBand(vm, "0", "10", "2:00", "0", "0");

        vm.Preview();

        Assert.Equal([0, 120, 240, 360, 480, 600], [.. vm.PreviewSlots]);
        Assert.Equal([0, 120, 240, 360, 480, 600], [.. vm.PreviewRows.Select(row => row.NewDelaySec)]);
        Assert.Null(vm.PreviewError);
    }

    [Fact]
    public void Preview_FillMode_PlacesExactlyMatchedCount()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(5));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.Mode = SpawnPlannerMode.Workload;
        vm.DistributeAllMatched = true;

        vm.Preview();

        Assert.Equal(5, vm.MatchedCount);
        Assert.Equal(5, vm.PreviewResult!.Assignments.Count);
        Assert.Equal(5, vm.PreviewRows.Count);
        Assert.Equal(0, vm.UnusedSlots);
        Assert.Empty(vm.UnassignedCallsigns);
    }

    [Fact]
    public void Preview_EmptyFilter_AssignsNothing()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(4));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.Departure = "KOAK";
        SpawnPlannerScenario.UseBand(vm, "0", "10", "2:00", "0", "0");

        vm.Preview();
        vm.Apply();

        Assert.Equal(0, vm.MatchedCount);
        Assert.Empty(vm.Matched);
        Assert.Empty(vm.PreviewRows);
        Assert.False(session.CanUndo);
        Assert.Equal([30, 60, 90, 120], Delays(session));
    }

    [Fact]
    public void Preview_SameSeed_SamePermutation()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(8));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        SpawnPlannerViewModel other = SpawnPlannerScenario.Planner(session);
        SpawnPlannerViewModel[] planners = [vm, other];
        foreach (SpawnPlannerViewModel planner in planners)
        {
            planner.RandomOrder = true;
            planner.Seed = 42;
            SpawnPlannerScenario.UseBand(planner, "0", "30", "2:00", "0", "0");
        }

        vm.Preview();
        string[] first = [.. vm.PreviewRows.Select(row => row.Callsign)];
        vm.Preview();
        string[] again = [.. vm.PreviewRows.Select(row => row.Callsign)];
        other.Preview();
        string[] elsewhere = [.. other.PreviewRows.Select(row => row.Callsign)];
        int before = vm.Seed;
        vm.RerollCommand.Execute(null);

        Assert.Equal(8, first.Length);
        Assert.Equal(first, again);
        Assert.Equal(first, elsewhere);
        Assert.NotEqual(before, vm.Seed);
    }

    [Fact]
    public void Apply_WritesSpawnDelaysInOneUndoStep()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(6));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        SpawnPlannerScenario.UseBand(vm, "0", "10", "2:00", "0", "0");

        vm.Apply();

        Assert.Equal([0, 120, 240, 360, 480, 600], Delays(session));
        Assert.True(session.CanUndo);
        Assert.Equal("Apply spawn plan", session.UndoDescription);
        Assert.True(session.Undo());
        Assert.Equal([30, 60, 90, 120, 150, 180], Delays(session));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Apply_AfterDocumentReplaced_UsesTheNewDocument()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(6));
        var vm = new SpawnPlannerViewModel(
            session,
            (description, change) =>
            {
                Assert.True(session.Edit("Remove aircraft", scenario => scenario.Aircraft.RemoveAt(0)));
                Assert.True(session.Undo());
                Assert.True(session.Redo());
                Assert.True(session.Edit(description, change));
            }
        );
        SpawnPlannerScenario.UseBand(vm, "0", "10", "2:00", "0", "0");
        vm.Preview();
        Assert.Equal(6, vm.PreviewRows.Count);

        vm.Apply();

        Assert.Equal(["AAL2", "AAL3", "AAL4", "AAL5", "AAL6"], session.Document.Scenario.Aircraft.Select(a => a.AircraftId));
        Assert.Equal([0, 120, 240, 360, 480], Delays(session));
    }

    [Fact]
    public void Apply_ExtendRemedy_SetsLengthPlusWorkingTime()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(7));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.PlanningLengthMinutes = 30;
        SpawnPlannerScenario.UseBand(vm, "0", "60", "10:00", "0", "0");
        vm.SelectedRemedy = SpawnOverrunRemedy.ExtendScenario;

        vm.Apply();

        Assert.Equal(4200, vm.PlanningLengthSec);
        Assert.Equal([0, 600, 1200, 1800, 2400, 3000, 3600], Delays(session));
    }

    [Fact]
    public void Apply_CompressRemedy_WritesTheCompressedDelays()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(8));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.PlanningLengthSec = 600;
        vm.MinGapSec = 120;
        SpawnPlannerScenario.UseBand(vm, "0", "20", "2:00", "0", "0");
        vm.SelectedRemedy = SpawnOverrunRemedy.CompressIntervals;

        vm.Apply();

        Assert.Equal([0, 86, 171, 257, 343, 429, 514, 600], Delays(session));
        Assert.Equal(600, vm.PlanningLengthSec);
    }

    [Fact]
    public void AddBand_StartsOneIntervalAfterTheLastEnd_SoNoSlotRepeats()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(1));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.Bands[0].PlusText = "0";
        vm.Bands[0].MinusText = "0";

        vm.AddBandCommand.Execute(null);
        vm.Preview();

        Assert.Equal(2, vm.Bands.Count);
        Assert.Equal("62", vm.Bands[1].FromText);
        Assert.Equal(vm.PreviewSlots.Count, vm.PreviewSlots.Distinct().Count());
        Assert.Equal(3720, vm.PreviewSlots[31]);
    }

    [Fact]
    public void Refresh_AfterAnEdit_UpdatesTheMatchedCountAndDropsThePreview()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(4));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.Preview();
        Assert.True(session.Edit("Remove aircraft", scenario => scenario.Aircraft.RemoveAt(0)));
        Assert.Equal(4, vm.MatchedCount);

        vm.Refresh();

        Assert.Equal(3, vm.MatchedCount);
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public void AutoFill_WithoutAPlanningLength_KeepsThePointsAndReports()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(1)));
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(10, 70));
        vm.PlanningLengthSec = 0;

        vm.AutoFillCommand.Execute(null);

        Assert.Equal([(10.0, 70.0)], vm.ControlPoints.Select(p => (p.TimeMinutes, p.Percent)));
        Assert.Equal(SpawnPlannerViewModel.LengthErrorText, vm.PreviewError);
    }

    [Fact]
    public void Extend_AddsWorkingTime()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(7));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.PlanningLengthMinutes = 30;
        SpawnPlannerScenario.UseBand(vm, "0", "60", "10:00", "0", "0");
        vm.Preview();
        Assert.Equal(3600, vm.Overrun!.ExtendedLengthSec);

        vm.ExtendCommand.Execute(null);

        Assert.Equal(4200, vm.PlanningLengthSec);
    }

    [Fact]
    public void Compress_PreviewShowsTheWarningBeforeApply()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(8));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.PlanningLengthSec = 600;
        vm.MinGapSec = 120;
        SpawnPlannerScenario.UseBand(vm, "0", "20", "2:00", "0", "0");
        vm.SelectedRemedy = SpawnOverrunRemedy.CompressIntervals;

        vm.Preview();

        Assert.True(vm.Overrun!.CompressBreaksSeparation);
        Assert.Equal(SpawnPlannerViewModel.CompressWarningText, vm.CompressWarning);
        Assert.Equal("Compressing brings same-origin spawns closer than the minimum gap.", vm.CompressWarning);
        Assert.Contains(SpawnPlannerViewModel.CompressWarningText, vm.Warnings);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Warn_BandMinusJitterAtLeastInterval()
    {
        var row = new SpawnBandRowViewModel("0", "60", "1:00", "0", "60");

        Assert.Equal("Minus jitter is at least the interval: spawns in this band can change order.", row.Warning);
        row.MinusText = "59";
        Assert.Null(row.Warning);
        Assert.Null(row.Error);
    }

    [Fact]
    public void Defaults()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(1)));

        Assert.Equal(3600, vm.PlanningLengthSec);
        Assert.Equal(60, vm.PlanningLengthMinutes);
        Assert.Equal(10, vm.WorkingTimeMinutes);
        Assert.Equal(60, vm.MinGapSec);
        Assert.Equal("arrivals or en route traffic: 15-20 min", SpawnPlannerViewModel.WorkingTimeHelp);
        Assert.Equal(
            "The minimum gap is a time gap between spawns at one origin, not a separation standard: 60 s suits departures and "
                + "terminal fixes, 90-120 s en route or mixed speeds. Departures on one runway need 3 minutes behind a super and 2 "
                + "behind a heavy for wake, so space heavies 120 s or more apart if you do not want wake holds.",
            SpawnPlannerViewModel.GapGuidance
        );
    }

    [Fact]
    public void KindIndex_RoundTripsEveryChoice_AndFollowsKind()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(1)));
        SpawnKind?[] kinds = [null, SpawnKind.Departure, SpawnKind.Arrival, SpawnKind.Overflight];
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(Enum.GetValues<SpawnKind>().Length + 1, SpawnPlannerViewModel.KindChoices.Count);
        for (int index = 0; index < kinds.Length; index++)
        {
            vm.KindIndex = index;
            Assert.Equal(kinds[index], vm.Kind);
            Assert.Equal(index, vm.KindIndex);
        }

        raised.Clear();
        vm.Kind = SpawnKind.Arrival;
        Assert.Contains(nameof(SpawnPlannerViewModel.KindIndex), raised);
        Assert.Equal(2, vm.KindIndex);
        vm.KindIndex = SpawnPlannerViewModel.KindChoices.Count;
        Assert.Null(vm.Kind);
    }

    private static int[] Delays(ScenarioEditSession session) => [.. session.Document.Scenario.Aircraft.Select(a => a.SpawnDelay)];
}

/// <summary>The spawn planner tests' scenarios: real airports, parking starts, and a planner over a text session.</summary>
internal static class SpawnPlannerScenario
{
    public static ScenarioEditSession Session(params ScenarioAircraft[] aircraft)
    {
        var scenario = new Scenario
        {
            Id = "spawn-plan",
            Name = "Spawn plan",
            ArtccId = "ZOA",
            PrimaryAirportId = "KSFO",
            Aircraft = [.. aircraft],
        };
        return ScenarioEditSession.OverText("ZOA", "spawn-plan", ScenarioDocument.FromScenario(scenario).Write());
    }

    // Departures from one airport, spawning 30 s apart from 30 s on.
    public static ScenarioAircraft[] Departures(int count, string departure = "KSFO") =>
        [.. Enumerable.Range(1, count).Select(i => Ground($"AAL{i}", departure, i * 30))];

    public static SpawnPlannerViewModel Planner(ScenarioEditSession session) =>
        new(session, (description, change) => session.Edit(description, change));

    public static void UseBand(SpawnPlannerViewModel vm, string from, string to, string interval, string plus, string minus)
    {
        vm.Bands.Clear();
        vm.Bands.Add(new SpawnBandRowViewModel(from, to, interval, plus, minus));
    }

    private static ScenarioAircraft Ground(string callsign, string departure, int spawnDelay) =>
        new()
        {
            Id = callsign,
            AircraftId = callsign,
            AircraftType = "B738",
            SpawnDelay = spawnDelay,
            FlightPlan = new ScenarioFlightPlan
            {
                Rules = "IFR",
                Departure = departure,
                Destination = "KLAX",
            },
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "A1" },
        };
}
