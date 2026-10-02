using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using SkiaSharp;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Commands;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

// The canvas section a view builds for itself, over the real view model and canvas: the ground's flat display items and
// the radar's Display submenu, each reading the state the running client keeps (the data-block offsets, minified and
// hidden choices, the nav route and the measure tool) and writing it back. CanvasMenuItemsTests pins the item builders;
// these pin the wiring that turns them into a section, since no golden carries a moved or minified data block, a shown
// nav route or a non-default taxi-route radio.
public class CanvasMenuSectionTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    /// <summary>An item's header text, or "---" for a separator, so a section's whole item sequence can be asserted.</summary>
    private static List<string> Headers(MenuItem menu) => [.. menu.Items.Select(Describe)];

    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem menuItem => menuItem.Header as string ?? "",
            _ => item?.GetType().Name ?? "null",
        };

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>
    /// A ground view over <paramref name="main"/>'s ground view model in a shown window; asserts the canvas's data-block
    /// state binding resolved to the view model's, as it does in the running client.
    /// </summary>
    private static (GroundView View, GroundCanvas Canvas, Window Window) ShowGroundView(MainViewModel main)
    {
        var view = new GroundView { DataContext = main.Ground };
        var window = new Window { DataContext = main, Content = view };
        window.ShowAndRunLayout();

        GroundCanvas? canvas = view.FindControl<GroundCanvas>("Canvas");
        Assert.NotNull(canvas);
        Assert.Same(main.Ground.DataBlockState, canvas!.DataBlockState);
        return (view, canvas, window);
    }

    /// <summary>The same for a radar view over <paramref name="main"/>'s radar view model.</summary>
    private static (RadarView View, RadarCanvas Canvas, Window Window) ShowRadarView(MainViewModel main)
    {
        var view = new RadarView { DataContext = main.Radar };
        var window = new Window { DataContext = main, Content = view };
        window.ShowAndRunLayout();

        RadarCanvas? canvas = view.FindControl<RadarCanvas>("Canvas");
        Assert.NotNull(canvas);
        Assert.Same(main.Radar.DataBlockState, canvas!.DataBlockState);
        return (view, canvas, window);
    }

    private static MenuContext Context(CatalogMenuView view) => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None, view);

    // The ground's flat display items: the order with a data-block offset set (no golden carries one), and that the
    // taxi-route radio and the datablock item write the ground view model and the canvas the section reads back.
    [AvaloniaFact]
    public void GroundCanvasItems_OrderAndClicks_ReachTheViewModelAndCanvas()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (GroundView view, GroundCanvas canvas, Window window) = ShowGroundView(main);
        try
        {
            GroundViewModel ground = main.Ground;
            ground.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
            MenuContext context = Context(CatalogMenuView.Ground);

            List<MenuItem> items = [.. view.BuildCanvasItems(ground, context).OfType<MenuItem>()];

            Assert.Equal(
                ["Taxi route", "Hide datablock", "Reset datablock position", $"Measure from {Callsign}"],
                items.Select(i => i.Header as string)
            );

            // The taxi-route radio sets the ground view model's per-aircraft mode.
            MenuItem alwaysHide = items[0].Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Always hide");
            Click(alwaysHide);
            Assert.Equal(TaxiRouteDisplayMode.AlwaysHide, ground.GetTaxiRouteMode(Callsign));

            // Hide datablock writes the canvas's per-callsign choice, which the rebuilt item reads back.
            Click(items[1]);
            Assert.True(canvas.IsDataBlockHidden(Callsign));

            List<MenuItem> rebuilt = [.. view.BuildCanvasItems(ground, context).OfType<MenuItem>()];

            Assert.Equal(
                ["Taxi route", "Show datablock", "Reset datablock position", $"Measure from {Callsign}"],
                rebuilt.Select(i => i.Header as string)
            );
        }
        finally
        {
            window.Close();
        }
    }

    // The radar's Display submenu at every state on at once: the minified form, a moved data block, a shown nav route
    // and an anchored measurement, which the header sequence and the labels that follow state pin.
    [AvaloniaFact]
    public void RadarCanvasDisplay_AllStatesOn_PinsTheHeaderSequence()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (RadarView view, RadarCanvas canvas, Window window) = ShowRadarView(main);
        try
        {
            RadarViewModel radar = main.Radar;
            var host = new RecordingMenuHost("");
            MenuContext context = Context(CatalogMenuView.Radar);

            canvas.ToggleMinifiedDataBlock(Callsign);
            radar.DataBlockState.ManualOffsets[Callsign] = new SKPoint(12, 34);
            radar.ToggleShowPath(Callsign);
            var measure = new RangeBearingViewState(new RangeBearingLineStore());
            radar.SetMeasureState(measure);
            measure.Pick(RblEndpoint.OnAircraft(Callsign), RblView.Radar, RangeBearingViewState.TrackLookup(_ => null), RblUnits.NauticalMiles);

            Assert.Equal(
                [
                    "Full datablock",
                    "Reset datablock position",
                    "Hide nav route",
                    $"Measure to {Callsign}",
                    "---",
                    "Leader direction",
                    "J-ring",
                    "Cone",
                    "---",
                    "Blank target",
                    "Unblank target",
                ],
                Headers(view.BuildCanvasDisplay(radar, context, host))
            );
        }
        finally
        {
            window.Close();
        }
    }

    // The radar's measure item against the real tool: no item with no tool, "from" while the tool is idle, "to" once it
    // is anchored.
    [AvaloniaFact]
    public void RadarCanvasDisplay_MeasureItem_NoTool_ThenIdle_ThenAnchored()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (RadarView view, RadarCanvas _, Window window) = ShowRadarView(main);
        try
        {
            RadarViewModel radar = main.Radar;
            var host = new RecordingMenuHost("");
            MenuContext context = Context(CatalogMenuView.Radar);

            radar.Measure = null;
            Assert.DoesNotContain("Measure", Headers(view.BuildCanvasDisplay(radar, context, host)));

            var measure = new RangeBearingViewState(new RangeBearingLineStore());
            radar.SetMeasureState(measure);
            Assert.Contains($"Measure from {Callsign}", Headers(view.BuildCanvasDisplay(radar, context, host)));

            measure.Pick(RblEndpoint.OnAircraft(Callsign), RblView.Radar, RangeBearingViewState.TrackLookup(_ => null), RblUnits.NauticalMiles);
            Assert.Contains($"Measure to {Callsign}", Headers(view.BuildCanvasDisplay(radar, context, host)));
        }
        finally
        {
            window.Close();
        }
    }
}
