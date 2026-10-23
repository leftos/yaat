using Avalonia.Automation;
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
/// A catalog row's command alias, drawn beside its label in the real ground menu opened at a KOAK stand: the row keeps its
/// string label as its header and its automation name, and its view shows the alias.
/// </summary>
public class MenuAliasRenderTests
{
    private const string TaxiToRunwayLabel = "Taxi to runway";

    private readonly NavigationDatabase _navDb;

    public MenuAliasRenderTests() => _navDb = MenuGoldenFixtures.EnsureNavData();

    [AvaloniaFact]
    public void TaxiToRunwayRow_ShowsTaxiBesideItsLabel()
    {
        using RowHost host = ShowTaxiToRunwayRow();
        MenuItem row = host.Row;

        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "TAXI");
        Assert.Equal(TaxiToRunwayLabel, row.Header);
        Assert.Equal(TaxiToRunwayLabel, AutomationProperties.GetName(row));
    }

    [AvaloniaFact]
    public void AliasSetAfterTheMenuOpens_UpdatesTheShownText()
    {
        using RowHost host = ShowTaxiToRunwayRow();

        MenuAlias.SetText(host.Row, "TAXIX");
        Render();

        IReadOnlyList<string?> shown = [.. host.Row.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)];
        Assert.Contains("TAXIX", shown);
        Assert.DoesNotContain("TAXI", shown);
    }

    /// <summary>The at-parking ground menu opened the way a right-click opens it, down All Commands › Taxi to its Taxi to runway row.</summary>
    private RowHost ShowTaxiToRunwayRow()
    {
        IDisposable navScope = NavigationDatabase.ScopedOverride(_navDb);
        var main = new MainViewModel(new FakeFilePickerService());
        main.DisplayFavorites.Clear();
        main.Ground.SetLayoutForTesting(MenuGoldenFixtures.OakLayoutForClient);
        MenuFixture fixture = MenuGoldenFixtures.For(MenuView.Ground).Single(f => f.Name == "at-parking");
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
        AllCommandsPath.AllCommands(menu).Open();
        Render();
        MenuItem taxi = AllCommandsPath.Group(menu, MenuCategory.Taxi);
        taxi.Open();
        Render();
        MenuItem row = taxi.Items.OfType<MenuItem>().Single(m => (m.Header as string) == TaxiToRunwayLabel);
        return new RowHost(navScope, window, menu, row);
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The shown window and open menu the row was laid out in, closed when the test ends.</summary>
    private sealed class RowHost(IDisposable navScope, Window window, ContextMenu menu, MenuItem row) : IDisposable
    {
        public MenuItem Row { get; } = row;

        public void Dispose()
        {
            menu.Close();
            window.Close();
            navScope.Dispose();
        }
    }
}
