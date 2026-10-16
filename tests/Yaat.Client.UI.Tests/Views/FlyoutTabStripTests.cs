using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The tab strip a designed flyout shows above its rows, and the two-line variant row: tabs never take keyboard focus,
/// Left/Right moves stop at either end, a press on a tab selects it, a searching tab shows a spinner, an empty tab is
/// dimmed with "none", and tabs past the 480 px cap wrap to further rows in order. The spinner runs only while its tab is
/// searching and on screen, so a closed window or a strip taken out of its parent leaves none running, and an index
/// outside the tabs, or a move delta other than ±1, is refused rather than clamped. A variant row puts its label over the
/// whole command in the monospace font; the label is ellipsized at the cap, the command wraps and is never trimmed.
/// </summary>
public class FlyoutTabStripTests
{
    private const double Cap = 480;

    private static readonly string[] ManyLabels =
    [
        "28L assigned",
        "28R departure",
        "1L",
        "1R",
        "10L",
        "10R",
        "19L",
        "19R",
        "8L",
        "26R",
        "9L",
        "27R",
        "10",
        "28",
    ];

    [AvaloniaFact]
    public void Move_AtEitherEnd_DoesNotWrap()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");
        List<int> raised = [];
        strip.SelectionChanged += (_, index) => raised.Add(index);

        Assert.False(strip.Move(-1));
        Assert.Equal(0, strip.SelectedIndex);
        Assert.Empty(raised);

        strip.Select(2);
        raised.Clear();
        Assert.False(strip.Move(+1));
        Assert.Equal(2, strip.SelectedIndex);
        Assert.Empty(raised);
    }

    [AvaloniaFact]
    public void Move_ChangesSelection_RaisesSelectionChanged()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");
        List<int> raised = [];
        strip.SelectionChanged += (_, index) => raised.Add(index);

        Assert.True(strip.Move(+1));
        Assert.True(strip.Move(+1));
        Assert.True(strip.Move(-1));

        Assert.Equal(1, strip.SelectedIndex);
        Assert.Equal([1, 2, 1], raised);
    }

    [AvaloniaFact]
    public void Tabs_AreNeverFocusable()
    {
        FlyoutTabStrip strip = StripWith(ManyLabels);
        Window window = Show(strip);

        Assert.Equal(ManyLabels.Length, Tabs(strip).Count);
        Assert.All(
            Tabs(strip),
            tab =>
            {
                Assert.False(tab.Focusable);
                Assert.False(tab.IsTabStop);
                Assert.All(tab.GetVisualDescendants().OfType<InputElement>(), part => Assert.False(part.Focusable));
            }
        );
        window.Close();
    }

    [AvaloniaFact]
    public void ManyTabs_WrapInsideTheCap_InOrder()
    {
        FlyoutTabStrip strip = StripWith(ManyLabels);
        Window window = Show(strip);
        var cap = (Control)strip.Parent!;

        List<(string Label, Point At, double Right)> placed =
        [
            .. Tabs(strip)
                .Select(tab =>
                {
                    Point at = tab.TranslatePoint(new Point(0, 0), cap)!.Value;
                    return (AutomationProperties.GetName(tab)!, at, at.X + tab.Bounds.Width);
                }),
        ];

        Assert.All(placed, tab => Assert.True(tab.Right <= Cap, $"{tab.Label} ends at {tab.Right}"));
        int rows = placed.Select(tab => Math.Round(tab.At.Y)).Distinct().Count();
        Assert.True(rows >= 2, $"rows: {rows}");
        Assert.Equal(ManyLabels, placed.OrderBy(tab => Math.Round(tab.At.Y)).ThenBy(tab => tab.At.X).Select(tab => tab.Label));
        window.Close();
    }

    [AvaloniaFact]
    public void SearchingTab_ShowsTheSpinner_AndReadyHidesIt()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");
        Window window = Show(strip);

        Assert.False(Spinner(Tabs(strip)[1]).IsEffectivelyVisible);

        strip.SetState(1, FlyoutTabState.Searching);
        window.UpdateLayout();
        Assert.True(Spinner(Tabs(strip)[1]).IsEffectivelyVisible);
        Assert.False(Spinner(Tabs(strip)[0]).IsEffectivelyVisible);

        strip.SetState(1, FlyoutTabState.Ready);
        window.UpdateLayout();
        Assert.False(Spinner(Tabs(strip)[1]).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyTab_IsDimmedAndSaysNone()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");
        Window window = Show(strip);

        strip.SetState(1, FlyoutTabState.Empty);
        window.UpdateLayout();

        Control empty = Tabs(strip)[1];
        Assert.Equal(MenuGlyphRowTemplate.DimmedOpacity, empty.Opacity);
        Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(), text => (text.Text == "none") && text.IsEffectivelyVisible);
        Assert.Equal(1, Tabs(strip)[0].Opacity);
        Assert.DoesNotContain(Tabs(strip)[0].GetVisualDescendants().OfType<TextBlock>(), text => (text.Text == "none") && text.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void PointerPress_SelectsTheTab()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");
        Window window = Show(strip);
        List<int> raised = [];
        strip.SelectionChanged += (_, index) => raised.Add(index);

        Control third = Tabs(strip)[2];
        Point centre = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, strip.SelectedIndex);
        Assert.Equal([2], raised);
        window.Close();
    }

    [AvaloniaFact]
    public void SetTabs_KeepsTheSelectionInRange()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");
        strip.Select(2);
        List<int> raised = [];
        strip.SelectionChanged += (_, index) => raised.Add(index);

        strip.SetTabs([Ready("28L"), Ready("28R"), Ready("1L"), Ready("1R")]);
        Assert.Equal(2, strip.SelectedIndex);

        strip.SetTabs([Ready("28L"), Ready("28R")]);
        Assert.Equal(0, strip.SelectedIndex);
        Assert.Equal(2, Tabs(strip).Count);
        Assert.Empty(raised);
    }

    [AvaloniaFact]
    public void TwoLineRow_LongCommand_IsNotTrimmed()
    {
        string command = "TAXI " + string.Join(' ', Enumerable.Range(0, 80).Select(i => $"W{i % 10}{(char)('A' + (i % 26))}"));
        command = command[..300];
        Window window = Show(MenuTwoLineRowTemplate.Build(VariantRow(command)));

        TextBlock shown = CommandText(window, command);
        Assert.Equal(TextTrimming.None, shown.TextTrimming);
        Assert.Equal(TextWrapping.Wrap, shown.TextWrapping);
        Assert.True(shown.Bounds.Width <= Cap, $"command is {shown.Bounds.Width} wide");
        Assert.True(shown.Bounds.Height > 2 * shown.FontSize, $"command is {shown.Bounds.Height} high");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Hold short 10R at A");
        window.Close();
    }

    [AvaloniaFact]
    public void TwoLineRow_CommandUsesTheMonoFont()
    {
        const string Command = "TAXI A B C 28R HS 10R";
        Window window = Show(MenuTwoLineRowTemplate.Build(VariantRow(Command)));

        Assert.True(window.TryFindResource(QuickCommandStrip.MonoFontKey, out object? mono));
        Assert.Equal(mono, CommandText(window, Command).FontFamily);
        window.Close();
    }

    [AvaloniaFact]
    public void Spinner_SearchingWhileShown_Spins()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");
        Window window = Show(strip);

        strip.SetState(1, FlyoutTabState.Searching);
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsSpinning(1));
        Assert.False(strip.IsSpinning(0));
        window.Close();
    }

    [AvaloniaFact]
    public void Spinner_SetStateReady_StopsSpinning()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");
        Window window = Show(strip);

        strip.SetState(1, FlyoutTabState.Searching);
        Dispatcher.UIThread.RunJobs();
        Assert.True(strip.IsSpinning(1));

        strip.SetState(1, FlyoutTabState.Ready);
        Dispatcher.UIThread.RunJobs();
        Assert.False(strip.IsSpinning(1));
        window.Close();
    }

    [AvaloniaFact]
    public void Spinner_WindowClosedOrStripRemoved_StopsSpinning()
    {
        FlyoutTabStrip closed = StripWith("28L", "28R");
        Window window = Show(closed);
        closed.SetState(1, FlyoutTabState.Searching);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed.IsSpinning(1));

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed.IsSpinning(1));

        FlyoutTabStrip detached = StripWith("28L", "28R");
        Window other = Show(detached);
        detached.SetState(1, FlyoutTabState.Searching);
        Dispatcher.UIThread.RunJobs();
        Assert.True(detached.IsSpinning(1));

        var host = (Border)detached.Parent!;
        host.Child = null;
        other.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.False(detached.IsSpinning(1));
        other.Close();
    }

    [AvaloniaFact]
    public void Spinner_SearchingBeforeShown_SpinsOnlyOnceShown()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");
        strip.SetState(1, FlyoutTabState.Searching);

        Assert.False(strip.IsSpinning(1));

        Window window = Show(strip);
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsSpinning(1));
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyStrip_HasNoSelection_AndMoveReturnsFalse()
    {
        var created = new FlyoutTabStrip();
        Assert.Equal(-1, created.SelectedIndex);
        Assert.False(created.Move(+1));

        var emptied = new FlyoutTabStrip();
        emptied.SetTabs([]);
        Assert.Equal(-1, emptied.SelectedIndex);
        Assert.False(emptied.Move(+1));
    }

    [AvaloniaFact]
    public void Select_OutOfRange_Throws()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");

        Assert.Throws<ArgumentOutOfRangeException>(() => strip.Select(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => strip.Select(2));
    }

    [AvaloniaFact]
    public void SetState_OutOfRange_Throws()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R");

        Assert.Throws<ArgumentOutOfRangeException>(() => strip.SetState(-1, FlyoutTabState.Ready));
        Assert.Throws<ArgumentOutOfRangeException>(() => strip.SetState(2, FlyoutTabState.Ready));
    }

    [AvaloniaFact]
    public void Move_DeltaOtherThanOne_Throws()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");

        Assert.Throws<ArgumentOutOfRangeException>(() => strip.Move(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => strip.Move(2));
    }

    [AvaloniaFact]
    public void PointerPress_RightButton_DoesNotSelect()
    {
        FlyoutTabStrip strip = StripWith("28L", "28R", "1L");
        Window window = Show(strip);
        List<int> raised = [];
        strip.SelectionChanged += (_, index) => raised.Add(index);

        Control third = Tabs(strip)[2];
        Point centre = third.TranslatePoint(new Point(third.Bounds.Width / 2, third.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Right);
        window.MouseUp(centre, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, strip.SelectedIndex);
        Assert.Empty(raised);
        window.Close();
    }

    [AvaloniaFact]
    public void TwoLineRow_LongLabel_IsEllipsized()
    {
        string label = string.Join(' ', Enumerable.Repeat("hold short", 40));
        var row = new MenuTwoLineRow(QuickCommandGlyphs.For(MenuIds.GroundTaxiToRunway)!, label, "TAXI A B C 28R HS 10R", IsDimmed: false);
        Window window = Show(MenuTwoLineRowTemplate.Build(row));

        TextBlock shown = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == label);
        Assert.Equal(TextTrimming.CharacterEllipsis, shown.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, shown.TextWrapping);
        window.Close();
    }

    private static FlyoutTabStrip StripWith(params string[] labels)
    {
        var strip = new FlyoutTabStrip();
        strip.SetTabs([.. labels.Select(Ready)]);
        return strip;
    }

    private static FlyoutTab Ready(string label) => new(label, FlyoutTabState.Ready);

    private static MenuTwoLineRow VariantRow(string command) =>
        new(QuickCommandGlyphs.For(MenuIds.GroundTaxiToRunway)!, "Hold short 10R at A", command, IsDimmed: false);

    /// <summary>Shows <paramref name="content"/> inside a 480 px cap, at the top left of a large window, laid out.</summary>
    private static Window Show(Control content)
    {
        var window = new Window
        {
            Width = 900,
            Height = 900,
            Content = new Border
            {
                MaxWidth = Cap,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = content,
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static IReadOnlyList<Control> Tabs(FlyoutTabStrip strip) => [.. strip.GetLogicalDescendants().OfType<WrapPanel>().Single().Children];

    private static Control Spinner(Control tab) =>
        tab.GetLogicalDescendants().OfType<Control>().Single(part => part.Name == FlyoutTabStrip.SpinnerName);

    private static TextBlock CommandText(Window window, string command) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == command);
}
