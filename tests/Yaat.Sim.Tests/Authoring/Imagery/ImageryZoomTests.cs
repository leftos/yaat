using Xunit;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Sim.Tests.Authoring.Imagery;

/// <summary>The tile zoom the imagery layer asks for at a latitude and a map zoom: capped at 18, none below 12.</summary>
public sealed class ImageryZoomTests
{
    [Fact]
    public void The_Default_Map_Zoom_At_Oakland_Asks_For_Zoom_12() => Assert.Equal(12, ImageryZoom.Choose(37.72, 1));

    [Fact]
    public void A_Close_Map_Zoom_Asks_For_Zoom_18() => Assert.Equal(18, ImageryZoom.Choose(37.72, 64));

    [Fact]
    public void A_Very_Close_Map_Zoom_Is_Capped_At_18() => Assert.Equal(18, ImageryZoom.Choose(37.72, 10000));

    [Theory]
    [InlineData(0.5)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_Far_Out_Map_Zoom_Asks_For_No_Tiles(double viewportZoom) => Assert.Null(ImageryZoom.Choose(37.72, viewportZoom));
}
