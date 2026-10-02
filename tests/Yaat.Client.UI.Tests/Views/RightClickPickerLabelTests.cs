using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;

namespace Yaat.Client.UI.Tests.Views;

// The entries a map right-click picker lists: aircraft as "CALLSIGN (TYPE)" (just the callsign with no type known),
// ground stands as "<Type> <Name>".
public class RightClickPickerLabelTests
{
    [Fact]
    public void RightClickPicker_AircraftWithType_IsLabelledCallsignAndType() =>
        Assert.Equal("UAL238 (B738)", RightClickTarget.ForAircraft("UAL238", "B738", viaDataBlock: false).Label);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RightClickPicker_AircraftWithoutType_IsLabelledCallsignOnly(string? type) =>
        Assert.Equal("UAL238", RightClickTarget.ForAircraft("UAL238", type, viaDataBlock: false).Label);

    [Theory]
    [InlineData("Parking", "A5", "Parking A5")]
    [InlineData("Spot", "3", "Spot 3")]
    [InlineData("Helipad", "H1", "Helipad H1")]
    public void RightClickPicker_StandNode_IsLabelledTypeAndName(string type, string name, string expected)
    {
        var target = RightClickTarget.ForNode(new GroundNodeDto(12, 37.62, -122.39, type, name, null, null));

        Assert.Equal(expected, target.Label);
        Assert.Equal(12, target.NodeId);
        Assert.Null(target.Callsign);
    }

    [Fact]
    public void RightClickPicker_UnnamedStandNode_IsLabelledTypeAndIdToken() =>
        Assert.Equal("Parking #12", RightClickTarget.ForNode(new GroundNodeDto(12, 37.62, -122.39, "Parking", null, null, null)).Label);
}
