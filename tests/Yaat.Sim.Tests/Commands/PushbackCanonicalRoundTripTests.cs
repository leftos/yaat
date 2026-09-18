using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Every accepted <c>PUSH</c> form through <c>Describe → parse → Describe</c>. Canonical text is what the
/// replay action router re-derives a command's state from, so a form whose canonical does not re-parse is a
/// replay-fidelity defect, not a cosmetic one.
///
/// <para>Two ways it did not round-trip. A facing rendered as degrees (<c>PUSH A 090</c>), which
/// <c>GroundCommandParser.ParsePushback</c> has refused outright since the cardinal rewrite; and a facing
/// taxiway dropped on the floor (<c>PUSH TE T</c> formatted as <c>PUSH TE</c>), which parses but has lost the
/// facing the controller asked for.</para>
///
/// <para>The forms are the ones documented in <c>COMMANDS.md</c>'s ground-command table. Where input and
/// canonical differ, the canonical is the same clearance in the one spelling the grammar prefers: a facing is
/// always <c>FACE &lt;cardinal&gt;</c>, so <c>TAIL W</c>, <c>&gt;E</c> and <c>&lt;W</c> all come back as
/// <c>FACE E</c>.</para>
/// </summary>
public class PushbackCanonicalRoundTripTests(ITestOutputHelper output)
{
    /// <summary>The eight cardinals the pushback grammar accepts, and the four spellings each has.</summary>
    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    [Theory]
    [InlineData("PUSH", "PUSH")]
    [InlineData("PUSH FACE E", "PUSH FACE E")]
    [InlineData("PUSH TAIL W", "PUSH FACE E")]
    [InlineData("PUSH >NE", "PUSH FACE NE")]
    [InlineData("PUSH <N", "PUSH FACE S")]
    [InlineData("PUSH A", "PUSH A")]
    [InlineData("PUSH A A1", "PUSH A A1")]
    [InlineData("PUSH A FACE E", "PUSH A FACE E")]
    [InlineData("PUSH TE T", "PUSH TE T")]
    [InlineData("PUSH TE TAIL W", "PUSH TE FACE E")]
    [InlineData("PUSH @4A", "PUSH @4A")]
    [InlineData("PUSH $7A", "PUSH $7A")]
    [InlineData("PUSH $7A TAIL W", "PUSH $7A FACE E")]
    public void EveryAcceptedForm_CanonicalReParsesToTheSameCommand(string input, string expectedCanonical)
    {
        PushbackCommand first = Parse(input);
        string canonical = CommandDescriber.DescribeCommand(first);
        output.WriteLine($"{input} → {canonical}   ({CommandDescriber.DescribeNatural(first)})");

        Assert.Equal(expectedCanonical, canonical);

        PushbackCommand second = Parse(canonical);
        Assert.Equal(first, second);
        Assert.Equal(canonical, CommandDescriber.DescribeCommand(second));
    }

    /// <summary>
    /// A facing renders as a cardinal token, which snaps a heading to the nearest of the eight compass points —
    /// so a facing that is not already on one of them could not round-trip exactly. No accepted <c>PUSH</c> form
    /// produces one, because the grammar accepts nothing but the eight cardinals in their four spellings. That
    /// is asserted here rather than assumed: it is the precondition the canonical form rests on.
    /// </summary>
    [Fact]
    public void EveryFacingTheGrammarAccepts_IsAlreadyOnAnEightPointBoundary()
    {
        foreach (string cardinal in Cardinals)
        {
            foreach (string form in new[] { $"FACE {cardinal}", $"TAIL {cardinal}", $">{cardinal}", $"<{cardinal}" })
            {
                PushbackCommand push = Parse($"PUSH {form}");
                Assert.NotNull(push.MagneticHeading);

                double degrees = push.MagneticHeading.Value.Degrees;
                output.WriteLine($"PUSH {form} → {degrees:000} → {CommandDescriber.DescribeCommand(push)}");
                Assert.Equal(0.0, degrees % 45.0, 6);
            }
        }
    }

    /// <summary>
    /// A push to a stand parks on the stand's own heading, so none of the facing spellings — nor a facing taxiway —
    /// is an accepted form with a stand destination.
    /// </summary>
    [Theory]
    [InlineData("PUSH @4A A")]
    [InlineData("PUSH @4A FACE NE")]
    [InlineData("PUSH @4A TAIL W")]
    [InlineData("PUSH @4A >E")]
    [InlineData("PUSH @4A <W")]
    [InlineData("PUSH @4A TE T")]
    [InlineData("PUSH @4A TE FACE E")]
    public void StandDestinationWithAFacing_Refused(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        output.WriteLine($"{input} → {result.Reason}");

        Assert.False(result.IsSuccess, $"'{input}' was accepted");
        Assert.Contains("PUSH @4A does not take a facing — the aircraft parks on the stand's own heading", result.Reason, StringComparison.Ordinal);
    }

    private static PushbackCommand Parse(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"'{input}' was refused: {result.Reason}");
        return Assert.IsType<PushbackCommand>(result.Value);
    }
}
