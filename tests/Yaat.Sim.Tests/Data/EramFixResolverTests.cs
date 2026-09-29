using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// <see cref="EramFixResolver.ParseLocation"/>, the ERAM field 68 location (<c>docs/eram/commands/QH.yaml</c>,
/// <c>QU.yaml</c>): a fix the navigation data knows, a fix radial distance with the radial 001–360 and the distance
/// 001–999, or a bounded lat/long. One accept case per grammar alternative and one refusal per rule.
/// </summary>
public class EramFixResolverTests
{
    [Fact]
    public void ParseLocation_ResolvesAFix()
    {
        NavigationDatabase navDb = RealNavDb();
        (double Lat, double Lon) oak = navDb.GetFixPosition("OAK")!.Value;

        LatLon? position = EramFixResolver.ParseLocation("oak", navDb);

        Assert.Equal(new LatLon(oak.Lat, oak.Lon), position);
    }

    [Fact]
    public void ParseLocation_ResolvesAFixRadialDistance()
    {
        NavigationDatabase navDb = RealNavDb();
        LatLon expected = FrdResolver.Resolve("OAK090010", navDb)!.Value;
        (double Lat, double Lon) oak = navDb.GetFixPosition("OAK")!.Value;

        LatLon? position = EramFixResolver.ParseLocation("OAK090010", navDb);

        Assert.Equal(expected, position);
        Assert.Equal(10.0, GeoMath.DistanceNm(new LatLon(oak.Lat, oak.Lon), position!.Value), 1);
    }

    [Theory]
    [InlineData("3730/12215", 37.5, -122.25)] // N and W implied
    [InlineData("3730N/12215W", 37.5, -122.25)]
    [InlineData("3730S/12215E", -37.5, 122.25)]
    [InlineData("3730/9915", 37.5, -99.25)] // four-digit longitude
    [InlineData("9000/18000", 90.0, -180.0)]
    public void ParseLocation_ParsesLatLong(string token, double lat, double lon)
    {
        LatLon? position = EramFixResolver.ParseLocation(token, RealNavDb());

        Assert.NotNull(position);
        Assert.Equal(lat, position.Value.Lat, 9);
        Assert.Equal(lon, position.Value.Lon, 9);
    }

    [Theory]
    [InlineData("XQXQX")] // not in the navigation data
    [InlineData("A")] // one-character fix
    [InlineData("OAK0900")] // neither a fix nor a fix radial distance
    [InlineData("OAK000010")] // radial 000
    [InlineData("OAK361010")] // radial above 360
    [InlineData("OAK090000")] // distance 000
    [InlineData("9100/12215")] // latitude above 90
    [InlineData("3760/12215")] // minutes above 59
    [InlineData("9001/12215")] // minutes at 90 degrees latitude
    [InlineData("3730/18100")] // longitude above 180
    [InlineData("3730/18001")] // minutes at 180 degrees longitude
    [InlineData("3730N/12215")] // latitude letter without the longitude letter
    [InlineData("3730/12215W")] // longitude letter without the latitude letter
    [InlineData("3730E/12215W")] // E is not a latitude hemisphere
    [InlineData("373/12215")] // three-digit latitude
    [InlineData("3730/122150")] // six-digit longitude
    [InlineData("3730/12215/1200")] // extra element
    public void ParseLocation_RefusesAnythingElse(string token) => Assert.Null(EramFixResolver.ParseLocation(token, RealNavDb()));

    private static NavigationDatabase RealNavDb()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);
        return navDb;
    }
}
