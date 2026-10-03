using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The aircraft list's menu host: the ground view's traffic, hold-short, route-preview, pushback and preset-taxi
/// members, which it does not serve. The host the radar builds is <see cref="ClientMenuHost"/>, in
/// <c>ClientMenuHostTests</c>.
/// </summary>
public class ListMenuHostTests
{
    private const string Callsign = "N123AB";

    [AvaloniaFact]
    public void ListHost_ThrowsForGroundMovementMembers()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        IMenuHost host = new ListMenuHost(main, new AircraftModel { Callsign = Callsign }, new Border());

        Assert.Throws<NotSupportedException>(() => host.GetGroundTrafficCallsigns(Callsign));
        Assert.Throws<NotSupportedException>(() => host.GetHoldShortChoices(Callsign));
        Assert.Throws<NotSupportedException>(() => host.SetRoutePreview(null));
        Assert.Throws<NotSupportedException>(() => host.GetPushbackFaceChoices(Callsign));
        Assert.Throws<NotSupportedException>(() => host.GetPushbackToChoices(Callsign));
        Assert.Throws<NotSupportedException>(() => host.GetPresetTaxiChoices(Callsign));
        Assert.Throws<NotSupportedException>(() => host.EnterPushRoute(Callsign));
    }
}
