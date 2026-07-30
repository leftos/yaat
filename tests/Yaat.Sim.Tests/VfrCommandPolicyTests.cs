using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// The VFR-only command classification and the three-way "VFR commands for IFR aircraft" policy
/// (issue #317). These predicates are what the desktop client enforces before a command reaches
/// the wire, so they are asserted here over real parsed commands rather than hand-built records —
/// that keeps the alias surface (EF, MSA, ML3, OFFSETL, ...) covered too.
/// </summary>
public class VfrCommandPolicyTests(ITestOutputHelper output)
{
    private ParsedCommand ParseSingle(string text)
    {
        var result = CommandParser.ParseCompound(text);
        Assert.True(result.IsSuccess, $"Parse failed for '{text}': {result.Reason}");
        var block = Assert.Single(result.Value!.Blocks);
        var command = Assert.Single(block.Commands);
        output.WriteLine($"{text} -> {command.GetType().Name}");
        return command;
    }

    /// <summary>Every alias of the traffic-pattern / VFR-tower set gated by <c>RequiresVfr</c>.</summary>
    [Theory]
    [InlineData("ELD 28R")]
    [InlineData("ERD 28R")]
    [InlineData("ELC 28R")]
    [InlineData("ERC 28R")]
    [InlineData("ELB 28R")]
    [InlineData("ERB 28R")]
    [InlineData("EF 28R")]
    [InlineData("MLT")]
    [InlineData("MRT")]
    [InlineData("TC")]
    [InlineData("TD")]
    [InlineData("TB")]
    [InlineData("EXT")]
    [InlineData("EXTEND")]
    [InlineData("SA")]
    [InlineData("MSA")]
    [InlineData("MNA")]
    [InlineData("L360")]
    [InlineData("ML3")]
    [InlineData("ML360")]
    [InlineData("R360")]
    [InlineData("MR3")]
    [InlineData("MR360")]
    [InlineData("L270")]
    [InlineData("R270")]
    [InlineData("P270")]
    [InlineData("PLAN270")]
    [InlineData("NO270")]
    [InlineData("CA")]
    [InlineData("CIRCLE")]
    [InlineData("PS 1.5")]
    [InlineData("PATTSIZE 1.5")]
    [InlineData("MLS")]
    [InlineData("MRS")]
    [InlineData("OFL")]
    [InlineData("OFFSETL")]
    [InlineData("OFR")]
    [InlineData("OFFSETR")]
    [InlineData("TG")]
    [InlineData("SG")]
    [InlineData("LA")]
    [InlineData("COPT")]
    [InlineData("HPPL")]
    [InlineData("HPPR")]
    [InlineData("HPP")]
    public void RequiresVfr_CoversThePatternSet(string commandText)
    {
        var command = ParseSingle(commandText);

        Assert.True(VfrCommandPolicy.RequiresVfr(command), $"'{commandText}' should be VFR-gated");
        Assert.True(VfrCommandPolicy.IsVfrOnly(command));
    }

    /// <summary>The three gates that live outside <c>RequiresVfr</c> but are still VFR-only.</summary>
    [Theory]
    [InlineData("FOLLOW UAL123")]
    [InlineData("CM A025")]
    [InlineData("CM B055")]
    [InlineData("CTO MRC")]
    [InlineData("CTO MLC")]
    [InlineData("CTO MRD")]
    [InlineData("CTO MLD")]
    [InlineData("CTO MR270")]
    [InlineData("CTO ML45")]
    [InlineData("CTO MRT")]
    [InlineData("CTO MLT")]
    public void IsVfrOnly_CoversTheNonPatternGates(string commandText)
    {
        var command = ParseSingle(commandText);

        Assert.True(VfrCommandPolicy.IsVfrOnly(command), $"'{commandText}' should be VFR-only");
    }

    /// <summary>Commands an IFR aircraft has always been able to receive stay ungated.</summary>
    [Theory]
    [InlineData("CVA 28R")]
    [InlineData("RFIS")]
    [InlineData("RTIS")]
    [InlineData("CIFR")]
    [InlineData("H 270")]
    [InlineData("CM 050")]
    [InlineData("DM 030")]
    [InlineData("CTO")]
    [InlineData("CTO RH")]
    [InlineData("CTO 270")]
    [InlineData("CTO OC")]
    [InlineData("CTO DCT SUNOL")]
    [InlineData("CTO TLDCT SUNOL")]
    [InlineData("CTO TRDCT SUNOL")]
    [InlineData("CLAND")]
    [InlineData("GA")]
    public void IsVfrOnly_LeavesEverythingElseAlone(string commandText)
    {
        var command = ParseSingle(commandText);

        Assert.False(VfrCommandPolicy.IsVfrOnly(command), $"'{commandText}' should not be VFR-only");
        Assert.True(VfrCommandPolicy.AllowsForIfr(command, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(VfrCommandsForIfr.None, false)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, true)]
    [InlineData(VfrCommandsForIfr.All, true)]
    public void AllowsForIfr_EnterFinal(VfrCommandsForIfr mode, bool expected)
    {
        Assert.Equal(expected, VfrCommandPolicy.AllowsForIfr(ParseSingle("EF 28R"), mode));
    }

    /// <summary>EnterFinalOnly opens up EF and nothing else — not even its sibling pattern entries.</summary>
    [Theory]
    [InlineData("ELD 28R")]
    [InlineData("ERB 28R")]
    [InlineData("MLT")]
    [InlineData("TG")]
    [InlineData("FOLLOW UAL123")]
    [InlineData("CM A025")]
    [InlineData("CTO MRC")]
    public void AllowsForIfr_EnterFinalOnly_BlocksEverythingButEf(string commandText)
    {
        var command = ParseSingle(commandText);

        Assert.False(VfrCommandPolicy.AllowsForIfr(command, VfrCommandsForIfr.None));
        Assert.False(VfrCommandPolicy.AllowsForIfr(command, VfrCommandsForIfr.EnterFinalOnly));
        Assert.True(VfrCommandPolicy.AllowsForIfr(command, VfrCommandsForIfr.All));
    }

    /// <summary>
    /// The forms an IFR departure receives routinely: a null instruction, the filed SID, an assigned
    /// heading, runway heading, a helicopter hover, and — since these are ordinary IFR clearances
    /// (7110.65 §5-6-2) that merely happen to be modelled as departure modifiers — on course and
    /// direct-to-fix.
    /// </summary>
    [Fact]
    public void IsVfrOnlyDeparture_AllowsTheIfrForms()
    {
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(null));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new DefaultDeparture()));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new RunwayHeadingDeparture()));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new PresentPositionHoverDeparture()));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new FlyHeadingDeparture(new MagneticHeading(270), null)));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new OnCourseDeparture()));
        Assert.False(VfrCommandPolicy.IsVfrOnlyDeparture(new DirectFixDeparture("SUNOL", 37.6, -121.9, null)));
    }

    /// <summary>
    /// The three that peel the aircraft toward the pattern at liftoff, abandoning an IFR departure's
    /// SID or filed route with no amended clearance (§4-3-2, §5-6-2).
    /// </summary>
    [Fact]
    public void IsVfrOnlyDeparture_RejectsPatternRelativeForms()
    {
        Assert.True(VfrCommandPolicy.IsVfrOnlyDeparture(new RelativeTurnDeparture(270, TurnDirection.Right)));
        Assert.True(VfrCommandPolicy.IsVfrOnlyDeparture(new ClosedTrafficDeparture(PatternDirection.Left, null, null)));
        Assert.True(VfrCommandPolicy.IsVfrOnlyDeparture(new PatternExitDeparture(PatternEntryLeg.Crosswind, PatternDirection.Right)));
    }

    /// <summary>Plain CM (no A/B modifier) is an ordinary IFR altitude assignment, not a VFR restriction.</summary>
    [Fact]
    public void IsVfrOnly_PlainClimbMaintain_NotGated()
    {
        Assert.False(VfrCommandPolicy.IsVfrOnly(new ClimbMaintainCommand(5000)));
        Assert.True(VfrCommandPolicy.IsVfrOnly(new ClimbMaintainCommand(5000, AltitudeAssignmentModifier.AtOrAbove)));
        Assert.True(VfrCommandPolicy.IsVfrOnly(new ClimbMaintainCommand(5000, AltitudeAssignmentModifier.AtOrBelow)));
    }
}
