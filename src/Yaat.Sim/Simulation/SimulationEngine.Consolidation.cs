using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Simulation;

// The manual-consolidation bodies (CON / CON+ / DECON) over ConsolidationState — one body for the live server, a replay
// and a bare run. The live server wraps them with its terminal echoes and the StarsConsolidation rebroadcast.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// <c>CON</c> / <c>CON+</c>: records the manual override consolidating the sending TCP into the receiving one. A full
    /// consolidation also moves the sender's whole block — the sender plus every descendant that is neither attended
    /// (<paramref name="isAttended"/>, the host's answer) nor carrying its own override — onto the receiver: owned tracks
    /// transfer and in-progress handoffs redirect. Refused for an unknown position and for an edge that would close a
    /// loop (<see cref="Simulation.ConsolidationState.Consolidate"/>), in which case nothing is written.
    /// </summary>
    public CommandResult Consolidate(ConsolidateCommand con, Func<Tcp, bool> isAttended)
    {
        if (Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var receivingTcp = TrackResolver.FindTcpByCode(scenario, con.ReceivingTcpCode);
        if (receivingTcp is null)
        {
            return new CommandResult(false, $"Unknown position: {con.ReceivingTcpCode}");
        }

        var sendingTcp = TrackResolver.FindTcpByCode(scenario, con.SendingTcpCode);
        if (sendingTcp is null)
        {
            return new CommandResult(false, $"Unknown position: {con.SendingTcpCode}");
        }

        if (!ConsolidationState.Consolidate(receivingTcp, sendingTcp, !con.Full))
        {
            return new CommandResult(false, $"Circular consolidation: {con.SendingTcpCode} → {con.ReceivingTcpCode} would create a loop");
        }

        if (!con.Full)
        {
            return new CommandResult(true, $"Basic consolidation: {con.SendingTcpCode} → {con.ReceivingTcpCode}");
        }

        var (transferred, redirected) = TransferTracksForConsolidation(scenario, sendingTcp, con.ReceivingTcpCode, isAttended);
        var message = $"Full consolidation: {con.SendingTcpCode} → {con.ReceivingTcpCode}";
        if ((transferred > 0) || (redirected > 0))
        {
            message += $" ({transferred} track(s) transferred, {redirected} handoff(s) redirected)";
        }

        return new CommandResult(true, message);
    }

    /// <summary><c>DECON</c>: removes the manual override keyed by the TCP (the sender of an earlier <c>CON</c>).</summary>
    public CommandResult Deconsolidate(DeconsolidateCommand decon)
    {
        if (Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var tcp = TrackResolver.FindTcpByCode(scenario, decon.TcpCode);
        if (tcp is null)
        {
            return new CommandResult(false, $"Unknown position: {decon.TcpCode}");
        }

        ConsolidationState.Deconsolidate(tcp);
        return new CommandResult(true, $"Deconsolidated: {decon.TcpCode}");
    }

    /// <summary>
    /// Moves the block of TCPs that folds into <paramref name="sendingTcp"/> onto the receiving position. Combining a
    /// sector takes its whole slice of airspace, subsectors included, so a track owned by an unattended descendant of
    /// the sender transfers too — matching the sender alone would strand those tracks on a position that no longer works
    /// that airspace (the ownership-vs-children split of issue #299, one layer down). Falls back to the sender alone when
    /// the scenario carries no ARTCC config or the student facility has no TCP table.
    /// </summary>
    private (int Transferred, int Redirected) TransferTracksForConsolidation(
        SimScenarioState scenario,
        Tcp sendingTcp,
        string receivingTcpCode,
        Func<Tcp, bool> isAttended
    )
    {
        var receivingOwner = TrackResolver.ResolveTcpToOwner(scenario, receivingTcpCode);
        if (receivingOwner is null)
        {
            return (0, 0);
        }

        var movedTcps =
            scenario.ArtccConfig?.GetConsolidatedDescendants(scenario.StudentPosition?.FacilityId ?? "", sendingTcp, isAttended, ConsolidationState)
            ?? [];
        var moved = movedTcps.Count > 0 ? movedTcps : [sendingTcp];
        bool MatchesMoved(TrackOwner owner) => moved.Any(t => (owner.Subset == t.Subset) && (owner.SectorId == t.SectorId));

        int transferred = 0;
        int redirected = 0;
        foreach (var ac in World.GetSnapshot())
        {
            if ((ac.Track.Owner is not null) && MatchesMoved(ac.Track.Owner))
            {
                ac.Track.Owner = receivingOwner;
                transferred++;
            }

            if ((ac.Track.HandoffPeer is not null) && MatchesMoved(ac.Track.HandoffPeer))
            {
                ac.Track.HandoffRedirectedBy = ac.Track.HandoffPeer;
                ac.Track.HandoffPeer = receivingOwner;
                redirected++;
            }
        }

        return (transferred, redirected);
    }
}
