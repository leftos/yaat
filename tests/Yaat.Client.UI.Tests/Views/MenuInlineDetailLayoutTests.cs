using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A command row's inline detail against the row's right-aligned distance, over the real ground menu opened at a KOAK
/// stand: the detail lives in its own column, so a menu holding a row too wide for its cap trims the detail with an
/// ellipsis and carries the whole detail as the row's tooltip, and the detail never runs under the distance.
/// </summary>
public class MenuInlineDetailLayoutTests
{
    private const double Epsilon = 0.5;

    /// <summary>The Fluent theme's <c>FlyoutThemeMaxWidth</c>, which the app raises to the row cap.</summary>
    private const double PreviousMenuMaxWidth = 456;

    private const string FlyoutMaxWidthKey = "FlyoutThemeMaxWidth";

    /// <summary>More room than any row of these menus needs, for the tooltip's own transition test.</summary>
    private const double RoomForEveryDetail = 2000;

    private readonly NavigationDatabase _navDb;

    public MenuInlineDetailLayoutTests() => _navDb = MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public void TaxiToRunwayRow_DetailEndsLeftOfDistance()
    {
        using RowsHost host = ShowTaxiToRunwayRows();
        Assert.NotEmpty(host.Rows);
        foreach ((MenuCommandRow row, MenuItem item) in host.Rows)
        {
            AssertDetailClearsDistance(row, item, Block(item, $" · {row.Detail}"), Block(item, row.Distance!));
        }
    }

    [AvaloniaFact]
    public void LongInlineDetail_PastTheCap_IsTrimmedAndInTooltip()
    {
        using RowsHost host = ShowTaxiToRunwayRows();
        (MenuCommandRow row, MenuItem item) = LongestTrimmed(host);
        TextBlock detail = Block(item, $" · {row.Detail}");
        Grid rowGrid = RowGrid(detail);

        Assert.True(Collapsed(detail), $"'{row.Name}' should trim its detail.");
        Assert.True(
            item.Bounds.Width <= MenuCommandRow.InlineRowMaxWidth + Epsilon,
            $"'{row.Name}' is wider than the cap: {item.Bounds.Width:F1}px against {MenuCommandRow.InlineRowMaxWidth:F0}px."
        );
        Assert.True(
            item.Bounds.Width > PreviousMenuMaxWidth,
            $"'{row.Name}' should outgrow the theme's old cap: {item.Bounds.Width:F1}px against {PreviousMenuMaxWidth:F0}px."
        );

        string? tooltip = ToolTip.GetTip(rowGrid) as string;
        Assert.NotNull(tooltip);
        Assert.Contains(row.Detail!, tooltip);

        AssertDetailClearsDistance(row, item, detail, Block(item, row.Distance!));
    }

    [AvaloniaFact]
    public void ShortInlineDetail_IsNotTrimmed()
    {
        using RowsHost host = ShowHoldingShortRows();
        (MenuCommandRow row, MenuItem item) = host.Rows.OrderBy(pair => pair.Row.Detail!.Length).First();
        TextBlock detail = Block(item, $" · {row.Detail}");

        Assert.False(Collapsed(detail), $"'{row.Name}' has a short detail that should fit the menu.");
        Assert.Null(ToolTip.GetTip(RowGrid(detail)));
    }

    [AvaloniaFact]
    public void ZeroRoomDetail_StillCarriesItsTooltip()
    {
        using RowsHost host = ShowTaxiToRunwayRows();
        (MenuCommandRow row, MenuItem item) = Roomless(host);
        TextBlock detail = Block(item, $" · {row.Detail}");

        Assert.True(detail.Bounds.Width < 1, $"'{row.Name}' should have no room for its detail: {detail.Bounds.Width:F1}px.");
        Assert.Equal(row.Detail, Assert.IsType<string>(ToolTip.GetTip(RowGrid(detail))));
    }

    [AvaloniaFact]
    public void TrimmedDetail_DropsItsTooltipWhenItFits()
    {
        using RowsHost host = ShowTaxiToRunwayRows();
        (MenuCommandRow row, MenuItem item) = LongestTrimmed(host);
        TextBlock detail = Block(item, $" · {row.Detail}");
        Grid rowGrid = RowGrid(detail);
        Assert.True(Collapsed(detail), $"'{row.Name}' should start trimmed.");
        Assert.NotNull(ToolTip.GetTip(rowGrid));

        Application.Current!.Resources[FlyoutMaxWidthKey] = RoomForEveryDetail;
        try
        {
            Render();
            TopLevel.GetTopLevel(item)?.UpdateLayout();
            Render();

            Assert.False(Collapsed(detail), $"'{row.Name}' has room now, so its detail should not be trimmed.");
            Assert.Null(ToolTip.GetTip(rowGrid));
        }
        finally
        {
            Application.Current!.Resources[FlyoutMaxWidthKey] = MenuCommandRow.InlineRowMaxWidth;
            Render();
        }
    }

    /// <summary>The at-parking ground menu's Taxi to runway rows, in their own submenu.</summary>
    private RowsHost ShowTaxiToRunwayRows() => ShowRows("at-parking", "Taxi to runway");

    /// <summary>The holding-short ground menu's preset taxi rows, whose detail fits the menu whole.</summary>
    private RowsHost ShowHoldingShortRows() => ShowRows("holding-short", "Preset taxi route");

    /// <summary>
    /// The <paramref name="fixtureName"/> ground menu built and opened at stand the way a right-click opens it, down the
    /// All Commands path to <paramref name="submenuHeader"/>: that submenu's own inline-detail rows, laid out in its popup.
    /// </summary>
    private RowsHost ShowRows(string fixtureName, string submenuHeader)
    {
        IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        MenuFixture fixture = MenuGoldenFixtures.For(MenuView.Ground).Single(f => f.Name == fixtureName);
        main.Aircraft.Clear();
        main.Aircraft.Add(fixture.Aircraft);

        var view = new GroundView { DataContext = main.Ground };
        var window = new Window
        {
            Width = 1400,
            Height = 1000,
            Content = view,
            DataContext = main,
        };
        window.ShowAndRunLayout();
        main.Ground.SelectedAircraft = fixture.Selected ?? fixture.Aircraft;
        ContextMenu menu = view.BuildAircraftContextMenu(main.Ground, fixture.Aircraft, fixture.Selected, fixture.Aircraft.Callsign);
        menu.Open(view);
        Render();

        MenuItem allCommands = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        allCommands.Open();
        Render();
        MenuItem submenu = allCommands.Items.OfType<MenuItem>().Single(m => (m.Header as string) == submenuHeader);
        OpenAll(submenu);

        var laidOut = new List<(MenuCommandRow Row, MenuItem Item)>();
        Collect(submenu, laidOut);
        return new RowsHost(navScope, window, menu, laidOut);
    }

    /// <summary>Opens <paramref name="item"/> and every submenu under it, so each of its rows is laid out in its popup.</summary>
    private static void OpenAll(MenuItem item)
    {
        item.Open();
        Render();
        foreach (MenuItem child in item.Items.OfType<MenuItem>().Where(menu => menu.Items.Count > 0))
        {
            OpenAll(child);
        }
    }

    /// <summary>Every inline-detail row under <paramref name="parent"/>, at any depth.</summary>
    private static void Collect(MenuItem parent, List<(MenuCommandRow Row, MenuItem Item)> rows)
    {
        foreach (MenuItem child in parent.Items.OfType<MenuItem>())
        {
            if ((child.Header is MenuCommandRow row) && IsLaidOutInlineRow(row))
            {
                rows.Add((row, child));
            }

            Collect(child, rows);
        }
    }

    /// <summary>Whether <paramref name="row"/> draws an inline detail beside a distance, the rows these tests lay out.</summary>
    private static bool IsLaidOutInlineRow(MenuCommandRow row) =>
        (row.DetailPlacement == MenuDetailPlacement.Inline) && (row.Detail is not null) && (row.Distance is not null);

    /// <summary>The longest of <paramref name="host"/>'s rows whose detail lost its whole text to an ellipsis.</summary>
    private static (MenuCommandRow Row, MenuItem Item) LongestTrimmed(RowsHost host)
    {
        List<(MenuCommandRow Row, MenuItem Item)> trimmed = [.. host.Rows.Where(pair => Collapsed(Block(pair.Item, $" · {pair.Row.Detail}")))];
        Assert.NotEmpty(trimmed);
        return trimmed.OrderByDescending(pair => pair.Row.Detail!.Length).First();
    }

    /// <summary>The longest of <paramref name="host"/>'s rows whose detail was laid out no room at all.</summary>
    private static (MenuCommandRow Row, MenuItem Item) Roomless(RowsHost host)
    {
        List<(MenuCommandRow Row, MenuItem Item)> roomless = [.. host.Rows.Where(pair => Block(pair.Item, $" · {pair.Row.Detail}").Bounds.Width < 1)];
        Assert.NotEmpty(roomless);
        return roomless.OrderByDescending(pair => pair.Row.Detail!.Length).First();
    }

    /// <summary>Whether <paramref name="detail"/> drew its text collapsed to an ellipsis.</summary>
    private static bool Collapsed(TextBlock detail) => detail.TextLayout.TextLines.Any(line => line.HasCollapsed);

    private static void AssertDetailClearsDistance(MenuCommandRow row, MenuItem item, TextBlock detail, TextBlock distance) =>
        Assert.True(
            RightEdge(detail, item) <= LeftEdge(distance, item) + Epsilon,
            $"'{row.Name}' draws its detail to x={RightEdge(detail, item):F1}, past the distance at x={LeftEdge(distance, item):F1}."
        );

    private static TextBlock Block(MenuItem item, string text) => item.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);

    /// <summary>The row's own grid: the four-column one the template builds, above the detail's own column.</summary>
    private static Grid RowGrid(TextBlock detail) => detail.GetVisualAncestors().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 4);

    private static double LeftEdge(Control control, Control ancestor) => control.TranslatePoint(new Point(0, 0), ancestor)!.Value.X;

    private static double RightEdge(Control control, Control ancestor) => LeftEdge(control, ancestor) + control.Bounds.Width;

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The shown window and open menu the rows were laid out in, closed when the test ends.</summary>
    private sealed class RowsHost(IDisposable navScope, Window window, ContextMenu menu, IReadOnlyList<(MenuCommandRow Row, MenuItem Item)> rows)
        : IDisposable
    {
        public IReadOnlyList<(MenuCommandRow Row, MenuItem Item)> Rows { get; } = rows;

        public void Dispose()
        {
            menu.Close();
            window.Close();
            navScope.Dispose();
        }
    }
}
