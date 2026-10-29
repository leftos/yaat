using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The shared harness builds OAK on its committed tower-cab videomap, as the server does, so every suite taking OAK from
/// <see cref="TestAirportGroundData"/> checks the hold-short points seated on the painted bars.
/// </summary>
public class TestAirportGroundDataVideoMapTests
{
    [Fact]
    public void TestAirportGroundData_Oak_UsesTheCommittedVideoMap()
    {
        AirportGroundLayout? harness = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(harness);

        List<string> harnessBars = HoldShortPositions(harness);
        List<string> snappedBars = HoldShortPositions(RunwayPaintSnapTests.ParseOak(RunwayPaintSnapTests.OakMap(), FilletMode.Standard));
        List<string> unsnappedBars = HoldShortPositions(RunwayPaintSnapTests.ParseOak(map: null, FilletMode.Standard));

        Assert.Equal(snappedBars, harnessBars);
        Assert.NotEqual(unsnappedBars, harnessBars);
    }

    private static List<string> HoldShortPositions(AirportGroundLayout layout) =>
        [
            .. layout
                .Nodes.Values.Where(node => node.Type == GroundNodeType.RunwayHoldShort)
                .Select(node => $"{node.RunwayId} {node.Position.Lat:F7} {node.Position.Lon:F7}")
                .Order(StringComparer.Ordinal),
        ];
}
