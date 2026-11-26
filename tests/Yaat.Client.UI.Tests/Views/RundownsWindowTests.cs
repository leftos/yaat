using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// View › Bars › Rundowns…: one Rundowns window per main view model, listing the room's STARS RD lists one section each, then
/// the hold-for-release rundown moved out of the command bar; a right click on an RD row opens the entry's coordination menu.
/// </summary>
public class RundownsWindowTests
{
    [AvaloniaFact]
    public void MenuItem_OpensOneWindow_AndASecondClickActivatesTheSameOne()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        RundownsWindow? opened = null;
        try
        {
            MenuItem item = main.FindControl<MenuItem>("RundownsMenuItem")!;

            Click(item);
            opened = Assert.Single(OpenWindows.All.OfType<RundownsWindow>());

            Click(item);

            Assert.Same(opened, Assert.Single(OpenWindows.All.OfType<RundownsWindow>()));
            Assert.Equal("Rundowns", opened.Title);
        }
        finally
        {
            opened?.Close();
            MainWindowHost.Pump();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void Lists_ShowOneSectionEach_InOrder_AndNoneShowsNoActiveRundowns()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        try
        {
            CoordinationListHubDto pfat = Pfat("Unacknowledged");
            CoordinationListHubDto empty = Untitled();
            vm.ApplyCoordinationLists([pfat, empty]);
            MainWindowHost.Pump();

            string[] expected =
            [
                "Oakland departure releases",
                .. RundownRows.Build([pfat], vm.ScenarioElapsedSeconds).Select(r => r.Text),
                "PSJC",
                .. RundownRows.Build([empty], vm.ScenarioElapsedSeconds).Select(r => r.Text),
            ];
            Assert.Equal(expected, ListTexts(window));
            Assert.False(EmptyText(window).IsVisible);

            vm.ApplyCoordinationLists([]);
            MainWindowHost.Pump();

            Assert.Empty(ListTexts(window));
            Assert.True(EmptyText(window).IsVisible);
            Assert.Equal("No active rundowns", EmptyText(window).Text);
        }
        finally
        {
            window.Close();
            MainWindowHost.Pump();
        }
    }

    [AvaloniaFact]
    public void HoldForRelease_ShowsTheAirportAndTheDeparture_WithItsReleaseButton()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        try
        {
            vm.ApplyRundown(new RundownDto(["OAK"], [new HeldDepartureDto("SWA123", "OAK", "B738", "SJC", true, "Held")]));
            MainWindowHost.Pump();

            Control section = window.FindControl<Control>("HoldForReleaseSection")!;
            Assert.True(section.IsEffectivelyVisible);
            Assert.False(EmptyText(window).IsVisible);
            List<string?> texts = [.. section.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];
            Assert.Contains("OAK (1)", texts);
            Assert.Contains("SWA123  B738 → SJC", texts);
            Button release = Assert.Single(section.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Release");
            Assert.Same(vm.ReleaseDepartureCommand, release.Command);
            Assert.Equal("SWA123", release.CommandParameter);
            Button next = Assert.Single(section.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) == "Release next");
            Assert.Same(vm.ReleaseNextAtAirportCommand, next.Command);
            Assert.Equal("OAK", next.CommandParameter);
        }
        finally
        {
            window.Close();
            MainWindowHost.Pump();
        }
    }

    [AvaloniaFact]
    public void RightClickOnAnUnacknowledgedRow_AcknowledgeSendsTheReceiversRdack()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        try
        {
            var sent = new List<(string Callsign, string Command)>();
            window.CommandSender = (callsign, command) =>
            {
                sent.Add((callsign, command));
                return Task.CompletedTask;
            };
            vm.ApplyCoordinationLists([Pfat("Unacknowledged")]);
            MainWindowHost.Pump();
            TextBlock row = Assert.Single(RowBlocks(window), t => t.Text?.Contains("SWA123", StringComparison.Ordinal) == true);

            Point? centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseDown(centre.Value, MouseButton.Right);
            window.MouseUp(centre.Value, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            ContextMenu menu = Assert.IsType<ContextMenu>(window.ActiveContextMenu);
            MenuItem acknowledge = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Acknowledge");
            Assert.True(acknowledge.IsEnabled);
            acknowledge.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([("SWA123", "AS 4O RDACK PFAT")], sent);
        }
        finally
        {
            window.Close();
            MainWindowHost.Pump();
        }
    }

    [AvaloniaFact]
    public void AListWithNoItems_ShowsNoSection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        try
        {
            CoordinationListHubDto pfat = Pfat("Unacknowledged");
            vm.ApplyCoordinationLists([pfat, Untitled() with { Items = [] }]);
            MainWindowHost.Pump();

            string[] expected = ["Oakland departure releases", .. RundownRows.Build([pfat], vm.ScenarioElapsedSeconds).Select(r => r.Text)];
            Assert.Equal(expected, ListTexts(window));
            Assert.DoesNotContain("PSJC", ListTexts(window));
        }
        finally
        {
            window.Close();
            MainWindowHost.Pump();
        }
    }

    [AvaloniaFact]
    public void UserClose_ClearsTheOpenFlag()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        Assert.True(vm.Preferences.IsRundownsWindowOpen);

        window.Close();
        MainWindowHost.Pump();

        Assert.False(RundownsWindow.IsOpen(vm));
        Assert.False(vm.Preferences.IsRundownsWindowOpen);
    }

    [AvaloniaFact]
    public void Refresh_MovesTheCountdown()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var window = RundownsWindow.ShowOrActivate(vm);
        try
        {
            CoordinationListHubDto pfat = Pfat("Acknowledged") with
            {
                Items = [new CoordinationItemHubDto("POAK-1", "SWA456", "Acknowledged", 7, "B738", "0425", "F", "SJC", "RWY 30", "3O", 120)],
            };
            vm.ScenarioElapsedSeconds = 0;
            vm.ApplyCoordinationLists([pfat]);
            MainWindowHost.Pump();
            Assert.Contains("2:00", ListTexts(window)[1], StringComparison.Ordinal);

            vm.ScenarioElapsedSeconds = 30;
            window.Refresh();

            Assert.Contains("1:30", ListTexts(window)[1], StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            MainWindowHost.Pump();
        }
    }

    [AvaloniaFact]
    public void CommandBar_HasNoReleasesButton()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        try
        {
            CommandInputView bar = Assert.Single(main.GetLogicalDescendants().OfType<CommandInputView>());

            Assert.DoesNotContain(bar.GetLogicalDescendants().OfType<Button>(), b => (b.Content as string) == "Releases");
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static List<TextBlock> RowBlocks(RundownsWindow window) =>
        [.. window.FindControl<StackPanel>("ListsPanel")!.GetVisualDescendants().OfType<TextBlock>()];

    private static List<string?> ListTexts(RundownsWindow window) => [.. RowBlocks(window).Select(t => t.Text)];

    private static TextBlock EmptyText(RundownsWindow window) => window.FindControl<TextBlock>("EmptyText")!;

    private static CoordinationListHubDto Pfat(string status) =>
        new(
            ListId: "PFAT",
            Title: "Oakland departure releases",
            AutoAcknowledge: false,
            SenderTcps: ["3O"],
            ReceiverTcps: ["4O"],
            Items:
            [
                new CoordinationItemHubDto("POAK-1", "SWA456", status, 7, "B738", "0425", "F", "SJC", "RWY 30", "3O", null),
                new CoordinationItemHubDto("POAK-2", "SWA123", status, 8, "B738", "0426", "F", "SJC", "RWY 30", "3O", null),
            ]
        );

    // A list with no adapted title heads its section with its list id.
    private static CoordinationListHubDto Untitled() =>
        new(
            ListId: "PSJC",
            Title: "",
            AutoAcknowledge: true,
            SenderTcps: ["3O"],
            ReceiverTcps: ["2S"],
            Items: [new CoordinationItemHubDto("PSJC-1", "AAL9", "Unsent", null, "A321", "1201", "D", "MOD", "", "3O", null)]
        );

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }
}
