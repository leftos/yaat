using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Altitude… flyout for a VFR aircraft (<see cref="VfrAltitudeFlyout"/>), opened from the strip: the mode toggle, the
/// thousands and hundreds columns, the cruising-altitude outlines (the magnetic ground track's hemisphere, more than 3,000 ft
/// above the nearest airport, on real airports placed under the aircraft), the now mark, the 17,500 ft top, the command each
/// mode sends and the live refresh.
/// </summary>
public class VfrAltitudeFlyoutTests
{
    private const string Callsign = "N302AB";
    private const string Initials = "AB";

    /// <summary>A field at 0 ft.</summary>
    private const string SeaLevelField = "KGAO";

    /// <summary>A field at 1,000 ft.</summary>
    private const string ThousandFootField = "KSWO";

    /// <summary>A field at 1,500 ft.</summary>
    private const string FifteenHundredFootField = "KCEK";

    /// <summary>A field with more than 10° of magnetic declination (east).</summary>
    private const string LargeDeclinationField = "KSEA";

    public VfrAltitudeFlyoutTests() => TestVnasData.EnsureInitialized();

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    [AvaloniaFact]
    public void Track090AtSeaLevel_OutlinesThreeAndFive_NotFour()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 2000), new RecordingMenuHost(""));

        AssertOutlined(open.Panel, outlined: [3, 5], plain: [4]);
    }

    [AvaloniaFact]
    public void Track270AtSeaLevel_OutlinesFourAndSix_NotFive()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 270, 2000), new RecordingMenuHost(""));

        AssertOutlined(open.Panel, outlined: [4, 6], plain: [5]);
    }

    /// <summary>At a 1,000 ft field, 3,500 is 2,500 ft above it, so thousand 3 is not outlined; 5,500 is.</summary>
    [AvaloniaFact]
    public void Track090AtField1000_LeavesThreeUnoutlined_OutlinesFive()
    {
        using Opened open = OpenStrip(Aircraft(ThousandFootField, 1000, 90, 2000), new RecordingMenuHost(""));

        AssertOutlined(open.Panel, outlined: [5], plain: [3]);
    }

    /// <summary>At a 1,500 ft field, 4,500 is exactly 3,000 ft above it: the rule wants more than 3,000, so it is not outlined.</summary>
    [AvaloniaFact]
    public void Track270AtField1500_ExactlyThreeThousandAbove_IsNotOutlined()
    {
        using Opened open = OpenStrip(Aircraft(FifteenHundredFootField, 1500, 270, 2000), new RecordingMenuHost(""));

        AssertOutlined(open.Panel, outlined: [6], plain: [4]);
    }

    [AvaloniaFact]
    public void Track1799VersusTrack1800_FlipsOddToEven()
    {
        using (Opened east = OpenStrip(Aircraft(SeaLevelField, 0, 179.9, 2000), new RecordingMenuHost("")))
        {
            AssertOutlined(east.Panel, outlined: [5], plain: [6]);
        }

        using Opened west = OpenStrip(Aircraft(SeaLevelField, 0, 180.0, 2000), new RecordingMenuHost(""));
        AssertOutlined(west.Panel, outlined: [6], plain: [5]);
    }

    [AvaloniaFact]
    public void At4200_ThousandFourIsMarkedNow()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost(""));

        Assert.Contains(VfrAltitudePanel.NowClass, open.Panel.ThousandCell(4).Classes);
        Assert.All(
            open.Panel.Thousands.Where(t => t != 4),
            t => Assert.DoesNotContain(VfrAltitudePanel.NowClass, open.Panel.ThousandCell(t).Classes)
        );
        Assert.StartsWith("now 4,200", open.Panel.TitleNow);
    }

    [AvaloniaFact]
    public void Thousand17_Offers17500_Disables17600To17900_NoThousand18()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost(""));

        Click(open.Panel.ThousandCell(17));

        Assert.Equal(17, open.Panel.Thousands.Max());
        Assert.True(open.Panel.HundredCell(500).IsEnabled);
        Assert.All([600, 700, 800, 900], hundred => Assert.False(open.Panel.HundredCell(hundred).IsEnabled));
    }

    /// <summary>Thousand 5 then hundred 500 sends the exact command of each mode: Maintain climbs above the aircraft and descends below it.</summary>
    [AvaloniaTheory]
    [InlineData(VfrAltitudeMode.Maintain, 4200, "CM 5500")]
    [InlineData(VfrAltitudeMode.Maintain, 6000, "DM 5500")]
    [InlineData(VfrAltitudeMode.AtOrAbove, 6000, "CM A5500")]
    [InlineData(VfrAltitudeMode.AtOrBelow, 4200, "CM B5500")]
    public void ThousandThenHundred_SendsTheModesCommand(VfrAltitudeMode mode, double altitude, string expected)
    {
        var host = new RecordingMenuHost("");
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, altitude), host);

        Click(open.Panel.Toggle(mode));
        Click(open.Panel.ThousandCell(5));
        Assert.Empty(host.Sent);
        Click(open.Panel.HundredCell(500));

        Assert.Equal([(Callsign, expected, Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void HeaderBeforeTheHundred_ShowsTheValueAndCommand()
    {
        var host = new RecordingMenuHost("");
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), host);

        Click(open.Panel.Toggle(VfrAltitudeMode.AtOrAbove));
        Click(open.Panel.ThousandCell(5));

        Assert.Equal("5,000 at or above · CM A5000", open.Panel.Header);
        Assert.Empty(host.Sent);
    }

    /// <summary>A live tick turning the track from 090 to 270 moves the outlines from odd thousands to even, mode and pick kept.</summary>
    [AvaloniaFact]
    public void LiveTick_TrackTurns090To270_MovesTheOutlines_KeepsTheMode()
    {
        var host = new RecordingMenuHost("");
        AircraftModel aircraft = Aircraft(SeaLevelField, 0, 90, 4200);
        using Opened open = OpenStrip(aircraft, host);
        Click(open.Panel.Toggle(VfrAltitudeMode.AtOrBelow));
        Click(open.Panel.ThousandCell(5));
        AssertOutlined(open.Panel, outlined: [5], plain: [6]);

        aircraft.TrueTrack = TrueTrackFor(aircraft.Position, 270);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(VfrAltitudeMode.AtOrBelow, open.Panel.Mode);
        Assert.True(open.Panel.Toggle(VfrAltitudeMode.AtOrBelow).IsChecked);
        Assert.Equal(5, open.Panel.PickedThousand);
        AssertOutlined(open.Panel, outlined: [6], plain: [5]);
    }

    // --- Floor, now mark, header, Maintain at the current altitude, declination, keyboard ---------------

    /// <summary>A 1,050 ft field: no thousand 0; in thousand 1, 000 (1,000) is below the 1,100 ft floor and disabled, 100 is offered.</summary>
    [AvaloniaFact]
    public void Field1050_NoThousandZero_Floor1100()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost("") { FieldElevation = 1050 });

        Assert.DoesNotContain(0, open.Panel.Thousands);
        Click(open.Panel.ThousandCell(1));

        Assert.False(open.Panel.HundredCell(0).IsEnabled);
        Assert.True(open.Panel.HundredCell(100).IsEnabled);
    }

    /// <summary>A field at 0 ft: thousand 0 is listed, but 000 (0 ft) is under the 100 ft floor and disabled.</summary>
    [AvaloniaFact]
    public void Field0_ThousandZeroListed_HundredZeroDisabled()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost(""));

        Assert.Contains(0, open.Panel.Thousands);
        Click(open.Panel.ThousandCell(0));

        Assert.False(open.Panel.HundredCell(0).IsEnabled);
        Assert.True(open.Panel.HundredCell(100).IsEnabled);
    }

    /// <summary>At 4,700 ft the now mark is on thousand 4, the thousand holding the altitude, not the nearest one.</summary>
    [AvaloniaFact]
    public void At4700_ThousandFourIsMarkedNow()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4700), new RecordingMenuHost(""));

        Assert.Contains(VfrAltitudePanel.NowClass, open.Panel.ThousandCell(4).Classes);
        Assert.DoesNotContain(VfrAltitudePanel.NowClass, open.Panel.ThousandCell(5).Classes);
    }

    /// <summary>The header's value under the pointer: shown with its command while offered, else <c>—</c> and the verb with <c>···</c>.</summary>
    [AvaloniaTheory]
    [InlineData(1050, 1, 0, "— maintain · CM ···")]
    [InlineData(1050, 1, 100, "1,100 maintain · DM 1100")]
    [InlineData(0, 0, 0, "— maintain · CM ···")]
    [InlineData(0, 17, 600, "— maintain · CM ···")]
    [InlineData(0, 17, 500, "17,500 maintain · CM 17500")]
    public void Header_ShowsOnlyAnOfferedValue(double field, int thousand, int hundred, string header)
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost("") { FieldElevation = field });

        Click(open.Panel.ThousandCell(thousand));
        open.Panel.Hover(hundred);

        Assert.Equal(header, open.Panel.Header);
    }

    /// <summary>Maintain at exactly the aircraft's altitude climbs: <c>CM 5500</c>, not <c>DM</c>.</summary>
    [AvaloniaFact]
    public void MaintainAtTheCurrentAltitude_SendsCm()
    {
        var host = new RecordingMenuHost("");
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 5500), host);

        Click(open.Panel.ThousandCell(5));
        Click(open.Panel.HundredCell(500));

        Assert.Equal([(Callsign, "CM 5500", Initials)], host.Sent);
    }

    /// <summary>
    /// At an airport with more than 10° of declination, a true track and its magnetic track on opposite sides of 180: the outlines
    /// follow the magnetic one.
    /// </summary>
    [AvaloniaFact]
    public void LargeDeclination_OutlinesFollowTheMagneticTrack()
    {
        (double lat, double lon) =
            NavigationDatabase.Instance.GetAirportPosition(LargeDeclinationField) ?? throw new InvalidOperationException("no field");
        var position = new LatLon(lat, lon);
        double declination = MagneticDeclination.GetDeclination(position);
        Assert.True(Math.Abs(declination) > 10, $"declination at {LargeDeclinationField} is {declination}");
        double trueTrack = 180 + (declination / 2);
        bool magneticOdd = (trueTrack - declination) < 180;
        Assert.NotEqual(magneticOdd, trueTrack < 180);
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            FlightRules = "VFR",
            Altitude = 4200,
            Position = position,
            TrueTrack = trueTrack,
        };

        using Opened open = OpenStrip(aircraft, new RecordingMenuHost(""));

        AssertOutlined(open.Panel, outlined: magneticOdd ? [5, 7] : [6, 8], plain: magneticOdd ? [6, 8] : [5, 7]);
    }

    /// <summary>Escape closes the flyout back to the strip button and leaves the menu open, sending nothing.</summary>
    [AvaloniaFact]
    public void Keyboard_Escape_ClosesTheFlyout_MenuStaysOpen()
    {
        var host = new RecordingMenuHost("");
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), host);

        Press(open.Flyout, Key.Escape);

        Assert.False(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);
    }

    /// <summary>Reopened, the flyout has forgotten the thousand picked before it closed.</summary>
    [AvaloniaFact]
    public void Reopen_ClearsThePickedThousand()
    {
        using Opened open = OpenStrip(Aircraft(SeaLevelField, 0, 90, 4200), new RecordingMenuHost(""));
        Click(open.Panel.ThousandCell(5));
        Press(open.Flyout, Key.Escape);

        QuickCommandStrip.OpenSubmenu(open.Button);
        Layout(open.Flyout);

        Assert.True(open.Flyout.IsOpen);
        Assert.Null(open.Panel.PickedThousand);
        Assert.Equal("— maintain · CM ···", open.Panel.Header);
    }

    /// <summary>
    /// Each of <paramref name="outlined"/> thousands is outlined and each of <paramref name="plain"/> is not; with each picked in
    /// turn, its 500 hundred carries the same outline.
    /// </summary>
    private static void AssertOutlined(VfrAltitudePanel panel, int[] outlined, int[] plain)
    {
        foreach ((int thousand, bool cruise) in outlined.Select(t => (t, true)).Concat(plain.Select(t => (t, false))))
        {
            Assert.True(
                panel.ThousandCell(thousand).Classes.Contains(VfrAltitudePanel.CruiseClass) == cruise,
                $"thousand {thousand} outline is not {cruise}"
            );
            int? picked = panel.PickedThousand;
            Click(panel.ThousandCell(thousand));
            Assert.True(panel.HundredCell(500).Classes.Contains(VfrAltitudePanel.CruiseClass) == cruise, $"{thousand},500's outline is not {cruise}");
            if (picked is { } before)
            {
                Click(panel.ThousandCell(before));
            }
        }
    }

    /// <summary>A VFR aircraft on <paramref name="airport"/>'s reference point, on magnetic track <paramref name="magneticTrack"/>.</summary>
    private static AircraftModel Aircraft(string airport, double elevation, double magneticTrack, double altitude)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        Assert.Equal(elevation, navDb.GetAirportElevation(airport) ?? double.NaN, 0.5);
        (double lat, double lon) = navDb.GetAirportPosition(airport) ?? throw new InvalidOperationException($"no {airport} in the navdata");
        var position = new LatLon(lat, lon);
        return new AircraftModel
        {
            Callsign = Callsign,
            FlightRules = "VFR",
            Altitude = altitude,
            Position = position,
            TrueTrack = TrueTrackFor(position, magneticTrack),
        };
    }

    /// <summary>The true track giving magnetic track <paramref name="magnetic"/> at <paramref name="position"/>, as the flyout reads it.</summary>
    private static double TrueTrackFor(LatLon position, double magnetic)
    {
        double declination = MagneticDeclination.GetDeclination(position);
        double track = magnetic + declination;
        while ((track - declination) < magnetic)
        {
            track = Math.BitIncrement(track);
        }

        while ((track - declination) > magnetic)
        {
            track = Math.BitDecrement(track);
        }

        return track;
    }

    private static Opened OpenStrip(IMenuAircraft aircraft, RecordingMenuHost host)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.AltitudeVfr);
        MenuItem built = entry.Build(aircraft, Context(), host) ?? throw new InvalidOperationException("no Altitude… item");
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(MenuIds.AltitudeVfr) ?? throw new InvalidOperationException("no Altitude… glyph");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, glyph), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 900, Height = 900 };
        try
        {
            window.Show();
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Layout(flyout);
            AltitudeFlyoutHost holder = Assert.IsType<AltitudeFlyoutHost>(Assert.Single(flyout.Items.OfType<MenuItem>()).Header);
            return new Opened(window, menu, flyout, button, Assert.IsType<VfrAltitudePanel>(holder.Child));
        }
        catch
        {
            menu.Close();
            window.Close();
            throw;
        }
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Layout(MenuFlyout flyout)
    {
        (flyout.Popup.Child as Control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        (flyout.Popup.Child as Control)?.UpdateLayout();
    }

    /// <summary>Presses <paramref name="key"/> on whatever holds focus in the flyout.</summary>
    private static void Press(MenuFlyout flyout, Key key)
    {
        IInputElement? focused = TopLevel.GetTopLevel(Assert.IsAssignableFrom<Visual>(flyout.Popup.Child))?.FocusManager?.GetFocusedElement();
        Assert.IsAssignableFrom<Control>(focused).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
        Layout(flyout);
    }

    /// <summary>The open flyout, its strip button and its panel. Disposing closes the menu, then the window.</summary>
    private sealed record Opened(Window Window, ContextMenu Menu, MenuFlyout Flyout, Button Button, VfrAltitudePanel Panel) : IDisposable
    {
        public void Dispose()
        {
            Menu.Close();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
        }
    }
}
