using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The radar menu host's read of the measure tool, which decides whether the Display menu offers a measure item and
/// which way that item reads. Built over a real radar view model, so the catalog's three measure states are exercised
/// against the tool they describe.
/// </summary>
public class RadarMenuHostTests
{
    private const string Callsign = "N123AB";

    [AvaloniaFact]
    public void GetMeasureState_NoTool_ThenIdle_ThenAnchored()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new RadarMenuHost(new RadarView(), main.Radar, main, null);

        main.Radar.Measure = null;
        Assert.Equal(MenuMeasureState.None, host.GetMeasureState());

        var measure = new RangeBearingViewState(new RangeBearingLineStore());
        main.Radar.SetMeasureState(measure);
        Assert.Equal(MenuMeasureState.NoAnchor, host.GetMeasureState());

        measure.Pick(RblEndpoint.OnAircraft(Callsign), RblView.Radar, RangeBearingViewState.TrackLookup(_ => null), RblUnits.NauticalMiles);
        Assert.Equal(MenuMeasureState.HasAnchor, host.GetMeasureState());
    }

    /// <summary>The ground view's taxi-route and hidden-datablock members, which neither the radar nor the list builds.</summary>
    [AvaloniaFact]
    public void RadarAndListHosts_ThrowForGroundDisplayMembers()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        IMenuHost[] hosts =
        [
            new RadarMenuHost(new RadarView(), main.Radar, main, null),
            new ListMenuHost(main, new AircraftModel { Callsign = Callsign }, new Border()),
        ];

        foreach (IMenuHost host in hosts)
        {
            Assert.Throws<NotSupportedException>(() => host.GetTaxiRouteMode(Callsign));
            Assert.Throws<NotSupportedException>(() => host.SetTaxiRouteMode(Callsign, TaxiRouteDisplayMode.AlwaysShow));
            Assert.Throws<NotSupportedException>(() => host.IsDataBlockHidden(Callsign));
            Assert.Throws<NotSupportedException>(() => host.ToggleHiddenDataBlock(Callsign));
        }
    }

    /// <summary>
    /// The ground view's traffic, hold-short, route-preview, pushback and preset-taxi members, which neither the radar nor
    /// the list builds.
    /// </summary>
    [AvaloniaFact]
    public void RadarAndListHosts_ThrowForGroundMovementMembers()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        IMenuHost[] hosts =
        [
            new RadarMenuHost(new RadarView(), main.Radar, main, null),
            new ListMenuHost(main, new AircraftModel { Callsign = Callsign }, new Border()),
        ];

        foreach (IMenuHost host in hosts)
        {
            Assert.Throws<NotSupportedException>(() => host.GetGroundTrafficCallsigns(Callsign));
            Assert.Throws<NotSupportedException>(() => host.GetHoldShortChoices(Callsign));
            Assert.Throws<NotSupportedException>(() => host.SetRoutePreview(null));
            Assert.Throws<NotSupportedException>(() => host.GetPushbackFaceChoices(Callsign));
            Assert.Throws<NotSupportedException>(() => host.GetPushbackToChoices(Callsign));
            Assert.Throws<NotSupportedException>(() => host.GetPresetTaxiChoices(Callsign));
            Assert.Throws<NotSupportedException>(() => host.EnterPushRoute(Callsign));
        }
    }

    /// <summary>
    /// The radar host's list picker opens the shared menu popup on the radar canvas's overlay, seeded with the current
    /// value, and a pick hands the item back and closes it.
    /// </summary>
    [AvaloniaFact]
    public void RadarHost_ListPicker_OpensTheSharedPopup()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = main.Radar };
        var window = new Window { DataContext = main, Content = view };
        window.ShowAndRunLayout();
        try
        {
            var host = new RadarMenuHost(view, main.Radar, main, null);
            object? picked = null;
            host.ShowListPopup(
                ["090", "180", "270"],
                "180",
                value =>
                {
                    picked = value;
                    return Task.CompletedTask;
                }
            );
            HeadlessWindowExtensions.PumpDispatcher();

            var overlay = OverlayLayer.GetOverlayLayer(view);
            Assert.NotNull(overlay);
            Popup popup = Assert.Single(overlay.Children.OfType<Popup>());
            Assert.True(popup.IsOpen, "The radar's list picker should open the shared popup.");
            ListBox list = Assert.Single(popup.Child!.GetLogicalDescendants().OfType<ListBox>());
            Assert.Equal<object?>("180", list.SelectedItem);

            list.SelectedIndex = 2;
            HeadlessWindowExtensions.PumpDispatcher();

            Assert.Equal<object?>("270", picked);
            Assert.False(popup.IsOpen, "A pick should close the popup.");
            Assert.DoesNotContain(popup, overlay.Children);
        }
        finally
        {
            window.Close();
        }
    }
}
