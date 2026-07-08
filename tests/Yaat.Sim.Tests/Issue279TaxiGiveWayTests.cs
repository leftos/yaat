using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #279: a controller typed <c>TAXI A A1 1R GIVEWAY KLM605</c> to taxi a route and
/// give way to another aircraft. The server swallowed GIVEWAY/KLM605 as taxiway names; after the
/// user added a comma (<c>taxi A A1 1R, GIVEWAY KLM605</c>) the client wrongly reported
/// <c>Unrecognized command "taxi"</c> and never sent the command.
///
/// Fix: <c>GIVEWAY &lt;callsign&gt;</c> is a valid standalone command. A trailing standalone give-way
/// clause on a TAXI command is split into a parallel command (<c>TAXI &lt;route&gt;, GIVEWAY &lt;cs&gt;</c>),
/// which dispatches correctly because the two commands share one block and apply in source order.
/// The parallel (comma) form must NOT be promoted to a sequential (semicolon) block, which never
/// fires behind an active ground phase.
/// </summary>
public class Issue279TaxiGiveWayTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    public Issue279TaxiGiveWayTests() => TestVnasData.EnsureInitialized();

    // ---- Client canonical (CommandSchemeParser) --------------------------------------------

    [Fact]
    public void CommaForm_StaysParallel_NotPromotedToSemicolon()
    {
        // The reported repro. Previously produced compound == null and the misleading
        // "Unrecognized command taxi" fallback.
        var result = CommandSchemeParser.ParseCompound("taxi A A1 1R, GIVEWAY KLM605", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("TAXI A A1 1R, GIVEWAY KLM605", result.CanonicalString);
    }

    [Fact]
    public void NoCommaForm_SplitsTrailingGiveWayIntoParallelCommand()
    {
        // The user's first attempt — no comma. The trailing give-way clause is split off.
        var result = CommandSchemeParser.ParseCompound("TAXI A A1 1R GIVEWAY KLM605", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("TAXI A A1 1R, GIVEWAY KLM605", result.CanonicalString);
    }

    [Fact]
    public void ConditionForm_StillPromotedToSequentialBlock()
    {
        // GIVEWAY as a *condition* prefix (callsign + ground verb) keeps its sequential semantics.
        var result = CommandSchemeParser.ParseCompound("TAXI S, GIVEWAY N152SP TAXI C", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("TAXI S; GIVEWAY N152SP TAXI C", result.CanonicalString);
    }

    [Fact]
    public void StandaloneGiveWay_ParsesUnchanged()
    {
        var result = CommandSchemeParser.ParseCompound("GIVEWAY KLM605", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("GIVEWAY KLM605", result.CanonicalString);
    }

    [Fact]
    public void TaxiwayNamedLikeGiveWayAlias_IsNotSplit()
    {
        // "GW" is both a give-way alias and a plausible taxiway label; "B" is not a callsign, so
        // "TAXI A GW B" must stay a plain taxi route, not split into a give-way command.
        var result = CommandSchemeParser.ParseCompound("TAXI A GW B", Scheme, out var failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal("TAXI A GW B", result.CanonicalString);
    }

    // ---- Robustness: never blame the leading verb (issue #279 core symptom) ----------------

    // A condition prefix with its own argument but no following command (LV 5000 <what?>) hits the
    // block-parser branches that previously returned null without setting a failure — the case that
    // made CommandErrorFormatter blame the leading verb (issue #279's "Unrecognized command taxi").
    [Theory]
    [InlineData("CM 100; LV 5000", "LV")]
    [InlineData("CM 100; AT 5000", "AT")]
    [InlineData("CM 100; ATFN 5", "ATFN")]
    public void MalformedTrailingCondition_BlamesTheConditionVerb_NotTheLeadingVerb(string input, string expectedVerb)
    {
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);

        Assert.Null(result);
        Assert.NotNull(failure);
        Assert.Equal(expectedVerb, failure.Verb);
    }

    // ---- Server parse (CommandParser) ------------------------------------------------------

    [Fact]
    public void ServerParse_NoCommaForm_YieldsTaxiWithDestRunwayPlusGiveWay()
    {
        var result = CommandParser.ParseCompound("TAXI A A1 1R GIVEWAY KLM605");

        Assert.True(result.IsSuccess, result.Reason);
        var block = Assert.Single(result.Value!.Blocks);
        Assert.Null(block.Condition);
        Assert.Collection(
            block.Commands,
            c =>
            {
                var taxi = Assert.IsType<TaxiCommand>(c);
                Assert.Equal("1R", taxi.DestinationRunway);
                Assert.Equal(["A", "A1"], taxi.Path);
            },
            c =>
            {
                var giveWay = Assert.IsType<GiveWayCommand>(c);
                Assert.Equal("KLM605", giveWay.TargetCallsign);
            }
        );
    }
}
