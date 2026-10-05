using Xunit;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.Tests;

public class FlightStripBarcodeTests
{
    // The barcode beside a strip's CID is drawn from this hash, so a strip
    // must hash the same in every process: the value is pinned, not compared
    // with another call in the same run (a per-process randomized string hash
    // would still agree with itself there).
    [Fact]
    public void BarcodeHash_IsTheSameInEveryProcess() => Assert.Equal(1047883589u, FlightStripControl.BarcodeHash("STRIP_SWA5456"));

    [Fact]
    public void BarcodeHash_DiffersBetweenStrips() =>
        Assert.NotEqual(FlightStripControl.BarcodeHash("STRIP_SWA5456"), FlightStripControl.BarcodeHash("STRIP_SWA919"));
}
