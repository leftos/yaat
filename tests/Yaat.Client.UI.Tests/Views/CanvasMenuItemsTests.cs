using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

// The canvas-only item builders each view's section is made of, each built from the canvas state handed to it: the
// data-block form and position, the nav route, the measurement in progress, the taxi-route mode, the hidden data block,
// the Draw route entry and the overlays. Each builder's labels for every state, including those no golden reaches;
// CanvasMenuSectionTests pins the wiring the views put around them.
public class CanvasMenuItemsTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.None);

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaTheory]
    [InlineData(false, "Mini datablock")]
    [InlineData(true, "Full datablock")]
    public void DataBlockForm_LabelFollowsTheForm_AndClickToggles(bool minified, string header)
    {
        int toggles = 0;
        MenuItem item = CanvasMenuItems.DataBlockForm(minified, () => toggles++);

        Assert.Equal(header, item.Header as string);
        Click(item);
        Assert.Equal(1, toggles);
    }

    [AvaloniaFact]
    public void ResetDataBlockPosition_HiddenWhenNotMoved_ShownAndResetsWhenMoved()
    {
        Assert.Null(CanvasMenuItems.ResetDataBlockPosition(false, () => { }));

        int resets = 0;
        MenuItem item = Assert.IsType<MenuItem>(CanvasMenuItems.ResetDataBlockPosition(true, () => resets++));

        Assert.Equal("Reset datablock position", item.Header as string);
        Click(item);
        Assert.Equal(1, resets);
    }

    [AvaloniaTheory]
    [InlineData(false, "Show nav route")]
    [InlineData(true, "Hide nav route")]
    public void NavRoute_LabelFollowsTheState_AndClickToggles(bool shown, string header)
    {
        int toggles = 0;
        MenuItem item = CanvasMenuItems.NavRoute(shown, () => toggles++);

        Assert.Equal(header, item.Header as string);
        Click(item);
        Assert.Equal(1, toggles);
    }

    [AvaloniaFact]
    public void Measure_NoneIsHidden_FromWithoutAnchor_ToWithAnchor_AndClickPicks()
    {
        Assert.Null(CanvasMenuItems.Measure(MenuMeasureState.None, Callsign, () => { }));

        List<string> picks = [];
        MenuItem from = Assert.IsType<MenuItem>(CanvasMenuItems.Measure(MenuMeasureState.NoAnchor, Callsign, () => picks.Add("from")));
        MenuItem to = Assert.IsType<MenuItem>(CanvasMenuItems.Measure(MenuMeasureState.HasAnchor, Callsign, () => picks.Add("to")));

        Assert.Equal($"Measure from {Callsign}", from.Header as string);
        Assert.Equal($"Measure to {Callsign}", to.Header as string);

        Click(from);
        Click(to);
        Assert.Equal(["from", "to"], picks);
    }

    [AvaloniaFact]
    public void TaxiRoute_OneRadioItemPerMode_CurrentChecked_AndClickSetsIt()
    {
        List<TaxiRouteDisplayMode> set = [];
        MenuItem menu = CanvasMenuItems.TaxiRoute(TaxiRouteDisplayMode.AlwaysHide, set.Add);
        List<MenuItem> modes = [.. menu.Items.OfType<MenuItem>()];

        Assert.Equal("Taxi route", menu.Header as string);
        Assert.Equal(["Always show", "Always hide", "Follow “Show all” setting"], modes.Select(i => i.Header as string));
        Assert.All(modes, i => Assert.Equal(MenuItemToggleType.Radio, i.ToggleType));
        Assert.Equal([false, true, false], modes.Select(i => i.IsChecked));

        foreach (MenuItem mode in modes)
        {
            Click(mode);
        }

        Assert.Equal([TaxiRouteDisplayMode.AlwaysShow, TaxiRouteDisplayMode.AlwaysHide, TaxiRouteDisplayMode.Follow], set);
    }

    [AvaloniaTheory]
    [InlineData(false, "Hide datablock")]
    [InlineData(true, "Show datablock")]
    public void HideDataBlock_LabelFollowsTheState_AndClickToggles(bool hidden, string header)
    {
        int toggles = 0;
        MenuItem item = CanvasMenuItems.HideDataBlock(hidden, () => toggles++);

        Assert.Equal(header, item.Header as string);
        Click(item);
        Assert.Equal(1, toggles);
    }

    [AvaloniaFact]
    public void DrawRoute_NamedLabel_AndClickEnters()
    {
        string? entered = null;
        MenuItem item = CanvasMenuItems.DrawRoute("Draw route", () => entered = Callsign);

        Assert.Equal("Draw route", item.Header as string);

        Click(item);
        Assert.Equal(Callsign, entered);
    }

    [AvaloniaFact]
    public void LeaderDirection_SubmenuSendsLdrThroughTheHost()
    {
        var host = new RecordingMenuHost("");
        List<MenuItem> items = [.. CanvasMenuItems.LeaderDirection(Context(), host).Items.OfType<MenuItem>()];

        Assert.Equal(["1", "2", "3", "4", "5 (default)", "6", "7", "8", "9"], items.Select(i => i.Header as string));
        foreach (MenuItem item in items)
        {
            Click(item);
        }

        Assert.Equal(Enumerable.Range(1, 9).Select(n => (Callsign, $"LDR {n}", Initials)), host.Sent);
    }

    [AvaloniaFact]
    public void JRingAndCone_SubmenusSendClearThenEveryRadius()
    {
        var host = new RecordingMenuHost("");

        MenuItem jring = CanvasMenuItems.JRing(Context(), host);
        MenuItem cone = CanvasMenuItems.Cone(Context(), host);

        Assert.Equal("J-ring", jring.Header as string);
        Assert.Equal("Cone", cone.Header as string);
        Assert.Equal(["Clear", "1 nm", "2 nm", "3 nm", "5 nm", "10 nm"], jring.Items.OfType<MenuItem>().Select(i => i.Header as string));
        Assert.Equal(["Clear", "1 nm", "2 nm", "3 nm", "5 nm", "10 nm"], cone.Items.OfType<MenuItem>().Select(i => i.Header as string));

        foreach (MenuItem item in jring.Items.OfType<MenuItem>())
        {
            Click(item);
        }

        foreach (MenuItem item in cone.Items.OfType<MenuItem>())
        {
            Click(item);
        }

        Assert.Equal(
            [
                (Callsign, "JRING", Initials),
                (Callsign, "JRING 1", Initials),
                (Callsign, "JRING 2", Initials),
                (Callsign, "JRING 3", Initials),
                (Callsign, "JRING 5", Initials),
                (Callsign, "JRING 10", Initials),
                (Callsign, "CONE", Initials),
                (Callsign, "CONE 1", Initials),
                (Callsign, "CONE 2", Initials),
                (Callsign, "CONE 3", Initials),
                (Callsign, "CONE 5", Initials),
                (Callsign, "CONE 10", Initials),
            ],
            host.Sent
        );
    }

    [AvaloniaFact]
    public void BlankAndUnblank_SendBlankAndBlankdThroughTheHost()
    {
        var host = new RecordingMenuHost("");

        MenuItem blank = CanvasMenuItems.Blank(Context(), host);
        MenuItem unblank = CanvasMenuItems.Unblank(Context(), host);

        Assert.Equal("Blank target", blank.Header as string);
        Assert.Equal("Unblank target", unblank.Header as string);

        Click(blank);
        Click(unblank);

        Assert.Equal([(Callsign, "BLANK", Initials), (Callsign, "BLANKD", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Display_AssemblesTheBlocks_SeparatedOnlyBetweenThem()
    {
        var host = new RecordingMenuHost("");
        List<MenuItem?> overlays = [CanvasMenuItems.LeaderDirection(Context(), host), CanvasMenuItems.JRing(Context(), host)];

        MenuItem menu = CanvasMenuItems.Display([
            [],
            overlays,
            [CanvasMenuItems.Blank(Context(), host)],
        ]);

        Assert.Equal("Display", menu.Header as string);
        Assert.Equal(["Leader direction", "J-ring", "---", "Blank target"], menu.Items.Select(Describe));
    }

    private static string Describe(object? item) => item is Separator ? "---" : (item as MenuItem)?.Header as string ?? "";
}
