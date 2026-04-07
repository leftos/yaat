using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class AdctParserTests
{
    [Fact]
    public void Adct_SingleFix_ParsesAsAppendDirectTo()
    {
        using var _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8) })
        );
        var cmd = CommandParser.Parse("ADCT SUNOL");

        var adct = Assert.IsType<AppendDirectToCommand>(cmd.Value);
        Assert.Single(adct.Fixes);
        Assert.Equal("SUNOL", adct.Fixes[0].Name);
    }

    [Fact]
    public void Adct_MultipleFixes_ParsesAll()
    {
        using var _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(
                fixes: new Dictionary<string, (double Lat, double Lon)> { ["SUNOL"] = (37.5, -121.8), ["MODESTO"] = (37.6, -121.0) }
            )
        );
        var cmd = CommandParser.Parse("ADCT SUNOL MODESTO");

        var adct = Assert.IsType<AppendDirectToCommand>(cmd.Value);
        Assert.Equal(2, adct.Fixes.Count);
        Assert.Equal("SUNOL", adct.Fixes[0].Name);
        Assert.Equal("MODESTO", adct.Fixes[1].Name);
    }

    [Fact]
    public void Adct_ChainsFiledRoute()
    {
        using var _ = NavigationDatabase.ScopedOverride(
            NavigationDatabase.ForTesting(
                fixes: new Dictionary<string, (double Lat, double Lon)>
                {
                    ["SUNOL"] = (37.5, -121.8),
                    ["MODESTO"] = (37.6, -121.0),
                    ["OXNARD"] = (34.2, -119.2),
                }
            )
        );
        var cmd = CommandParser.Parse("ADCT SUNOL", aircraftRoute: "SUNOL MODESTO OXNARD");

        var adct = Assert.IsType<AppendDirectToCommand>(cmd.Value);
        Assert.Equal(3, adct.Fixes.Count);
        Assert.Equal("SUNOL", adct.Fixes[0].Name);
        Assert.Equal("MODESTO", adct.Fixes[1].Name);
        Assert.Equal("OXNARD", adct.Fixes[2].Name);
    }

    [Fact]
    public void Adct_UnknownFix_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var cmd = CommandParser.Parse("ADCT BOGUS");

        Assert.False(cmd.IsSuccess);
    }

    [Fact]
    public void Adct_NoArg_ReturnsNull()
    {
        using var _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var cmd = CommandParser.Parse("ADCT");

        Assert.False(cmd.IsSuccess);
    }
}
