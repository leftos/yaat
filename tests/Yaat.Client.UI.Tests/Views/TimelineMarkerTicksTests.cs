using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The timeline bar's bookmark and finding ticks in playback: each sits along the rail in proportion to
/// its time on the tape.
/// </summary>
public sealed class TimelineMarkerTicksTests
{
    [AvaloniaFact]
    public void BookmarkTicks_ArrangeInProportionToTheirTime()
    {
        using var harness = TimelineTestHarness.BootInPlayback();

        harness.Vm.ApplyBookmarks([new TimelineBookmarkDto("a", 30, null, null), new TimelineBookmarkDto("b", 90, null, null)]);
        harness.Settle();

        AssertTicksAt(harness.Window, "BookmarkMarkerOverlay", [30, 90]);
    }

    [AvaloniaFact]
    public void FindingTicks_ArrangeInProportionToTheirTime()
    {
        using var harness = TimelineTestHarness.BootInPlayback();

        harness.Vm.TimelineMarkers.Add(Finding("a", 30));
        harness.Vm.TimelineMarkers.Add(Finding("b", 90));
        harness.Settle();

        AssertTicksAt(harness.Window, "TimelineMarkerOverlay", [30, 90]);
    }

    private static void AssertTicksAt(Window window, string overlayName, double[] timesSeconds)
    {
        ItemsControl overlay =
            window.FindControl<ItemsControl>(overlayName) ?? throw new InvalidOperationException($"MainWindow has no {overlayName}");
        TimelineMarkerCanvas canvas = overlay.GetVisualDescendants().OfType<TimelineMarkerCanvas>().Single();
        double usableWidth = canvas.Bounds.Width - (2 * TimelineMarkerCanvas.EdgeInsetPx);
        Assert.True(usableWidth > 0, $"{overlayName} was not laid out (width {canvas.Bounds.Width}).");

        double[] expected =
        [
            .. timesSeconds.Select(t => TimelineMarkerCanvas.EdgeInsetPx + ((t / TimelineTestHarness.TapeEndSeconds) * usableWidth)),
        ];
        double[] actual = [.. canvas.Children.Select(c => c.Bounds.Center.X)];
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // Layout rounding snaps each tick to the pixel grid.
            Assert.Equal(expected[i], actual[i], tolerance: 1.0);
        }
    }

    private static TimelineMarkerVm Finding(string id, double timeSeconds) =>
        new()
        {
            Id = id,
            Kind = TimelineMarkerKind.Finding,
            TimeSeconds = timeSeconds,
            Title = $"Finding {id}",
            Callsigns = [],
        };
}
