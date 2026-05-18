using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Tests for the RES command and its CROSS overload.
///
/// Forms:
///   <c>RES</c>                       — resume taxi (no pre-clearances)
///   <c>RES CROSS 28R</c>             — resume taxi + pre-clear an upcoming 28R crossing
///   <c>RES CROSS 28R 28L</c>         — resume taxi + pre-clear multiple crossings (unordered set)
///
/// Each listed runway must match an upcoming RunwayCrossing hold-short on the
/// aircraft's taxi route; otherwise the whole command fails (mirrors bare CROSS).
/// </summary>
public sealed class ResumeCommandParseTests
{
    [Fact]
    public void BareRes_ParsesAsEmptyCrossList()
    {
        var result = CommandParser.Parse("RES");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Empty(resume.CrossRunways);
    }

    [Fact]
    public void ResumeAlias_SameShape()
    {
        var result = CommandParser.Parse("RESUME");

        Assert.IsType<ResumeCommand>(result.Value);
        Assert.Empty(((ResumeCommand)result.Value!).CrossRunways);
    }

    [Fact]
    public void ResCross_SingleRunway()
    {
        var result = CommandParser.Parse("RES CROSS 28R");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Equal(new[] { "28R" }, resume.CrossRunways);
    }

    [Fact]
    public void ResCross_MultipleRunways_PreservesOrder()
    {
        var result = CommandParser.Parse("RES CROSS 28R 28L");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Equal(new[] { "28R", "28L" }, resume.CrossRunways);
    }

    [Fact]
    public void ResCross_LowercaseRunwaysAreNormalized()
    {
        var result = CommandParser.Parse("RES cross 28r 10l");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Equal(new[] { "28R", "10L" }, resume.CrossRunways);
    }

    [Fact]
    public void ResCross_NoRunways_Fails()
    {
        var result = CommandParser.Parse("RES CROSS");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Reason);
        Assert.Contains("CROSS", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    // --- HS modifier ---------------------------------------------------------

    [Fact]
    public void ResHs_SingleTarget()
    {
        var result = CommandParser.Parse("RES HS 20");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Empty(resume.CrossRunways);
        Assert.Equal(new[] { "20" }, resume.HoldShorts);
    }

    [Fact]
    public void ResHs_TaxiwayTarget()
    {
        // HS accepts taxiways too, mirroring TAXI's HS modifier
        var result = CommandParser.Parse("RES HS B");

        Assert.IsType<ResumeCommand>(result.Value);
        Assert.Equal(new[] { "B" }, ((ResumeCommand)result.Value!).HoldShorts);
    }

    [Fact]
    public void ResCrossThenHs_CombinesBoth()
    {
        var result = CommandParser.Parse("RES CROSS 28R 28L HS 20");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Equal(new[] { "28R", "28L" }, resume.CrossRunways);
        Assert.Equal(new[] { "20" }, resume.HoldShorts);
    }

    [Fact]
    public void ResHsThenCross_OrderIndependent()
    {
        var result = CommandParser.Parse("RES HS 20 CROSS 28R");

        Assert.IsType<ResumeCommand>(result.Value);
        var resume = (ResumeCommand)result.Value!;
        Assert.Equal(new[] { "28R" }, resume.CrossRunways);
        Assert.Equal(new[] { "20" }, resume.HoldShorts);
    }

    [Fact]
    public void ResHs_NoTarget_Fails()
    {
        var result = CommandParser.Parse("RES HS");

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Reason);
        Assert.Contains("HS", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Res_UnknownModifier_Fails()
    {
        var result = CommandParser.Parse("RES FOO 28R");

        Assert.False(result.IsSuccess);
    }
}
