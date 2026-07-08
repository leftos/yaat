using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandErrorFormatterTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static IReadOnlyCollection<AircraftModel> Aircraft(params string[] callsigns)
    {
        return callsigns.Select(c => new AircraftModel { Callsign = c }).ToArray();
    }

    private static ParseFailure? FailureFor(string input)
    {
        CommandSchemeParser.ParseCompound(input, Scheme, out var failure);
        return failure;
    }

    [Fact]
    public void KnownCallsign_BadVerb_BlamesVerbNotCallsign()
    {
        // Reproduces N929AW SPEEDN 80: the raw parse blames the callsign "N929AW".
        const string input = "N929AW SPEEDN 80";
        var result = CommandErrorFormatter.Format(input, FailureFor(input), Scheme, Aircraft("N929AW"));

        Assert.Equal("SPEEDN", result.Verb);
        Assert.DoesNotContain("N929AW", result.StatusText);
        Assert.Contains("SPEEDN", result.StatusText);
    }

    [Fact]
    public void PartialCallsignMatch_BadVerb_BlamesVerbNotCallsign()
    {
        const string input = "929AW BOGUS 1";
        var result = CommandErrorFormatter.Format(input, FailureFor(input), Scheme, Aircraft("N929AW"));

        Assert.Equal("BOGUS", result.Verb);
        Assert.DoesNotContain("929AW", result.StatusText);
        Assert.Contains("BOGUS", result.StatusText);
    }

    [Fact]
    public void NoCallsignPrefix_BlamesTheVerb()
    {
        // Aircraft already selected: input is just the (bad) command, no callsign token.
        const string input = "BOGUS 80";
        var result = CommandErrorFormatter.Format(input, FailureFor(input), Scheme, Aircraft("N929AW"));

        Assert.Equal("BOGUS", result.Verb);
        Assert.Contains("BOGUS", result.StatusText);
    }

    [Fact]
    public void UnknownLeadingToken_NotTreatedAsCallsign()
    {
        // First token is not a known aircraft — it stays the blamed verb.
        const string input = "BOGUS 80";
        var result = CommandErrorFormatter.Format(input, FailureFor(input), Scheme, Aircraft("N1234"));

        Assert.Equal("BOGUS", result.Verb);
    }

    [Fact]
    public void TrailingConditionBlockFails_BlamesConditionVerb_NotLeadingVerb()
    {
        // Issue #279: a malformed trailing condition block (LV with no following command) must not
        // blame the valid leading verb — the client used to report "Unrecognized command CM".
        const string input = "CM 100; LV 5000";
        var result = CommandErrorFormatter.Format(input, FailureFor(input), Scheme, Aircraft());

        Assert.Equal("LV", result.Verb);
        Assert.Contains("LV", result.StatusText);
    }
}
