using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Zoom bands: a manual change stays pinned until AUTO. The range picks the band and never pins; a pin holds over every
/// band and every profile until AUTO clears it, AUTO lights while any pin is set, and pref sets and the saved state keep
/// the pinned values only. The canvas draws others' tracks at the band's level, or the pin's over it.
/// </summary>
public class ScopeBandPinTests : IDisposable
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-scope-pin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    /// <summary>A scope viewed from ZOA sector 44, saving through this test's store.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenStoredScopeAsync()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        return (main, Reopen(main));
    }

    private ScopeViewModel Reopen(MainViewModel main)
    {
        var scope = new ScopeViewModel(main, Store(), profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return scope;
    }

    private ScopePrefSetStore Store() => new(_root, AppLog.CreateLogger<ScopePrefSetStore>());

    /// <summary>A shown scope window, so the canvas builds frames; closing the window disposes the scope.</summary>
    private static (ScopeView View, Window Window) ShowScope(ScopeViewModel scope)
    {
        var view = new ScopeView { DataContext = scope };
        var side = new Border { Width = 100 };
        DockPanel.SetDock(side, Dock.Left);
        var panel = new DockPanel();
        panel.Children.Add(side);
        panel.Children.Add(view);
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = panel,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    /// <summary>An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.</summary>
    private static void AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        main.Aircraft.Add(
            new AircraftModel
            {
                Callsign = callsign,
                AircraftType = "B738",
                FiledAircraftType = "B738",
                FlightRules = "IFR",
                TransponderMode = "C",
                Altitude = 5000,
                GroundSpeed = 250,
                Position = new LatLon(lat, lon),
            }
        );
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task RangeChange_AfterAPin_StaysPinned()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);

            main.RunScopeCommand(".scope others partial");
            main.RunScopeCommand(".scope range 25");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);

            main.RunScopeCommand(".scope range 200");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
            Assert.Null(scope.EffectiveBand.MaxNm);
            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RangeAlone_PinsNothing_AndMovesTheBandAcrossItsEdge()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeZoomBand at40 = scope.EffectiveBand;
            Assert.Equal(ScopeProfile.OthersPartial, at40.OthersLevel);

            main.RunScopeCommand(".scope range 20");
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
            Assert.NotEqual(at40, scope.EffectiveBand);
            Assert.Equal(ScopeBandPins.None, scope.Pins);

            main.RunScopeCommand(".scope range 60");
            Assert.Equal(at40, scope.EffectiveBand);
            Assert.Equal(ScopeBandPins.None, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MixedUnownedTemplate_IsStarsUpTo120Nm_AndEramBeyond()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            var unowned = new AircraftModel { Callsign = "SKW5410" };
            main.RunScopeCommand(".scope template mixed");
            Assert.Equal(ScopeTemplates.MixedId, scope.TemplateId);

            main.RunScopeCommand(".scope range 100");
            Assert.Equal(ScopeTemplates.StarsId, scope.EffectiveBand.Unowned);
            Assert.Equal(ScopeTemplates.StarsId, scope.TrackTemplateId(unowned));

            main.RunScopeCommand(".scope range 200");
            Assert.Equal(ScopeTemplates.EramId, scope.EffectiveBand.Unowned);
            Assert.Equal(ScopeTemplates.EramId, scope.TrackTemplateId(unowned));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Canvas_OthersLevel_FollowsTheEffectiveBand()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        (ScopeView view, Window window) = ShowScope(scope);
        try
        {
            main.RunScopeCommand(".scope template eram");

            // No one owns this track, so it draws at the band's others' level.
            AddAt(main, view.Canvas, "SKW5410", 300, 300);
            Assert.Equal(ScopeBlockLevel.Partial, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);

            main.RunScopeCommand(".scope others full");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ScopeBlockLevel.Full, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Canvas_UnownedTrackLook_FollowsTheEffectiveBand()
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        (ScopeView view, Window window) = ShowScope(scope);
        try
        {
            main.RunScopeCommand(".scope profile mixed");
            main.RunScopeCommand(".scope template mixed");
            main.RunScopeCommand(".scope range 200");

            // No one owns this track: beyond 120 NM the Mixed profile's band draws it in the ERAM look.
            AddAt(main, view.Canvas, "SKW5410", 300, 300);
            Assert.True(Assert.Single(view.Canvas.CurrentFrame.Tracks).EramLook);

            main.RunScopeCommand(".scope profile stars");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200, scope.RangeNm);
            Assert.False(Assert.Single(view.Canvas.CurrentFrame.Tracks).EramLook);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Auto_ClearsThePin_AndTheValueFollowsTheBandAgain()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            main.RunScopeCommand(".scope others partial");

            main.RunScopeCommand(".scope auto");

            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
            main.RunScopeCommand(".scope range 60");
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_IsLitOnlyWhileAPinIsSet()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.False(scope.IsActive(Button(ScopeButtonId.Others)));

            main.RunScopeCommand(".scope others full");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.True(scope.IsActive(Button(ScopeButtonId.Others)));

            main.RunScopeCommand(".scope range 100");
            Assert.True(scope.IsActive(Button(ScopeButtonId.Auto)));

            main.RunScopeCommand(".scope auto");
            Assert.False(scope.IsActive(Button(ScopeButtonId.Auto)));
            Assert.False(scope.IsActive(Button(ScopeButtonId.Others)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OthersButton_FlipsTheLevelInEffect_AndPinsIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            Assert.Equal("FULL", scope.SpinnerValue(Button(ScopeButtonId.Others)));

            scope.Press(Button(ScopeButtonId.Others));

            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
            Assert.Equal("PARTIAL", scope.SpinnerValue(Button(ScopeButtonId.Others)));
            Assert.Equal("Radar others PARTIAL (pinned)", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ProfileChange_KeepsThePins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 20");
            main.RunScopeCommand(".scope others partial");

            main.RunScopeCommand(".scope profile stars");

            Assert.Equal(ScopeProfileKind.Stars, scope.ProfileKind);
            Assert.Equal(ScopeProfile.OthersPartial, scope.Pins.OthersLevel);
            Assert.Equal(ScopeProfile.OthersPartial, scope.EffectiveBand.OthersLevel);
            main.RunScopeCommand(".scope auto");
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_CapturesAndAppliesEveryPin_AndUnpinnedValuesAreNotSaved()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopePrefSet unpinned = scope.CapturePrefSet(null);
            Assert.Null(unpinned.PinOthersLevel);
            Assert.Null(unpinned.PinVectorMin);
            Assert.Null(unpinned.PinHistory);
            Assert.Null(unpinned.PinRings);

            ScopePrefSet pinned = unpinned with { PinOthersLevel = "full", PinVectorMin = 2, PinHistory = 7, PinRings = "dri" };
            ScopePrefSet read = JsonSerializer.Deserialize<ScopePrefSet>(
                JsonSerializer.Serialize(pinned, ScopePrefSet.JsonOptions),
                ScopePrefSet.JsonOptions
            )!;
            scope.ApplyPrefSet(read);

            Assert.Equal(new ScopeBandPins("full", 2, 7, "dri"), scope.Pins);
            ScopePrefSet captured = scope.CapturePrefSet(null);
            Assert.Equal("full", captured.PinOthersLevel);
            Assert.Equal(2, captured.PinVectorMin);
            Assert.Equal(7, captured.PinHistory);
            Assert.Equal("dri", captured.PinRings);

            ScopePrefSet half = pinned with { PinVectorMin = 1.5 };
            ScopePrefSet readHalf = JsonSerializer.Deserialize<ScopePrefSet>(
                JsonSerializer.Serialize(half, ScopePrefSet.JsonOptions),
                ScopePrefSet.JsonOptions
            )!;
            scope.ApplyPrefSet(readHalf);

            Assert.Equal(1.5, scope.Pins.VectorMin);
            Assert.Equal(1.5, scope.CapturePrefSet(null).PinVectorMin);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_UnknownPinValues_KeepTheCurrentPins()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");

            scope.ApplyPrefSet(scope.CapturePrefSet(null) with { PinOthersLevel = "half", PinHistory = -1, PinRings = "halo" });

            Assert.Equal(ScopeBandPins.None with { OthersLevel = "full" }, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PrefSet_SaveAsThenLoad_RestoresThePin()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenStoredScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");
            main.RunScopeCommand(".scope pref saveas PINS");
            main.RunScopeCommand(".scope auto");

            main.RunScopeCommand(".scope pref load PINS");

            Assert.Equal(ScopeProfile.OthersFull, scope.Pins.OthersLevel);
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task SavedState_RestoresThePins_AndSavesNoneOnceCleared()
    {
        (MainViewModel main, ScopeViewModel first) = await OpenStoredScopeAsync();
        main.RunScopeCommand(".scope others full");
        first.Dispose();

        ScopeViewModel reopened = Reopen(main);
        try
        {
            Assert.Equal(ScopeProfile.OthersFull, reopened.Pins.OthersLevel);
            Assert.True(reopened.IsActive(Button(ScopeButtonId.Auto)));
            main.RunScopeCommand(".scope auto");
        }
        finally
        {
            reopened.Dispose();
        }

        Assert.Null(Store().LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.PinOthersLevel);
    }
}
