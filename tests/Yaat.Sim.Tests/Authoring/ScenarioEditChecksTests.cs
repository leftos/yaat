using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// The Scenario Editor's live checks over real scenarios: the untouched corpus gives no error, and a real scenario edited
/// to break one check fires that check on the aircraft, field and preset it broke. Positions are checked against a
/// catalog of the committed ZOA and ZLA snapshots plus each scenario's own ATC rows; TAXI presets route on the committed
/// OAK layout.
/// </summary>
public sealed class ScenarioEditChecksTests
{
    private const string Oak4 = "01HG3N8Q5PPR7QXZK33ZPC4D5M";

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly string[] TestDataScenarios =
    [
        Path.Combine("VnasTraining", "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"),
        Path.Combine("VnasTraining", "01H08FSWF5NCDXTQMQ3BWB0BND.json"),
        "oak-arrival-generators-scenario.json",
        "issue153-s2-oak-5-2-scenario.json",
        "sfo-gc-scenario.json",
    ];

    private static readonly TestAirportGroundData Layouts = new();

    /// <summary>Every example scenario under <c>docs/atctrainer-scenario-examples</c>, then the TestData scenarios.</summary>
    public static TheoryData<string> CorpusScenarios()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(ExamplesDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        foreach (string relative in TestDataScenarios)
        {
            data.Add(Path.Combine(TestDataDir, relative));
        }

        return data;
    }

    /// <summary>Every starting-condition kind the editor lists.</summary>
    public static TheoryData<string> Kinds() => [.. StartingConditionKinds.Kinds];

    [Theory]
    [MemberData(nameof(CorpusScenarios))]
    public void Corpus_Untouched_ReportsNoErrors(string scenarioPath)
    {
        Scenario scenario = Read(scenarioPath);

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, Layouts));

        foreach (ScenarioEditIssue warning in issues.Where(i => i.Severity != ScenarioEditIssueSeverity.Error))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(scenarioPath)}: {Describe(warning)}");
        }

        ScenarioEditIssue[] errors = [.. issues.Where(i => i.Severity == ScenarioEditIssueSeverity.Error)];
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(Describe)));
    }

    [Fact]
    public void PresetTypo_FiresOnThatAircraftAndPreset()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N80ZU");
        PresetCommand preset = aircraft.PresetCommands[0];
        preset.Command = "DM 2O";

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.Severity == ScenarioEditIssueSeverity.Error);
        Assert.Equal(aircraft.Id, issue.AircraftId);
        Assert.Equal("N80ZU", issue.Callsign);
        Assert.Equal(ScenarioEditFields.PresetCommand, issue.Field);
        Assert.Equal(preset.Id, issue.PresetId);
    }

    [Fact]
    public void ProcedureNotFound_Fires()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N80ZU");
        string missing = MissingProcedureOn(scenario);
        NavigationDatabase navDb = NavigationDatabase.Instance;
        Assert.NotNull(navDb.GetFixPosition(missing[..^1]));
        Assert.Null(navDb.GetFixPosition(missing));
        Assert.Null(navDb.ResolveSidId(missing));
        Assert.Null(navDb.ResolveStarId(missing));
        aircraft.StartingConditions.NavigationPath = missing + " OAK";

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.Severity == ScenarioEditIssueSeverity.Error);
        Assert.Equal(aircraft.Id, issue.AircraftId);
        Assert.Equal(ScenarioEditFields.NavigationPath, issue.Field);
        Assert.Contains(missing, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateCallsign_FiresOnEveryAircraftOfTheGroup_CaseInsensitive()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft first = scenario.Aircraft[0];
        ScenarioAircraft second = scenario.Aircraft[1];
        second.AircraftId = first.AircraftId.ToLowerInvariant();

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

        ScenarioEditIssue[] callsigns = [.. issues.Where(i => i.Field == ScenarioEditFields.Callsign)];
        Assert.Equal([first.Id, second.Id], callsigns.Select(i => i.AircraftId));
        Assert.All(callsigns, i => Assert.Equal(ScenarioEditIssueSeverity.Error, i.Severity));
        Assert.Equal(2, issues.Count(i => i.Severity == ScenarioEditIssueSeverity.Error));
    }

    [Fact]
    public void AutoTrackWithoutPosition_Fires()
    {
        Scenario scenario = Read(Example("01HCHWFSVGKA6H0F0QSFFG9MMN"));
        ScenarioAircraft aircraft = scenario.Aircraft.First(a => a.AutoTrackConditions is not null);
        aircraft.AutoTrackConditions!.PositionId = "";

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.Severity == ScenarioEditIssueSeverity.Error);
        Assert.Equal(aircraft.Id, issue.AircraftId);
        Assert.Equal(ScenarioEditFields.AutoTrackPosition, issue.Field);
    }

    [Fact]
    public void AutoTrackPositionNotInCatalog_Warns_AndQuietWithNoCatalog()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N80ZU");
        aircraft.AutoTrackConditions!.PositionId = "01NOTAPOSITION000000000000";

        IReadOnlyList<ScenarioEditIssue> withCatalog = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));
        IReadOnlyList<ScenarioEditIssue> withoutCatalog = ScenarioEditChecks.Run(
            scenario,
            new ScenarioEditInputs(() => NavigationDatabase.InstanceOrNull is not null, null, null)
        );

        ScenarioEditIssue issue = Assert.Single(withCatalog, i => (i.AircraftId == aircraft.Id) && (i.Field == ScenarioEditFields.AutoTrackPosition));
        Assert.Equal(ScenarioEditIssueSeverity.Warning, issue.Severity);
        Assert.DoesNotContain(withoutCatalog, i => i.Field == ScenarioEditFields.AutoTrackPosition);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void KindMissingField_Fires_PerKind(string kind)
    {
        IReadOnlyList<string> required = StartingConditionKinds.RequiredOf(kind);
        Assert.NotEmpty(required);
        foreach (string member in required)
        {
            (Scenario scenario, ScenarioAircraft aircraft) = FirstOfKind(kind);
            Clear(aircraft.StartingConditions, member);

            IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

            ScenarioEditIssue issue = Assert.Single(issues, i => (i.AircraftId == aircraft.Id) && (i.Field == member));
            Assert.Equal(ScenarioEditIssueSeverity.Error, issue.Severity);
        }
    }

    [Fact]
    public void TaxiPresetUnroutable_Fires_OnRealOak()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N172SP");
        PresetCommand preset = aircraft.PresetCommands[0];
        Assert.Equal("TAXI D C B 28R", preset.Command);
        preset.Command = "TAXI D ZZ B 28R";

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, Layouts));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.Severity == ScenarioEditIssueSeverity.Error);
        Assert.Equal(aircraft.Id, issue.AircraftId);
        Assert.Equal(ScenarioEditFields.PresetCommand, issue.Field);
        Assert.Equal(preset.Id, issue.PresetId);
    }

    [Fact]
    public void TaxiAtGateDestination_IsNotChecked()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N172SP");
        PresetCommand preset = aircraft.PresetCommands[0];
        preset.Command = "TAXI D C @GATE";

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, Layouts));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.PresetId == preset.Id);
        Assert.Equal(ScenarioEditIssueSeverity.NotChecked, issue.Severity);
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioEditIssueSeverity.Error);
    }

    [Fact]
    public void OnlyTheFirstTaxi_IsChecked()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N172SP");
        aircraft.PresetCommands.Add(new PresetCommand { Id = "01KTESTSECONDTAXI000000000", Command = "TAXI D ZZ B 28R" });

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, Layouts));

        Assert.DoesNotContain(issues, i => i.AircraftId == aircraft.Id);
    }

    [Fact]
    public void TaxiOnAParkingStartWithNoMap_IsNotChecked()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N172SP");
        PresetCommand preset = aircraft.PresetCommands[0];
        Assert.Equal(StartingConditionKinds.Parking, aircraft.StartingConditions.Type);
        Assert.Equal("TAXI D C B 28R", preset.Command);

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));

        ScenarioEditIssue issue = Assert.Single(issues, i => i.PresetId == preset.Id);
        Assert.Equal(ScenarioEditIssueSeverity.NotChecked, issue.Severity);
        Assert.Equal(aircraft.Id, issue.AircraftId);
        Assert.Equal(ScenarioEditFields.PresetCommand, issue.Field);
    }

    [Fact]
    public void TaxiOnAnAirborneStart_IsNeverNotChecked()
    {
        Scenario scenario = Read(Example(Oak4));
        ScenarioAircraft aircraft = Callsign(scenario, "N80ZU");
        PresetCommand preset = aircraft.PresetCommands[0];
        preset.Command = "TAXI B 28R";

        IReadOnlyList<ScenarioEditIssue> withoutMaps = ScenarioEditChecks.Run(scenario, Inputs(scenario, null));
        IReadOnlyList<ScenarioEditIssue> withMaps = ScenarioEditChecks.Run(scenario, Inputs(scenario, Layouts));

        Assert.DoesNotContain(withoutMaps, i => i.PresetId == preset.Id);
        Assert.DoesNotContain(withMaps, i => i.PresetId == preset.Id);
    }

    [Fact]
    public void NavdataNotLoaded_ReportsAnIssue_AndDoesNotThrow()
    {
        Scenario scenario = Read(Example(Oak4));

        IReadOnlyList<ScenarioEditIssue> issues = ScenarioEditChecks.Run(scenario, new ScenarioEditInputs(() => false, null, Layouts));

        ScenarioEditIssue issue = Assert.Single(issues);
        Assert.Null(issue.AircraftId);
        Assert.Equal(ScenarioEditFields.Scenario, issue.Field);
        Assert.Equal(ScenarioEditChecks.NavdataNotLoadedText, issue.Message);
    }

    private static string Example(string id) => Path.Combine(ExamplesDir, id + ".json");

    private static Scenario Read(string path) => ScenarioDocument.Read(File.ReadAllText(path)).Scenario;

    private static ScenarioAircraft Callsign(Scenario scenario, string callsign) =>
        scenario.Aircraft.Single(a => string.Equals(a.AircraftId, callsign, StringComparison.Ordinal));

    private static ScenarioEditInputs Inputs(Scenario scenario, TestAirportGroundData? layouts) =>
        new(() => NavigationDatabase.InstanceOrNull is not null, Catalog(scenario), layouts);

    // The committed ZOA and ZLA snapshots' positions, plus the scenario's own ATC rows.
    private static HashSet<string> Catalog(Scenario scenario)
    {
        ArtccConfigRoot zoa = TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("the ZOA snapshot is missing");
        ArtccConfigRoot zla = TestArtccConfig.LoadZla() ?? throw new InvalidOperationException("the ZLA snapshot is missing");
        var catalog = ArtccPositionCatalog.Build(zoa, [zla], []);
        var ids = new HashSet<string>(catalog.Positions.Select(p => p.PositionId), StringComparer.Ordinal);
        ids.UnionWith((scenario.Atc ?? []).Select(a => a.PositionId));
        return ids;
    }

    // The first aircraft of the kind in the example corpus, in a fresh read of its scenario.
    private static (Scenario Scenario, ScenarioAircraft Aircraft) FirstOfKind(string kind)
    {
        foreach (string path in Directory.EnumerateFiles(ExamplesDir, "*.json").Order(StringComparer.Ordinal))
        {
            Scenario scenario = Read(path);
            if (scenario.Aircraft.FirstOrDefault(a => a.StartingConditions.Type == kind) is { } aircraft)
            {
                return (scenario, aircraft);
            }
        }

        throw new InvalidOperationException($"no example scenario has a {kind} aircraft");
    }

    // A procedure ScenarioValidator reports as not found: a fix of the scenario's routes and paths with a version digit
    // that names no fix, SID or STAR.
    private static string MissingProcedureOn(Scenario scenario)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        IEnumerable<string> tokens = scenario.Aircraft.SelectMany(a =>
            $"{a.StartingConditions.NavigationPath} {a.FlightPlan?.Route}".Split(' ', StringSplitOptions.RemoveEmptyEntries)
        );
        foreach (string fix in tokens.Where(t => t.All(char.IsLetter)).Distinct(StringComparer.Ordinal))
        {
            string candidate = fix + "1";
            bool isProcedure = (navDb.ResolveSidId(candidate) is not null) || (navDb.ResolveStarId(candidate) is not null);
            if (!isProcedure && (navDb.GetFixPosition(candidate) is null) && (navDb.GetFixPosition(fix) is not null))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("no fix of the scenario makes a missing procedure");
    }

    private static void Clear(StartingConditions conditions, string member)
    {
        switch (member)
        {
            case "parking":
                conditions.Parking = null;
                break;
            case "runway":
                conditions.Runway = null;
                break;
            case "fix":
                conditions.Fix = null;
                break;
            case "coordinates":
                conditions.Coordinates = null;
                break;
            default:
                throw new InvalidOperationException($"no clear for {member}");
        }
    }

    private static string Describe(ScenarioEditIssue issue) =>
        $"{issue.Severity} {issue.Callsign ?? "(scenario)"} {issue.Field}{(issue.PresetId is null ? "" : " " + issue.PresetId)}: {issue.Message}";
}
