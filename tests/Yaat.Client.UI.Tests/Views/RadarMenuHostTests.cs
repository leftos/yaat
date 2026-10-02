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
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The radar menu host and the aircraft list's: the ground view's traffic, hold-short, route-preview, pushback and
/// preset-taxi members, which neither of them serves, and the radar host's list picker, which opens the shared popup.
/// The radar canvas's own display items live in <c>CanvasMenuSectionTests</c>.
/// </summary>
public class RadarMenuHostTests
{
    private const string Callsign = "N123AB";

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
    /// The radar host's route-drawing member, which only the ground view's catalog leaf reaches: the radar's Draw route
    /// item is a canvas item the view builds, so this member is dead here.
    /// </summary>
    [AvaloniaFact]
    public void RadarHost_EnterDrawRoute_ThrowsForTheDeadMember()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        IMenuHost host = new RadarMenuHost(new RadarView(), main.Radar, main, null);

        Assert.Throws<NotSupportedException>(() => host.EnterDrawRoute(Callsign));
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
