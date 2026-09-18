using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Pins the ATPA cone / in-trail palette: a Monitor pairing stays in the STARS TPA blue the manual
/// J-Ring and Cone overlay uses, and the two advisory states escalate to yellow and red-orange so a
/// tightening pair reads at a glance without being confused for an instructor-placed graphic.
/// </summary>
public class TargetRendererAtpaColorTests
{
    [Theory]
    [InlineData(AtpaConeState.Monitor, 90, 180, 255)]
    [InlineData(AtpaConeState.Warning, 255, 255, 0)]
    [InlineData(AtpaConeState.Alert, 255, 55, 0)]
    public void AtpaConeColorFor_MapsEachState(AtpaConeState state, byte r, byte g, byte b)
    {
        Assert.Equal(new SKColor(r, g, b), TargetRenderer.AtpaConeColorFor(state));
    }

    [Fact]
    public void AtpaConeColorFor_EscalatesDistinctly()
    {
        SKColor monitor = TargetRenderer.AtpaConeColorFor(AtpaConeState.Monitor);
        SKColor warning = TargetRenderer.AtpaConeColorFor(AtpaConeState.Warning);
        SKColor alert = TargetRenderer.AtpaConeColorFor(AtpaConeState.Alert);

        Assert.NotEqual(monitor, warning);
        Assert.NotEqual(warning, alert);
        Assert.NotEqual(monitor, alert);
    }
}
