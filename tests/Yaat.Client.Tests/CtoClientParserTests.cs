using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CtoClientParserTests
{
    private readonly CommandScheme _scheme = CommandScheme.Default();

    [Fact]
    public void BareCto_ParsesAsNoArg()
    {
        var result = CommandSchemeParser.ParseCompound("CTO", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifier_CapturesFullText()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRC 014", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRC 014", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifierNoAlt()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRD", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRD", result.CanonicalString);
    }

    [Fact]
    public void CtoRunwayHeading()
    {
        var result = CommandSchemeParser.ParseCompound("CTO RH", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO RH", result.CanonicalString);
    }

    [Fact]
    public void CtoFlyHeading()
    {
        var result = CommandSchemeParser.ParseCompound("CTO H270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO H270", result.CanonicalString);
    }

    [Fact]
    public void CtoDirectFix()
    {
        var result = CommandSchemeParser.ParseCompound("CTO DCT SUNOL 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO DCT SUNOL 050", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_Legacy()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMRT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void Ctomlt_Legacy()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMLT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_WithAlt()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMRT 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT 050", result.CanonicalString);
    }

    [Fact]
    public void CtoOnCourse()
    {
        var result = CommandSchemeParser.ParseCompound("CTO OC", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO OC", result.CanonicalString);
    }

    [Fact]
    public void CtoLeftHeading()
    {
        var result = CommandSchemeParser.ParseCompound("CTO LH270 014", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO LH270 014", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficRight()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficLeft()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MLT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void CtoWithBareAlt()
    {
        var result = CommandSchemeParser.ParseCompound("CTO 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO 050", result.CanonicalString);
    }

    // Concatenation tests — verb+digits without space

    [Fact]
    public void FlyHeading_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("FH270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("FH 270", result.CanonicalString);
    }

    [Fact]
    public void FlyHeading_ViceAlias_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("H270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("FH 270", result.CanonicalString);
    }

    [Fact]
    public void ClimbMaintain_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("CM240", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CM 240", result.CanonicalString);
    }

    [Fact]
    public void ClimbMaintain_ViceAlias_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("C240", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CM 240", result.CanonicalString);
    }

    [Fact]
    public void DescendMaintain_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("DM050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("DM 050", result.CanonicalString);
    }

    [Fact]
    public void Speed_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("SPD250", _scheme);
        Assert.NotNull(result);
        Assert.Equal("SPD 250", result.CanonicalString);
    }

    [Fact]
    public void Speed_ViceAlias_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("S250", _scheme);
        Assert.NotNull(result);
        Assert.Equal("SPD 250", result.CanonicalString);
    }

    [Fact]
    public void Squawk_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("SQ1234", _scheme);
        Assert.NotNull(result);
        Assert.Equal("SQ 1234", result.CanonicalString);
    }

    [Fact]
    public void TurnLeft_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("TL180", _scheme);
        Assert.NotNull(result);
        Assert.Equal("TL 180", result.CanonicalString);
    }

    [Fact]
    public void TurnLeft_ViceAlias_Concatenated()
    {
        var result = CommandSchemeParser.ParseCompound("L180", _scheme);
        Assert.NotNull(result);
        Assert.Equal("TL 180", result.CanonicalString);
    }

    [Fact]
    public void RelativeLeft_T30L()
    {
        var result = CommandSchemeParser.ParseCompound("T30L", _scheme);
        Assert.NotNull(result);
        Assert.Equal("LT 30", result.CanonicalString);
    }

    [Fact]
    public void RelativeRight_T30R()
    {
        var result = CommandSchemeParser.ParseCompound("T30R", _scheme);
        Assert.NotNull(result);
        Assert.Equal("RT 30", result.CanonicalString);
    }

    [Fact]
    public void FlyPresentHeading_BareH()
    {
        var result = CommandSchemeParser.ParseCompound("H", _scheme);
        Assert.NotNull(result);
        Assert.Equal("FPH", result.CanonicalString);
    }

    [Fact]
    public void Delete_ViceAlias()
    {
        var result = CommandSchemeParser.ParseCompound("X", _scheme);
        Assert.NotNull(result);
        Assert.Equal("DEL", result.CanonicalString);
    }
}
