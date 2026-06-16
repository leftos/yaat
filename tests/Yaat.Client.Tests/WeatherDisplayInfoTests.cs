using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the wind group rendered by the radar/ground weather overlay. The wind must carry the
/// standard METAR <c>KT</c> unit suffix, with <c>KT</c> after the gust group (dddss[Ggg]KT),
/// matching <see cref="Yaat.Sim.MetarComposer"/>.
/// </summary>
public class WeatherDisplayInfoTests
{
    [Fact]
    public void ToDisplayString_PlainWind_HasKtSuffix()
    {
        var info = new WeatherDisplayInfo("OAK", WindDirectionDeg: 230, WindSpeedKts: 5, WindGustKts: null, AltimeterInHg: 29.92);

        Assert.Equal("OAK 29.92 23005KT", info.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_GustWind_KtFollowsGust()
    {
        var info = new WeatherDisplayInfo("OAK", WindDirectionDeg: 360, WindSpeedKts: 8, WindGustKts: 18, AltimeterInHg: 29.92);

        Assert.Equal("OAK 29.92 36008G18KT", info.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_CalmWind_IsZerosWithKt()
    {
        var info = new WeatherDisplayInfo("OAK", WindDirectionDeg: 0, WindSpeedKts: 0, WindGustKts: null, AltimeterInHg: null);

        Assert.Equal("OAK 00000KT", info.ToDisplayString());
    }
}
