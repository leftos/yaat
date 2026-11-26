using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope toolbar's PREF menu: a menu labelled with the active pref set, whose tray lists the facility's sets and then
/// SAVE, SAVE AS, RENAME and DELETE.
/// </summary>
public class ScopePrefMenuTests : IDisposable
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-prefmenu-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PrefLabel_IsTheMenuTriangleAlone_ThenTheActiveSetsName()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.Equal("PREF ▸ —", DrawnText(ButtonOf(window, ScopeButtonId.Pref)));
            Assert.Equal("PREF", AutomationProperties.GetName(ButtonOf(window, ScopeButtonId.Pref)));

            main.RunScopeCommand(".scope pref saveas SR-DAY");

            Assert.Equal("PREF ▸ SR-DAY", DrawnText(ButtonOf(window, ScopeButtonId.Pref)));
            Run value = ((TextBlock)ButtonOf(window, ScopeButtonId.Pref).Content!).Inlines!.OfType<Run>().Last();
            Assert.Equal("SR-DAY", value.Text);
            Assert.Equal(FontWeight.Bold, value.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PrefTray_ListsTheSetsInFileOrder_ThenTheFourActions()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            main.RunScopeCommand(".scope pref saveas B");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Assert.Equal(["A", "B", "SAVE", "SAVE AS", "RENAME", "DELETE"], TrayButtons(window).Select(DrawnText));
            Assert.Contains("active", TrayButton(window, "B").Classes);
            Assert.DoesNotContain("active", TrayButton(window, "A").Classes);

            main.RunScopeCommand(".scope pref load a");

            Assert.Contains("active", TrayButton(window, "A").Classes);
            Assert.DoesNotContain("active", TrayButton(window, "B").Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ASetButton_LoadsItsSet_AndClosesTheTray()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            main.RunScopeCommand(".scope pref saveas B");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "A"));

            Assert.Equal(".scope pref load A", main.StatusText);
            Assert.Equal("A", scope.ActivePrefSet);
            Assert.Null(scope.OpenMenu);
            Assert.Empty(TrayButtons(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SaveAs_TypesThePrefixAndAsksForTheCommandBox()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            bool focused = false;
            main.RequestCommandInputFocus += () => focused = true;
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "SAVE AS"));

            Assert.Equal(".scope pref saveas ", main.CommandText);
            Assert.Equal(main.CommandText.Length, main.CommandCaretIndex);
            Assert.True(focused, "SAVE AS asks for the command box");
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Rename_WithAnActiveSet_TypesTheRename_AndClosesTheTray()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "RENAME"));

            Assert.Equal(".scope pref rename A ", main.CommandText);
            Assert.Equal(main.CommandText.Length, main.CommandCaretIndex);
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Rename_WithNoActiveSet_SaysWhatToDo_AndLeavesTheCommandBox()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.CommandText = "typed";
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "RENAME"));

            Assert.Equal("No pref set is active: use .scope pref saveas <name>", main.StatusText);
            Assert.Equal("typed", main.CommandText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Delete_WithAnActiveSet_DeletesIt_AndTheOpenTrayRebuilds()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            main.RunScopeCommand(".scope pref saveas B");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "DELETE"));

            Assert.Equal(".scope pref delete B", main.StatusText);
            Assert.Null(scope.ActivePrefSet);
            Assert.Equal(ScopeButtonId.Pref, scope.OpenMenu);
            Assert.Equal(["A", "SAVE", "SAVE AS", "RENAME", "DELETE"], TrayButtons(window).Select(DrawnText));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Delete_WithNoActiveSet_SaysWhatToDo()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Click(TrayButton(window, "DELETE"));

            Assert.Equal("No pref set is active: use .scope pref saveas <name>", main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnOpenTray_PicksUpASetSavedThroughTheCommandLine()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Click(ButtonOf(window, ScopeButtonId.Pref));

            main.RunScopeCommand(".scope pref saveas C");

            Assert.Equal(ScopeButtonId.Pref, scope.OpenMenu);
            Assert.Equal(["C", "SAVE", "SAVE AS", "RENAME", "DELETE"], TrayButtons(window).Select(DrawnText));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APrefSetButton_HasNoStrip_WhilePrefTearsOff()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Assert.Null(BarControl(window, ScopeButtonId.PrefSet).Strip);
            Assert.NotNull(BarControl(window, ScopeButtonId.Pref).Strip);

            var key = new ScopeButtonKey(ScopeButtonId.Pref, null);
            scope.PlaceTearoff(key, new ScopePlacement(ScopeCorner.TopLeft, 10, 10));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Contains(window.GetVisualDescendants().OfType<ScopeButtonControl>(), c => c.IsTearoff && (c.Key == key));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ASetNamedLikeATemplate_LightsOnlyWhenItIsTheActiveSet()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunScopeCommand(".scope template eram");
            main.RunScopeCommand(".scope pref saveas STARS");
            main.RunScopeCommand(".scope pref saveas B");
            Click(ButtonOf(window, ScopeButtonId.Pref));

            Assert.DoesNotContain("active", TrayButton(window, "STARS").Classes);
            Assert.Contains("active", TrayButton(window, "B").Classes);

            // Only the active set's name lights a set button, so a set named after the template in use stays dark.
            var templateNamedSet = new ScopeButton(
                ScopeButtonId.PrefSet,
                ScopeTemplates.EramId,
                ScopeButtonKind.Command,
                [],
                ScopeTemplates.EramId,
                null
            );
            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            Assert.Equal("B", scope.ActivePrefSet);
            Assert.False(scope.IsActive(templateNamedSet));

            Click(ButtonOf(window, ScopeButtonId.Template));

            Assert.DoesNotContain("active", ButtonOf(window, ScopeButtonId.TemplateStars).Classes);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.TemplateEram).Classes);

            // The set named STARS captures the ERAM template, so making it active leaves the STARS pick unlit.
            main.RunScopeCommand(".scope pref load stars");

            // A template pick lights by the template in use alone: with the ERAM template picked and a set named STARS
            // active, the STARS pick stays dark.
            Assert.Equal("STARS", scope.ActivePrefSet);
            Assert.Equal(ScopeTemplates.EramId, scope.TemplateId);
            Assert.False(scope.IsActive(ScopeToolbarButtons.Find(ScopeButtonId.TemplateStars)!));
            Assert.DoesNotContain("active", ButtonOf(window, ScopeButtonId.TemplateStars).Classes);
            Assert.Contains("active", ButtonOf(window, ScopeButtonId.TemplateEram).Classes);
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenAsync()
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        main.SetActivePositionFromServer(EramConfig());
        SeedSource(main);
        var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        // The ERAM position gives the pref sets their facility; the PREF tray is the classic bar's, so the room runs Mixed.
        main.RunScopeCommand(".scope profile mixed");
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (main, scope, window);
    }

    private static PositionDisplayConfigDto EramConfig() => new([], [], [], "C44", Sector44, 3, NoStarsDisplay: true);

    /// <summary>Loads the Scope's map source as a scenario load does, so the Scope seeds and a load adds no waiting note.</summary>
    private static void SeedSource(MainViewModel main) =>
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", 37.62, -122.38, 60, [])],
                [MapInfo(31), MapInfo(2), MapInfo(10)],
                []
            ),
            null,
            null
        );

    private static VideoMapInfoDto MapInfo(int starsId) =>
        new($"m{starsId}", $"Map {starsId}", $"MAP{starsId}", [], "A", starsId, AlwaysVisible: false, TdmOnly: false);

    /// <summary>The text <paramref name="button"/> draws, read as one line (<see cref="ScopeToolbarTests.DrawnText"/>).</summary>
    private static string DrawnText(Button button) => ScopeToolbarTests.DrawnText(button);

    private static string Label(Button button) => AutomationProperties.GetName(button) ?? "";

    /// <summary>The shown toolbar button with <paramref name="id"/>, on the bar or in the tray.</summary>
    private static Button ButtonOf(Window window, ScopeButtonId id) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => (b.Tag is ScopeButtonId tag) && (tag == id) && b.IsEffectivelyVisible);

    /// <summary>The toolbar buttons shown in the tray.</summary>
    private static Button[] TrayButtons(Window window) =>
        [
            .. window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(b => (b.Tag is ScopeButtonId) && b.IsEffectivelyVisible && b.GetVisualAncestors().OfType<Border>().Any(a => a.Name == "Tray")),
        ];

    /// <summary>The shown tray button labelled <paramref name="label"/>; pref-set buttons share one id, so they are found by label.</summary>
    private static Button TrayButton(Window window, string label) => TrayButtons(window).Single(b => Label(b) == label);

    /// <summary>The shown control of the button with <paramref name="id"/>, bar or tray.</summary>
    private static ScopeButtonControl BarControl(Window window, ScopeButtonId id) =>
        window.GetVisualDescendants().OfType<ScopeButtonControl>().Single(c => !c.IsTearoff && (c.Model.Id == id) && c.IsEffectivelyVisible);

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());
}
