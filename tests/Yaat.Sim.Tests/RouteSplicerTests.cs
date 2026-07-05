using Xunit;
using Yaat.Sim;

namespace Yaat.Sim.Tests;

/// <summary>
/// ERAM AM RTE route-splice grammar (#256, docs/crc/eram.md Table 8). Verifies the join/resume/replace
/// semantics — NOT CRC's SID/STAR route normalization (so the doc's expanded BUZRD fixes are out of scope).
/// The route is modelled as separate departure / enroute / destination fields, matching YAAT's
/// AircraftFlightPlan storage. Initial route in each case: KBOS · SSOXS SEY PARCH3 · KJFK.
/// </summary>
public class RouteSplicerTests
{
    private const string Dep = "KBOS";
    private const string Enroute = "SSOXS SEY PARCH3";
    private const string Dest = "KJFK";

    private static (string Departure, string Enroute, string Destination) Splice(string spliceArg) =>
        RouteSplicer.Splice(Dep, Enroute, Dest, spliceArg)!.Value;

    [Fact]
    public void ReplaceBeginning_NewLeadingAnchor_KeepsDepAndTail()
    {
        // LOGAN4.SSOXS: LOGAN4 is new (splice after dep), SSOXS is the resume anchor.
        var (dep, enroute, dest) = Splice("LOGAN4.SSOXS");
        Assert.Equal("KBOS", dep);
        Assert.Equal("LOGAN4 SSOXS SEY PARCH3", enroute);
        Assert.Equal("KJFK", dest);
    }

    [Fact]
    public void ReplaceMiddle_BothAnchorsPresent_ReplacesSpanBetween()
    {
        // SSOXS.BUZRD.SEY: insert BUZRD between the SSOXS and SEY anchors.
        var (dep, enroute, dest) = Splice("SSOXS.BUZRD.SEY");
        Assert.Equal("KBOS", dep);
        Assert.Equal("SSOXS BUZRD SEY PARCH3", enroute);
        Assert.Equal("KJFK", dest);
    }

    [Fact]
    public void ReplaceEnd_NewTrailingAnchor_KeepsFiledDestination()
    {
        // SEY.ROBER2: SEY anchors, ROBER2 is new → replace the enroute end, keep KJFK.
        var (dep, enroute, dest) = Splice("SEY.ROBER2");
        Assert.Equal("KBOS", dep);
        Assert.Equal("SSOXS SEY ROBER2", enroute);
        Assert.Equal("KJFK", dest);
    }

    [Fact]
    public void ReplaceEntireEnroute_BothAnchorsNew_KeepsDepAndDest()
    {
        // LOGAN4.BOSOX.JFK: neither LOGAN4 nor JFK is in the route (KJFK != JFK) → whole enroute replaced.
        var (dep, enroute, dest) = Splice("LOGAN4.BOSOX.JFK");
        Assert.Equal("KBOS", dep);
        Assert.Equal("LOGAN4 BOSOX JFK", enroute);
        Assert.Equal("KJFK", dest);
    }

    [Theory]
    [InlineData("KBED↑")]
    [InlineData("KBED[")]
    public void DepartureSwap_Arrow_ReplacesDepartureOnly(string spliceArg)
    {
        var (dep, enroute, dest) = Splice(spliceArg);
        Assert.Equal("KBED", dep);
        Assert.Equal("SSOXS SEY PARCH3", enroute);
        Assert.Equal("KJFK", dest);
    }

    [Theory]
    [InlineData("SEY.VALRE.HAARP3.KLGA↓")]
    [InlineData("SEY.VALRE.HAARP3.KLGA]")]
    public void DestinationSwap_Arrow_SplicesFromAnchorAndSwapsDest(string spliceArg)
    {
        var (dep, enroute, dest) = Splice(spliceArg);
        Assert.Equal("KBOS", dep);
        Assert.Equal("SSOXS SEY VALRE HAARP3", enroute);
        Assert.Equal("KLGA", dest);
    }

    [Fact]
    public void DepartureSwap_MultipleTokens_Rejected()
    {
        // The departure airport must be the only element entered.
        Assert.Null(RouteSplicer.Splice(Dep, Enroute, Dest, "KBED.SSOXS↑"));
    }

    [Fact]
    public void ReversedSplice_ResumeAnchorBeforeJoin_Rejected()
    {
        // PARCH3.SSOXS: join at PARCH3 (idx3), resume anchor SSOXS exists only earlier (idx1) → reversed.
        Assert.Null(RouteSplicer.Splice(Dep, Enroute, Dest, "PARCH3.SSOXS"));
    }

    [Fact]
    public void EmptyArg_Rejected()
    {
        Assert.Null(RouteSplicer.Splice(Dep, Enroute, Dest, "   "));
    }

    [Fact]
    public void CaseInsensitiveAnchors_MatchExistingRoute()
    {
        var (_, enroute, _) = Splice("ssoxs.buzrd.sey");
        Assert.Equal("ssoxs buzrd sey PARCH3", enroute);
    }
}
