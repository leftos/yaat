using Xunit;
using Yaat.Sim.Simulation.Oracle;

namespace Yaat.Sim.Tests.Simulation.Oracle;

/// <summary>
/// What the oracle's verdict <em>says</em>, not what it computes. The comparison logic is symmetric — a new
/// divergence and a vanished one both fail — but the two directions mean opposite things, and the failure message is
/// the only place that distinction reaches the person reading it. A message that framed a shrinking diff as progress
/// and offered the re-baseline command would be defeated most reliably by the regressions it exists to catch, so the
/// wording is behaviour and is pinned here.
/// </summary>
public class TickOracleBaselineTests
{
    private const string RebaselineVariable = "YAAT_ORACLE_REBASELINE";

    private static TickOracleBaseline BaselineOf(params string[] paths) =>
        new() { Entries = paths.Select(path => new TickOracleBaselineEntry { Path = path, FirstSecond = 5 }).ToList() };

    private static DivergenceAccumulator SweepOf(params string[] paths)
    {
        var accumulator = new DivergenceAccumulator("live", "replay");
        accumulator.Add(5, paths.Select(path => new SnapshotDivergence(path, "1", "2")).ToList());
        return accumulator;
    }

    [Fact]
    public void MatchingSweep_IsClean()
    {
        var comparison = BaselineOf("Aircraft[*].Track.Owner.SectorId").CompareTo(SweepOf("Aircraft[SWA1].Track.Owner.SectorId"), _ => false);

        Assert.True(comparison.IsClean);
        Assert.Empty(comparison.Added);
        Assert.Empty(comparison.Removed);
    }

    [Fact]
    public void NewDivergence_IsReportedAndOffersTheRebaselineCommand()
    {
        var comparison = BaselineOf().CompareTo(SweepOf("Aircraft[SWA1].Track.HandoffPeer"), _ => false);

        Assert.False(comparison.IsClean);
        string message = comparison.Describe("live vs replay", RebaselineVariable);

        Assert.Contains("live vs replay", message, StringComparison.Ordinal);
        Assert.Contains("NEW divergence path(s)", message, StringComparison.Ordinal);
        Assert.Contains("+ Aircraft[*].Track.HandoffPeer", message, StringComparison.Ordinal);
        Assert.Contains(RebaselineVariable, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the wording exists for. During behaviour-preserving work the likeliest cause of a divergence
    /// disappearing is that a path lost the step that produced it — so this branch must read as a regression and
    /// must not hand the developer the command that banks it.
    /// </summary>
    [Fact]
    public void VanishedDivergence_ReadsAsARegressionAndWithholdsTheRebaselineCommand()
    {
        var comparison = BaselineOf("Aircraft[*].Track.HandoffPeer").CompareTo(SweepOf(), _ => false);

        Assert.False(comparison.IsClean);
        string message = comparison.Describe("live vs replay", RebaselineVariable);

        Assert.Contains("GONE", message, StringComparison.Ordinal);
        Assert.Contains("regression", message, StringComparison.Ordinal);
        Assert.Contains("- Aircraft[*].Track.HandoffPeer", message, StringComparison.Ordinal);
        Assert.DoesNotContain(RebaselineVariable, message, StringComparison.Ordinal);
    }

    /// <summary>A sweep that both gained and lost a path still needs the command, for the gained half.</summary>
    [Fact]
    public void GainedAndLostTogether_ReportsBothAndKeepsTheCommand()
    {
        var comparison = BaselineOf("Scenario.DelayedHandoffQueue[*]").CompareTo(SweepOf("Aircraft[SWA1].Track.HandoffPeer"), _ => false);

        string message = comparison.Describe("live vs reconstruct", RebaselineVariable);

        Assert.Contains("+ Aircraft[*].Track.HandoffPeer", message, StringComparison.Ordinal);
        Assert.Contains("- Scenario.DelayedHandoffQueue[*]", message, StringComparison.Ordinal);
        Assert.Contains(RebaselineVariable, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExemptPath_IsDroppedFromBothSides()
    {
        var comparison = BaselineOf().CompareTo(SweepOf("Aircraft[SWA1].Wallclock"), path => path == "Aircraft[*].Wallclock");

        Assert.True(comparison.IsClean);
    }

    /// <summary>Example lines carry the pair's own side names, so a report says which run kind held which value.</summary>
    [Fact]
    public void ExampleLines_AreLabelledWithThePairsSides()
    {
        var accumulator = new DivergenceAccumulator("live", "reconstruct");
        accumulator.Add(7, [new SnapshotDivergence("Aircraft[SWA1].Altitude", "3000", "3100")]);

        string example = Assert.Single(Assert.Single(accumulator.Entries).Examples);

        Assert.Equal("t=7 Aircraft[SWA1].Altitude: live=3000 reconstruct=3100", example);
    }
}
