using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CallsignPrefixResolverTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static AircraftModel Ac(string callsign)
    {
        return new AircraftModel { Callsign = callsign };
    }

    private static IReadOnlyCollection<AircraftModel> Aircraft(params string[] callsigns)
    {
        return callsigns.Select(Ac).ToArray();
    }

    [Fact]
    public void Ambiguous_FirstToken_ReturnsAmbiguousWithBothCallsigns()
    {
        var result = CallsignPrefixResolver.Resolve("N12 FH 270", Scheme, Aircraft("N1234", "N1256"));

        var ambiguous = Assert.IsType<CallsignPrefixResolver.Ambiguous>(result);
        Assert.Contains("matches multiple aircraft", ambiguous.Message);
        Assert.Contains("N1234", ambiguous.Message);
        Assert.Contains("N1256", ambiguous.Message);
        Assert.DoesNotContain("is not a recognized command", ambiguous.Message);
    }

    [Fact]
    public void Ambiguous_FirstToken_RemainderNotACommand_StillReportsAmbiguity()
    {
        var result = CallsignPrefixResolver.Resolve("N12 BLAH", Scheme, Aircraft("N1234", "N1256"));

        var ambiguous = Assert.IsType<CallsignPrefixResolver.Ambiguous>(result);
        Assert.Contains("matches multiple aircraft", ambiguous.Message);
        Assert.Contains("N1234", ambiguous.Message);
        Assert.Contains("N1256", ambiguous.Message);
    }

    [Fact]
    public void Resolved_UniqueSubstring_ReturnsResolvedWithRemainder()
    {
        var result = CallsignPrefixResolver.Resolve("N12 FH 270", Scheme, Aircraft("N1234", "SWA456"));

        var resolved = Assert.IsType<CallsignPrefixResolver.Resolved>(result);
        Assert.Equal("N1234", resolved.Aircraft.Callsign);
        Assert.Equal("FH 270", resolved.Remainder);
    }

    [Fact]
    public void Resolved_ExactMatch_ReturnsResolvedWithRemainder()
    {
        var result = CallsignPrefixResolver.Resolve("N1234 FH 270", Scheme, Aircraft("N1234", "N1256"));

        var resolved = Assert.IsType<CallsignPrefixResolver.Resolved>(result);
        Assert.Equal("N1234", resolved.Aircraft.Callsign);
        Assert.Equal("FH 270", resolved.Remainder);
    }

    [Theory]
    [InlineData("N7LJ RES CROSS 28L", "RES CROSS 28L")]
    [InlineData("n7lj res cross 28l", "res cross 28l")]
    [InlineData("N7LJ CLAND NODEL", "CLAND NODEL")]
    public void Resolved_ModifierOnlyVerb_StripsCallsign(string input, string expectedRemainder)
    {
        // Regression (S2-OAK-5): "N7LJ RES CROSS 28L" must strip the callsign. The combined
        // RES CROSS form previously failed ParseCompound (Resume was ArgMode.None), so the
        // remainder didn't parse, the callsign wasn't stripped, and the verb "N7LJ" was
        // reported as an unknown command.
        var result = CallsignPrefixResolver.Resolve(input, Scheme, Aircraft("N7LJ", "N1234"));

        var resolved = Assert.IsType<CallsignPrefixResolver.Resolved>(result);
        Assert.Equal("N7LJ", resolved.Aircraft.Callsign);
        Assert.Equal(expectedRemainder, resolved.Remainder);
    }

    [Fact]
    public void NotAPrefix_UniqueMatch_RemainderNotACommand()
    {
        // Don't steal inputs where the first token happens to match an aircraft but the
        // rest isn't a command — could be e.g. `RTIS N17` where N17 is a callsign-shaped
        // second arg of a different verb. The argument-position resolver handles those.
        var result = CallsignPrefixResolver.Resolve("N1234 BLAH", Scheme, Aircraft("N1234"));

        Assert.IsType<CallsignPrefixResolver.NotAPrefix>(result);
    }

    [Fact]
    public void NotAPrefix_FirstTokenNotCallsignShaped()
    {
        var result = CallsignPrefixResolver.Resolve("FH 270", Scheme, Aircraft("N1234"));

        Assert.IsType<CallsignPrefixResolver.NotAPrefix>(result);
    }

    [Fact]
    public void NotAPrefix_SingleTokenOnly()
    {
        // Single-token select branch in SendCommandAsync handles this case separately.
        var result = CallsignPrefixResolver.Resolve("N12", Scheme, Aircraft("N1234", "N1256"));

        Assert.IsType<CallsignPrefixResolver.NotAPrefix>(result);
    }

    [Fact]
    public void NotAPrefix_NoMatch()
    {
        // Zero-match callsign is out of scope for this fix — preserve fall-through to the
        // command parser, which will surface its own "is not a recognized command" message.
        var result = CallsignPrefixResolver.Resolve("N99 FH 270", Scheme, Aircraft("N1234"));

        Assert.IsType<CallsignPrefixResolver.NotAPrefix>(result);
    }
}
