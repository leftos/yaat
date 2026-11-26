using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ERAM Master Toolbar's clicks — a menu, toggle and command button on either mouse button, an inc/dec button by
/// which button is pressed, a right press ignored, a button YAAT cannot drive taking no input — and the fill and text
/// each kind draws.
/// </summary>
public class EramMasterToolbarTests
{
    [AvaloniaFact]
    public void IncDec_LeftDecrements_MiddleIncrements()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<int> deltas = [];
        bar.IncDec += (_, e) => deltas.Add(e.Delta);
        EramToolbarButton vector = bar.Find(EramButtonType.VectorLength)!;

        Click(window, vector, MouseButton.Left);
        Click(window, vector, MouseButton.Middle);

        Assert.Equal<int>([-1, 1], deltas);
    }

    [AvaloniaFact]
    public void Menu_LeftOrMiddleRaisesOnMenu()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<EramButtonType> opened = [];
        bar.Menu += (_, e) => opened.Add(e.Type);
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;

        Click(window, views, MouseButton.Left);
        Click(window, views, MouseButton.Middle);

        Assert.Equal<EramButtonType>([EramButtonType.ViewsMenu, EramButtonType.ViewsMenu], opened);
    }

    [AvaloniaFact]
    public void Toggle_LeftOrMiddleRaisesOnToggle()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<bool> requested = [];
        bar.Toggle += (_, e) => requested.Add(e.IsOn);
        EramToolbarButton range = bar.Find(EramButtonType.RangeView)!;

        Click(window, range, MouseButton.Left);
        Click(window, range, MouseButton.Middle);

        Assert.Equal<bool>([true, true], requested);
        Assert.False(range.IsOn);

        range.IsOn = true;
        Click(window, range, MouseButton.Left);

        Assert.Equal<bool>([true, true, false], requested);
        Assert.True(range.IsOn);
    }

    [AvaloniaFact]
    public void Command_LeftOrMiddleRaisesOnCommand()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<EramButtonType> pressed = [];
        bar.Command += (_, e) => pressed.Add(e.Type);
        EramToolbarButton delete = bar.Find(EramButtonType.DeleteTearoff)!;

        Click(window, delete, MouseButton.Left);
        Click(window, delete, MouseButton.Middle);

        Assert.Equal<EramButtonType>([EramButtonType.DeleteTearoff, EramButtonType.DeleteTearoff], pressed);
    }

    [AvaloniaFact]
    public void RightClick_IsIgnored()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<EramButtonType> opened = [];
        bar.Menu += (_, e) => opened.Add(e.Type);
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;

        Click(window, views, MouseButton.Right);

        Assert.Empty(opened);
    }

    [AvaloniaFact]
    public void DisabledButton_IgnoresClicks()
    {
        (Window window, EramMasterToolbar bar) = Host();
        List<EramButtonType> opened = [];
        bar.Menu += (_, e) => opened.Add(e.Type);
        EramToolbarButton draw = bar.Find(EramButtonType.AnnotationMenu)!;

        Click(window, draw, MouseButton.Left);
        Click(window, draw, MouseButton.Middle);

        Assert.Empty(opened);
        Assert.Equal(EramColors.DisabledOpacity, draw.Opacity);
    }

    [AvaloniaFact]
    public void OpenMenuButton_DrawsTheBurntCoralFill()
    {
        (Window _, EramMasterToolbar bar) = Host();
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;

        bar.OpenMenu = EramButtonType.ViewsMenu;

        Assert.Equal(EramColors.BurntCoral, ColourOf(views.PickArea.Background));
        Assert.Equal(EramColors.Border, ColourOf(views.PickArea.BorderBrush));
        Assert.Equal(new Thickness(1), views.PickArea.BorderThickness);

        bar.OpenMenu = null;

        Assert.Equal(EramColors.MenuBlue, ColourOf(views.PickArea.Background));
    }

    [AvaloniaTheory]
    [InlineData(EramButtonType.ViewsMenu, 0, 0, 212, false)]
    [InlineData(EramButtonType.RangeView, 0, 0, 0, false)]
    [InlineData(EramButtonType.VectorLength, 0, 205, 0, false)]
    [InlineData(EramButtonType.DeleteTearoff, 0, 201, 212, false)]
    [InlineData(EramButtonType.AnnotationMenu, 0, 0, 212, true)]
    public void EachKind_DrawsCrcsFillAndText(EramButtonType type, int red, int green, int blue, bool isDisabled)
    {
        (Window _, EramMasterToolbar bar) = Host();
        EramToolbarButton button = bar.Find(type)!;

        Assert.Equal(Color.FromRgb((byte)red, (byte)green, (byte)blue), ColourOf(button.PickArea.Background));
        Assert.Equal(EramColors.Text, ColourOf(button.TextLines.Foreground));
        Assert.Equal(isDisabled ? EramColors.DisabledOpacity : 1, button.Opacity);
    }

    private static (Window Window, EramMasterToolbar Bar) Host()
    {
        var bar = new EramMasterToolbar();
        var window = new Window
        {
            Width = 1280,
            Height = 120,
            Content = bar,
        };
        window.ShowAndRunLayout();
        return (window, bar);
    }

    /// <summary>Presses and releases <paramref name="target"/>'s centre with <paramref name="button"/> on the headless mouse device.</summary>
    private static void Click(Window window, Control target, MouseButton button)
    {
        Point? centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
        Assert.NotNull(centre);
        window.MouseDown(centre.Value, button);
        window.MouseUp(centre.Value, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static Color ColourOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;
}
