using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// The shared chained non-compoundable verdict (<see cref="CompoundPolicy"/>) — consumed by both
/// the server's dispatch routing (RoomEngine) and the client's pre-send validation (MainViewModel),
/// so these tests pin the verdicts for both sides at once.
/// </summary>
public class CompoundPolicyTests
{
    [Theory]
    [InlineData("FH 090; PAUSE", typeof(PauseCommand))]
    [InlineData("FH 090; SPAWN", typeof(SpawnNowCommand))]
    [InlineData("CM 5000; SIMRATE 2", typeof(SimRateCommand))]
    public void ChainWithNonCompoundable_IsFound(string command, Type expectedType)
    {
        var found = CompoundPolicy.FindNonCompoundableInChain(command);
        Assert.NotNull(found);
        Assert.IsType(expectedType, found);
    }

    [Theory]
    [InlineData("FH 090; CM 5000")] // plain aviation chain
    [InlineData("CROSS 28R; DEL")] // DEL has real chain semantics (issue #311)
    [InlineData("AT 5000 APT OAK")] // DEST/APT dispatches through the queue
    [InlineData("PAUSE")] // single command, not a chain
    [InlineData("NOTE hold at gate; expect delay")] // free text the single parser accepts whole
    public void NonChains_AndChainCapableCommands_PassThrough(string command)
    {
        Assert.Null(CompoundPolicy.FindNonCompoundableInChain(command));
    }

    /// <summary>
    /// A parallel block pairing a takeoff clearance with an immediate turn (`CTO, R270`) is the mis-spelling of
    /// the departure modifier `CTO MR270`: the turn is refused on the ground, so the block leaves the aircraft
    /// rolling with only half of what was typed. Both orders are found, and every takeoff clearance in the family
    /// (CTO, CTOPP, GO) counts.
    /// </summary>
    [Theory]
    [InlineData("CTO, R270", typeof(MakeRight270Command))]
    [InlineData("R270, CTO", typeof(MakeRight270Command))]
    [InlineData("CTOPP, R270", typeof(MakeRight270Command))]
    [InlineData("GO, L360", typeof(MakeLeft360Command))]
    public void TakeoffPairedWithImmediateTurn_IsFound(string command, Type expectedType)
    {
        var found = CompoundPolicy.FindTakeoffPairedWithImmediateTurn(command);
        Assert.NotNull(found);
        Assert.IsType(expectedType, found);
    }

    [Theory]
    [InlineData("CTO; R270")] // sequential: the turn queues until airborne, which is what the RPO asked for
    [InlineData("CTO MR270")] // the departure modifier itself
    [InlineData("R270")] // single command, not a pairing
    [InlineData("CTO, SQ 1234")] // a takeoff clearance may still be paired with other verbs
    public void TakeoffWithoutAPairedImmediateTurn_PassesThrough(string command)
    {
        Assert.Null(CompoundPolicy.FindTakeoffPairedWithImmediateTurn(command));
    }
}
