using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// A spawn plan file holds every planner input under a version; a load reads it back whole, refuses a version it does
/// not know while keeping the planner's inputs, and takes a plan for other airports with nothing matched.
/// </summary>
public sealed class SpawnPlanFileTests : IDisposable
{
    private readonly string _dir = YaatPaths.Combine("spawn-plan-tests", Guid.NewGuid().ToString("N"));

    public SpawnPlanFileTests()
    {
        TestVnasData.EnsureInitialized();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Plan_RoundTrip_PreservesEveryField()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        SetEveryField(vm);
        string path = Path.Combine(_dir, "plan.json");

        Assert.True(vm.SavePlan(path));
        SpawnPlannerViewModel loaded = SpawnPlannerScenario.Planner(session);
        bool ok = loaded.LoadPlanFile(path);

        Assert.True(ok);
        Assert.Null(loaded.PlanError);
        Assert.Equal(SpawnPlanFile.Serialize(vm.ToPlan()), SpawnPlanFile.Serialize(loaded.ToPlan()));
        Assert.Contains("\"version\": 1", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("VFR", loaded.FlightRules);
        Assert.Equal(SpawnKind.Arrival, loaded.Kind);
        Assert.Equal(SpawnPlannerMode.Workload, loaded.Mode);
        Assert.Equal(["5", "25", "1:30", "10", "20"], BandTexts(loaded.Bands[1]));
        Assert.Equal([(12.5, 80.0), (40.0, 10.0)], [.. loaded.ControlPoints.Select(p => (p.TimeMinutes, p.Percent))]);
        Assert.Equal(5400, loaded.PlanningLengthSec);
        Assert.Equal(17, loaded.WorkingTimeMinutes);
        Assert.Equal(90, loaded.MinGapSec);
        Assert.Equal(1234, loaded.Seed);
        Assert.True(loaded.RandomOrder);
        Assert.Equal(SpawnOverrunRemedy.CompressIntervals, loaded.SelectedRemedy);
    }

    [Fact]
    public void Plan_LoadUnknownVersion_ReportsAndKeepsCurrent()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3));
        SpawnPlannerViewModel source = SpawnPlannerScenario.Planner(session);
        SetEveryField(source);
        string path = Path.Combine(_dir, "future.json");
        File.WriteAllText(path, SpawnPlanFile.Serialize(source.ToPlan()).Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        vm.Departure = "KSFO";
        vm.Seed = 5;
        string before = SpawnPlanFile.Serialize(vm.ToPlan());

        bool ok = vm.LoadPlanFile(path);

        Assert.False(ok);
        Assert.NotNull(vm.PlanError);
        Assert.Contains("version 2", vm.PlanError, StringComparison.Ordinal);
        Assert.Equal(before, SpawnPlanFile.Serialize(vm.ToPlan()));
    }

    [Fact]
    public void Plan_UnknownAirports_LoadsWithMatchedZero()
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3));
        SpawnPlannerViewModel source = SpawnPlannerScenario.Planner(session);
        source.Departure = "EGLL";
        source.Destination = "LFPG";
        string path = Path.Combine(_dir, "elsewhere.json");
        Assert.True(source.SavePlan(path));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        Assert.Equal(3, vm.MatchedCount);

        bool ok = vm.LoadPlanFile(path);

        Assert.True(ok);
        Assert.Null(vm.PlanError);
        Assert.Equal("EGLL", vm.Departure);
        Assert.Equal(0, vm.MatchedCount);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"mode\": \"Bands\"}")]
    [InlineData("[1, 2]")]
    [InlineData("{\"version\": 1, \"remedy\": 7}")]
    [InlineData("{\"version\": 1, \"mode\": 9}")]
    [InlineData("{\"version\": 1, \"mode\": \"Sideways\"}")]
    [InlineData("{\"version\": 1, \"filter\": {\"kind\": 5}}")]
    [InlineData("{\"version\": 1, \"bands\": [null]}")]
    [InlineData("{\"version\": 1, \"controlPoints\": [{\"timeMinutes\": 5, \"percent\": 50}, null]}")]
    [InlineData("{\"version\": 1, \"planningLengthSec\": 0}")]
    [InlineData("{\"version\": 1, \"planningLengthSec\": -60}")]
    public void Plan_LoadBadFile_ReportsAndKeepsCurrent(string text)
    {
        ScenarioEditSession session = SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3));
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(session);
        SetEveryField(vm);
        string before = SpawnPlanFile.Serialize(vm.ToPlan());
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, text);

        bool ok = vm.LoadPlanFile(path);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(vm.PlanError));
        Assert.Equal(before, SpawnPlanFile.Serialize(vm.ToPlan()));
    }

    [Fact]
    public void Plan_LoadMissingFile_ReportsAndKeepsCurrent()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3)));
        string before = SpawnPlanFile.Serialize(vm.ToPlan());

        bool ok = vm.LoadPlanFile(Path.Combine(_dir, "absent.json"));

        Assert.False(ok);
        Assert.StartsWith("Cannot read the plan file", vm.PlanError, StringComparison.Ordinal);
        Assert.Equal(before, SpawnPlanFile.Serialize(vm.ToPlan()));
    }

    [Fact]
    public void Plan_VersionOnly_LoadsTheDefaults()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(3)));
        vm.PlanningLengthSec = 900;
        vm.WorkingTimeMinutes = 3;
        vm.MinGapSec = 5;
        string path = Path.Combine(_dir, "bare.json");
        File.WriteAllText(path, "{\"version\": 1}");

        bool ok = vm.LoadPlanFile(path);

        Assert.True(ok);
        Assert.Null(vm.PlanError);
        Assert.Equal(3600, vm.PlanningLengthSec);
        Assert.Equal(10, vm.WorkingTimeMinutes);
        Assert.Equal(60, vm.MinGapSec);
        Assert.Equal(3, vm.MatchedCount);
    }

    [Fact]
    public void SavePlan_UnwritablePath_ReportsAndReturnsFalse()
    {
        SpawnPlannerViewModel vm = SpawnPlannerScenario.Planner(SpawnPlannerScenario.Session(SpawnPlannerScenario.Departures(1)));

        bool ok = vm.SavePlan(Path.Combine(_dir, "no-such-folder", "plan.json"));

        Assert.False(ok);
        Assert.StartsWith("Cannot write the plan file", vm.PlanError, StringComparison.Ordinal);
    }

    private static string[] BandTexts(SpawnBandRowViewModel row) => [row.FromText, row.ToText, row.IntervalText, row.PlusText, row.MinusText];

    private static void SetEveryField(SpawnPlannerViewModel vm)
    {
        vm.FlightRules = "VFR";
        vm.Kind = SpawnKind.Arrival;
        vm.Departure = "KOAK";
        vm.Destination = "KSFO";
        vm.Approach = "I28R";
        vm.ExcludeDeparture = "KSJC";
        vm.ExcludeDestination = "KSMF";
        vm.ExcludeCallsigns = "AAL1, UAL2";
        vm.Mode = SpawnPlannerMode.Workload;
        vm.Bands.Clear();
        vm.Bands.Add(new SpawnBandRowViewModel("0", "5", "2:00", "0", "0"));
        vm.Bands.Add(new SpawnBandRowViewModel("5", "25", "1:30", "10", "20"));
        vm.BucketSizeSec = 600;
        vm.MinIntervalSec = 45;
        vm.MaxIntervalSec = 400;
        vm.JitterSec = 25;
        vm.StartPercent = 15;
        vm.EndPercent = 35;
        vm.DistributeAllMatched = false;
        vm.AutoPeaks = 3;
        vm.AutoPeakPercent = 90;
        vm.AutoTroughPercent = 25;
        vm.ControlPoints.Clear();
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(12.5, 80));
        vm.ControlPoints.Add(new WorkloadPointRowViewModel(40, 10));
        vm.PlanningLengthSec = 5400;
        vm.WorkingTimeMinutes = 17;
        vm.MinGapSec = 90;
        vm.Seed = 1234;
        vm.RandomOrder = true;
        vm.SelectedRemedy = SpawnOverrunRemedy.CompressIntervals;
    }
}
