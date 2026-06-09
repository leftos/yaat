using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Bug "manual handoff ACCEPT skips the white-FDB datablock stage": when a track is handed off and a
/// position accepts via the <c>ACCEPT</c> command, the previous owner's STARS datablock must stay a
/// white FDB (CRC's <c>WasPreviouslyOwned</c>) until that controller slews it — matching the
/// <c>[AutoAccept]</c> timer path. Before the fix the manual-accept path left <c>WasPreviouslyOwned</c>
/// unset, so the previous owner's block dropped straight to a green PDB.
///
/// Reproduction: bundle "S2-OAK-4 | VFR Transitions/Radar Concepts", N569SX — student OAK_G_APP (3G)
/// hands off to OAK_TWR (3O), RPO accepts via <c>AS 3O ACCEPT</c>; the student (previous owner) should
/// then see a white FDB.
/// </summary>
public class HandoffAcceptDatablockColorTests
{
    private static readonly Tcp StudentTcp = new(3, "G", "tcp-3g", null);
    private static readonly TrackOwner StudentPosition = TrackOwner.CreateStars("OAK_G_APP", "NCT", 3, "G");
    private static readonly TrackOwner AcceptingPosition = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");

    private static SimScenarioState Scenario() =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = 0,
            StudentPosition = StudentPosition,
            StudentTcp = StudentTcp,
        };

    private static AircraftState Aircraft()
    {
        var ac = new AircraftState
        {
            Callsign = "N569SX",
            AircraftType = "C172",
            Position = new LatLon(37.66, -122.0),
            TrueHeading = new TrueHeading(292),
            Altitude = 3000,
        };
        ac.Track.Owner = StudentPosition;
        ac.Track.HandoffPeer = AcceptingPosition;
        return ac;
    }

    [Fact]
    public void ManualAccept_SetsWasPreviouslyOwned_OnPreviousOwnerTcp()
    {
        var ac = Aircraft();
        var scenario = Scenario();

        var result = TrackEngine.HandleAccept(ac, scenario);

        Assert.True(result.Success, result.Message);
        Assert.Equal(AcceptingPosition, ac.Track.Owner);
        Assert.True(
            ac.Stars.SharedState.TryGetValue(StudentTcp.Id, out var shared) && shared.WasPreviouslyOwned,
            "Previous owner's SharedState should have WasPreviouslyOwned set after a manual accept"
        );
    }

    [Fact]
    public void ManualAccept_PreviousOwnerSeesWhiteFullDatablock()
    {
        var ac = Aircraft();
        var scenario = Scenario();

        TrackEngine.HandleAccept(ac, scenario);

        var view = StarsDatablockClassifier.Classify(ac, StudentTcp, StudentPosition);

        Assert.Equal(StarsDatablockColor.Owned, view.Color); // white
        Assert.Equal(StarsDatablockLevel.Full, view.Level); // FDB
    }
}
