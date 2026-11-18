using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope map right click's point menu: the point section alone with nothing selected (the measurement items, then the
/// charted MVA there, with the point's FRD rows when the fixes name it), and with an aircraft selected the shared point menu
/// carrying its header and quick strip with the point section after it.
/// </summary>
public class ScopePointMenuTests
{
    private const string Callsign = "AAL202";
    private const string Separator = "-";

    // Nothing selected, no fixes and no measuring tool state: the point section and nothing that belongs to an aircraft.
    [AvaloniaFact]
    public async Task Map_NothingSelected_HasNoPointHeaderOrQuickStrip()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ContextMenu menu = view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10));

            List<string> labels = Labels(menu.Items);
            Assert.DoesNotContain(labels, l => l.EndsWith(" → this point", StringComparison.Ordinal));
            Assert.DoesNotContain(labels, l => l.StartsWith("Fly heading ", StringComparison.Ordinal));
            Assert.DoesNotContain(labels, l => l.StartsWith("Direct to ", StringComparison.Ordinal));
        }
        finally
        {
            Close(window, view);
        }
    }

    // With an aircraft selected the shared point menu opens: its header names the aircraft and the point, and the quick
    // strip follows, then the Scope's own point section.
    [AvaloniaFact]
    public async Task Map_AirborneSelected_PointItemsThenThePointSection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ac = Airborne(new LatLon(37.50, -121.70));
            main.Aircraft.Add(ac);
            scope.SelectedAircraft = ac;

            ContextMenu menu = view.BuildMapPointMenu(scope, new LatLon(37.60, -121.90), default);

            List<string> labels = Labels(menu.Items);
            Assert.Equal($"{Callsign} · B738 → this point", labels[0]);
            Assert.Equal("", labels[1]);
            Assert.EndsWith(" from the aircraft", labels[2]);
            Assert.Equal(Separator, labels[3]);
            Assert.Contains(labels, l => l.StartsWith("Fly heading ", StringComparison.Ordinal));
            Assert.StartsWith("MVA", labels[^1]);
        }
        finally
        {
            Close(window, view);
        }
    }

    // The header's second row names the point by its FRD, so the Scope's section leaves out its own FRD row.
    [AvaloniaFact]
    public async Task Map_SelectedWithFrd_HeaderCarriesTheFrdAndTheSectionDropsItsRow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.ScopeMaps.SetFixesForTesting([("OAK", 37.7213, -122.2208)]);
            AircraftModel ac = Airborne(new LatLon(37.50, -121.70));
            main.Aircraft.Add(ac);
            scope.SelectedAircraft = ac;
            var position = new LatLon(37.80, -122.20);
            string frd = FrdResolver.ToFrd(position.Lat, position.Lon, main.ScopeMaps.Fixes!, NavigationDatabase.Instance)!;

            ContextMenu menu = view.BuildMapPointMenu(scope, position, default);

            List<string> labels = Labels(menu.Items);
            Assert.StartsWith($"{frd} · ", labels[2]);
            Assert.DoesNotContain(frd, labels);
            Assert.Contains("Copy FRD", labels);
        }
        finally
        {
            Close(window, view);
        }
    }

    // The clipboard can fail (lost, locked, or a platform that refuses it): Copy FRD logs that once and lets no exception
    // escape the item's async click handler.
    [AvaloniaFact]
    public async Task CopyFrd_WhenTheClipboardThrows_LogsAndDoesNotThrow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.ScopeMaps.SetFixesForTesting([("OAK", 37.7213, -122.2208)]);
            ContextMenu menu = view.BuildMapPointMenu(scope, new LatLon(37.80, -122.20), default);
            MenuItem copy = Assert.IsType<MenuItem>(menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "Copy FRD"));
            string? written = null;
            view.ClipboardWriter = frd =>
            {
                written = frd;
                throw new InvalidOperationException("the clipboard is gone");
            };
            long before = AppLog.RecentErrors.LastSequence;

            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(written);
            Assert.Single(AppLog.RecentErrors.Since(before, 50), entry => entry.Message.Contains("Copy FRD failed", StringComparison.Ordinal));
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task Map_NothingSelectedWithFrd_KeepsTheFrdRow()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.ScopeMaps.SetFixesForTesting([("OAK", 37.7213, -122.2208)]);
            var position = new LatLon(37.80, -122.20);
            string frd = FrdResolver.ToFrd(position.Lat, position.Lon, main.ScopeMaps.Fixes!, NavigationDatabase.Instance)!;

            ContextMenu menu = view.BuildMapPointMenu(scope, position, default);

            List<string> labels = Labels(menu.Items);
            Assert.Equal([frd, "Copy FRD"], labels[..2]);
        }
        finally
        {
            Close(window, view);
        }
    }

    // The point section with no fixes loaded: the measuring items, then the MVA row, and no point item.
    [AvaloniaFact]
    public async Task Map_NothingSelected_OpensOnlyThePointSection()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ContextMenu menu = view.BuildMapPointMenu(scope, new LatLon(37.60, -121.90), default);

            AssertOnlyPointSection(Labels(menu.Items));
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task Map_MvaRow_NamesTheFloorAndSector()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            var koak = new LatLon(37.7213, -122.2208);
            MvaSector sector = MvaDatabase.Default.FindSector(koak, preferredFacility: null)!;
            Assert.Equal(2000, sector.FloorFtMsl);

            ContextMenu menu = view.BuildMapPointMenu(scope, koak, default);

            MenuItem row = Assert.IsType<MenuItem>(menu.Items[^1]);
            Assert.Equal($"MVA 2,000 ft (sector {sector.Sector})", row.Header);
            Assert.False(row.IsEnabled);
            Assert.Equal(0.8, row.Opacity);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task Map_MvaRow_PrefersTheLoadedFacilityChart()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            Assert.Null(main.ScopeMaps.MvaFacilityId);

            // Video maps for FAT load: the Scope's MVA readouts now prefer FAT's chart, so KFAT reads FAT's 1900 (ZOA map
            // 322) rather than NCT's overlapping 2000.
            main.ScopeMaps.ApplyVideoMapsDtoForTesting(new FacilityVideoMapsDto("ZOA", "FAT", [], [], []), null, null);
            Assert.Equal("FAT", main.ScopeMaps.MvaFacilityId);
            var kfat = new LatLon(36.7762, -119.7181);
            MvaSector sector = MvaDatabase.Default.FindSector(kfat, preferredFacility: "FAT")!;

            ContextMenu menu = view.BuildMapPointMenu(scope, kfat, default);

            MenuItem row = Assert.IsType<MenuItem>(menu.Items[^1]);
            Assert.Equal($"MVA 1,900 ft (sector {sector.Sector})", row.Header);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task MvaFacility_FollowsVideoMapLoadsOntoTheCanvas()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.ScopeMaps.ApplyVideoMapsDtoForTesting(new FacilityVideoMapsDto("ZOA", "FAT", [], [], []), null, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("FAT", view.Canvas.MvaFacilityId);

            main.ScopeMaps.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(main.ScopeMaps.MvaFacilityId);
            Assert.Null(view.Canvas.MvaFacilityId);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task Map_NoMvaData_HasNoMvaRow()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            var midAtlantic = new LatLon(40.0, -70.0);
            Assert.Null(MvaDatabase.Default.FindSector(midAtlantic, preferredFacility: null));

            ContextMenu menu = view.BuildMapPointMenu(scope, midAtlantic, default);

            Assert.DoesNotContain(Labels(menu.Items), l => l.StartsWith("MVA", StringComparison.Ordinal));
        }
        finally
        {
            Close(window, view);
        }
    }

    // No measuring tool state: the item starts a measurement and nothing offers to remove or clear one.
    [AvaloniaFact]
    public async Task Map_NoMeasurements_OffersOnlyMeasureFromHere()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ContextMenu menu = view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10));

            List<string> labels = Labels(menu.Items);
            Assert.Contains("Measure from here", labels);
            Assert.DoesNotContain("Measure to here", labels);
            Assert.DoesNotContain(labels, l => l.StartsWith("Remove measurement", StringComparison.Ordinal));
            Assert.DoesNotContain("Clear measurements", labels);
        }
        finally
        {
            Close(window, view);
        }
    }

    // An anchor another Scope picked counts as no anchor here: the menu starts a measurement, and picking here moves the
    // one anchor to this Scope.
    [AvaloniaFact]
    public async Task Map_AnAnchorOnAnotherScope_OffersMeasureFromHere_AndAPickHereTakesTheAnchor()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            other.Vm.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.70, -122.20), ""));

            ContextMenu menu = view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10));

            List<string> labels = Labels(menu.Items);
            Assert.Contains("Measure from here", labels);
            Assert.DoesNotContain("Measure to here", labels);

            scope.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.60, -121.90), ""));

            Assert.Empty(main.Measure.Lines);
            Assert.Same(scope, Assert.NotNull(main.Measure.Anchor).Surface);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            Close(window, view);
        }
    }

    // A measurement the click lands on is offered for removal, and the store's lines for clearing.
    [AvaloniaFact]
    public async Task Map_MeasuredLineNearTheClick_OffersItsRemoveAndClearRows()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            (double fromLat, double fromLon) = view.Canvas.Viewport.ScreenToLatLon(100, 300);
            (double toLat, double toLon) = view.Canvas.Viewport.ScreenToLatLon(500, 300);
            (int? slot, _) = main.Measure.PlaceSilently(
                RblEndpoint.AtPoint(new LatLon(fromLat, fromLon), ""),
                RblEndpoint.AtPoint(new LatLon(toLat, toLon), ""),
                scope.MeasureTarget
            );
            Assert.NotNull(slot);
            Dispatcher.UIThread.RunJobs();
            (double midLat, double midLon) = view.Canvas.Viewport.ScreenToLatLon(300, 300);

            ContextMenu menu = view.BuildMapContextMenu(scope, new LatLon(midLat, midLon), new Point(300, 300));

            List<string> labels = Labels(menu.Items);
            Assert.Contains($"Remove measurement {slot}", labels);
            Assert.Contains("Clear measurements", labels);
            Assert.Contains("Measure from here", labels);
        }
        finally
        {
            Close(window, view);
        }
    }

    // "Show all datablocks" is disabled while none is hidden, and clears the hidden set when it is.
    [AvaloniaFact]
    public async Task Map_ShowAllDataBlocks_DisabledUntilOneIsHidden_ThenShowsAll()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            Assert.False(ShowAllItem(view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10))).IsEnabled);

            scope.HideDataBlock(Callsign);
            MenuItem showAll = ShowAllItem(view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10)));
            Assert.True(showAll.IsEnabled);

            showAll.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(scope.AnyDataBlockHidden);
            Assert.False(ShowAllItem(view.BuildMapContextMenu(scope, new LatLon(37.60, -121.90), new Point(10, 10))).IsEnabled);
        }
        finally
        {
            Close(window, view);
        }
    }

    // --- Fixtures ---------------------------------------------------------------------------

    /// <summary>The Scope's point section with no fixes loaded: the measuring items, then the MVA row, and no point item.</summary>
    private static void AssertOnlyPointSection(List<string> labels)
    {
        Assert.StartsWith("MVA", labels[^1]);
        Assert.All(labels[..^1], label => Assert.True((label == Separator) || label.StartsWith("Measure", StringComparison.Ordinal), label));
    }

    private static AircraftModel Airborne(LatLon position) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            CurrentPhase = "",
            IsOnGround = false,
            Position = position,
            Heading = new TrueHeading(120),
            Altitude = 33000,
            IndicatedAirspeed = 280,
            GroundSpeed = 280,
        };

    /// <summary>
    /// An open Scope in a window, its map source given no fixes so a section holds no FRD rows until a test sets them: the
    /// navigation-data load names points once it lands, which would race the tests asserting what opens before it does.
    /// </summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        main.ScopeMaps.SetFixesForTesting([]);
        return (main, scope, view, window);
    }

    private static void Close(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem ShowAllItem(ContextMenu menu) =>
        Assert.IsType<MenuItem>(menu.Items.OfType<MenuItem>().Single(item => (item.Header as string) == "Show all datablocks"));

    private static List<string> Labels(IEnumerable<object?> items) =>
        [
            .. items.Select(i =>
                i switch
                {
                    Avalonia.Controls.Separator => Separator,
                    MenuItem m => m.Header as string ?? "",
                    _ => i?.GetType().Name ?? "null",
                }
            ),
        ];
}
