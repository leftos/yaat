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
using Yaat.Client.Views.Ground;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

// The canvas section the ground view builds for itself, over the real view model and canvas: its Display items, reading
// the state the running client keeps (the data-block offsets, hidden choices and the measure tool) and writing it back.
// CanvasMenuItemsTests pins the item builders; these pin the wiring that turns them into a section, since no golden
// carries a moved data block or a non-default taxi-route radio. The Scope's Display submenu is pinned by
// ScopeAircraftMenuTests.
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

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    // The ground's Display items: the order with a data-block offset set (no golden carries one), and that the
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
            MenuContext context = Context();

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

    // The ground's background (empty-space) menu offers "Show all datablocks", disabled while no block is hidden; it
    // clears the set.
    [AvaloniaFact]
    public void GroundBackgroundMenu_ShowAllDataBlocks_DisabledUntilOneIsHidden_ThenShowsAll()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (GroundView view, GroundCanvas canvas, Window window) = ShowGroundView(main);
        try
        {
            main.Ground.SetLayoutForTesting(
                new GroundLayoutDto(
                    "SFO",
                    [new GroundNodeDto(1, 37.62, -122.39, "Taxiway", null, null, null)],
                    [],
                    null,
                    null,
                    null,
                    IsRunwaysOnly: false
                )
            );
            canvas.Aircraft = [new AircraftModel { Callsign = Callsign, IsOnGround = true }];
            Assert.False(ShowAllItem(view.BuildNodeContextMenu(1, new Avalonia.Point(10, 10))).IsEnabled);

            canvas.ToggleHiddenDataBlock(Callsign);
            MenuItem showAll = ShowAllItem(view.BuildNodeContextMenu(1, new Avalonia.Point(10, 10)));
            Assert.True(showAll.IsEnabled);

            Click(showAll);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
        }
        finally
        {
            window.Close();
        }
    }

    private static MenuItem ShowAllItem(ContextMenu? menu)
    {
        Assert.NotNull(menu);
        return menu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Show all datablocks"));
    }
}
