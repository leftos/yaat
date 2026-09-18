using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

// An extra Radar/Ground view window (#2, #3, …) drives its own view-model: per-scenario settings key off
// "{scenarioId}#n" so the windows don't overwrite each other's center/range, global preference writes are
// gated to the primary view so an extra window's toggles never rewrite the app-wide defaults, and an extra
// Ground View mirrors the primary's layout instead of fetching and decoding its own copy.
public class ViewInstanceViewModelTests
{
    private static RadarViewModel ExtraRadarVm() =>
        new(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask) { SettingsKeySuffix = "#2", IsPrimary = false };

    private static GroundViewModel GroundVm(bool isPrimary) =>
        new(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask)
        {
            SettingsKeySuffix = isPrimary ? "" : "#2",
            IsPrimary = isPrimary,
        };

    private static GroundLayoutDto Layout(string airportId, double lat)
    {
        const double lon = -122.38;
        return new GroundLayoutDto(
            airportId,
            [
                new GroundNodeDto(1, lat, lon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, lat + 0.001, lon, "TaxiwayIntersection", null, null, null),
            ],
            [new GroundEdgeDto(1, 2, "C", DistanceNm: 0.06, IntermediatePoints: null)],
            null,
            null,
            null
        );
    }

    [Fact]
    public void ExtraRadarView_UsesOwnPerScenarioSettingsKey()
    {
        const string scenarioId = "vivm-radar-scenario";
        var prefs = new UserPreferences();
        RadarViewModel vm = ExtraRadarVm();
        vm.SetPreferences(prefs);
        vm.SetScenarioIdForTesting(scenarioId);

        vm.ApplyCopiedSettings(
            new SavedRadarSettings
            {
                CenterLat = 37.5,
                CenterLon = -122.2,
                RangeNm = 27,
            }
        );

        // The primary view's key is the bare scenario id and must be untouched by the extra window.
        Assert.Null(prefs.GetRadarSettings(scenarioId));

        SavedRadarSettings? saved = prefs.GetRadarSettings(scenarioId + "#2");
        Assert.NotNull(saved);
        Assert.Equal(27, saved.RangeNm);
        Assert.Equal(37.5, saved.CenterLat);
    }

    [Fact]
    public void ExtraGroundView_UsesOwnPerScenarioSettingsKey()
    {
        const string scenarioId = "vivm-ground-scenario";
        var prefs = new UserPreferences();
        var vm = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (_, _, _) => Task.CompletedTask,
            onSelectionChanged: null,
            preferences: prefs
        )
        {
            SettingsKeySuffix = "#2",
            IsPrimary = false,
        };
        vm.SetScenarioId(scenarioId);

        vm.ViewCenterLat = 37.62;

        Assert.Null(prefs.GetGroundSettings(scenarioId));

        SavedGroundSettings? saved = prefs.GetGroundSettings(scenarioId + "#2");
        Assert.NotNull(saved);
        Assert.Equal(37.62, saved.CenterLat);
    }

    [Fact]
    public void ExtraRadarView_DoesNotWriteGlobalPreferences()
    {
        var prefs = new UserPreferences();
        bool before = prefs.RadarDcbVisible;
        RadarViewModel vm = ExtraRadarVm();
        vm.SetPreferences(prefs);

        try
        {
            vm.ToggleDcbVisibleCommand.Execute(null);

            // The window's own DCB flips; the app-wide default keeps its value, on this instance and on disk.
            Assert.NotEqual(before, vm.IsDcbVisible);
            Assert.Equal(before, prefs.RadarDcbVisible);
            Assert.Equal(before, new UserPreferences().RadarDcbVisible);
        }
        finally
        {
            prefs.SetRadarDcbVisible(before);
        }
    }

    [Fact]
    public void ExtraGroundView_DoesNotWriteGlobalPreferences()
    {
        var prefs = new UserPreferences();
        bool beforeLocked = prefs.GroundPanZoomLocked;
        bool beforeRunwayLabels = prefs.GroundShowRunwayLabels;
        var vm = new GroundViewModel(
            new ServerConnection(),
            sendCommand: (_, _, _) => Task.CompletedTask,
            onSelectionChanged: null,
            preferences: prefs
        )
        {
            SettingsKeySuffix = "#2",
            IsPrimary = false,
        };

        try
        {
            vm.IsPanZoomLocked = !beforeLocked;
            vm.ShowRunwayLabels = !beforeRunwayLabels;
            vm.SaveLabelAndLockSettings();

            Assert.Equal(beforeLocked, new UserPreferences().GroundPanZoomLocked);
            Assert.Equal(beforeRunwayLabels, new UserPreferences().GroundShowRunwayLabels);
        }
        finally
        {
            prefs.SetGroundPanZoomLocked(beforeLocked);
            prefs.SetGroundLabelFilters(
                beforeRunwayLabels,
                prefs.GroundShowTaxiwayLabels,
                prefs.GroundShowHoldShort,
                prefs.GroundShowParking,
                prefs.GroundShowSpot,
                prefs.GroundShowAdwMarkings
            );
        }
    }

    [AvaloniaFact]
    public void ExtraGroundView_MirrorsPrimaryLayout()
    {
        GroundViewModel primary = GroundVm(isPrimary: true);
        GroundViewModel mirror = GroundVm(isPrimary: false);
        mirror.DataBlockState.ToggleHiddenDataBlock("AAL1");
        Assert.NotEmpty(mirror.DataBlockState.HiddenDataBlockCallsigns);

        mirror.MirrorLayoutFrom(primary);
        primary.SetLayoutForTesting(Layout("TST", 37.62));

        Assert.Same(primary.Layout, mirror.Layout);
        Assert.Same(primary.DomainLayout, mirror.DomainLayout);
        Assert.NotNull(mirror.DomainLayout);
        // A layout change is an airport change: the mirror drops the per-callsign datablock state that
        // belonged to the airport that was showing, exactly as a fresh load does.
        Assert.Empty(mirror.DataBlockState.HiddenDataBlockCallsigns);
    }

    [AvaloniaFact]
    public void MirrorLayoutFrom_CopiesStateAlreadyLoaded()
    {
        GroundViewModel primary = GroundVm(isPrimary: true);
        primary.SetLayoutForTesting(Layout("TST", 37.62));
        primary.AirportCenterLat = 37.621;
        primary.AirportCenterLon = -122.381;
        primary.AirportElevation = 12;

        GroundViewModel mirror = GroundVm(isPrimary: false);
        mirror.MirrorLayoutFrom(primary);

        Assert.Same(primary.Layout, mirror.Layout);
        Assert.Same(primary.DomainLayout, mirror.DomainLayout);
        Assert.Equal(37.621, mirror.AirportCenterLat);
        Assert.Equal(-122.381, mirror.AirportCenterLon);
        Assert.Equal(12, mirror.AirportElevation);
    }

    [AvaloniaFact]
    public void StopMirroring_DetachesFromSource()
    {
        GroundViewModel primary = GroundVm(isPrimary: true);
        GroundViewModel mirror = GroundVm(isPrimary: false);
        mirror.MirrorLayoutFrom(primary);
        primary.SetLayoutForTesting(Layout("TST", 37.62));
        GroundLayoutDto? mirrored = mirror.Layout;

        mirror.StopMirroring();
        primary.SetLayoutForTesting(Layout("OTH", 37.72));

        Assert.Same(mirrored, mirror.Layout);
        Assert.NotSame(primary.Layout, mirror.Layout);
    }

    [AvaloniaFact]
    public async Task ExtraGroundView_LayoutLoadsAreNoOps()
    {
        GroundViewModel mirror = GroundVm(isPrimary: false);

        // No server and no tower-cab services are wired: a non-primary instance must return without
        // touching either, since it shows the primary's mirrored layout.
        await mirror.LoadLayoutAsync("KSFO");
        await mirror.LoadTowerCabLayersAsync("ZOA", "KSFO");

        Assert.Null(mirror.Layout);
        Assert.Null(mirror.DomainLayout);
        Assert.Null(mirror.BackgroundImage);
    }
}
