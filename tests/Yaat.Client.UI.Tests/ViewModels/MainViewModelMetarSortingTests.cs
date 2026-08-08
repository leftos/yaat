using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The METAR window lists stations alphabetically, with stations favorited for the active
/// scenario surfaced to the top (alphabetical among themselves). Favorites persist per-scenario
/// in UserPreferences. Unique scenario keys keep tests independent of the shared per-process
/// preferences.json and of test ordering.
/// </summary>
public class MainViewModelMetarSortingTests
{
    private const string SfoMetar = "METAR KSFO 081956Z 31019KT 10SM FEW008 18/13 A2997";
    private const string OakMetar = "METAR KOAK 082018Z 28011KT 10SM SCT010 18/13 A2998";
    private const string NuqMetar = "METAR KNUQ 082015Z AUTO 35011KT 10SM CLR 21/15 A2996";
    private const string HafMetar = "METAR KHAF 082015Z AUTO 32006KT 7SM SCT008 16/15 A3001";

    [AvaloniaFact]
    public void PopulateMetars_NoFavorites_SortsAlphabetically()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.PopulateMetars([SfoMetar, OakMetar, NuqMetar, HafMetar]);

        Assert.Equal(["HAF", "NUQ", "OAK", "SFO"], vm.Metars.Select(m => m.StationId));
    }

    [AvaloniaFact]
    public void PopulateMetars_FavoritesSurfaceToTop_AlphabeticalWithinGroups()
    {
        const string scenarioId = "TEST-metar-fav-sort-scenario";
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ActiveScenarioId = scenarioId;
        vm.Preferences.SetFavoriteMetarStation(scenarioId, "SFO", true);
        vm.Preferences.SetFavoriteMetarStation(scenarioId, "NUQ", true);

        vm.PopulateMetars([SfoMetar, OakMetar, NuqMetar, HafMetar]);

        Assert.Equal(["NUQ", "SFO", "HAF", "OAK"], vm.Metars.Select(m => m.StationId));
        Assert.Equal([true, true, false, false], vm.Metars.Select(m => m.IsFavorite));
    }

    [AvaloniaFact]
    public void ToggleMetarFavorite_ReordersAndPersists()
    {
        const string scenarioId = "TEST-metar-fav-toggle-scenario";
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ActiveScenarioId = scenarioId;
        vm.PopulateMetars([SfoMetar, OakMetar, HafMetar]);
        Assert.Equal(["HAF", "OAK", "SFO"], vm.Metars.Select(m => m.StationId));

        var sfo = vm.Metars.Single(m => m.StationId == "SFO");
        vm.ToggleMetarFavoriteCommand.Execute(sfo);

        Assert.Equal(["SFO", "HAF", "OAK"], vm.Metars.Select(m => m.StationId));
        Assert.True(vm.Preferences.IsFavoriteMetarStation(scenarioId, "SFO"));

        // Toggling again unfavorites and restores plain alphabetical order.
        vm.ToggleMetarFavoriteCommand.Execute(vm.Metars.Single(m => m.StationId == "SFO"));
        Assert.Equal(["HAF", "OAK", "SFO"], vm.Metars.Select(m => m.StationId));
        Assert.False(vm.Preferences.IsFavoriteMetarStation(scenarioId, "SFO"));
    }

    [AvaloniaFact]
    public void PopulateMetars_UnparseableMetar_SortsLastAndCannotBeFavorited()
    {
        const string scenarioId = "TEST-metar-fav-unparseable-scenario";
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ActiveScenarioId = scenarioId;

        vm.PopulateMetars(["bad metar line x", SfoMetar, OakMetar]);

        Assert.Equal(["OAK", "SFO", null], vm.Metars.Select(m => m.StationId));
        Assert.False(vm.Metars[^1].CanFavorite);
        Assert.True(vm.Metars[0].CanFavorite);
    }

    [AvaloniaFact]
    public void PopulateMetars_NoActiveScenario_NothingCanBeFavorited()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.PopulateMetars([SfoMetar, OakMetar]);

        Assert.All(vm.Metars, m => Assert.False(m.CanFavorite));
    }

    [AvaloniaFact]
    public void ActiveScenarioChange_ReordersForNewScenarioFavorites()
    {
        const string scenarioA = "TEST-metar-fav-switch-A";
        const string scenarioB = "TEST-metar-fav-switch-B";
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.Preferences.SetFavoriteMetarStation(scenarioB, "SFO", true);

        vm.ActiveScenarioId = scenarioA;
        vm.PopulateMetars([SfoMetar, OakMetar, HafMetar]);
        Assert.Equal(["HAF", "OAK", "SFO"], vm.Metars.Select(m => m.StationId));

        vm.ActiveScenarioId = scenarioB;

        Assert.Equal(["SFO", "HAF", "OAK"], vm.Metars.Select(m => m.StationId));
    }
}
