using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's pilot lens: off by default; <c>.scope lens on</c> with <c>.scope lens path on</c> draws the selected
/// aircraft's intent path and <c>.scope lens path &lt;callsign&gt;</c> adds another track's; turning either off draws none;
/// a scenario unload or rewind drops the toggled tracks; a pref set keeps the switches but not the tracks.
/// </summary>
public class ScopeLensTests
{
    private const string LensUsage = "Usage: .scope lens <on|off> | .scope lens <path|queue|checkin> <on|off> | .scope lens path <callsign>";

    private const string PendingTwo = "[Active] FH 270\n[1] AT BRIXX DM 040\n[2] WAIT 30 SPD 210";

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (main, scope, view, window);
    }

    /// <summary>Adds <paramref name="callsign"/> at the canvas's screen point, flying a two-fix route.</summary>
    private static AircraftModel AddRouted(MainViewModel main, ScopeCanvas canvas, string callsign, float screenX, float screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(screenX, screenY);
        AircraftModel ac = AddAircraft(main, callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        ac.NavRouteFixes = [new NavRouteFixDto("ALPHA", lat + 0.05, lon, null, null), new NavRouteFixDto("BRAVO", lat + 0.1, lon + 0.05, null, null)];
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static string[] WithIntentPath(ScopeCanvas canvas)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. canvas.CurrentFrame.Tracks.Where(t => t.IntentPath is not null).Select(t => t.Aircraft.Callsign).Order(StringComparer.Ordinal)];
    }

    [AvaloniaFact]
    public async Task FreshScope_HasTheLensOff()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.Equal(ScopeLensSettings.Off, scope.Lens);
            Assert.Empty(scope.LensPathCallsigns);
            Assert.Equal(new ScopePrefLens(false, false, false, false), scope.CapturePrefSet(null).Lens);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task LensAndPathOn_DrawTheSelectedPath_AndAToggledTrackAddsItsOwn()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope();
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            AircraftModel selected = AddRouted(main, view.Canvas, "N123AB", 300, 300);
            AddRouted(main, view.Canvas, "UAL1", 500, 400);
            main.SelectedAircraft = selected;
            Assert.Empty(WithIntentPath(view.Canvas));

            main.RunScopeCommand(".scope lens on");
            Assert.Equal("Scope lens ON", main.StatusText);
            Assert.Empty(WithIntentPath(view.Canvas));

            main.RunScopeCommand(".scope lens path on");
            Assert.Equal("Scope lens path ON", main.StatusText);
            Assert.Equal(["N123AB"], WithIntentPath(view.Canvas));

            main.RunScopeCommand(".scope lens path ual1");
            Assert.Equal("Scope lens path UAL1", main.StatusText);
            Assert.Equal(["UAL1"], scope.LensPathCallsigns);
            Assert.Equal(["N123AB", "UAL1"], WithIntentPath(view.Canvas));

            main.RunScopeCommand(".scope lens path UAL1");
            Assert.Equal("Scope lens path UAL1 removed", main.StatusText);
            Assert.Equal(["N123AB"], WithIntentPath(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope lens off")]
    [InlineData(".scope lens path off")]
    public async Task EitherSwitchOff_DrawsNoPath(string command)
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope();
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            main.SelectedAircraft = AddRouted(main, view.Canvas, "N123AB", 300, 300);
            AddRouted(main, view.Canvas, "UAL1", 500, 400);
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens path on");
            main.RunScopeCommand(".scope lens path UAL1");
            Assert.Equal(["N123AB", "UAL1"], WithIntentPath(view.Canvas));

            main.RunScopeCommand(command);

            Assert.Empty(WithIntentPath(view.Canvas));
            Assert.Equal(["UAL1"], scope.LensPathCallsigns);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope lens")]
    [InlineData(".scope lens maybe")]
    [InlineData(".scope lens wx on")]
    [InlineData(".scope lens path")]
    [InlineData(".scope lens path on now")]
    public async Task BadShapes_AndAnUnknownOverlay_AnswerTheUsageLine(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal(LensUsage, main.StatusText);
            Assert.Equal(ScopeLensSettings.Off, scope.Lens);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PathToggle_OfAnUnknownAircraft_SaysSo()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope lens path NOPE1");

            Assert.Equal("Unknown aircraft: NOPE1", main.StatusText);
            Assert.Empty(scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Unload_DropsTheToggledTracks()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope lens path UAL1");
            Assert.Equal(["UAL1"], scope.LensPathCallsigns);

            main.ClearScenarioState();

            Assert.Empty(scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RemovedAircraft_DropsItsToggledPath()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 5000);
            main.RunScopeCommand(".scope lens path UAL1");
            main.RunScopeCommand(".scope lens path UAL2");

            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["UAL2"], scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Rewind_DropsTheToggledTracks()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope lens path UAL1");

            main.ApplyScenarioRewind([]);

            Assert.Empty(scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_KeepsTheSwitches_ButNotTheToggledTracks()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens path on");
            main.RunScopeCommand(".scope lens path UAL1");
            string json = JsonSerializer.Serialize(scope.CapturePrefSet("LENS"), ScopePrefSet.JsonOptions);
            Assert.DoesNotContain("UAL1", json, StringComparison.Ordinal);
            main.RunScopeCommand(".scope lens off");
            main.RunScopeCommand(".scope lens path off");

            scope.ApplyPrefSet(Assert.IsType<ScopePrefSet>(JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)));

            Assert.Equal(new ScopeLensSettings(Enabled: true, Path: true, Queue: false, CheckIn: false), scope.Lens);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static string[] WithQueueBadge(ScopeCanvas canvas)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. canvas.CurrentFrame.Tracks.Where(t => t.QueueBadge is not null).Select(t => t.Aircraft.Callsign).Order(StringComparer.Ordinal)];
    }

    [AvaloniaFact]
    public async Task LensAndQueueOn_DrawABadge_ForAnAircraftWithPendingLines()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope();
        try
        {
            AddRouted(main, view.Canvas, "N123AB", 300, 300).PendingCommands = PendingTwo;
            AddRouted(main, view.Canvas, "UAL1", 500, 400);
            main.RunScopeCommand(".scope lens on");
            Assert.Empty(WithQueueBadge(view.Canvas));

            main.RunScopeCommand(".scope lens queue on");

            Assert.Equal("Scope lens queue ON", main.StatusText);
            Assert.Equal(["N123AB"], WithQueueBadge(view.Canvas));
            ScopeTrack track = view.Canvas.CurrentFrame.Tracks.Single(t => t.Aircraft.Callsign == "N123AB");
            Assert.Equal(2, track.QueueBadge?.Count);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope lens off")]
    [InlineData(".scope lens queue off")]
    public async Task EitherSwitchOff_DrawsNoBadge(string command)
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope();
        try
        {
            AddRouted(main, view.Canvas, "N123AB", 300, 300).PendingCommands = PendingTwo;
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens queue on");
            Assert.Equal(["N123AB"], WithQueueBadge(view.Canvas));

            main.RunScopeCommand(command);

            Assert.Empty(WithQueueBadge(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_KeepsTheQueueSwitch()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens queue on");
            string json = JsonSerializer.Serialize(scope.CapturePrefSet("QUEUE"), ScopePrefSet.JsonOptions);
            main.RunScopeCommand(".scope lens off");
            main.RunScopeCommand(".scope lens queue off");

            scope.ApplyPrefSet(Assert.IsType<ScopePrefSet>(JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)));

            Assert.Equal(new ScopeLensSettings(Enabled: true, Path: false, Queue: true, CheckIn: false), scope.Lens);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task QueueWithACallsign_AnswersTheUsageLine()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "KSFO", 5000);

            main.RunScopeCommand(".scope lens queue KSFO");

            Assert.Equal(LensUsage, main.StatusText);
            Assert.Equal(ScopeLensSettings.Off, scope.Lens);
            Assert.Empty(scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Makes ERAM sector 44 the active position, so the Scope is viewed from it.</summary>
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

    /// <summary>Adds <paramref name="callsign"/> at the canvas's screen point, owned by sector 44, its pilot not yet called.</summary>
    private static AircraftModel AddOwnedNotCalled(MainViewModel main, ScopeCanvas canvas, string callsign, float screenX, float screenY)
    {
        AircraftModel ac = AddRouted(main, canvas, callsign, screenX, screenY);
        ac.Owner = "OAK_44_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";
        ac.HasMadeInitialContact = false;
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static (string Callsign, string? Tag)[] WithCheckInTag(ScopeCanvas canvas)
    {
        Dispatcher.UIThread.RunJobs();
        return
        [
            .. canvas
                .CurrentFrame.Tracks.Where(t => t.CheckInTag is not null)
                .Select(t => (t.Aircraft.Callsign, t.CheckInTag))
                .OrderBy(t => t.Callsign, StringComparer.Ordinal),
        ];
    }

    [AvaloniaFact]
    public async Task LensAndCheckInOn_TagAnOwnedAircraftWhosePilotHasNotCalled()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope();
        try
        {
            ViewFromSector44(main);
            AddOwnedNotCalled(main, view.Canvas, "N123AB", 300, 300);
            AddRouted(main, view.Canvas, "UAL1", 500, 400);
            main.RunScopeCommand(".scope lens on");
            Assert.Empty(WithCheckInTag(view.Canvas));

            main.RunScopeCommand(".scope lens checkin on");

            Assert.Equal("Scope lens checkin ON", main.StatusText);
            Assert.Equal([("N123AB", (string?)"NO CALL")], WithCheckInTag(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope lens off")]
    [InlineData(".scope lens checkin off")]
    public async Task EitherSwitchOff_DrawsNoCheckInTag(string command)
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScope();
        try
        {
            ViewFromSector44(main);
            AddOwnedNotCalled(main, view.Canvas, "N123AB", 300, 300);
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens checkin on");
            Assert.NotEmpty(WithCheckInTag(view.Canvas));

            main.RunScopeCommand(command);

            Assert.Empty(WithCheckInTag(view.Canvas));
            if (command == ".scope lens checkin off")
            {
                Assert.Equal("Scope lens checkin OFF", main.StatusText);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_KeepsTheCheckInSwitch()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope lens on");
            main.RunScopeCommand(".scope lens checkin on");
            string json = JsonSerializer.Serialize(scope.CapturePrefSet("CHECKIN"), ScopePrefSet.JsonOptions);
            main.RunScopeCommand(".scope lens off");
            main.RunScopeCommand(".scope lens checkin off");

            scope.ApplyPrefSet(Assert.IsType<ScopePrefSet>(JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)));

            Assert.Equal(new ScopeLensSettings(Enabled: true, Path: false, Queue: false, CheckIn: true), scope.Lens);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CheckInWithACallsign_AnswersTheUsageLine()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "KSFO", 5000);

            main.RunScopeCommand(".scope lens checkin KSFO");

            Assert.Equal(LensUsage, main.StatusText);
            Assert.Equal(ScopeLensSettings.Off, scope.Lens);
            Assert.Empty(scope.LensPathCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
