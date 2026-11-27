using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The TOOLS tray's Rings tool: opening the tray arms the band's kind, a kind button arms or disarms, Esc and the tray
/// closing disarm, an armed click places a ring and selects nothing; one ring per kind per track, toggled off at the same
/// size and resized at another; DRI 3 refused above FL230; the <c>.scope rings</c> verbs and their echoes; rings cleared
/// when their aircraft leaves and on an unload, restart or rewind.
/// </summary>
public class ScopeRingsToolTests
{
    private const string RingsUsage =
        "Usage: .scope rings <jring|cone> <1.0-9.9|10-30> <callsign> | .scope rings <dri5|dri3> <callsign> | .scope rings clear [callsign]";

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScope(string? templateId = null)
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        if (templateId is not null)
        {
            Helpers.ScopeTestFixture.PickLook(main, templateId);
        }

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

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        AircraftModel ac = AddAircraft(main, callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static void ClickSymbol(Window window, ScopeCanvas canvas, AircraftModel ac)
    {
        ScopeTrack track = Assert.Single(canvas.CurrentFrame.Tracks, t => ReferenceEquals(t.Aircraft, ac));
        Point? inWindow = canvas.TranslatePoint(new Point(track.Symbol.X, track.Symbol.Y), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Centre(ScopeCanvas canvas, ScopeFieldSlot slot)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    private static void ClickAt(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task OpeningTools_ArmsTheBandsKind_JRingForJring_Dri5ForDri()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
            scope.Press(Button(ScopeButtonId.Tools));
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);
            Assert.True(scope.IsActive(Button(ScopeButtonId.ToolJRing)));
            scope.Press(Button(ScopeButtonId.Tools));
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);

            scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsDri };
            scope.Press(Button(ScopeButtonId.Tools));
            Assert.Equal(ScopeRingTool.Dri5, scope.ArmedRingTool);
            Assert.True(scope.IsActive(Button(ScopeButtonId.ToolDri5)));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AKindButton_ArmsIt_KeepsTheTrayOpen_AndASecondPressDisarms()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            scope.Press(Button(ScopeButtonId.Tools));
            scope.Press(Button(ScopeButtonId.ToolCone));

            Assert.Equal(ScopeRingTool.Cone, scope.ArmedRingTool);
            Assert.Equal(ScopeButtonId.Tools, scope.OpenMenu);
            Assert.True(scope.IsActive(Button(ScopeButtonId.ToolCone)));
            Assert.False(scope.IsActive(Button(ScopeButtonId.ToolJRing)));

            scope.Press(Button(ScopeButtonId.ToolCone));

            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
            Assert.False(scope.IsRingToolArmed);
            Assert.Equal(ScopeButtonId.Tools, scope.OpenMenu);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheTray_OrOpeningAnother_Disarms()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            scope.Press(Button(ScopeButtonId.Tools));
            scope.Press(Button(ScopeButtonId.ToolDri3));
            scope.Press(Button(ScopeButtonId.Brite));

            Assert.Equal(ScopeButtonId.Brite, scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Size_StepsCrcsGrid_AndShowsItsValue()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton size = Button(ScopeButtonId.ToolSize);
            Assert.Equal("3.0", scope.SpinnerValue(size));

            scope.StepSpinner(ScopeButtonId.ToolSize, 2);
            Assert.Equal(3.2, scope.RingSizeNm, 6);
            Assert.Equal("3.2", scope.SpinnerValue(size));

            scope.StepSpinner(ScopeButtonId.ToolSize, 200);
            Assert.Equal("30", scope.SpinnerValue(size));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ArmedClick_PlacesTheRing_AndDoesNotSelect_ThenEscDisarms()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope();
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            AircraftModel ac = AddAt(main, view.Canvas, "UAL238", 300, 300);
            main.SelectedAircraft = null;
            Dispatcher.UIThread.RunJobs();
            // Opening the tray arms the band's kind: J-ring, pinned here.
            scope.Pins = scope.Pins with
            {
                Rings = ScopeProfile.RingsJRing,
            };
            scope.Press(Button(ScopeButtonId.Tools));
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);

            ClickSymbol(window, view.Canvas, ac);

            Assert.Null(main.SelectedAircraft);
            Assert.Equal(new ScopeTrackRings(3.0, null, null), scope.LocalRings["UAL238"]);
            Assert.Equal("Radar J-ring 3.0 NM UAL238", main.StatusText);
            Assert.NotNull(Assert.Single(view.Canvas.CurrentFrame.Tracks).Rings.JRing);

            window.DispatchKey(Key.Escape);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);

            ClickSymbol(window, view.Canvas, ac);
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Equal(new ScopeTrackRings(3.0, null, null), scope.LocalRings["UAL238"]);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task ArmedClick_OnAnEramFieldA_PlacesTheRing_AndTogglesNoDwellLock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScope(ScopeTemplates.EramId);
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            AddAt(main, view.Canvas, "UAL238", 300, 300);
            // Opening the tray arms the band's kind: J-ring, pinned here.
            scope.Pins = scope.Pins with
            {
                Rings = ScopeProfile.RingsJRing,
            };
            scope.Press(Button(ScopeButtonId.Tools));
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);

            ClickAt(window, view.Canvas, Centre(view.Canvas, ScopeFieldSlot.Callsign));

            Assert.Equal(new ScopeTrackRings(3.0, null, null), scope.LocalRings["UAL238"]);
            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Placing_SameSizeRemoves_AnotherSizeResizes_OnePerKind()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(".scope rings jring 3.0 N123AB");
            Assert.Equal("Radar J-ring 3.0 NM N123AB", main.StatusText);
            main.RunScopeCommand(".scope rings cone 5 n123ab");
            Assert.Equal("Radar cone 5.0 NM N123AB", main.StatusText);
            main.RunScopeCommand(".scope rings dri5 N123AB");
            Assert.Equal("Radar DRI 5 NM N123AB", main.StatusText);
            Assert.Equal(new ScopeTrackRings(3.0, 5.0, ScopeDriKind.Standard), scope.RingsOf("N123AB"));

            main.RunScopeCommand(".scope rings jring 12 N123AB");
            Assert.Equal("Radar J-ring 12 NM N123AB", main.StatusText);
            main.RunScopeCommand(".scope rings dri3 N123AB");
            Assert.Equal(new ScopeTrackRings(12.0, 5.0, ScopeDriKind.Reduced), scope.RingsOf("N123AB"));

            main.RunScopeCommand(".scope rings cone 5.0 N123AB");
            Assert.Equal("Radar cone 5.0 NM N123AB removed", main.StatusText);
            main.RunScopeCommand(".scope rings dri3 N123AB");
            Assert.Equal("Radar DRI 3 NM N123AB removed", main.StatusText);
            Assert.Equal(new ScopeTrackRings(12.0, null, null), scope.RingsOf("N123AB"));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Dri3_AboveFl230_IsRefusedWithATerminalWarning_AndPlacesNothing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            await main.CrcAliasLoad;
            AddAircraft(main, "UAL1", altitudeFt: 24000);
            AddAircraft(main, "UAL2", altitudeFt: 23000);

            main.RunScopeCommand(".scope rings dri3 UAL1");

            Assert.Equal("DRI 3 needs FL230 or below", main.StatusText);
            Assert.Contains(main.TerminalEntries, e => (e.Kind == TerminalEntryKind.Warning) && (e.Message == "DRI 3 needs FL230 or below"));
            Assert.Equal(ScopeTrackRings.None, scope.RingsOf("UAL1"));

            main.RunScopeCommand(".scope rings dri3 UAL2");
            Assert.Equal(ScopeDriKind.Reduced, scope.RingsOf("UAL2").Dri);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope rings")]
    [InlineData(".scope rings halo N123AB")]
    [InlineData(".scope rings jring 0.9 N123AB")]
    [InlineData(".scope rings jring 10.5 N123AB")]
    [InlineData(".scope rings cone 31 N123AB")]
    [InlineData(".scope rings jring N123AB")]
    [InlineData(".scope rings dri5 5 N123AB")]
    [InlineData(".scope rings clear N123AB extra")]
    public async Task ABadRingsCommand_AnswersTheUsage_AndChangesNothing(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(command);

            Assert.Equal(RingsUsage, main.StatusText);
            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RingsBrightness_RoundTripsThroughAPrefSet_AndThePrefSetHoldsNoRing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope rings jring 3 UAL1");
            main.RunScopeCommand(".scope brite rings 40");
            Assert.Equal(".scope brite rings 40", main.StatusText);

            string json = JsonSerializer.Serialize(scope.CapturePrefSet("NIGHT"), ScopePrefSet.JsonOptions);
            main.RunScopeCommand(".scope brite rings 100");
            scope.ApplyPrefSet(JsonSerializer.Deserialize<ScopePrefSet>(json, ScopePrefSet.JsonOptions)!);

            Assert.Equal(40, scope.Brightness.Rings);
            Assert.DoesNotContain("UAL1", json, StringComparison.Ordinal);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task UnknownAircraft_IsSaid()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope rings jring 3 N999");

            Assert.Equal("Unknown aircraft: N999", main.StatusText);
            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Clear_RemovesOneTracksRings_OrEveryRing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 5000);
            main.RunScopeCommand(".scope rings jring 3 UAL1");
            main.RunScopeCommand(".scope rings cone 3 UAL2");

            main.RunScopeCommand(".scope rings clear ual1");
            Assert.Equal("Radar rings cleared UAL1", main.StatusText);
            Assert.Equal(["UAL2"], scope.LocalRings.Keys);

            main.RunScopeCommand(".scope rings clear");
            Assert.Equal("Radar rings cleared", main.StatusText);
            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task TheAircraftLeaving_DropsItsRings()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 5000);
            main.RunScopeCommand(".scope rings jring 3 UAL1");
            main.RunScopeCommand(".scope rings jring 3 UAL2");

            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["UAL2"], scope.LocalRings.Keys);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Unload_ClearsEveryRing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope rings dri5 UAL1");

            main.ClearScenarioState();

            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RewindAndRestart_ClearEveryRing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope rings dri5 UAL1");
            main.ApplyScenarioRewind([]);
            Assert.Empty(scope.LocalRings);

            AddAircraft(main, "UAL1", 5000);
            main.RunScopeCommand(".scope rings cone 4 UAL1");
            main.ApplyScenarioRestart([], null);
            Assert.Empty(scope.LocalRings);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
