using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Coast;

/// <summary>
/// The disconnect-coast dictionary is snapshotted scenario state: a rewind that restored an empty set would lose a
/// coast the live run had registered, and the coasts registered in the undone future would coast on. The restore
/// replaces the dictionary whole — absent means empty — and empties the pending cleared-callsign list through
/// <see cref="SimulationEngine.ResetDisconnectCoastClears"/>, so a spawn the rewind undid stops waiting to reach the
/// host. The fixture is the real ZOA configuration, where a parked OAK aircraft is an SFO ASDE-X member and an HWD
/// and OAK SAID member once the membership step has run.
/// </summary>
public class DisconnectCoastSnapshotTests
{
    private const string Callsign = "N152SP";

    /// <summary>Above OAK's ERAM floor (field + 1,500 ft) but still inside the SFO ASDE-X and HWD / OAK SAID hysteresis bands.</summary>
    private const double InsideTheSurfaceBandsFt = 2000;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DisconnectCoastSnapshotTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    /// <summary>
    /// Lifts the parked aircraft into the surface bands and above the ERAM floor, so a registration carries an ERAM
    /// facet beside the surface ones.
    /// </summary>
    private static AircraftState RegisteredCoast(SimulationEngine engine, SpineCapturingHost spine)
    {
        engine.RunSecond(spine);
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Altitude = InsideTheSurfaceBandsFt;
        ac.IsOnGround = false;
        engine.TickSurfaceMembership();
        engine.RegisterDisconnectCoast(ac);
        return ac;
    }

    [Fact]
    public void Snapshot_RoundTripsEveryCoastAndFacet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        // ERAM (no facility) beside the SFO ASDE-X facet and the HWD / OAK SAID facets, in facet order.
        RegisteredCoast(engine, new SpineCapturingHost(engine));
        AircraftDisconnectCoast live = Assert.Single(engine.Scenario!.DisconnectCoasts).Value;
        Assert.Equal(4, live.Facets.Length);
        Assert.Equal(DisconnectCoastScope.Eram, live.Facets[0].Scope);
        Assert.Equal("SFO", live.Facets[1].FacilityId);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        // Through the serializer a recording writes the snapshot with, not just the in-memory DTO: the anchor and
        // the per-facet lists are the part a plain object graph would hide.
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        if (Engine() is not { } target)
        {
            return;
        }

        target.RestoreFromSnapshot(reread);

        KeyValuePair<string, AircraftDisconnectCoast> entry = Assert.Single(target.Scenario!.DisconnectCoasts);
        Assert.Equal(Callsign, entry.Key);
        Assert.Equal(live.Anchor, entry.Value.Anchor);
        Assert.Equal(live.AnchorTrackDeg, entry.Value.AnchorTrackDeg);
        Assert.Equal(live.AnchorGroundSpeed, entry.Value.AnchorGroundSpeed);
        Assert.Equal(live.CoastStartSimSeconds, entry.Value.CoastStartSimSeconds);
        Assert.Equal<DisconnectCoastFacet>(live.Facets, entry.Value.Facets);
    }

    [Fact]
    public void Restore_DropsACoastRegisteredAfterTheSnapshot()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Null(snapshot.Scenario.DisconnectCoasts);

        RegisteredCoast(engine, spine);
        Assert.NotEmpty(engine.Scenario!.DisconnectCoasts);

        // The rewind undoes the registration: the coast belongs to the future the target second has not reached.
        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.Scenario!.DisconnectCoasts);
    }

    [Fact]
    public void Restore_EmptiesThePendingClears()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        AircraftState ac = RegisteredCoast(engine, spine);
        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        // A same-callsign spawn clears the entry and queues the callsign for the next drain, which has not run yet.
        engine.DeleteAircraft(Callsign, "DEL");
        engine.World.AddAircraft(ac);
        engine.AfterAircraftSpawned(ac);
        Assert.Empty(engine.Scenario!.DisconnectCoasts);
        Assert.Empty(spine.DisconnectCoastClears);

        // Restoring the snapshot before the drain must take the pending clear with it: the spawn the rewind undid
        // never reaches the host.
        engine.RestoreFromSnapshot(snapshot);
        Assert.Single(engine.Scenario!.DisconnectCoasts);

        engine.RunSecond(spine);

        Assert.Empty(spine.DisconnectCoastClears);
    }

    [Fact]
    public void Restore_OfASnapshotWithoutTheField_RestoresEmpty()
    {
        if ((Engine() is not { } engine) || (Engine() is not { } blank))
        {
            return;
        }

        RegisteredCoast(engine, new SpineCapturingHost(engine));
        Assert.NotEmpty(engine.Scenario!.DisconnectCoasts);

        // A snapshot from a run that never held a coast: the field is absent, which is what an empty dictionary
        // restores to even when the engine being restored onto was holding one.
        StateSnapshotDto snapshot = blank.CaptureSnapshot();
        Assert.Null(snapshot.Scenario.DisconnectCoasts);

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.Scenario!.DisconnectCoasts);
    }
}
