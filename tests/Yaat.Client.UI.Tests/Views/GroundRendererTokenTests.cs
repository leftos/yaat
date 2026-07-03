using Xunit;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ground-view hover tooltip leads with the command token that references a spot/parking node,
/// so a controller can read straight off the map how to route or warp to it. A taxi spot uses the
/// <c>$</c> prefix ($9); parking and helipads use <c>@</c> (@F7), matching TAXI/PUSH/WARPG.
/// </summary>
public class GroundRendererTokenTests
{
    [Theory]
    [InlineData("Spot", "9", "$9")]
    [InlineData("Parking", "F7", "@F7")]
    [InlineData("Helipad", "H1", "@H1")]
    public void CommandTokenFor_UsesDollarForSpot_AtForParkingAndHelipad(string nodeType, string name, string expected)
    {
        Assert.Equal(expected, GroundRenderer.CommandTokenFor(nodeType, name));
    }
}
