using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CallsignArgumentResolverTests
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
    public void Follow_UniqueSubstring_Rewrites()
    {
        var result = CallsignArgumentResolver.TryRewrite("FOLLOW AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("FOLLOW AAL1234", result.Text);
    }

    [Fact]
    public void Follow_ExactMatch_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("FOLLOW AAL1234", Scheme, Aircraft("AAL1234", "SWA456"));

        Assert.Null(result.Error);
        Assert.Equal("FOLLOW AAL1234", result.Text);
    }

    [Fact]
    public void Followg_UniqueSubstring_Rewrites()
    {
        var result = CallsignArgumentResolver.TryRewrite("FOLLOWG SWA", Scheme, Aircraft("SWA456", "AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("FOLLOWG SWA456", result.Text);
    }

    [Fact]
    public void Rtis_NoArg_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTIS", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("RTIS", result.Text);
    }

    [Fact]
    public void Rtis_UniqueSubstring_Rewrites()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTIS AAL", Scheme, Aircraft("AAL1234", "SWA456"));

        Assert.Null(result.Error);
        Assert.Equal("RTIS AAL1234", result.Text);
    }

    [Fact]
    public void Rtisf_UniqueSubstring_Rewrites()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTISF SWA", Scheme, Aircraft("AAL1234", "SWA456"));

        Assert.Null(result.Error);
        Assert.Equal("RTISF SWA456", result.Text);
    }

    [Fact]
    public void Rtis_Ambiguous_ReturnsError()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTIS A", Scheme, Aircraft("AAL1234", "AWE5"));

        Assert.Null(result.Text);
        Assert.NotNull(result.Error);
        Assert.Contains("\"A\"", result.Error);
        Assert.Contains("AAL1234", result.Error);
        Assert.Contains("AWE5", result.Error);
    }

    [Fact]
    public void Rtis_NoMatch_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTIS ZZZ", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("RTIS ZZZ", result.Text);
    }

    [Fact]
    public void Cva_FollowSuffix_Rewrites()
    {
        var result = CallsignArgumentResolver.TryRewrite("CVA 28R LEFT FOLLOW AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("CVA 28R LEFT FOLLOW AAL1234", result.Text);
    }

    [Fact]
    public void Cva_WithoutFollow_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("CVA 28R LEFT", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("CVA 28R LEFT", result.Text);
    }

    [Fact]
    public void Cva_FollowSuffix_Ambiguous_ReturnsError()
    {
        var result = CallsignArgumentResolver.TryRewrite("CVA 28R FOLLOW A", Scheme, Aircraft("AAL1234", "AWE5"));

        Assert.Null(result.Text);
        Assert.NotNull(result.Error);
        Assert.Contains("\"A\"", result.Error);
    }

    [Fact]
    public void CompoundBlocks_EachBlockResolvedIndependently()
    {
        var result = CallsignArgumentResolver.TryRewrite("FH 270 ; RTIS AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("FH 270 ; RTIS AAL1234", result.Text);
    }

    [Fact]
    public void CompoundBlocks_CommaSeparated_EachBlockResolvedIndependently()
    {
        var result = CallsignArgumentResolver.TryRewrite("RTIS AAL , FOLLOW SWA", Scheme, Aircraft("AAL1234", "SWA456"));

        Assert.Null(result.Error);
        Assert.Equal("RTIS AAL1234 , FOLLOW SWA456", result.Text);
    }

    [Fact]
    public void LvConditionPrefix_PreservedVerbatim_FollowCallsignResolved()
    {
        var result = CallsignArgumentResolver.TryRewrite("LV 050 FOLLOW AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("LV 050 FOLLOW AAL1234", result.Text);
    }

    [Fact]
    public void AcceptHandoff_CallsignArg_LeavesUntouched()
    {
        // ACCEPT has a callsign-typed argument but is explicitly out of scope for partial matching.
        // The resolver's allowlist excludes it — the server decides whether the pending handoff
        // matches the typed callsign exactly.
        var result = CallsignArgumentResolver.TryRewrite("ACCEPT AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("ACCEPT AAL", result.Text);
    }

    [Fact]
    public void UnknownVerb_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("NOTAVERB AAL", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("NOTAVERB AAL", result.Text);
    }

    [Fact]
    public void NonCallsignCommand_LeavesUntouched()
    {
        var result = CallsignArgumentResolver.TryRewrite("FH 270", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("FH 270", result.Text);
    }

    [Fact]
    public void EmptyInput_ReturnsUnchanged()
    {
        var result = CallsignArgumentResolver.TryRewrite("", Scheme, Aircraft("AAL1234"));

        Assert.Null(result.Error);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void EmptyAircraftList_ReturnsUnchanged()
    {
        var result = CallsignArgumentResolver.TryRewrite("FOLLOW AAL", Scheme, []);

        Assert.Null(result.Error);
        Assert.Equal("FOLLOW AAL", result.Text);
    }
}
