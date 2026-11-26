using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>A STARS DCB tile at YAAT size: each kind's drawing and interaction, driven headless.</summary>
public class DcbTileTests
{
    /// <summary>Shows <paramref name="content"/> in a window, laid out and rendered.</summary>
    private static Window Show(Control content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = content,
        };
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The point at <paramref name="control"/>'s centre, in window coordinates.</summary>
    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>Presses and releases the left button at <paramref name="point"/>, in window coordinates.</summary>
    private static void Press(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A spinner tile over 40, stepping by 5 within 20 and 60, and a normal twin that counts its clicks.</summary>
    private static (DcbTile Spinner, DcbTile Counter, int[] Clicks, StackPanel Panel) SpinnerAndCounter(Action<double>? onAccept = null)
    {
        int[] clicks = [0];
        var spinner = new DcbTile(DcbTileShape.Full, DcbTileKind.Spinner, "RANGE\n%value%")
        {
            Value = 40,
            MinValue = 20,
            MaxValue = 60,
            Step = 5,
            OnAccept = onAccept,
        };
        var counter = new DcbTile(DcbTileShape.Full, DcbTileKind.Normal, "MAPS") { OnClick = () => clicks[0]++ };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { spinner, counter } };
        return (spinner, counter, clicks, panel);
    }

    [AvaloniaFact]
    public void DisabledTile_DrawsDimmedAndIgnoresClicks()
    {
        int clicks = 0;
        var tile = new DcbTile(DcbTileShape.Full, DcbTileKind.Disabled, "SHIFT") { OnClick = () => clicks++ };
        Window window = Show(tile);
        try
        {
            Assert.Equal(DcbColors.Dim(DcbColors.Back), tile.FillColor);
            Assert.Equal(DcbColors.Dim(DcbColors.Text), tile.TextColor);

            Press(window, Centre(window, tile));

            Assert.Equal(0, clicks);
            Assert.False(tile.IsPushed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisabledTile_DimsTheBevel()
    {
        var tile = new DcbTile(DcbTileShape.Full, DcbTileKind.Disabled, "MODE");
        Window window = Show(tile);
        try
        {
            Assert.Equal(DcbColors.Dim(DcbColors.Back), tile.Palette.Fill);
            Assert.Equal(DcbColors.Dim(DcbColors.Highlight), tile.Palette.TopLeft);
            Assert.Equal(DcbColors.Dim(DcbColors.Shadow), tile.Palette.BottomRight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ToggleTile_FlipsOnPress()
    {
        bool? flipped = null;
        var tile = new DcbTile(DcbTileShape.Half, DcbTileKind.Toggle, "OFF\nCNTR") { OnToggle = state => flipped = state };
        Window window = Show(tile);
        try
        {
            Press(window, Centre(window, tile));

            Assert.True(tile.IsPushed);
            Assert.Equal(true, flipped);
            Assert.Equal(DcbColors.Pushed, tile.FillColor);

            Press(window, Centre(window, tile));

            Assert.False(tile.IsPushed);
            Assert.Equal(false, flipped);
            Assert.Equal(DcbColors.Back, tile.FillColor);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NormalTile_ClickOnReleaseInside_NotOutside()
    {
        int clicks = 0;
        var tile = new DcbTile(DcbTileShape.Full, DcbTileKind.Normal, "MAPS") { OnClick = () => clicks++ };
        Window window = Show(tile);
        try
        {
            Press(window, Centre(window, tile));
            Assert.Equal(1, clicks);

            // Held down and dragged off the tile: the press is dropped, so the release elsewhere never clicks.
            Point inside = Centre(window, tile);
            var outside = new Point(300, 150);
            window.MouseMove(inside);
            window.MouseDown(inside, MouseButton.Left);
            Assert.True(tile.IsPushed);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            Assert.False(tile.IsPushed, "leaving the tile drops the press");
            window.MouseUp(outside, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, clicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SpinnerTile_HoldsThePointerUntilAccept()
    {
        double? accepted = null;
        (DcbTile spinner, DcbTile counter, int[] clicks, StackPanel panel) = SpinnerAndCounter(value => accepted = value);
        Window window = Show(panel);
        try
        {
            Press(window, Centre(window, spinner));
            Assert.True(spinner.IsLatched);

            // The latch holds the pointer: the press on the other tile is swallowed and never reaches it.
            Press(window, Centre(window, counter));

            Assert.True(spinner.IsLatched);
            Assert.Equal(0, clicks[0]);
            Assert.Null(accepted);

            // The wheel steps the latched value, and a second press on the spinner accepts it.
            window.MouseWheel(Centre(window, spinner), new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Press(window, Centre(window, spinner));

            Assert.False(spinner.IsLatched);
            Assert.Equal(45d, accepted);
            Assert.Equal(45d, spinner.Value);
            Assert.Equal(0, clicks[0]);

            // Once accepted, the pointer is the other tile's again.
            Press(window, Centre(window, counter));
            Assert.Equal(1, clicks[0]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SpinnerTile_RemovedWhileLatched_FreesTheWindow()
    {
        (DcbTile spinner, DcbTile counter, int[] clicks, StackPanel panel) = SpinnerAndCounter();
        Window window = Show(panel);
        try
        {
            Press(window, Centre(window, spinner));
            Assert.True(spinner.IsLatched);

            panel.Children.Remove(spinner);
            Settle(window);

            Assert.False(spinner.IsLatched, "leaving the tree cancels the latch");
            Press(window, Centre(window, counter));
            Assert.Equal(1, clicks[0]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SpinnerTile_WheelOverAnotherControl_StepsTheSpinner()
    {
        double? accepted = null;
        (DcbTile spinner, DcbTile counter, int[] clicks, StackPanel panel) = SpinnerAndCounter(value => accepted = value);
        Window window = Show(panel);
        try
        {
            Press(window, Centre(window, spinner));
            Assert.True(spinner.IsLatched);

            // The wheel goes to the latch wherever the pointer is, and never to the control under it.
            window.MouseWheel(Centre(window, counter), new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Press(window, Centre(window, spinner));

            Assert.Equal(45d, accepted);
            Assert.Equal(0, clicks[0]);
        }
        finally
        {
            window.Close();
        }
    }
}
