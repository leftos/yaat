using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A recording made before snapshot schema 42 read a 1-2 digit CTO argument as a heading and refused a runway after CTO
/// or LUAW. The rewrite keeps each as recorded under the current grammar: the heading gains its leading zeros, and the
/// runway forms become text the parser still refuses.
/// </summary>
public class DepartureGrammarCanonicalRewriterTests
{
    [Theory]
    [InlineData("CTO 28", "CTO 028")]
    [InlineData("CTO 5", "CTO 005")]
    [InlineData("CTO 05", "CTO 005")]
    [InlineData("CTO 28 50", "CTO 028 50")]
    [InlineData("CTO 28 IMM", "CTO 028 IMM")]
    [InlineData("cto 28", "cto 028")]
    [InlineData("CTO 28R", "CTO RWY 28R")]
    [InlineData("CTO 4L", "CTO RWY 4L")]
    [InlineData("CTO 28L MRC", "CTO RWY 28L MRC")]
    [InlineData("LUAW 28L", "LUAW RWY 28L")]
    [InlineData("LUAW 28", "LUAW RWY 28")]
    [InlineData("LUAW 4", "LUAW RWY 4")]
    [InlineData("LUAW 28L IMM", "LUAW RWY 28L IMM")]
    [InlineData("CTO 28; CM 050", "CTO 028; CM 050")]
    [InlineData("CM 015, CTO 28R", "CM 015, CTO RWY 28R")]
    [InlineData("LUAW 28L; CTO 28", "LUAW RWY 28L; CTO 028")]
    [InlineData("WAIT 10 CTO 28", "WAIT 10 CTO 028")]
    [InlineData("AT SUNOL CTO 28R", "AT SUNOL CTO RWY 28R")]
    [InlineData("ONHO LUAW 28L", "ONHO LUAW RWY 28L")]
    [InlineData("AS 2B CTO 28", "AS 2B CTO 028")]
    public void Rewrite_PreRunwayGrammarForm_KeepsItsRecordedMeaning(string recorded, string expected) =>
        Assert.Equal(expected, DepartureGrammarCanonicalRewriter.Rewrite(recorded));

    [Theory]
    [InlineData("CTO")]
    [InlineData("CTO 330")]
    [InlineData("CTO 028")]
    [InlineData("CTO 280")]
    [InlineData("CTO IMM")]
    [InlineData("CTO MRC")]
    [InlineData("CTO TRDCT TOLLPLAZA 025")]
    [InlineData("CTO 28X")]
    [InlineData("CTO 100L")]
    [InlineData("CTO RWY 28R")]
    [InlineData("LUAW")]
    [InlineData("LUAW IMM")]
    [InlineData("LUAW $5A")]
    [InlineData("LUAW RWY 28L")]
    [InlineData("CTOPP 28")]
    [InlineData("FH 28")]
    [InlineData("CM 28; FH 280")]
    [InlineData("WAIT 28")]
    public void Rewrite_FormOutsideThePreRunwayGrammar_IsReturnedUnchanged(string recorded) =>
        Assert.Same(recorded, DepartureGrammarCanonicalRewriter.Rewrite(recorded));
}
