using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CtoClientParserTests
{
    private readonly CommandScheme _atcTrainer = CommandScheme.AtcTrainer();
    private readonly CommandScheme _vice = CommandScheme.Vice();

    [Fact]
    public void BareCto_AtcTrainer_ParsesAsNoArg()
    {
        var result = CommandSchemeParser.ParseCompound("CTO", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifier_AtcTrainer_CapturesFullText()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRC 014", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MRC 014", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifierNoAlt_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRD", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MRD", result.CanonicalString);
    }

    [Fact]
    public void CtoRunwayHeading_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO RH", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO RH", result.CanonicalString);
    }

    [Fact]
    public void CtoFlyHeading_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO H270", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO H270", result.CanonicalString);
    }

    [Fact]
    public void CtoDirectFix_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO DCT SUNOL 050", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO DCT SUNOL 050", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_Legacy_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMRT", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void Ctomlt_Legacy_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMLT", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void Ctomrt_WithAlt_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTOMRT 050", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT 050", result.CanonicalString);
    }

    [Fact]
    public void CtoOnCourse_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO OC", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO OC", result.CanonicalString);
    }

    [Fact]
    public void CtoLeftHeading_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO LH270 014", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO LH270 014", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficRight_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MRT", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MRT", result.CanonicalString);
    }

    [Fact]
    public void CtoClosedTrafficLeft_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MLT", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO MLT", result.CanonicalString);
    }

    [Fact]
    public void BareCto_Vice_ParsesAsNoArg()
    {
        var result = CommandSchemeParser.ParseCompound("CTO", _vice);
        Assert.NotNull(result);
        Assert.Equal("CTO", result.CanonicalString);
    }

    [Fact]
    public void CtoWithModifier_Vice_CapturesFullText()
    {
        var result = CommandSchemeParser.ParseCompound("CTO MLC 050", _vice);
        Assert.NotNull(result);
        Assert.Equal("CTO MLC 050", result.CanonicalString);
    }

    [Fact]
    public void CtoWithBareAlt_AtcTrainer()
    {
        var result = CommandSchemeParser.ParseCompound("CTO 050", _atcTrainer);
        Assert.NotNull(result);
        Assert.Equal("CTO 050", result.CanonicalString);
    }
}
