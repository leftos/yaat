extern alias mcp;

using mcp::Yaat.ClientDriver.Mcp.Recording;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The crop that keeps a recording to the window's client area: its offset inside the DWM frame the capture delivers, and
/// its size rounded down to even, in physical pixels at any DPI.
/// </summary>
public sealed class ClientAreaCropTests
{
    // 96 DPI: a 1-pixel border and a 31-pixel title bar, the docs' crop=1200:700:1:31.
    [Fact]
    public void Crop_100Percent()
    {
        var window = new WindowGeometry(new ScreenRect(100, 200, 1302, 932), new ScreenRect(101, 231, 1301, 931), false);

        CropBox crop = ClientAreaCrop.Compute(window);

        Assert.Equal(new CropBox(1, 31, 1200, 700), crop);
        Assert.Equal("crop=1200:700:1:31", crop.Filter);
    }

    // 144 DPI: the same window scaled 1.5x, so the border is 2 pixels and the title bar 46.
    [Fact]
    public void Crop_150Percent()
    {
        var window = new WindowGeometry(new ScreenRect(150, 300, 1954, 1398), new ScreenRect(152, 346, 1952, 1396), false);

        CropBox crop = ClientAreaCrop.Compute(window);

        Assert.Equal(new CropBox(2, 46, 1800, 1050), crop);
        Assert.Equal("crop=1800:1050:2:46", crop.Filter);
    }

    // yuv420p needs even sides, so an odd client size loses its last column and row rather than gaining the frame's.
    [Fact]
    public void Crop_OddClientSize_RoundsDownToEven()
    {
        var window = new WindowGeometry(new ScreenRect(0, 0, 1203, 733), new ScreenRect(1, 31, 1202, 732), false);

        CropBox crop = ClientAreaCrop.Compute(window);

        Assert.Equal(new CropBox(1, 31, 1200, 700), crop);
    }
}
