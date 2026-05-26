using Xunit;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

/// <summary>
/// Tests for the trace produced by <see cref="PhraseologyMapper.MapWithTrace"/>. The trace is the
/// source of truth for the Speech Debug window's per-stage display, so it must accurately reflect
/// each pipeline step (normalization, condition extraction, rule matches, failure reasons).
/// </summary>
public class PhraseologyMapperTraceTests
{
    [Fact]
    public void Successful_Match_Populates_Output_And_Matched_Rule()
    {
        var (result, trace) = PhraseologyMapper.MapWithTrace("fly heading two seven zero", MapContext.Empty);
        Assert.NotNull(result);
        Assert.Equal("FH 270", result!.CanonicalCommand);

        Assert.Null(trace.FailureReason);
        Assert.Equal("FH 270", trace.OutputCanonical);
        Assert.Contains("fly heading 270", trace.NormalizedTokens);
        Assert.NotEmpty(trace.MatchedRulePatterns);
        Assert.Contains(trace.MatchedRulePatterns, p => p.Contains("heading"));
    }

    [Fact]
    public void Condition_Prefix_Is_Captured_In_Trace()
    {
        var ctx = new MapContext(ActiveCallsigns: [], ProgrammedFixes: ["CEPIN"]);
        var (result, trace) = PhraseologyMapper.MapWithTrace("at cepin climb and maintain five thousand", ctx);
        Assert.NotNull(result);
        Assert.Equal("AT CEPIN CM 5000", result!.CanonicalCommand);
        Assert.Equal("AT CEPIN", trace.ConditionPrefix);
    }

    [Fact]
    public void Empty_Transcript_Returns_Trace_With_FailureReason()
    {
        var (result, trace) = PhraseologyMapper.MapWithTrace("   ", MapContext.Empty);
        Assert.Null(result);
        Assert.NotNull(trace.FailureReason);
    }

    [Fact]
    public void Unmatched_Transcript_Returns_Trace_With_NoRuleMatched_Reason()
    {
        var (result, trace) = PhraseologyMapper.MapWithTrace("the weather is nice today", MapContext.Empty);
        Assert.Null(result);
        Assert.NotNull(trace.FailureReason);
        Assert.Empty(trace.MatchedRulePatterns);
    }

    [Fact]
    public void Map_Without_Trace_Returns_Same_Result_As_MapWithTrace()
    {
        // The simple Map() overload is just a thin discarding wrapper; success/failure semantics
        // must stay byte-identical to the trace-collecting variant.
        var fixtures = new[] { "fly heading two seven zero", "climb and maintain five thousand", "the weather is nice today", "" };
        foreach (var t in fixtures)
        {
            var direct = PhraseologyMapper.Map(t, MapContext.Empty);
            var (withTrace, _) = PhraseologyMapper.MapWithTrace(t, MapContext.Empty);
            Assert.Equal(direct?.CanonicalCommand, withTrace?.CanonicalCommand);
            Assert.Equal(direct?.Callsign, withTrace?.Callsign);
            Assert.Equal(direct?.MatchedRuleCount, withTrace?.MatchedRuleCount);
        }
    }
}
