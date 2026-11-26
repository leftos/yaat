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
    /// <summary>A host wide enough for every menu to open right of its button.</summary>
    private const double DefaultWidth = 1280;

    /// <summary>A host narrower than the bar's YAAT menu opened right of its button.</summary>
    private const double NarrowWidth = 700;

    /// <summary>How far, in DIP, a fitted expansion's edge may sit from where the fit puts it.</summary>
    private const double EdgeTolerance = 0.5;

    [AvaloniaFact]
    public void IncDec_LeftDecrements_MiddleIncrements()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
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
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
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
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
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
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
        List<EramButtonType> pressed = [];
        bar.Command += (_, e) => pressed.Add(e.Type);
        EramToolbarButton hide = bar.Find(EramButtonType.HideToolbar)!;

        Click(window, hide, MouseButton.Left);
        Click(window, hide, MouseButton.Middle);

        Assert.Equal<EramButtonType>([EramButtonType.HideToolbar, EramButtonType.HideToolbar], pressed);
    }

    [AvaloniaFact]
    public void RightClick_IsIgnored()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
        List<EramButtonType> opened = [];
        bar.Menu += (_, e) => opened.Add(e.Type);
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;

        Click(window, views, MouseButton.Right);

        Assert.Empty(opened);
    }

    [AvaloniaFact]
    public void DisabledButton_IgnoresClicks()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
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
        (Window _, EramMasterToolbar bar) = Host(DefaultWidth);
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;

        bar.OpenMenu = EramButtonType.ViewsMenu;

        Assert.Equal(EramColors.BurntCoral, ColourOf(views.PickArea.Background));
        Assert.Equal(EramColors.Border, ColourOf(views.PickArea.BorderBrush));
        Assert.Equal(new Thickness(1), views.PickArea.BorderThickness);

        bar.OpenMenu = null;

        Assert.Equal(EramColors.MenuBlue, ColourOf(views.PickArea.Background));
    }

    [AvaloniaFact]
    public void OpenMenu_ExpandsItsButtonsRightOfItsButton_AndHidesTheOthersInPlace()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
        EramToolbarButton radarFilter = bar.Find(EramButtonType.RadarFilterMenu)!;
        EramToolbarButton views = bar.Find(EramButtonType.ViewsMenu)!;
        Rect viewsBounds = views.Bounds;

        bar.OpenMenu = EramButtonType.RadarFilterMenu;
        window.UpdateLayout();

        Assert.Equal(EramMenus.RadarFilter.Select(b => b.Type), bar.MenuButtons.Select(b => b.Type));
        Assert.All(bar.MenuButtons, b => Assert.True(b.IsVisible));
        Assert.NotNull(bar.Expansion);
        Assert.Equal(EramColors.BurntCoral, ColourOf(bar.Expansion!.BorderBrush));
        Point menuLeft = bar.MenuButtons[0].TranslatePoint(default, window)!.Value;
        Point ownerRight = radarFilter.TranslatePoint(new Point(radarFilter.Bounds.Width, 0), window)!.Value;
        Assert.True(menuLeft.X >= ownerRight.X);
        Assert.Equal(0, views.Opacity);
        Assert.False(views.IsHitTestVisible);
        Assert.Equal(viewsBounds, views.Bounds);
        Assert.Equal(1, radarFilter.Opacity);

        bar.OpenMenu = null;
        window.UpdateLayout();

        Assert.Empty(bar.MenuButtons);
        Assert.Null(bar.Expansion);
        Assert.Equal(1, views.Opacity);
        Assert.True(views.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void YaatMenu_InANarrowHost_LiesWithinTheHost()
    {
        (Window window, EramMasterToolbar bar) = Host(NarrowWidth);

        bar.OpenMenu = EramButtonType.YaatMenu;
        window.UpdateLayout();

        AssertWithinHost(window, bar.Expansion);
    }

    [AvaloniaFact]
    public void YaatMenu_InAHostTooNarrowOnItsRight_FlipsToItsButtonsLeft()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
        EramToolbarButton yaat = bar.Find(EramButtonType.YaatMenu)!;
        bar.OpenMenu = EramButtonType.YaatMenu;
        window.UpdateLayout();
        double ownerLeft = yaat.TranslatePoint(default, window)!.Value.X;
        double ownerRight = ownerLeft + yaat.Bounds.Width;
        double menuWidth = bar.Expansion!.Bounds.Width;
        Assert.True(ownerLeft >= menuWidth);

        // The YAAT button stays inside the host, but its menu no longer fits right of it.
        window.Width = ownerRight + (menuWidth / 2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Border expansion = bar.Expansion!;
        double right = expansion.TranslatePoint(new Point(expansion.Bounds.Width, 0), window)!.Value.X;
        Assert.Equal(ownerLeft, right, EdgeTolerance);
    }

    [AvaloniaFact]
    public void AMenuTooWideForEitherSide_StartsAtTheHostsLeftEdge()
    {
        (Window window, EramMasterToolbar bar) = Host(NarrowWidth);
        EramMenuButton[] wide = [.. Enumerable.Range(0, 20).Select(column => new EramMenuButton(0, column, EramButtonType.YaatOthers))];
        bar.MenuSource = menu => (menu == EramButtonType.YaatMenu) ? wide : EramMenus.Fixed(menu);

        bar.OpenMenu = EramButtonType.YaatMenu;
        window.UpdateLayout();

        Border expansion = bar.Expansion!;
        Assert.True(expansion.Bounds.Width > NarrowWidth);
        Assert.Equal(0, expansion.TranslatePoint(default, window)!.Value.X, EdgeTolerance);
    }

    [AvaloniaFact]
    public void YaatDbSubmenu_InANarrowHost_LiesWithinTheHost()
    {
        (Window window, EramMasterToolbar bar) = Host(NarrowWidth);

        bar.OpenMenu = EramButtonType.YaatMenu;
        bar.OpenSubmenu = EramButtonType.YaatDbMenu;
        window.UpdateLayout();

        AssertWithinHost(window, bar.Expansion);
        AssertWithinHost(window, bar.SubmenuExpansion);
    }

    [AvaloniaFact]
    public void YaatMenuAndDbSubmenu_OpenWhileTheHostNarrows_FitTheNarrowerHost()
    {
        (Window window, EramMasterToolbar bar) = Host(DefaultWidth);
        bar.OpenMenu = EramButtonType.YaatMenu;
        bar.OpenSubmenu = EramButtonType.YaatDbMenu;
        window.UpdateLayout();

        window.Width = NarrowWidth;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(NarrowWidth, window.ClientSize.Width);
        AssertWithinHost(window, bar.Expansion);
        AssertWithinHost(window, bar.SubmenuExpansion);
    }

    [AvaloniaTheory]
    [InlineData(EramButtonType.ViewsMenu, 0, 0, 212, false)]
    [InlineData(EramButtonType.RangeView, 0, 0, 0, false)]
    [InlineData(EramButtonType.VectorLength, 0, 205, 0, false)]
    [InlineData(EramButtonType.HideToolbar, 0, 201, 212, false)]
    [InlineData(EramButtonType.AnnotationMenu, 0, 0, 212, true)]
    public void EachKind_DrawsCrcsFillAndText(EramButtonType type, int red, int green, int blue, bool isDisabled)
    {
        (Window _, EramMasterToolbar bar) = Host(DefaultWidth);
        EramToolbarButton button = bar.Find(type)!;

        Assert.Equal(Color.FromRgb((byte)red, (byte)green, (byte)blue), ColourOf(button.PickArea.Background));
        Assert.Equal(EramColors.Text, ColourOf(button.TextLines.Foreground));
        Assert.Equal(isDisabled ? EramColors.DisabledOpacity : 1, button.Opacity);
    }

    private static (Window Window, EramMasterToolbar Bar) Host(double width)
    {
        var bar = new EramMasterToolbar();
        var window = new Window
        {
            Width = width,
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

    /// <summary>Asserts that <paramref name="expansion"/> is open and lies fully inside <paramref name="window"/>'s width.</summary>
    private static void AssertWithinHost(Window window, Border? expansion)
    {
        Assert.NotNull(expansion);
        double left = expansion.TranslatePoint(default, window)!.Value.X;
        double right = expansion.TranslatePoint(new Point(expansion.Bounds.Width, 0), window)!.Value.X;
        Assert.InRange(left, 0, window.ClientSize.Width);
        Assert.InRange(right, 0, window.ClientSize.Width);
    }

    private static Color ColourOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color;
}
