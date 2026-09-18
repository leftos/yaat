using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The <c>PUSHM</c> grammar: a tug move through two or more ramp points with an optional final rest facing.
///
/// <para>Forms:
///   <c>PUSHM $6A $6B</c>            — two painted ramp spots
///   <c>PUSHM #1926 $5A</c>          — a graph node, then a spot
///   <c>PUSHM @D15 $6A FACE E</c>    — a stand, then a spot, left facing east
/// </para>
///
/// <para>Two invariants carry weight beyond the shapes. Every target keeps its sigil: it is the only thing
/// separating spot <c>$7</c> from gate <c>@7</c>, and stripping it is how <c>ATXI $7</c> once resolved to gate 7
/// at KOAK. And every target needs one: plain <c>PUSH TE @B27</c> silently reads <c>@B27</c> as a facing taxiway
/// because only its first token's sigil is honoured, so here a sigil-less token is refused outright.</para>
/// </summary>
public class PushbackMultiParseTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("PUSHM $6A $6B", "$6A|$6B")]
    [InlineData("PUSHM #1926 $5A", "#1926|$5A")]
    [InlineData("PUSHM $6A $6B $6C", "$6A|$6B|$6C")]
    [InlineData("PUSHM @D15 @D14", "@D15|@D14")]
    [InlineData("pushm $6a $6b", "$6A|$6B")]
    public void TargetsOnly_ParsesEveryTargetInOrderWithItsSigil(string input, string expectedTargets)
    {
        PushbackMultiCommand push = Parse(input);

        Assert.Equal(expectedTargets.Split('|'), push.Targets);
        Assert.Null(push.FinalFacing);
    }

    [Theory]
    [InlineData("PUSHM @D15 $6A FACE E", 90.0)]
    [InlineData("PUSHM $6A $6B FACE N", 0.0)]
    [InlineData("PUSHM $6A $6B TAIL W", 90.0)]
    [InlineData("PUSHM $6A $6B >NE", 45.0)]
    [InlineData("PUSHM $6A $6B <N", 180.0)]
    public void TrailingOrientation_IsTheFinalRestFacing(string input, double expectedFacingDeg)
    {
        PushbackMultiCommand push = Parse(input);

        Assert.NotNull(push.FinalFacing);
        Assert.Equal(expectedFacingDeg, push.FinalFacing.Value.Degrees, 3);
        Assert.Equal(2, push.Targets.Count);
    }

    [Fact]
    public void SigilsAreNotStripped_SoASpotIsNeverReadAsAGateOfTheSameName()
    {
        PushbackMultiCommand spots = Parse("PUSHM $7 $8");
        PushbackMultiCommand gates = Parse("PUSHM @7 @8");

        Assert.Equal(new[] { "$7", "$8" }, spots.Targets);
        Assert.Equal(new[] { "@7", "@8" }, gates.Targets);
    }

    [Theory]
    [InlineData("PUSHM")]
    [InlineData("PUSHM $6A")]
    [InlineData("PUSHM $6A FACE E")]
    public void FewerThanTwoTargets_RefusedNamingPlainPush(string input)
    {
        string reason = Refusal(input);

        Assert.Contains("needs at least two targets — use PUSH to move to a single one", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PUSHM TE @B27", "TE")]
    [InlineData("PUSHM $6A 6B", "6B")]
    [InlineData("PUSHM $6A $6B C", "C")]
    [InlineData("PUSHM $ $6B", "$")]
    [InlineData("PUSHM #A1 $6B", "#A1")]
    public void TargetWithoutASigil_RefusedSayingWhereTheSigilGoes(string input, string offendingToken)
    {
        string reason = Refusal(input);

        Assert.Contains(
            $"target '{offendingToken}' needs a sigil — $ for a spot ($6A), @ for a gate or helipad (@D15), # for a graph node (#1926)",
            reason,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("PUSHM FACE E $6A $6B")]
    [InlineData("PUSHM $6A FACE E $6B")]
    [InlineData("PUSHM $6A >E $6B")]
    public void OrientationBeforeTheLastTarget_Refused(string input)
    {
        string reason = Refusal(input);

        Assert.Contains("takes the facing last — put FACE/TAIL or </> with a cardinal after the final target", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FaceWithoutACardinal_RefusedByTheSharedOrientationGrammar()
    {
        Assert.Contains("FACE requires a cardinal direction (N/NE/E/SE/S/SW/W/NW)", Refusal("PUSHM $6A $6B FACE"), StringComparison.Ordinal);
        Assert.Contains("invalid cardinal 'UP' after FACE", Refusal("PUSHM $6A $6B FACE UP"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical text is what the replay action router re-derives a command from, so
    /// <c>Describe → parse → Describe</c> has to be stable. A facing therefore renders as a cardinal token:
    /// degrees would not parse back, because <c>PUSHM</c> accepts no numeric facing.
    /// </summary>
    [Theory]
    [InlineData("PUSHM $6A $6B", "PUSHM $6A $6B")]
    [InlineData("PUSHM #1926 $5A", "PUSHM #1926 $5A")]
    [InlineData("PUSHM @D15 $6A FACE E", "PUSHM @D15 $6A FACE E")]
    [InlineData("PUSHM $6A $6B TAIL W", "PUSHM $6A $6B FACE E")]
    [InlineData("PUSHM $6A $6B >NE", "PUSHM $6A $6B FACE NE")]
    [InlineData("PUSHM $6A $6B <N", "PUSHM $6A $6B FACE S")]
    [InlineData("pushm $6a $6b FACE N", "PUSHM $6A $6B FACE N")]
    public void Canonical_RoundTrips(string input, string expectedCanonical)
    {
        PushbackMultiCommand first = Parse(input);
        string canonical = CommandDescriber.DescribeCommand(first);
        output.WriteLine($"{input} → {canonical} → {CommandDescriber.DescribeNatural(first)}");

        Assert.Equal(expectedCanonical, canonical);

        PushbackMultiCommand second = Parse(canonical);
        Assert.Equal(first.Targets, second.Targets);
        Assert.Equal(first.FinalFacing, second.FinalFacing);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(second));
    }

    [Theory]
    [InlineData("PUSHM $6A $6B", "Tug move to spot 6A, then spot 6B")]
    [InlineData("PUSHM #1926 $5A", "Tug move to node 1926, then spot 5A")]
    [InlineData("PUSHM @D15 $6A FACE E", "Tug move to parking D15, then spot 6A, facing E")]
    public void Natural_ListsTheLegsInOrderAndKeepsTheFacing(string input, string expectedNatural)
    {
        Assert.Equal(expectedNatural, CommandDescriber.DescribeNatural(Parse(input)));
    }

    /// <summary>The verb is a ground command: a fired dimension of None would wipe the aircraft's queue.</summary>
    [Fact]
    public void IsAGroundCommand()
    {
        Assert.True(CommandDescriber.IsGroundCommand(Parse("PUSHM $6A $6B")));
        Assert.Equal(CommandDimension.Ground, CommandDescriber.GetCommandDimension(Parse("PUSHM $6A $6B")));
    }

    private static PushbackMultiCommand Parse(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"'{input}' was refused: {result.Reason}");
        return Assert.IsType<PushbackMultiCommand>(result.Value);
    }

    private string Refusal(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        Assert.False(result.IsSuccess, $"'{input}' was expected to be refused but parsed");
        Assert.NotNull(result.Reason);
        output.WriteLine($"{input} → {result.Reason}");
        return result.Reason;
    }
}
