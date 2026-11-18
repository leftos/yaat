using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope window's views dock: a 360 px column right of the scope holding the docked views' cards (the SSA, then
/// LA/CA/MCI), hidden when no open view is docked. The SSA opens floating on the scope, so most tests dock it first. Each
/// view opens, closes and docks through <c>.view</c>.
/// </summary>
public class ScopeDockTests
{
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeWindow Window)> OpenAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var window = new ScopeWindow(main.Preferences, scope);
        window.Show();
        Settle(window);
        return (main, scope, window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static ScopeDock DockOf(Window window) => window.GetVisualDescendants().OfType<ScopeDock>().Single();

    private static ScopeCanvas CanvasOf(Window window) => window.GetVisualDescendants().OfType<ScopeCanvas>().Single();

    private static ScopeViewCard[] Cards(Window window) =>
        [.. window.GetVisualDescendants().OfType<ScopeViewCard>().Where(c => c.IsEffectivelyVisible)];

    private static ScopeViewCard CardOf(Window window, string name) => Cards(window).Single(c => c.ViewName == name);

    private static double ContentWidth(Window window) => ((Control)window.Content!).Bounds.Width;

    internal static string[] Texts(Control control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    [AvaloniaFact]
    public async Task TheWindow_Opens1460Wide_WithTheDockHidden()
    {
        (_, _, ScopeWindow window) = await OpenAsync();
        try
        {
            // The SSA floats on the scope by default (ScopeViewTearoffTests), so no view is docked.
            Assert.Equal(1460, window.Width);
            Assert.False(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), CanvasOf(window).Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheLastView_HidesTheDock_AndKeepsTheScale()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeCanvas canvas = CanvasOf(window);
            double zoom = canvas.Viewport.Zoom;

            main.RunViewCommand(".view ssa off");
            Settle(window);

            Assert.False(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), canvas.Bounds.Width);
            Assert.Equal(zoom, canvas.Viewport.Zoom);

            main.RunViewCommand(".view ssa on");
            Settle(window);

            Assert.True(DockOf(window).IsEffectivelyVisible);
            Assert.Equal(ContentWidth(window), canvas.Bounds.Width + 360);
            Assert.Equal(zoom, canvas.Viewport.Zoom);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheConflictCard_SitsBelowTheSsa_AndListsThePairs()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            Assert.True(main.Preferences.ShowConflictAlerts);
            main.RunViewCommand(".view ca on");
            Settle(window);

            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], Cards(window).Select(c => c.ViewName));
            ScopeViewCard ssa = CardOf(window, ScopeToolbarButtons.ViewSsaName);
            ScopeViewCard ca = CardOf(window, ScopeToolbarButtons.ViewCaName);
            Assert.True(ca.TranslatePoint(new Point(0, 0), window)!.Value.Y >= ssa.TranslatePoint(new Point(0, ssa.Bounds.Height), window)!.Value.Y);
            Assert.Contains("LA/CA/MCI", Texts(ca));

            // The list shows the pairs whatever the conflict-alert preference says: an opened view is shown.
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);
            Settle(window);
            Assert.Contains("AAL22*UAL1        CA", Texts(ca));

            main.ApplyConflictAlerts([]);
            Settle(window);
            Assert.DoesNotContain("AAL22*UAL1        CA", Texts(ca));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheCardsCloseButton_ClosesItsView_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            main.RunViewCommand(".view ca on");
            Settle(window);

            Button close = CardOf(window, ScopeToolbarButtons.ViewCaName).GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible);
            Assert.Equal("–", close.Content);
            // A real click: the button runs its bound command, which a raised Click event alone does not.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Point centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca off", main.StatusText);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName], Cards(window).Select(c => c.ViewName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheViewsTray_TogglesKeepItOpen_AndDrawTheOpenViewActive()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Views));
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa).Classes);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa).Classes);

            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa));

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
            Assert.Equal(".view ca on", main.StatusText);
            Assert.Equal(ScopeButtonId.Views, scope.OpenMenu);
            Assert.Contains("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewCa).Classes);

            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa));

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewSsaName));
            Assert.Equal(".view ssa off", main.StatusText);
            Assert.Equal(ScopeButtonId.Views, scope.OpenMenu);
            Assert.DoesNotContain("active", ScopeToolbarTests.ButtonOf(window, ScopeButtonId.ViewSsa).Classes);

            // Pressing VIEWS again closes the tray.
            ScopeToolbarTests.Click(ScopeToolbarTests.ButtonOf(window, ScopeButtonId.Views));
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheSsaClock_RunsOnlyWhileTheSsaIsOpen_AndStopsWithTheWindow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            Assert.True(scope.IsClockRunning);

            main.RunViewCommand(".view ssa off");
            Assert.False(scope.IsClockRunning);

            main.RunViewCommand(".view ssa on");
            Assert.True(scope.IsClockRunning);
        }
        finally
        {
            window.Close();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.IsClockRunning);
    }

    [AvaloniaFact]
    public async Task ClearingTheScenario_EmptiesTheConflictCard()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);
            Settle(window);
            Assert.Contains("AAL22*UAL1        CA", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));

            main.ClearScenarioState();
            Settle(window);

            Assert.DoesNotContain("AAL22*UAL1        CA", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClosedConflictView_StopsListeningForPairs()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ca on");
            ConflictListViewModel list = scope.DockViews.OfType<ConflictListViewModel>().Single();

            main.RunViewCommand(".view ca off");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);

            Assert.Empty(list.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    // Closing the window disposes the scope, which stops every view: the open list clears its rows and stops listening.
    [AvaloniaFact]
    public async Task ClosingTheWindow_StopsTheOpenConflictView()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        main.RunViewCommand(".view ca on");
        ConflictListViewModel list = scope.DockViews.OfType<ConflictListViewModel>().Single();

        window.Close();
        Dispatcher.UIThread.RunJobs();
        main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);

        Assert.Empty(list.Rows);

        main.OnAircraftUpdated(QueuedDto("UAL1", ""));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(list.Rows);
    }

    // A listed pair's member that arrives through the aircraft list after the broadcast relabels its row.
    [AvaloniaFact]
    public async Task ConflictList_RelabelsAPair_WhenAMemberArrivesAfterTheBroadcast()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel tracked = ScopeTestFixture.AddAircraft(main, "SKW5", 9000);
            tracked.Owner = "OAK_44_CTR";
            main.RunViewCommand(".view ca on");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_N123_SKW5", "N123", "SKW5")]);
            Settle(window);
            Assert.DoesNotContain("1200*SKW5         MCI", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));

            main.Aircraft.Add(
                new AircraftModel
                {
                    Callsign = "N123",
                    AircraftType = "B738",
                    FiledAircraftType = "B738",
                    FlightRules = "IFR",
                    TransponderMode = "C",
                    BeaconCode = 1200,
                    Altitude = 9000,
                    GroundSpeed = 250,
                    Position = new LatLon(37.7, -122.2),
                }
            );
            Settle(window);

            Assert.Contains("1200*SKW5         MCI", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));
        }
        finally
        {
            window.Close();
        }
    }

    // The intruder files a plan between two conflict broadcasts: the aircraft update alone turns its MCI row into a CA row.
    [AvaloniaFact]
    public async Task ConflictList_FlipsFromMciToCa_WhenTheIntruderFilesAPlanBetweenBroadcasts()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel intruder = ScopeTestFixture.AddAircraft(main, "N123", 9000);
            intruder.BeaconCode = 1200;
            AircraftModel tracked = ScopeTestFixture.AddAircraft(main, "SKW5", 9000);
            tracked.Owner = "OAK_44_CTR";
            main.RunViewCommand(".view ca on");
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_N123_SKW5", "N123", "SKW5")]);
            Settle(window);
            Assert.Contains("1200*SKW5         MCI", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));

            main.OnAircraftUpdated(QueuedDto("N123", ""));
            Settle(window);

            Assert.True(intruder.HasFlightPlan);
            Assert.Contains("N123*SKW5         CA", Texts(CardOf(window, ScopeToolbarButtons.ViewCaName)));
        }
        finally
        {
            window.Close();
        }
    }

    // An update to a pair's member that changes neither its track nor its plan keeps the list's rows as they were, so the
    // card does not rebuild every tick.
    [AvaloniaFact]
    public async Task ConflictList_KeepsItsRows_WhenAnAircraftUpdateChangesNoRow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            ScopeTestFixture.AddAircraft(main, "UAL1", 9000);
            ScopeTestFixture.AddAircraft(main, "AAL22", 9000);
            // The first update gives UAL1 the update's track and plan; the second repeats them.
            main.OnAircraftUpdated(QueuedDto("UAL1", ""));
            main.RunViewCommand(".view ca on");
            ConflictListViewModel list = scope.DockViews.OfType<ConflictListViewModel>().Single();
            main.ApplyConflictAlerts([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);
            Settle(window);
            IReadOnlyList<ConflictListRow> before = list.Rows;
            Assert.NotEmpty(before);

            main.OnAircraftUpdated(QueuedDto("UAL1", ""));
            Settle(window);

            Assert.Same(before, list.Rows);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AReopenedSsa_TakesItsPlaceAboveTheConflictList()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            main.RunViewCommand(".view ssa off");
            main.RunViewCommand(".view ca on");
            main.RunViewCommand(".view ssa on");
            Settle(window);

            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], scope.OpenViews);
            Assert.Equal([ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName], Cards(window).Select(c => c.ViewName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ApplyOpenViews_OpensTheKnownNamedViews_AndClosesTheRest()
    {
        (_, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            scope.ApplyOpenViews([ScopeToolbarButtons.ViewCaName, "bogus"]);

            Assert.Equal([ScopeToolbarButtons.ViewCaName], scope.OpenViews);
            Assert.False(scope.IsClockRunning);

            scope.ApplyOpenViews([]);

            Assert.False(scope.HasDockedViews);
        }
        finally
        {
            window.Close();
        }
    }

    private static AircraftDto QueuedDto(string callsign, string pendingCommands) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Latitude = 37.7,
            Longitude = -122.2,
            Heading = 90,
            Altitude = 9000,
            GroundSpeed = 250,
            BeaconCode = 1200,
            TransponderMode = "C",
            Departure = "OAK",
            Destination = "SFO",
            Route = "",
            FlightRules = "IFR",
            Status = "Active",
            PendingCommands = pendingCommands,
        };

    [AvaloniaFact]
    public async Task TheRpoQueueCard_CountsItsRows_AndFollowsAnAircraftUpdate()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel ac = ScopeTestFixture.AddAircraft(main, "UAL1549", 9000);
            ac.PendingCommands = "[Active] FH 270\n[1] AT MENLO DM 040";

            main.RunViewCommand(".view rpoq on");
            Settle(window);

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewRpoQueueName));
            ScopeViewCard card = CardOf(window, ScopeToolbarButtons.ViewRpoQueueName);
            Assert.Contains("PILOT LENS · RPO QUEUE 1", Texts(card));
            Assert.Contains("UAL1549 AT MENLO DM 040 wait", Texts(card));

            main.OnAircraftUpdated(QueuedDto("UAL1549", "[1] AT MENLO DM 040\n[2] CM 050"));
            Settle(window);

            Assert.Equal("[1] AT MENLO DM 040\n[2] CM 050", ac.PendingCommands);
            Assert.Contains("PILOT LENS · RPO QUEUE 2", Texts(card));
            Assert.Contains("UAL1549 CM 050 wait", Texts(card));

            main.RunViewCommand(".view rpoq off");
            Settle(window);

            Assert.False(scope.IsViewOpen(ScopeToolbarButtons.ViewRpoQueueName));
            Assert.DoesNotContain(Cards(window), c => c.ViewName == ScopeToolbarButtons.ViewRpoQueueName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheCrrCard_ListsEachGroupAndItsMembers_AndFollowsAGroupChange()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel ac = ScopeTestFixture.AddAircraft(main, "UAL1549", 9000);
            ac.EramCrrGroup = "A";
            main.ApplyCrrGroups([new EramCrrGroupHubDto("A", "Coral", 37.5, -122.2)]);

            main.RunViewCommand(".view crr on");
            Settle(window);

            Assert.True(scope.IsViewOpen(ScopeToolbarButtons.ViewCrrName));
            ScopeViewCard card = CardOf(window, ScopeToolbarButtons.ViewCrrName);
            Assert.Contains("CRR", Texts(card));
            Assert.Contains("A", Texts(card));
            Assert.Contains("UAL1549   12.0", Texts(card));

            main.ApplyCrrGroups([new EramCrrGroupHubDto("A", "Coral", 37.5, -122.2), new EramCrrGroupHubDto("B", "Green", 37.0, -122.0)]);
            Settle(window);

            Assert.Contains("B", Texts(card));

            main.RunViewCommand(".view crr off");
            Settle(window);

            Assert.DoesNotContain(Cards(window), c => c.ViewName == ScopeToolbarButtons.ViewCrrName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARemovedAircraft_LeavesTheRpoQueue()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel ac = ScopeTestFixture.AddAircraft(main, "UAL1549", 9000);
            ac.PendingCommands = "[1] AT MENLO DM 040";
            main.RunViewCommand(".view rpoq on");
            Settle(window);
            ScopeViewCard card = CardOf(window, ScopeToolbarButtons.ViewRpoQueueName);
            Assert.Contains("PILOT LENS · RPO QUEUE 1", Texts(card));

            main.OnAircraftDeleted("UAL1549");
            Settle(window);

            Assert.Contains("PILOT LENS · RPO QUEUE 0", Texts(card));
            Assert.DoesNotContain("UAL1549 AT MENLO DM 040 wait", Texts(card));
        }
        finally
        {
            window.Close();
        }
    }

    private static void ViewFromSector44(MainViewModel main)
    {
        var sector44 = new ActivePositionDto(
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
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", sector44, 3, NoStarsDisplay: true));
    }

    [AvaloniaFact]
    public async Task AViewerChange_RebuildsTheOpenRpoQueue()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel ac = ScopeTestFixture.AddAircraft(main, "N123AB", 9000);
            ac.Owner = "OAK_44_CTR";
            ac.OwnerFacilityId = "ZOA";
            ac.OwnerSectorCode = "C44";
            ac.HasMadeInitialContact = false;
            main.RunViewCommand(".view rpoq on");
            Settle(window);
            ScopeViewCard card = CardOf(window, ScopeToolbarButtons.ViewRpoQueueName);
            Assert.Contains("PILOT LENS · RPO QUEUE 0", Texts(card));

            ViewFromSector44(main);
            Settle(window);

            Assert.Contains("PILOT LENS · RPO QUEUE 1", Texts(card));
            Assert.Contains("N123AB not checked in (no VCI) !", Texts(card));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClosedRpoQueue_StopsListeningForAircraftUpdates()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view rpoq on");
            RpoQueueViewModel queue = scope.DockViews.OfType<RpoQueueViewModel>().Single();

            main.RunViewCommand(".view rpoq off");
            main.OnAircraftUpdated(QueuedDto("UAL1549", "[1] AT MENLO DM 040"));
            Settle(window);

            Assert.Empty(queue.Rows);
            Assert.Equal("PILOT LENS · RPO QUEUE 0", queue.Title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRpoQueueCard_SitsBelowTheConflictList()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            main.RunViewCommand(".view rpoq on");
            main.RunViewCommand(".view ca on");
            Settle(window);

            string[] order = [ScopeToolbarButtons.ViewSsaName, ScopeToolbarButtons.ViewCaName, ScopeToolbarButtons.ViewRpoQueueName];
            Assert.Equal(order, scope.OpenViews);
            Assert.Equal(order, Cards(window).Select(c => c.ViewName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClickingAnRpoQueueRow_SelectsItsAircraft_AndLeavesTheScopeFocused()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeWindow window) = await OpenAsync();
        try
        {
            AircraftModel ac = ScopeTestFixture.AddAircraft(main, "UAL1549", 9000);
            ac.PendingCommands = "[1] AT MENLO DM 040";
            main.RunViewCommand(".view rpoq on");
            Settle(window);
            ScopeCanvas canvas = CanvasOf(window);
            Point canvasCentre = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
            window.MouseDown(canvasCentre, MouseButton.Left);
            window.MouseUp(canvasCentre, MouseButton.Left);
            Settle(window);
            Assert.True(canvas.IsFocused);
            Assert.Null(scope.SelectedAircraft);

            TextBlock row = CardOf(window, ScopeToolbarButtons.ViewRpoQueueName)
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.Text == "UAL1549 AT MENLO DM 040 wait");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Point centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);

            Assert.Same(ac, scope.SelectedAircraft);
            Assert.True(canvas.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheSsaCard_OutsideARoom_ShowsTheRedNaStatusAndTheRange()
    {
        (MainViewModel main, _, ScopeWindow window) = await OpenAsync();
        try
        {
            main.RunViewCommand(".view ssa dock");
            Settle(window);
            ScopeViewCard ssa = CardOf(window, ScopeToolbarButtons.ViewSsaName);

            string[] texts = Texts(ssa);
            Assert.Contains("NA/NA/NA", texts);
            Assert.Contains("FUSED 40NM", texts);
            TextBlock status = ssa.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "NA/NA/NA");
            Assert.Contains("alert", status.Classes);
        }
        finally
        {
            window.Close();
        }
    }
}
