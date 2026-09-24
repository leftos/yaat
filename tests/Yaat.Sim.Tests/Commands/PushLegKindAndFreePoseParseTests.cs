using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The per-target leg-kind suffix and the marked-point target (issue #462). A <c>PUSH</c> or <c>PUSHM</c> target may
/// carry <c>/PUSH</c> or <c>/PULL</c>, forcing the tug motion on the leg that ends at it; <c>~lat/lon[/facing]</c> is a
/// free position on the ramp, its facing a magnetic heading 1–360 like every typed heading.
/// </summary>
public class PushLegKindAndFreePoseParseTests(ITestOutputHelper output)
{
    /// <summary>Each accepted form, its canonical text, and the forced kind of each target in order (<c>-</c> for none).</summary>
    [Theory]
    [InlineData("PUSH $7A/PULL", "PUSH $7A/PULL", "Pull")]
    [InlineData("PUSH $7A/PUSH FACE E", "PUSH $7A/PUSH FACE E", "Push")]
    [InlineData("push $7a/pull", "PUSH $7A/PULL", "Pull")]
    [InlineData("PUSH @F8/PULL", "PUSH @F8/PULL", "Pull")]
    [InlineData("PUSH #1926/PUSH", "PUSH #1926/PUSH", "Push")]
    [InlineData("PUSH ~37.61523/-122.38604/270/PULL", "PUSH ~37.615230/-122.386040/270/PULL", "Pull")]
    [InlineData("PUSH ~37.61523/-122.38604", "PUSH ~37.615230/-122.386040", "-")]
    [InlineData("PUSH ~37.61523/-122.38604/090", "PUSH ~37.615230/-122.386040/090", "-")]
    [InlineData("PUSH ~37.61523/-122.38604 FACE E", "PUSH ~37.615230/-122.386040 FACE E", "-")]
    [InlineData("PUSHM @F8 $7A/PULL", "PUSHM @F8 $7A/PULL", "-|Pull")]
    [InlineData("PUSHM $6A/PUSH $6B/PULL FACE N", "PUSHM $6A/PUSH $6B/PULL FACE N", "Push|Pull")]
    [InlineData("PUSHM ~37.61523/-122.38604/360 ~37.6155/-122.3862/PUSH", "PUSHM ~37.615230/-122.386040/360 ~37.615500/-122.386200/PUSH", "-|Push")]
    public void Accepted_ParsesEveryTargetsForcedKind_AndCanonicalises(string input, string expectedCanonical, string expectedKinds)
    {
        ParsedCommand parsed = Parse(input);
        string canonical = CommandDescriber.DescribeCommand(parsed);
        output.WriteLine($"{input} → {canonical}   ({CommandDescriber.DescribeNatural(parsed)})");

        IReadOnlyList<PushDestination> targets = parsed switch
        {
            PushbackCommand push => [push.Destination!],
            PushbackMultiCommand move => move.Legs,
            _ => throw new InvalidOperationException($"'{input}' parsed as {parsed.GetType().Name}"),
        };
        string[] kinds = [.. targets.Select(t => t.ForcedKind?.ToString() ?? "-")];
        Assert.Equal(expectedKinds.Split('|'), kinds);
        Assert.Equal(expectedCanonical, canonical);
    }

    [Theory]
    [InlineData("PUSH ~37.61523/-122.38604", 37.61523, -122.38604, 0)]
    [InlineData("PUSH ~37.61523/-122.38604/090", 37.61523, -122.38604, 90)]
    [InlineData("PUSH ~+37.6152349/-122.3860449/1", 37.615235, -122.386045, 1)]
    public void FreePose_CarriesLatLonRoundedToSixDecimals_AndAMagneticFacing(string input, double lat, double lon, int facingDeg)
    {
        PushbackCommand push = Assert.IsType<PushbackCommand>(Parse(input));
        PushFreePose pose = push.Destination?.FreePose ?? throw new InvalidOperationException($"'{input}' carries no free pose");

        Assert.Equal(lat, pose.Latitude, 9);
        Assert.Equal(lon, pose.Longitude, 9);
        if (facingDeg == 0)
        {
            Assert.Null(pose.Facing);
        }
        else
        {
            Assert.Equal(new MagneticHeading(facingDeg), pose.Facing);
        }
    }

    [Fact]
    public void ForcedKind_OnAPushmTarget_IsThatTargetsAlone()
    {
        PushbackMultiCommand move = Assert.IsType<PushbackMultiCommand>(Parse("PUSHM @F8 $7A/PULL"));

        Assert.Equal("F8", move.Legs[0].Parking);
        Assert.Null(move.Legs[0].ForcedKind);
        Assert.Equal("7A", move.Legs[1].Spot);
        Assert.Equal(PushbackLegKind.Pull, move.Legs[1].ForcedKind);
    }

    [Theory]
    [InlineData("PUSH $7A/", "'$7A/' needs PUSH or PULL after the slash")]
    [InlineData("PUSH $7A/TOW", "'/TOW' is not a leg kind — use /PUSH or /PULL")]
    [InlineData("PUSH $7A/PULL/PUSH", "'$7A/PULL/PUSH' carries more than one leg kind — one /PUSH or /PULL per target")]
    [InlineData("PUSH Y/PULL", "'Y/PULL': only a $spot, @gate, #node or ~point takes /PUSH or /PULL")]
    [InlineData("PUSH $7A Y/PULL", "'Y/PULL': only a $spot, @gate, #node or ~point takes /PUSH or /PULL")]
    [InlineData("PUSH ~37.6", "'~37.6' is not a marked point — ~<lat>/<lon> or ~<lat>/<lon>/<facing>")]
    [InlineData("PUSH ~abc/def", "'~abc/def' is not a marked point — ~<lat>/<lon> or ~<lat>/<lon>/<facing>, e.g. ~37.61523/-122.38604/090")]
    [InlineData("PUSH ~abc/1", "'~abc/1' is not a marked point — ~<lat>/<lon> or ~<lat>/<lon>/<facing>, e.g. ~37.61523/-122.38604/090")]
    [InlineData("PUSH ~37.6/-122.3/TOW", "'/TOW' is not a leg kind — use /PUSH or /PULL")]
    [InlineData("PUSH ~NaN/0", "latitude 'NaN' is out of range (-90 to 90)")]
    [InlineData("PUSH ~0/NaN", "longitude 'NaN' is out of range (-180 to 180)")]
    [InlineData("PUSH ~0/NaN/090", "longitude 'NaN' is out of range (-180 to 180)")]
    [InlineData("PUSH ~Infinity/0", "latitude 'Infinity' is out of range (-90 to 90)")]
    [InlineData("PUSH ~37.6/-122.3/", "'~37.6/-122.3/' needs PUSH or PULL after the slash")]
    [InlineData("PUSH ~37.6/-122.3/090/TOW", "'/TOW' is not a leg kind — use /PUSH or /PULL")]
    [InlineData("PUSH ~37.6/-122.3/PULL/PUSH", "'~37.6/-122.3/PULL/PUSH' carries more than one leg kind — one /PUSH or /PULL per target")]
    [InlineData("PUSH TE ~37.6/-122.3", "'~37.6/-122.3' must be the first PUSH argument")]
    [InlineData("PUSH ~37.6/-122.3/400", "marked-point facing '400' is not a heading 001-360")]
    [InlineData("PUSH ~91/0", "latitude '91' is out of range (-90 to 90)")]
    [InlineData("PUSH ~37.6/-181", "longitude '-181' is out of range (-180 to 180)")]
    [InlineData("PUSH ~37.61523/-122.38604/090 FACE E", "facing given twice — the marked point already carries one")]
    [InlineData("PUSH ~37.61523/-122.38604 A", "a marked point takes its facing as ~<lat>/<lon>/<facing> or FACE/TAIL, not a facing taxiway")]
    [InlineData("PUSHM $6A ~37.61523/-122.38604/090 FACE E", "facing given twice — the marked point already carries one")]
    [InlineData("PUSHM $6A/TOW $6B", "'/TOW' is not a leg kind — use /PUSH or /PULL")]
    [InlineData("PUSH ~", "~ needs a point — PUSH ~37.61523/-122.38604/090")]
    public void Refused_WithAMessageNamingWhatIsWrong(string input, string expectedReason)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        output.WriteLine($"{input} → {result.Reason}");

        Assert.False(result.IsSuccess, $"'{input}' was accepted");
        Assert.Contains(expectedReason, result.Reason, StringComparison.Ordinal);
    }

    private static ParsedCommand Parse(string input)
    {
        ParseResult<ParsedCommand> result = CommandParser.Parse(input);
        Assert.True(result.IsSuccess, $"'{input}' was refused: {result.Reason}");
        return result.Value!;
    }
}
