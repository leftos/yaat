using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests;

public class CtoClientParserTests
{
    private readonly CommandScheme _scheme = CommandScheme.Default();

    public CtoClientParserTests()
    {
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
    }

    [Fact]
    public void BareCto_ParsesAsNoArg()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifier_CapturesFullText()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO MRC 014", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRC 014", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifierNoAlt()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO MRD", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRD", result.CanonicalString);
    }

    [Fact]
    public void CtoRunwayHeading()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO RH", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO RH", result.CanonicalString);
    }

    [Fact]
    public void CtoFlyHeading()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO H270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO H270", result.CanonicalString);
    }

    [Fact]
    public void CtoDirectFix()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO DCT SUNOL 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO DCT SUNOL 050", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_Legacy()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTOMRT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void Ctomlt_Legacy()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTOMLT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_WithAlt()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTOMRT 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT 050", result.CanonicalString);
    }

    [Fact]
    public void CtoOnCourse()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO OC", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO OC", result.CanonicalString);
    }

    [Fact]
    public void CtoLeftHeading()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO LH270 014", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO LH270 014", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficRight()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO MRT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficLeft()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO MLT", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void CtoWithBareAlt()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CTO 050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CTO 050", result.CanonicalString);
    }

    // Concatenation tests — verb+digits without space

    [Fact]
    public void FlyHeading_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("FH270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("FH 270", result.CanonicalString);
    }

    [Fact]
    public void FlyHeading_ViceAlias_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("H270", _scheme);
        Assert.NotNull(result);
        Assert.Equal("FH 270", result.CanonicalString);
    }

    [Fact]
    public void ClimbMaintain_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("CM240", _scheme);
        Assert.NotNull(result);
        Assert.Equal("CM 240", result.CanonicalString);
    }

    [Fact]
    public void ForceAltitude_DmnConcatenated_StaysDistinctFromDm()
    {
        // The client's concatenation mirror splits the verb off the digits, so DMN and DM are the one pair
        // that could swallow each other here.
        CompoundParseResult? forced = CommandSchemeParser.ParseCompound("DMN240", _scheme);
        CompoundParseResult? descend = CommandSchemeParser.ParseCompound("DM240", _scheme);

        Assert.NotNull(forced);
        Assert.NotNull(descend);
        Assert.Equal("CMN 240", forced.CanonicalString);
        Assert.Equal("DM 240", descend.CanonicalString);
    }

    [Fact]
    public void ClimbMaintain_ViceAlias_Concatenated()
    {
        // C was removed as CM alias for ATCTrainer compatibility (C is not an ATCTrainer alias)
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("C240", _scheme);
        Assert.Null(result);
    }

    [Fact]
    public void DescendMaintain_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("DM050", _scheme);
        Assert.NotNull(result);
        Assert.Equal("DM 050", result.CanonicalString);
    }

    [Fact]
    public void Speed_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("SPD250", _scheme);
        Assert.NotNull(result);
        Assert.Equal("SPD 250", result.CanonicalString);
    }

    [Fact]
    public void Speed_ViceAlias_Concatenated()
    {
        // S was removed as SPD alias for ATCTrainer compatibility (S is not an ATCTrainer alias)
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("S250", _scheme);
        Assert.Null(result);
    }

    [Fact]
    public void Squawk_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("SQ1234", _scheme);
        Assert.NotNull(result);
        Assert.Equal("SQ 1234", result.CanonicalString);
    }

    [Fact]
    public void TurnLeft_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("TL180", _scheme);
        Assert.NotNull(result);
        Assert.Equal("TL 180", result.CanonicalString);
    }

    [Fact]
    public void TurnLeft_ViceAlias_Concatenated()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("L180", _scheme);
        Assert.NotNull(result);
        Assert.Equal("TL 180", result.CanonicalString);
    }

    [Fact]
    public void RelativeLeft_T30L()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("T30L", _scheme);
        Assert.NotNull(result);
        Assert.Equal("RELL 30", result.CanonicalString);
    }

    [Fact]
    public void RelativeRight_T30R()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("T30R", _scheme);
        Assert.NotNull(result);
        Assert.Equal("RELR 30", result.CanonicalString);
    }

    [Fact]
    public void FlyHeading_BareH_ReturnsNull()
    {
        // H maps to FlyHeading (via registry), which requires a heading argument
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("H", _scheme);
        Assert.Null(result);
    }

    [Fact]
    public void Delete_ViceAlias()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound("X", _scheme);
        Assert.NotNull(result);
        Assert.Equal("DEL", result.CanonicalString);
    }
}
