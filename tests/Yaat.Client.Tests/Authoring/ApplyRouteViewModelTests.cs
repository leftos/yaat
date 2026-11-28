using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Apply Route for the aircraft selected in the Scenario Editor's Aircraft tab, over the committed SFO ground scenario,
/// real navdata and aircraft tables, and real profile and route stores in a per-test folder: the preview is exactly the
/// stamp's change list, and Apply writes exactly what the preview showed as one undo step.
/// </summary>
public sealed class ApplyRouteViewModelTests : IDisposable
{
    private const string CopyId = "C1";

    private const int Seed = 11;

    private static readonly DateTimeOffset FixedNow = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ScenarioEditorFixture _fx = new();

    private readonly FakeTimeProvider _clock = new(FixedNow);

    private readonly string _root = YaatPaths.Combine("apply-route-tests", Guid.NewGuid().ToString("N"));

    public ApplyRouteViewModelTests()
    {
        TestVnasData.EnsureInitialized();
        TestVnasData.EnsureAircraftDataInitialized();
        Profiles = new StampProfileStore(Path.Combine(_root, "stamp-profiles"));
        Library = new RouteLibrary(Path.Combine(_root, "routes"));
    }

    private StampProfileStore Profiles { get; }

    private RouteLibrary Library { get; }

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Preview_RowsAreTheStampsChanges_WithLabels_DocumentUnchanged()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        StampProfile profile = Save(CruiseAndPreset("Cruise"));
        SavedRoute saved = SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        string before = session.Document.Write();
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Id == profile.Id);
        vm.SelectedRoute = vm.Routes.Single(r => r.Id == saved.Id);

        vm.Preview();

        StampResult expected = ExpectedStamp(session, 0, LibraryRoute(session, 0, saved), profile, FirstSeeds(), []);
        Assert.Equal(
            expected.Changes.Select(c => (c.Path, c.Kind, c.OldValue, c.NewValue)),
            vm.PreviewRows.Select(r => (r.Path, r.Kind, r.OldValue, r.NewValue))
        );
        Assert.All(vm.PreviewRows, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
        Assert.Equal("Route", vm.PreviewRows.Single(r => r.Path == "/flightplan/route").Label);
        Assert.Equal("Cruise altitude (ft)", vm.PreviewRows.Single(r => r.Path == "/flightplan/cruiseAltitude").Label);
        Assert.Equal("Remarks", vm.PreviewRows.Single(r => r.Path == "/flightplan/remarks").Label);
        Assert.Contains(vm.PreviewRows, r => r.Label == "Preset SAY EJA427");
        Assert.Equal(before, session.Document.Write());
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_WritesTheStampForTheKeptSeeds_OneUndoStep()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        StampProfile profile = Save(CruiseAndPreset("Cruise"));
        SavedRoute saved = SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        string before = session.Document.Write();
        StampResult expected = ExpectedStamp(session, 0, LibraryRoute(session, 0, saved), profile, FirstSeeds(), []);
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();

        vm.ApplyCommand.Execute(null);

        Assert.Equal(Json(expected.Aircraft), Json(session.Document.Scenario.Aircraft[0]));
        Assert.Equal("Apply Route", session.UndoDescription);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
    }

    [Fact]
    public void Apply_AfterAnEditAndAClockAdvance_StampsTheAircraftAsItIsNow_WithTheKeptTimeAndSeeds()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        StampProfile cruise = CruiseAndPreset("Cruise");
        StampProfile profile = Save(cruise with { FlightPlan = cruise.FlightPlan with { Remarks = null } });
        SavedRoute saved = SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        ScenarioAircraftRowViewModel row = aircraft.Selected;
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();

        Assert.True(session.Edit("x", s => s.Aircraft[0].FlightPlan!.Remarks = "EDITED"));
        aircraft.Reload();
        Assert.Same(row, aircraft.Selected);
        _clock.Advance(TimeSpan.FromHours(1));
        StampResult expected = ExpectedStamp(session, 0, LibraryRoute(session, 0, saved), profile, FirstSeeds(), []);
        vm.ApplyCommand.Execute(null);

        Assert.Equal("EDITED", session.Document.Scenario.Aircraft[0].FlightPlan!.Remarks);
        Assert.Equal(Json(expected.Aircraft), Json(session.Document.Scenario.Aircraft[0]));
        Assert.Equal("Apply Route", session.UndoDescription);
    }

    [Fact]
    public void RowReorderAfterPreview_DisablesApply_AndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();
        Assert.True(vm.ApplyCommand.CanExecute(null));

        Assert.True(session.Edit("Swap", s => (s.Aircraft[0], s.Aircraft[1]) = (s.Aircraft[1], s.Aircraft[0])));
        aircraft.Reload();
        string after = session.Document.Write();

        Assert.False(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);
        Assert.Equal(after, session.Document.Write());
        Assert.Equal("Swap", session.UndoDescription);
    }

    [Fact]
    public void SkipFoundAtApply_IsShown_AndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpusWith(0, a => a["aircraftType"] = "B77W");
        Save(CruiseAndPreset("Heavies") with { HeavyOnly = true });
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();
        Assert.True(vm.ApplyCommand.CanExecute(null));

        Assert.True(session.Edit("x", s => s.Aircraft[0].AircraftType = "E55P"));
        aircraft.Reload();
        string after = session.Document.Write();
        vm.ApplyCommand.Execute(null);

        Assert.StartsWith("Skipped: E55P is wake class", vm.SkipText, StringComparison.Ordinal);
        Assert.Equal(after, session.Document.Write());
        Assert.Equal("x", session.UndoDescription);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void StampAlreadyInTheDocumentAtApply_SaysNothingToChange_AndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        StampProfile profile = Save(CruiseAndPreset("Cruise") with { Presets = new StampPresets() });
        SavedRoute saved = SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();
        Assert.True(vm.ApplyCommand.CanExecute(null));

        StampResult stamped = ExpectedStamp(session, 0, LibraryRoute(session, 0, saved), profile, FirstSeeds(), []);
        Assert.True(session.Edit("x", s => s.Aircraft[0] = stamped.Aircraft));
        aircraft.Reload();
        string after = session.Document.Write();
        vm.ApplyCommand.Execute(null);

        Assert.Equal("Nothing to change: this profile and route already match EJA427.", vm.NothingText);
        Assert.Equal(after, session.Document.Write());
        Assert.Equal("x", session.UndoDescription);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void StampWithNoChanges_SaysSo_AndDisablesApply()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(new StampProfile { Id = "noop", Name = "Noop" });
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.UseFoundRoute(new FoundRoute("SFO MOD", 1, ["Test"], null, null, null));

        vm.Preview();

        Assert.Empty(vm.PreviewRows);
        Assert.Equal("Nothing to change: this profile and route already match EJA427.", vm.NothingText);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void NoSelectedAircraft_DisablesApply()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();

        vm.Preview();

        Assert.Null(aircraft.Selected);
        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void NoProfile_DisablesApply()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedRoute = vm.Routes.Single();

        vm.Preview();

        Assert.Null(vm.SelectedProfile);
        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void NoRoute_DisablesApply()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();

        vm.Preview();

        Assert.Empty(vm.PreviewRows);
        Assert.Null(vm.RouteProblem);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ProfileChangeAfterPreview_ClearsIt_AndDisablesApply()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Alpha"));
        Save(CruiseAndPreset("Bravo"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles[0];
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.SelectedProfile = vm.Profiles[1];

        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void HeavyOnlyProfile_OnANonHeavyType_IsSkipped_AndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Heavies") with { HeavyOnly = true });
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        string before = session.Document.Write();
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();

        vm.Preview();

        Assert.Equal("E55P", session.Document.Scenario.Aircraft[0].AircraftType);
        Assert.StartsWith("Skipped: E55P is wake class", vm.SkipText, StringComparison.Ordinal);
        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);
        Assert.Equal(before, session.Document.Write());
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void LibraryRouteThatDoesNotConvert_ShowsWhy_AndDisablesApply()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("QQZZX"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();

        vm.Preview();

        Assert.Equal("Can't use this route: Route West: Point 2: Unknown fix QQZZX.", vm.RouteProblem);
        Assert.Empty(vm.PreviewRows);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void FoundRouteThenLibraryRoute_UsesTheLibraryRoute_AndTheReverse()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SavedRoute saved = SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        var found = new FoundRoute("SFO OAK", 1, ["Test"], null, null, null);
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();

        vm.UseFoundRoute(found);
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();

        Assert.Null(vm.FoundRoute);
        string libraryText = LibraryRoute(session, 0, saved).RouteText;
        Assert.Equal(JsonSerializer.Serialize(libraryText), RouteRow(vm).NewValue);

        vm.UseFoundRoute(found);
        vm.Preview();

        Assert.Null(vm.SelectedRoute);
        Assert.Equal("\"SFO OAK\"", RouteRow(vm).NewValue);
    }

    [Fact]
    public void SelectionChangeAfterPreview_ClearsIt_AndApplyWritesOnlyTheNewSelection()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Cruise"));
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        string firstBefore = Json(session.Document.Scenario.Aircraft[0]);
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedRoute = vm.Routes.Single();
        vm.Preview();
        Assert.NotEmpty(vm.PreviewRows);

        aircraft.Selected = aircraft.Rows[1];

        Assert.Empty(vm.PreviewRows);
        Assert.Empty(vm.Warnings);
        Assert.False(vm.ApplyCommand.CanExecute(null));

        vm.Preview();
        vm.ApplyCommand.Execute(null);

        Assert.Equal(firstBefore, Json(session.Document.Scenario.Aircraft[0]));
        Assert.Equal(35000, session.Document.Scenario.Aircraft[1].FlightPlan!.CruiseAltitude);
        Assert.Equal("Apply Route", session.UndoDescription);
    }

    [Fact]
    public void SameServicesSeed_GivesTheSamePreviewTwice()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(
            CruiseAndPreset("Pools") with
            {
                Pools = new StampPools { AircraftTypes = ["B738", "A320"], CallsignPrefixes = ["TST"] },
            }
        );
        SaveRoute("West", RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel first = NewVm(aircraft);
        using ApplyRouteViewModel second = NewVm(aircraft);
        foreach (ApplyRouteViewModel vm in (ApplyRouteViewModel[])[first, second])
        {
            vm.SelectedProfile = vm.Profiles.Single();
            vm.SelectedRoute = vm.Routes.Single();
            vm.Preview();
        }

        Assert.NotEmpty(first.PreviewRows);
        Assert.Equal(first.PreviewRows, second.PreviewRows);
        Assert.Equal(first.Warnings, second.Warnings);
    }

    [Fact]
    public void StampedCallsign_IsNoOtherAircraftsCallsign_IgnoringCase()
    {
        StampProfile profile = new()
        {
            Id = "pool",
            Name = "Pool",
            Pools = new StampPools { CallsignPrefixes = ["TST"] },
        };
        (ScenarioEditSession probe, _) = OpenOn(CorpusText(), "P1");
        StampRoute route = new("probe", "SFO MOD", []);
        string unconstrained = ExpectedStamp(probe, 0, route, profile, FirstSeeds(), []).Aircraft.AircraftId;
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpusWith(
            1,
            a => a["aircraftId"] = unconstrained.ToLowerInvariant()
        );
        Save(profile);
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.UseFoundRoute(new FoundRoute("SFO MOD", 1, ["Test"], null, null, null));
        vm.Preview();

        vm.ApplyCommand.Execute(null);

        List<ScenarioAircraft> all = session.Document.Scenario.Aircraft;
        string stamped = all[0].AircraftId;
        Assert.StartsWith("TST", stamped, StringComparison.Ordinal);
        Assert.DoesNotContain(all.Skip(1), a => string.Equals(a.AircraftId, stamped, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CorruptProfileFile_IsListedAsAProblem_AndTheOthersLoad()
    {
        (_, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Save(CruiseAndPreset("Alpha"));
        Save(CruiseAndPreset("Bravo"));
        File.WriteAllText(Path.Combine(Profiles.Root, "broken.json"), "{ not json");
        StoreProblem problem = Assert.Single(Profiles.List().Problems);

        using ApplyRouteViewModel vm = NewVm(aircraft);

        Assert.Equal(["Alpha", "Bravo"], vm.Profiles.Select(p => p.Name));
        Assert.Equal([$"Couldn't read {problem.FileName}: {problem.Reason}"], vm.ProfileProblems);
        Assert.Equal("broken.json", problem.FileName);
    }

    [Fact]
    public void ErrorWarning_IsListedFirst_AndApplyStaysEnabled()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel aircraft) = OpenCorpus();
        Assert.Null(NavigationDatabase.Instance.ResolveStarId("QQZZX7"));
        Save(
            CruiseAndPreset("Alias") with
            {
                StarAliases = [new StarAlias("QQZZY1", "QQZZX7")],
                Pools = new StampPools { AircraftTypes = [" "] },
            }
        );
        aircraft.Selected = aircraft.Rows[0];
        using ApplyRouteViewModel vm = NewVm(aircraft);
        vm.SelectedProfile = vm.Profiles.Single();
        vm.UseFoundRoute(new FoundRoute("LAX QQZZY1", 1, ["Test"], null, null, null));

        vm.Preview();

        Assert.StartsWith("Error:", vm.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Pool aircraft types has a blank entry; skipped", vm.Warnings);
        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Apply Route", session.UndoDescription);
    }

    // Cruise 35000, remarks, the route written to the plan, and one preset naming the callsign.
    private static StampProfile CruiseAndPreset(string name) =>
        new()
        {
            Id = name.ToLowerInvariant(),
            Name = name,
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                CruiseAltitude = 35000,
                Remarks = "STAMPED",
                ApplyRoute = true,
            },
            Presets = new StampPresets { Apply = true, Commands = [new StampPresetCommand("SAY {CS}", 30)] },
        };

    private static ApplyRoutePreviewRow RouteRow(ApplyRouteViewModel vm) => vm.PreviewRows.Single(r => r.Path == "/flightplan/route");

    private static string Json(ScenarioAircraft aircraft) => JsonSerializer.Serialize(aircraft);

    // The two seeds the first Preview draws from the services' random source: pool, then id.
    private static (int Pool, int Id) FirstSeeds()
    {
        var random = new Random(Seed);
        int pool = random.Next();
        return (pool, random.Next());
    }

    private static StampRoute LibraryRoute(ScenarioEditSession session, int index, SavedRoute saved)
    {
        ScenarioFlightPlan? plan = session.Document.Scenario.Aircraft[index].FlightPlan;
        StampRouteConversion conversion = StampRoute.FromSavedRoute(
            saved,
            new FixResolutionContext(plan?.Destination, plan?.Departure),
            NavigationDatabase.Instance
        );
        return conversion.Route ?? throw new InvalidOperationException(conversion.Failure);
    }

    // What RouteStamp makes of the aircraft at index with the given seeds and every other aircraft's callsign taken.
    private static StampResult ExpectedStamp(
        ScenarioEditSession session,
        int index,
        StampRoute route,
        StampProfile profile,
        (int Pool, int Id) seeds,
        IEnumerable<string> extraTaken
    )
    {
        List<ScenarioAircraft> all = session.Document.Scenario.Aircraft;
        var taken = new HashSet<string>(
            all.Where((_, i) => i != index).Select(a => a.AircraftId).Concat(extraTaken),
            StringComparer.OrdinalIgnoreCase
        );
        var context = new StampContext(FixedNow, new Random(seeds.Pool), new Random(seeds.Id), taken, MagneticDeclination.EvaluationDateUtc);
        return RouteStamp.Apply(all[index], route, profile, context);
    }

    private ApplyRouteViewModel NewVm(ScenarioAircraftViewModel aircraft) =>
        new(
            aircraft,
            new ApplyRouteServices(Profiles, Library, () => NavigationDatabase.Instance, new Random(Seed), _clock),
            ScenarioEditorFixture.Zoa
        );

    private StampProfile Save(StampProfile profile)
    {
        Profiles.Save(profile);
        return profile;
    }

    private SavedRoute SaveRoute(string name, params RoutePoint[] points)
    {
        var route = new SavedRoute(SavedRoute.NewId(), name, null, ScenarioEditorFixture.Zoa, points);
        Library.Save(route);
        return route;
    }

    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenCorpus() => OpenOn(CorpusText());

    // The corpus scenario with the aircraft at index changed by hand.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenCorpusWith(int index, Action<JsonNode> edit)
    {
        JsonNode root = JsonNode.Parse(CorpusText()) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        edit(root["aircraft"]![index]!);
        return OpenOn(root.ToJsonString());
    }

    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenOn(string text, string copyId = CopyId)
    {
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, text, copyId);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, copyId);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        return (session, vm);
    }

    private static string CorpusText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
}
