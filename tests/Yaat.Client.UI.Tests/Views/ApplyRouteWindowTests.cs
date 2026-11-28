using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Apply Route window over the Scenario Editor's OAK copy: Tools › Apply Route… and the aircraft detail's Apply Route…
/// button open it modal for the selected aircraft; a library route and a profile preview with the Aircraft tab's labels and
/// Apply writes one undo step and closes it; a warning starting "Error:" disables Apply; Cancel and the close box write
/// nothing; the Profiles expander creates, saves and deletes profiles, Save refusing a list or a number box that does not
/// read; a field still being typed in is committed before the window opens; All ARTCCs lists another ARTCC's routes; the
/// window keeps its geometry under its key. Profiles and routes go to a folder of the test's own, removed after it; Find
/// Route searches a stub source only.
/// </summary>
public sealed class ApplyRouteWindowTests : IDisposable
{
    private const string Zoa = "ZOA";
    private const string OakId = "01HHWYG5C2DCFR09A7S5A0Q0KP";

    private const string FoundRouteText = "OAK SAC FMG";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly string _root = YaatPaths.Combine("apply-route-window-tests", Guid.NewGuid().ToString("N"));
    private readonly StampProfileStore _profiles;
    private readonly RouteLibrary _library;
    private readonly GatedSource _source = new();

    public ApplyRouteWindowTests()
    {
        _profiles = new StampProfileStore(Path.Combine(_root, "stamp-profiles"));
        _library = new RouteLibrary(Path.Combine(_root, "routes"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ToolsItem_IsDisabledWithNoAircraftSelected_AndEnabledWithOne()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await ShowAsync(editor);
            MenuItem item = editor.FindControl<MenuItem>("ApplyRouteMenuItem")!;
            Assert.Equal("Apply Route…", item.Header);
            Assert.False(item.IsEnabled);

            await OpenOakAsync(editor);
            Assert.Null(editor.Editor.Aircraft!.Selected);
            Assert.False(item.IsEnabled);

            editor.Editor.Aircraft.Selected = editor.Editor.Aircraft.Rows[0];
            MainWindowHost.Pump();

            Assert.True(item.IsEnabled);
        }
        finally
        {
            CloseNow(editor);
        }
    }

    [AvaloniaFact]
    public async Task ToolsItem_OpensTheModalWindowForTheSelectedAircraft_WithItsCallsignInTheTitle()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            ScenarioAircraftRowViewModel row = await OpenWithSelectedAsync(editor, 0);

            Click(editor.FindControl<MenuItem>("ApplyRouteMenuItem")!);

            ApplyRouteWindow window = Assert.Single(WindowsOf(editor));
            Assert.True(window.IsVisible);
            Assert.True(ShownAsDialog(window));
            Assert.Contains(row.Callsign, window.Title, StringComparison.Ordinal);
            Assert.Contains(row.Callsign, window.FindControl<TextBlock>("HeaderText")!.Text, StringComparison.Ordinal);
            Press(window, "CancelButton");
            Assert.Empty(WindowsOf(editor));
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task AircraftViewButton_OpensTheWindowForTheShownAircraft()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            ScenarioAircraftRowViewModel row = await OpenWithSelectedAsync(editor, 1);
            ScenarioAircraftView view = editor.FindControl<ScenarioAircraftView>("AircraftView")!;
            Button button = view.FindControl<Button>("ApplyRouteButton")!;
            Assert.True(button.IsVisible);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MainWindowHost.Pump();

            ApplyRouteWindow window = Assert.Single(WindowsOf(editor));
            Assert.True(ShownAsDialog(window));
            Assert.Contains(row.Callsign, window.Title, StringComparison.Ordinal);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task LibraryRouteAndProfile_PreviewShowsFieldLabels_ApplyWritesOneUndoStepAndCloses()
    {
        StampProfile profile = SaveProfile(RouteAndRemarks("Route and remarks"));
        SavedRoute route = SaveRoute(Zoa, RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ScenarioEditSession session = editor.Editor.Session!;
            string before = session.Document.Write();
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Pick(window, vm, profile, route);

            Press(window, "PreviewButton");

            List<string?> shown = TextsIn(window.FindControl<ItemsControl>("PreviewList")!);
            Assert.Contains("Route", shown);
            Assert.Contains("Remarks", shown);
            Assert.DoesNotContain("/flightplan/route", shown);
            Button apply = window.FindControl<Button>("ApplyButton")!;
            Assert.True(apply.IsEnabled);

            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MainWindowHost.Pump();

            Assert.Empty(WindowsOf(editor));
            Assert.Equal(ScenarioAircraftViewModel.ApplyRouteStep, editor.Editor.UndoDescription);
            Assert.Equal("STAMPED", session.Document.Scenario.Aircraft[0].FlightPlan!.Remarks);
            Assert.True(session.Undo());
            Assert.Equal(before, session.Document.Write());
            Assert.False(session.CanUndo);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task AWarningStartingError_DisablesApply()
    {
        StampProfile profile = SaveProfile(RouteAndRemarks("Missing STAR") with { StarAliases = [new StarAlias("MOD", "QQZZX7")] });
        SavedRoute route = SaveRoute(Zoa, RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ScenarioEditSession session = editor.Editor.Session!;
            string before = session.Document.Write();
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Pick(window, vm, profile, route);

            Press(window, "PreviewButton");

            Assert.StartsWith("Error:", vm.Warnings[0], StringComparison.Ordinal);
            Assert.True(vm.ApplyCommand.CanExecute(null));
            Button apply = window.FindControl<Button>("ApplyButton")!;
            Assert.False(apply.IsEnabled);
            Assert.True(window.FindControl<TextBlock>("ErrorBlockText")!.IsVisible);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MainWindowHost.Pump();

            Assert.Equal(before, session.Document.Write());
            Assert.False(session.CanUndo);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelOrTheCloseBox_WritesNothing(bool cancel)
    {
        StampProfile profile = SaveProfile(RouteAndRemarks("Cancelled"));
        SavedRoute route = SaveRoute(Zoa, RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ScenarioEditSession session = editor.Editor.Session!;
            string before = session.Document.Write();
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Pick(window, vm, profile, route);
            Press(window, "PreviewButton");
            Assert.NotEmpty(vm.PreviewRows);

            if (cancel)
            {
                Press(window, "CancelButton");
            }
            else
            {
                window.Close();
                MainWindowHost.Pump();
            }

            Assert.Empty(WindowsOf(editor));
            Assert.Equal(before, session.Document.Write());
            Assert.False(session.CanUndo);
            Assert.False(session.IsDirty);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task ProfilesExpander_NewThenSave_ListsTheProfile_DeleteWaitsForASave_AndARefusedSaveShowsItsMessage()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Expander expander = window.FindControl<Expander>("ProfilesExpander")!;
            Assert.Equal("Profiles", expander.Header);
            expander.IsExpanded = true;
            MainWindowHost.Pump();

            Press(window, "NewProfileButton");
            string id = vm.Editor.Id;
            Button delete = window.FindControl<Button>("DeleteProfileButton")!;
            Assert.False(delete.IsEnabled);

            window.FindControl<TextBox>("ProfileNameBox")!.Text = "  ";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;
            Assert.True(message.IsVisible);
            Assert.Equal("Give the profile a name before saving.", message.Text);
            Assert.DoesNotContain(vm.Profiles, p => p.Id == id);
            Assert.False(delete.IsEnabled);

            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Window test profile";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.Contains(vm.Profiles, p => (p.Id == id) && (p.Name == "Window test profile"));
            Assert.False(message.IsVisible);
            Assert.True(delete.IsEnabled);

            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MainWindowHost.Pump();

            Assert.DoesNotContain(vm.Profiles, p => p.Id == id);
            Assert.Null(_profiles.Load(id));
            Assert.False(delete.IsEnabled);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task AllArtccsBox_ShowsARouteOfAnotherArtcc()
    {
        SavedRoute other = SaveRoute("ZNY", RoutePoint.AtFix("JFK"), RoutePoint.AtFix("LGA"));
        SavedRoute own = SaveRoute(Zoa, RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ListBox routes = window.FindControl<ListBox>("RouteList")!;
            CheckBox all = window.FindControl<CheckBox>("AllArtccsBox")!;
            Assert.Equal("All ARTCCs", all.Content);
            Assert.False(all.IsChecked);
            Assert.Contains(routes.Items.Cast<SavedRoute>(), r => r.Id == own.Id);
            Assert.DoesNotContain(routes.Items.Cast<SavedRoute>(), r => r.Id == other.Id);

            all.IsChecked = true;
            MainWindowHost.Pump();

            Assert.Contains(routes.Items.Cast<SavedRoute>(), r => r.Id == other.Id);
            Assert.Contains(routes.Items.Cast<SavedRoute>(), r => r.Id == own.Id);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task Geometry_RestoresUnderTheApplyRouteKey()
    {
        var preferences = new UserPreferences();
        preferences.SetWindowGeometry(
            "ApplyRoute",
            new SavedWindowGeometry
            {
                X = 120,
                Y = 90,
                Width = 1234,
                Height = 777,
                IsMaximized = false,
                ScreenIndex = 0,
                IsTopmost = false,
            }
        );
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            ScenarioAircraftRowViewModel row = await OpenWithSelectedAsync(editor, 0);
            ApplyRouteViewModel vm = editor.Editor.CreateApplyRoute()!;

            var window = ApplyRouteWindow.Create(vm, editor.Editor.Aircraft!, preferences, null);
            Assert.False(window.FindControl<Button>("FindRouteButton")!.IsEnabled);
            try
            {
                Assert.Equal("ApplyRoute", ApplyRouteWindow.GeometryKey);
                Assert.Equal(1234, window.Width);
                Assert.Equal(777, window.Height);
                Assert.Equal($"Apply Route — {row.Callsign}", window.Title);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            CloseNow(editor);
        }
    }

    [AvaloniaFact]
    public async Task AFieldStillBeingTypedIn_IsCommittedBeforeApplyRouteOpens()
    {
        StampProfile profile = SaveProfile(RouteAndRemarks("Route and remarks"));
        SavedRoute route = SaveRoute(Zoa, RoutePoint.AtFix("SFO"), RoutePoint.AtFix("MOD"));
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ScenarioEditSession session = editor.Editor.Session!;
            TextBox remarks = editor.FindControl<ScenarioAircraftView>("AircraftView")!.FindControl<TextBox>("RemarksBox")!;
            remarks.Focus();
            MainWindowHost.Pump();
            Assert.True(remarks.IsFocused, "the detail panel's Remarks box did not take the focus");

            remarks.Text = "TYPED REMARK";
            MainWindowHost.Pump();
            Assert.Equal("TYPED REMARK", editor.Editor.Aircraft!.Remarks);
            Assert.False(
                string.Equals("TYPED REMARK", session.Document.Scenario.Aircraft[0].FlightPlan!.Remarks, StringComparison.Ordinal),
                "the text reached the document without the field being committed"
            );

            ApplyRouteWindow window = OpenFromTools(editor);

            Assert.Equal("TYPED REMARK", session.Document.Scenario.Aircraft[0].FlightPlan!.Remarks);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Pick(window, vm, profile, route);
            Press(window, "PreviewButton");
            // The stamp writes "STAMPED", so the Remarks row's Now column is the typed text, as JSON text like every value.
            ApplyRoutePreviewRow row = vm.PreviewRows.Single(r => (r.Label == "Remarks") && (r.OldValue is not null));
            Assert.Equal("\"TYPED REMARK\"", row.OldValue);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaTheory]
    [InlineData("StartingAltitudeBox", "Altitude (ft)")]
    [InlineData("StartingSpeedBox", "Speed (kt)")]
    [InlineData("StartingHeadingBox", "Heading (deg)")]
    [InlineData("CruiseAltitudeBox", "Cruise altitude (ft)")]
    [InlineData("CruiseSpeedBox", "Cruise speed (kt)")]
    [InlineData("HandoffDelayBox", "Handoff delay (seconds)")]
    public async Task Save_RefusesANumberBoxShowingBadText_AndWritesOnceTheBoxReads(string boxName, string label)
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            window.FindControl<Expander>("ProfilesExpander")!.IsExpanded = true;
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            string id = vm.Editor.Id;
            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Number fields";
            TextBox box = window.FindControl<TextBox>(boxName)!;
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;

            box.Text = "250";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.Equal(250, NumberField(_profiles.Load(id)!, boxName));
            string saved = _profiles.Load(id)!.ToJson();

            box.Text = "25O";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.True(message.IsVisible);
            Assert.Equal($"{label}: \"25O\" is not a number", message.Text);
            Assert.Equal(saved, _profiles.Load(id)!.ToJson());

            box.Text = "300";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.False(message.IsVisible);
            Assert.Equal(300, NumberField(_profiles.Load(id)!, boxName));
        }
        finally
        {
            CloseAll(editor);
        }
    }

    // The profile field each number box writes, by the box's name.
    private static double? NumberField(StampProfile profile, string boxName) =>
        boxName switch
        {
            "StartingAltitudeBox" => profile.StartingConditions.Altitude,
            "StartingSpeedBox" => profile.StartingConditions.Speed,
            "StartingHeadingBox" => profile.StartingConditions.Heading,
            "CruiseAltitudeBox" => profile.FlightPlan.CruiseAltitude,
            "CruiseSpeedBox" => profile.FlightPlan.CruiseSpeed,
            "HandoffDelayBox" => profile.AutoTrack.HandoffDelay,
            _ => throw new ArgumentOutOfRangeException(nameof(boxName), boxName, "not a number box"),
        };

    [AvaloniaFact]
    public async Task Save_ABlankNumberBox_SavesTheFieldAsNull()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            window.FindControl<Expander>("ProfilesExpander")!.IsExpanded = true;
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            string id = vm.Editor.Id;
            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Blank cruise altitude";
            TextBox cruise = window.FindControl<TextBox>("CruiseAltitudeBox")!;
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;

            cruise.Text = "35000";
            MainWindowHost.Pump();
            cruise.Text = "";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.False(message.IsVisible);
            Assert.Null(_profiles.Load(id)!.FlightPlan.CruiseAltitude);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task New_AfterARejectedNumberBox_RefusesSaveWhileTheBoxStillShowsThatText()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            window.FindControl<Expander>("ProfilesExpander")!.IsExpanded = true;
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Rejected text kept";
            TextBox cruise = window.FindControl<TextBox>("CruiseAltitudeBox")!;
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;

            cruise.Text = "35OOO";
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            string id = vm.Editor.Id;

            // A new profile's cruise altitude is null, the value the rejected text never replaced, so the binding does not
            // rewrite the box: it keeps its text and its error, and Save stays refused.
            Assert.Equal("35OOO", cruise.Text);
            Press(window, "SaveProfileButton");

            Assert.True(message.IsVisible);
            Assert.Equal("Cruise altitude (ft): \"35OOO\" is not a number", message.Text);
            Assert.Null(_profiles.Load(id));
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task Duplicate_IsRefusedWhileANumberBoxShowsBadText()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            window.FindControl<Expander>("ProfilesExpander")!.IsExpanded = true;
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Duplicate refused";
            TextBox cruise = window.FindControl<TextBox>("CruiseAltitudeBox")!;
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;
            Press(window, "SaveProfileButton");
            int profiles = vm.Profiles.Count;

            cruise.Text = "35OOO";
            MainWindowHost.Pump();
            Press(window, "DuplicateProfileButton");

            Assert.True(message.IsVisible);
            Assert.Equal("Cruise altitude (ft): \"35OOO\" is not a number", message.Text);
            Assert.Equal(profiles, vm.Profiles.Count);
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task ProfileLists_TypedPoolAndPresets_SaveAsTheProfileTheStoreLoads_AndAListThatDoesNotReadRefusesSave()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            window.FindControl<Expander>("ProfilesExpander")!.IsExpanded = true;
            MainWindowHost.Pump();
            Press(window, "NewProfileButton");
            string id = vm.Editor.Id;
            window.FindControl<TextBox>("ProfileNameBox")!.Text = "Typed lists";
            TextBox aliases = window.FindControl<TextBox>("StarAliasesBox")!;
            TextBlock message = window.FindControl<TextBlock>("ProfileMessageText")!;

            aliases.Text = "MOD";
            MainWindowHost.Pump();
            Press(window, "SaveProfileButton");

            Assert.True(message.IsVisible);
            Assert.Equal("Line 1 of STAR aliases: write the STAR and the STAR replacing it, two words", message.Text);
            Assert.Null(_profiles.Load(id));

            aliases.Text = "";
            window.FindControl<TextBox>("AircraftTypesBox")!.Text = "B738, A320";
            window.FindControl<TextBox>("PresetCommandsBox")!.Text = "30 CM 50";
            MainWindowHost.Pump();
            Assert.False(message.IsVisible);
            Press(window, "SaveProfileButton");

            var expected = new StampProfile
            {
                Id = id,
                Name = "Typed lists",
                Presets = new StampPresets { Commands = [new StampPresetCommand("CM 50", 30)] },
                Pools = new StampPools { AircraftTypes = ["B738", "A320"] },
            };
            Assert.Equal(expected.ToJson(), _profiles.Load(id)!.ToJson());
        }
        finally
        {
            CloseAll(editor);
        }
    }

    [AvaloniaFact]
    public async Task FindRoute_InTheWindow_HandsTheAppliedRouteToApplyRoute_AndReopeningDisposesTheOldFinder()
    {
        ScenarioEditorWindow editor = NewEditorWindow(OakStore());
        try
        {
            await OpenWithSelectedAsync(editor, 0);
            ApplyRouteWindow window = OpenFromTools(editor);
            ApplyRouteViewModel vm = Assert.IsType<ApplyRouteViewModel>(window.DataContext);
            Button find = window.FindControl<Button>("FindRouteButton")!;
            Assert.True(find.IsEnabled);
            Flyout flyout = Assert.IsType<Flyout>(find.Flyout);
            RouteFinderView panel = window.FindControl<RouteFinderView>("FinderView")!;

            flyout.ShowAt(find);
            MainWindowHost.Pump();
            RouteFinderViewModel first = Assert.IsType<RouteFinderViewModel>(panel.DataContext);
            await first.SearchTask.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            MainWindowHost.Pump();
            first.SelectedRoute = Assert.Single(first.Routes);
            first.ApplyCommand.Execute(null);
            MainWindowHost.Pump();

            Assert.Equal(FoundRouteText, vm.FoundRoute?.RouteText);
            Assert.False(flyout.IsOpen);

            flyout.ShowAt(find);
            MainWindowHost.Pump();
            RouteFinderViewModel second = Assert.IsType<RouteFinderViewModel>(panel.DataContext);
            Assert.NotSame(first, second);
            Assert.False(second.SearchTask.IsCompleted);

            flyout.Hide();
            MainWindowHost.Pump();
            flyout.ShowAt(find);
            MainWindowHost.Pump();

            RouteFinderViewModel third = Assert.IsType<RouteFinderViewModel>(panel.DataContext);
            Assert.NotSame(second, third);
            await AwaitEndAsync(second.SearchTask);
            Assert.True(second.SearchTask.IsCompleted);
            flyout.Hide();
            MainWindowHost.Pump();
        }
        finally
        {
            CloseAll(editor);
        }
    }

    // Waits for a search a Dispose cancelled to end, however it ends.
    private static async Task AwaitEndAsync(Task search)
    {
        try
        {
            await search.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled search may end cancelled.
        }
    }

    // The route written to the plan, and remarks the OAK aircraft do not have, so a stamp always changes something.
    private static StampProfile RouteAndRemarks(string name) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                Remarks = "STAMPED",
                ApplyRoute = true,
            },
        };

    private StampProfile SaveProfile(StampProfile profile)
    {
        _profiles.Save(profile);
        return profile;
    }

    private SavedRoute SaveRoute(string artccId, params RoutePoint[] points)
    {
        var route = new SavedRoute(SavedRoute.NewId(), "Window test " + artccId, null, artccId, points);
        _library.Save(route);
        return route;
    }

    // Picks the profile and the library route in the window's lists, as a click on each does.
    private static void Pick(ApplyRouteWindow window, ApplyRouteViewModel vm, StampProfile profile, SavedRoute route)
    {
        window.FindControl<ListBox>("ProfileList")!.SelectedItem = vm.Profiles.Single(p => p.Id == profile.Id);
        window.FindControl<ListBox>("RouteList")!.SelectedItem = vm.Routes.Single(r => r.Id == route.Id);
        MainWindowHost.Pump();
        Assert.Equal(profile.Id, vm.SelectedProfile?.Id);
        Assert.Equal(route.Id, vm.SelectedRoute?.Id);
    }

    private static List<string?> TextsIn(Control control) => [.. control.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];

    private static ApplyRouteWindow OpenFromTools(ScenarioEditorWindow editor)
    {
        Click(editor.FindControl<MenuItem>("ApplyRouteMenuItem")!);
        return Assert.Single(WindowsOf(editor));
    }

    private static ApplyRouteWindow[] WindowsOf(ScenarioEditorWindow editor) =>
        [.. OpenWindows.All.OfType<ApplyRouteWindow>().Where(w => ReferenceEquals(w.Owner, editor) && w.IsVisible)];

    // Whether the window was shown with ShowDialog: Avalonia keeps it in a private field (Window._showingAsDialog) only.
    private static bool ShownAsDialog(Window window) =>
        (bool)typeof(Window).GetField("_showingAsDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    // Clicks a button that has no bound command, by its name.
    private static void Press(Window window, string name)
    {
        Button button = window.FindControl<Button>(name)!;
        if (button.Command is { } command)
        {
            command.Execute(button.CommandParameter);
        }
        else
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        MainWindowHost.Pump();
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }

    private static async Task<ScenarioAircraftRowViewModel> OpenWithSelectedAsync(ScenarioEditorWindow editor, int row)
    {
        await ShowAsync(editor);
        await OpenOakAsync(editor);
        editor.FindControl<TabItem>("AircraftTab")!.IsSelected = true;
        MainWindowHost.Pump();
        ScenarioAircraftViewModel aircraft = editor.Editor.Aircraft!;
        aircraft.Selected = aircraft.Rows[row];
        MainWindowHost.Pump();
        return aircraft.Rows[row];
    }

    private static void CloseAll(ScenarioEditorWindow editor)
    {
        foreach (ApplyRouteWindow window in WindowsOf(editor))
        {
            window.Close();
        }

        MainWindowHost.Pump();
        CloseNow(editor);
    }

    // Closes without the unsaved-changes question, which would show a real dialog and hold the window open.
    private static void CloseNow(ScenarioEditorWindow window)
    {
        window.CanCloseNow = () => Task.FromResult(true);
        window.Close();
    }

    private static async Task ShowAsync(ScenarioEditorWindow window)
    {
        window.Show();
        MainWindowHost.Pump();
        await window.BrowserReady.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    // Opens the OAK copy through the browser's Open, as a double-click does, and waits for its pickers.
    private static async Task OpenOakAsync(ScenarioEditorWindow window)
    {
        ScenarioBrowserViewModel browser = window.Browser;
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == OakId);
        await browser.OpenCommand.ExecuteAsync(null);
        await window.Editor.OpenTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await window.Editor.General!.PickersLoaded.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
    }

    // A store holding the committed OAK scenario of the Sim tests' test data as a local working copy.
    private LocalScenarioStore OakStore()
    {
        var store = new LocalScenarioStore(Path.Combine(_root, "store"));
        store.CreateLocalScenario(Zoa, File.ReadAllText(SimTestData("issue153-s2-oak-5-2-scenario.json")), OakId);
        return store;
    }

    // A window over the store whose data-api answers ZOA's config from the committed snapshot and nothing else, whose
    // profiles and routes are this test's own and whose Find Route searches only the gated stub; the prompts replace the
    // window's dialogs, so no test shows a real one.
    private ScenarioEditorWindow NewEditorWindow(LocalScenarioStore store)
    {
        TestVnasData.EnsureAircraftDataInitialized();
        var preferences = new UserPreferences();
        string snapshot = File.ReadAllText(SimTestData("artcc-zoa-snapshot.json"));
        var configs = new ArtccConfigSource(new HttpClient(new ZoaOnlyHandler(snapshot)), Path.Combine(_root, "artcc"), () => DateTime.UtcNow);
        var routeFinding = new RouteFindingServices(new HttpClient(new ZoaOnlyHandler(snapshot)), RouteFindingServices.NoEndpoint, () => [_source]);
        return new ScenarioEditorWindow(
            preferences,
            _ =>
                ScenarioBrowserServices.ForApp(preferences, new DiscardingPrompts(), new StubAccess([Zoa])) with
                {
                    Store = store,
                    Download = NeverFinishes,
                    Configs = configs,
                    DownloadVideoMaps = (_, _, _) => Task.FromResult<VideoMapData?>(null),
                    RouteFinding = routeFinding,
                    StampProfiles = _profiles,
                    Routes = _library,
                },
            _ => new FakeFilePickerService(),
            NoLayouts,
            Task.CompletedTask
        );
    }

    private static Task<AirportGroundLayout?> NoLayouts(string airportId, CancellationToken ct) => Task.FromResult<AirportGroundLayout?>(null);

    private static async Task<DownloadReport> NeverFinishes(string artccId, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }

    // A file of the Sim tests' committed test data, found from the test's output folder up.
    private static string SimTestData(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"{name} is not in tests/Yaat.Sim.Tests/TestData above {AppContext.BaseDirectory}");
    }

    // The first search finds one csko route over real fixes; every later one waits until it is cancelled.
    private sealed class GatedSource : IRouteSource
    {
        private int _calls;

        public string Name => "csko";

        public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return new RouteSourceResult([new FoundRoute(FoundRouteText, 5, ["csko"], null, null, null)], null);
            }

            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class StubAccess(string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }

    // The window's dialogs: every question answered so nothing is kept or written, and no real dialog shows.
    private sealed class DiscardingPrompts : IScenarioBrowserPrompts
    {
        public Task<bool> ConfirmDeleteAsync(ScenarioBrowserRowViewModel row) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> PickImportFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<ImportConflictAnswer> AskImportConflictAsync(ImportConflictQuestion question) =>
            Task.FromException<ImportConflictAnswer>(new NotSupportedException("no import in these tests"));

        public Task<string?> PickExportPathAsync(string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<string?> PickExportFolderAsync() => Task.FromResult<string?>(null);

        public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string scenarioName, UnsavedChangesContext context) =>
            Task.FromResult(UnsavedChangesChoice.Discard);

        public Task<bool> ConfirmReplaceOpenCopyAsync(string scenarioName) => Task.FromResult(false);

        public Task<bool> ConfirmReplaceRunningScenarioAsync(string scenarioName) => Task.FromResult(false);

        public Task<UnexportedChangesChoice> AskUnexportedChangesAsync(IReadOnlyList<string> names, UnexportedChangesContext context) =>
            Task.FromResult(UnexportedChangesChoice.Leave);

        public Task<UnsavedAndUnexportedChoice> AskUnsavedAndUnexportedAsync(
            string openName,
            IReadOnlyList<string> names,
            UnexportedChangesContext context
        ) => Task.FromResult(UnsavedAndUnexportedChoice.DiscardAndLeave);

        public Task<ScenarioMergeResult?> ShowMergeAsync(ScenarioMergeRequest request) => Task.FromResult<ScenarioMergeResult?>(null);

        public Task<ScenarioComparePick?> PickCompareAsync(ScenarioComparePickerViewModel picker) => Task.FromResult<ScenarioComparePick?>(null);
    }

    // data-api answers ZOA with the snapshot and every other ARTCC (a neighbour) as the network being down.
    private sealed class ZoaOnlyHandler(string zoa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri?.AbsolutePath.EndsWith("/" + Zoa, StringComparison.Ordinal) == true)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("the network is down"));
    }
}
