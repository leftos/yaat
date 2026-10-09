using System;
using System.IO;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// <see cref="GroundMapCheck.Check"/> turns one airport's vNAS map fetch into the outcome the scenario validator reports:
/// a 404 is no map, an unreachable vNAS is a fetch failure, an unparseable map is a parse failure, and a map that parses is Ok.
/// </summary>
public class GroundMapCheckTests
{
    public GroundMapCheckTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void Check_NotFoundWithoutContent_IsNoMap()
    {
        GroundMapCheckResult result = GroundMapCheck.Check("XYZ", new HttpCacheResult(null, NotFound: true, RefreshFailed: false));

        Assert.Equal("XYZ", result.Airport);
        Assert.Equal(GroundMapOutcome.NoMap, result.Outcome);
        Assert.Null(result.Error);
        Assert.Null(result.Exception);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Check_RefreshFailedWithoutContent_IsFetchFailed()
    {
        GroundMapCheckResult result = GroundMapCheck.Check("XYZ", new HttpCacheResult(null, NotFound: false, RefreshFailed: true));

        Assert.Equal(GroundMapOutcome.FetchFailed, result.Outcome);
        Assert.Null(result.Exception);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Check_ContentThatIsNotJson_IsParseFailedWithTheError()
    {
        GroundMapCheckResult result = GroundMapCheck.Check("CMH", new HttpCacheResult("{ not json", NotFound: false, RefreshFailed: false));

        Assert.Equal(GroundMapOutcome.ParseFailed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.NotNull(result.Exception);
        Assert.StartsWith(result.Exception.GetType().Name, result.Error);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Check_RealOakMap_IsOk()
    {
        string oak = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson"));

        GroundMapCheckResult result = GroundMapCheck.Check("OAK", new HttpCacheResult(oak, NotFound: false, RefreshFailed: false));

        Assert.Equal(GroundMapOutcome.Ok, result.Outcome);
        Assert.Null(result.Error);
        Assert.Null(result.Exception);
    }
}
