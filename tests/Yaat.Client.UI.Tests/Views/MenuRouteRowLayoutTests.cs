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
/// A command row's route line under its name, over the real ground menu opened at a KOAK stand: a preset taxi route's
/// taxiway line that fits the row is drawn whole, with no tooltip.
/// </summary>
public class MenuRouteRowLayoutTests
{
    private readonly NavigationDatabase _navDb;

    public MenuRouteRowLayoutTests() => _navDb = MenuGoldenFixtures.EnsureNavData();

    /// <summary>A preset taxi route's two-line row whose taxiway line fits the row draws it whole, with no tooltip.</summary>
    [AvaloniaFact]
    public void ShortRouteLine_IsNotTrimmed()
    {
        using RowsHost host = ShowPresetRows();
        Assert.NotEmpty(host.Rows);
        (MenuCommandRow row, MenuItem item) = host.Rows.OrderBy(pair => pair.Row.Detail!.Length).First();
        TextBlock via = Block(item, row.Detail!);

        Assert.False(Collapsed(via), $"'{row.Name}' has a short taxiway line that should fit the row.");
        Assert.Null(ToolTip.GetTip(RowGrid(via)));
    }

    /// <summary>
    /// The holding-short ground menu built and opened at stand the way a right-click opens it, down the All Commands path to
    /// Preset taxi route: that submenu's own route rows with a distance, laid out in its popup.
    /// </summary>
    private RowsHost ShowPresetRows()
    {
        IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        MenuFixture fixture = MenuGoldenFixtures.For(MenuView.Ground).Single(f => f.Name == "holding-short");
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
        MenuItem submenu = allCommands.Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Preset taxi route");
        submenu.Open();
        Render();

        List<(MenuCommandRow Row, MenuItem Item)> laidOut =
        [
            .. submenu
                .Items.OfType<MenuItem>()
                .Where(child => child.Header is MenuCommandRow { DetailPlacement: MenuDetailPlacement.Route, Detail: not null, Distance: not null })
                .Select(child => ((MenuCommandRow)child.Header!, child)),
        ];
        return new RowsHost(navScope, window, menu, laidOut);
    }

    /// <summary>Whether <paramref name="detail"/> drew its text collapsed to an ellipsis.</summary>
    private static bool Collapsed(TextBlock detail) => detail.TextLayout.TextLines.Any(line => line.HasCollapsed);

    private static TextBlock Block(MenuItem item, string text) => item.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);

    /// <summary>The row's own grid: the four-column one the template builds, above the detail's own column.</summary>
    private static Grid RowGrid(TextBlock detail) => detail.GetVisualAncestors().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 4);

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
