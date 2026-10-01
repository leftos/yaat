using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.UI.Tests.Fakes;
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
}
