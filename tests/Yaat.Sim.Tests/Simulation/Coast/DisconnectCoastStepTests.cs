using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Coast;

/// <summary>
/// The disconnect-coast lifecycle on the bare engine: every Sim removal body registers what the removed track coasts
/// on (ERAM when the en-route radar still saw it, one facet per ASDE-X and SAAB SAID display it was a member of),
/// <see cref="SimulationEngine.TickDisconnectCoastExpiry"/> expires each facet on sim time, and a spawn under a coasting
/// callsign clears its entry. The fixture is the real ZOA configuration, where an aircraft parked at OAK is an SFO
/// ASDE-X member and an HWD and OAK SAID member once the membership step has run.
/// </summary>
public class DisconnectCoastStepTests
{
    private const string Callsign = "N152SP";

    /// <summary>Clear of every ZOA surface display's ceiling and hysteresis band, and above OAK's ERAM floor.</summary>
    private const double WellAboveEveryCeilingFt = 5000;

    /// <summary>Above OAK's ERAM floor (field + 1,500 ft) but still inside the SFO ASDE-X and HWD / OAK SAID hysteresis bands.</summary>
    private const double InsideTheSurfaceBandsFt = 2000;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DisconnectCoastStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    /// <summary>A second of ticks, so the parked aircraft has its SFO ASDE-X and OAK SAID membership.</summary>
    private static AircraftState AfterOneSecond(SimulationEngine engine, SpineCapturingHost spine)
    {
        engine.RunSecond(spine);
        return engine.World.GetSnapshot()[0];
    }

    private static void Lift(AircraftState ac, double altitudeFt)
    {
        ac.Altitude = altitudeFt;
        ac.IsOnGround = false;
    }

    private static AircraftDisconnectCoast EntryFor(SimulationEngine engine, string callsign) =>
        Assert.Contains(callsign, (IReadOnlyDictionary<string, AircraftDisconnectCoast>)engine.Scenario!.DisconnectCoasts);

    [Fact]
    public void DeletingAParkedAircraft_RegistersItsSurfaceFacets()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        double now = engine.Scenario!.ElapsedSeconds;
        LatLon lastPosition = ac.Position;

        engine.DeleteAircraft(Callsign, "DEL");

        AircraftDisconnectCoast coast = Assert.Single(engine.Scenario!.DisconnectCoasts).Value;
        Assert.Equal(lastPosition, coast.Anchor);
        Assert.Equal(ac.TrueTrack.Degrees, coast.AnchorTrackDeg);
        Assert.Equal(ac.GroundSpeed, coast.AnchorGroundSpeed);
        Assert.Equal(now, coast.CoastStartSimSeconds);
        Assert.Collection(
            coast.Facets,
            asdex =>
            {
                Assert.Equal(DisconnectCoastScope.Asdex, asdex.Scope);
                Assert.Equal("SFO", asdex.FacilityId);
                Assert.Equal(now + SimScenarioState.SurfaceDisconnectCoastSeconds, asdex.DeadlineSimSeconds);
            },
            hwd =>
            {
                Assert.Equal(DisconnectCoastScope.Said, hwd.Scope);
                Assert.Equal("HWD", hwd.FacilityId);
                Assert.Equal(now + SimScenarioState.SurfaceDisconnectCoastSeconds, hwd.DeadlineSimSeconds);
            },
            oak =>
            {
                Assert.Equal(DisconnectCoastScope.Said, oak.Scope);
                Assert.Equal("OAK", oak.FacilityId);
                Assert.Equal(now + SimScenarioState.SurfaceDisconnectCoastSeconds, oak.DeadlineSimSeconds);
            }
        );
        Assert.Equal(45, SimScenarioState.SurfaceDisconnectCoastSeconds);
    }

    [Fact]
    public void ASurfaceFacetAtTheDestination_IsADrop()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        ac.FlightPlan.Destination = "OAK";

        engine.DeleteAircraft(Callsign, "DEL");

        AircraftDisconnectCoast coast = EntryFor(engine, Callsign);
        Assert.True(Assert.Single(coast.Facets, f => f.FacilityId == "OAK").IsDrop);
        Assert.False(Assert.Single(coast.Facets, f => f.FacilityId == "HWD").IsDrop);
        Assert.False(Assert.Single(coast.Facets, f => f.Scope == DisconnectCoastScope.Asdex).IsDrop);
    }

    [Fact]
    public void AnAirborneAircraftAboveTheEramFloor_GetsAnEramFacet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        Lift(ac, WellAboveEveryCeilingFt);
        engine.TickSurfaceMembership();
        Assert.Empty(ac.Stars.VisibleAsdexAirports);
        Assert.Empty(ac.Stars.VisibleSaidAirports);
        double now = engine.Scenario!.ElapsedSeconds;

        engine.DeleteAircraft(Callsign, "DEL");

        DisconnectCoastFacet eram = Assert.Single(EntryFor(engine, Callsign).Facets);
        Assert.Equal(DisconnectCoastScope.Eram, eram.Scope);
        Assert.Null(eram.FacilityId);
        Assert.False(eram.IsDrop);
        Assert.Equal(now + 24, eram.DeadlineSimSeconds);
        Assert.Equal(24, SimScenarioState.EramDisconnectCoastSeconds);
    }

    [Fact]
    public void AFrozenEramTrack_GetsNoEramFacet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        Lift(ac, InsideTheSurfaceBandsFt);
        ac.Eram.IsFrozen = true;

        engine.DeleteAircraft(Callsign, "DEL");

        AircraftDisconnectCoast coast = EntryFor(engine, Callsign);
        Assert.DoesNotContain(coast.Facets, f => f.Scope == DisconnectCoastScope.Eram);
        Assert.Equal(3, coast.Facets.Length);
    }

    /// <summary>
    /// Lifts the parked aircraft (already a surface member) to <paramref name="aglFt"/> above the field elevation the
    /// ERAM check resolves for it, deletes it, and reports whether its entry carries an ERAM facet.
    /// </summary>
    private static bool DeletedAtAglGetsAnEramFacet(SimulationEngine engine, double aglFt)
    {
        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        Lift(ac, FieldElevationResolver.Resolve(ac, NavigationDatabase.Instance) + aglFt);

        engine.DeleteAircraft(Callsign, "DEL");

        return EntryFor(engine, Callsign).Facets.Any(f => f.Scope == DisconnectCoastScope.Eram);
    }

    [Fact]
    public void JustBelowTheEramFloor_GetsNoEramFacet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.False(DeletedAtAglGetsAnEramFacet(engine, 1499));
    }

    [Fact]
    public void JustAboveTheEramFloor_GetsAnEramFacet()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.True(DeletedAtAglGetsAnEramFacet(engine, 1501));
    }

    [Fact]
    public void AnAircraftWithNoFacet_RegistersNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        // Before any tick the parked aircraft is a member of no surface display, and it is on the ground.
        engine.DeleteAircraft(Callsign, "DEL");
        Assert.Empty(engine.Scenario!.DisconnectCoasts);

        if (Engine() is not { } second)
        {
            return;
        }

        // A standing entry for the callsign goes when a registration finds nothing to coast on.
        AircraftState ac = AfterOneSecond(second, new SpineCapturingHost(second));
        second.RegisterDisconnectCoast(ac);
        Assert.Single(second.Scenario!.DisconnectCoasts);
        ac.Stars.VisibleAsdexAirports = ac.Stars.VisibleAsdexAirports.Clear();
        ac.Stars.VisibleSaidAirports = ac.Stars.VisibleSaidAirports.Clear();

        second.DeleteAircraft(Callsign, "DEL");

        Assert.Empty(second.Scenario!.DisconnectCoasts);
    }

    [Fact]
    public void ASecondRegistration_ReplacesTheFirst()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AfterOneSecond(engine, new SpineCapturingHost(engine));
        engine.RegisterDisconnectCoast(ac);
        Assert.Equal(3, EntryFor(engine, Callsign).Facets.Length);

        Lift(ac, WellAboveEveryCeilingFt);
        engine.TickSurfaceMembership();
        engine.RegisterDisconnectCoast(ac);

        Assert.Single(engine.Scenario!.DisconnectCoasts);
        Assert.Equal(DisconnectCoastScope.Eram, Assert.Single(EntryFor(engine, Callsign).Facets).Scope);
    }

    [Fact]
    public void EachRemovalBody_Registers()
    {
        if ((Engine() is not { } deleted) || (Engine() is not { } shadowDropped) || (Engine() is not { } autoDeleted))
        {
            return;
        }

        AfterOneSecond(deleted, new SpineCapturingHost(deleted));
        deleted.DeleteAircraft(Callsign, "DEL");
        EntryFor(deleted, Callsign);

        AircraftState shadow = AfterOneSecond(shadowDropped, new SpineCapturingHost(shadowDropped));
        shadow.LiveTraffic = new AircraftLiveTraffic { Source = LiveTrafficSource.Asdex };
        Assert.True(shadowDropped.RemoveLiveTraffic(Callsign, LiveTrafficRemovalReason.Dropped));
        EntryFor(shadowDropped, Callsign);

        AircraftState pending = AfterOneSecond(autoDeleted, new SpineCapturingHost(autoDeleted));
        pending.Ground.PendingAutoDelete = true;
        Assert.Single(autoDeleted.TickAutoDelete());
        EntryFor(autoDeleted, Callsign);
    }

    [Fact]
    public void FacetsExpireOnSimTime_AndTheEntryGoesWithTheLast()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        AircraftState ac = AfterOneSecond(engine, spine);
        Lift(ac, InsideTheSurfaceBandsFt);
        double start = engine.Scenario!.ElapsedSeconds;
        engine.DeleteAircraft(Callsign, "DEL");
        Assert.Equal(4, EntryFor(engine, Callsign).Facets.Length);

        var firedAt = new List<double>();
        void RunSeconds(int count)
        {
            for (int second = 0; second < count; second++)
            {
                int before = spine.DisconnectCoastExpiries.Count;
                engine.RunSecond(spine);
                if (spine.DisconnectCoastExpiries.Count > before)
                {
                    firedAt.Add(engine.Scenario!.ElapsedSeconds);
                }
            }
        }

        // Between the two deadlines: the ERAM facet is gone, the surface facets still stand.
        RunSeconds(30);
        Assert.Equal(start + 30, engine.Scenario!.ElapsedSeconds);
        Assert.Equal([start + 24], firedAt);
        Assert.Equal(3, EntryFor(engine, Callsign).Facets.Length);

        RunSeconds(20);
        Assert.Equal([start + 24, start + 45], firedAt);

        ExpiredDisconnectCoastFacet eram = Assert.Single(spine.DisconnectCoastExpiries[0]);
        Assert.Equal(Callsign, eram.Callsign);
        Assert.Equal(DisconnectCoastScope.Eram, eram.Facet.Scope);

        Assert.Collection(
            spine.DisconnectCoastExpiries[1],
            asdex => Assert.Equal((Callsign, DisconnectCoastScope.Asdex, "SFO"), (asdex.Callsign, asdex.Facet.Scope, asdex.Facet.FacilityId)),
            hwd => Assert.Equal((Callsign, DisconnectCoastScope.Said, "HWD"), (hwd.Callsign, hwd.Facet.Scope, hwd.Facet.FacilityId)),
            oak => Assert.Equal((Callsign, DisconnectCoastScope.Said, "OAK"), (oak.Callsign, oak.Facet.Scope, oak.Facet.FacilityId))
        );
        Assert.Empty(engine.Scenario!.DisconnectCoasts);
    }

    [Fact]
    public void ARespawnUnderACoastingCallsign_ClearsItsEntry()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        AircraftState ac = AfterOneSecond(engine, spine);
        engine.DeleteAircraft(Callsign, "DEL");
        EntryFor(engine, Callsign);

        engine.World.AddAircraft(ac);
        engine.AfterAircraftSpawned(ac);

        Assert.Empty(engine.Scenario!.DisconnectCoasts);
        Assert.Empty(spine.DisconnectCoastClears);

        engine.RunSecond(spine);

        Assert.Equal([Callsign], Assert.Single(spine.DisconnectCoastClears));
    }

    [Fact]
    public void NoScenario_ExpiryIsANoOp()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        AircraftState ac = AfterOneSecond(engine, spine);
        engine.RegisterDisconnectCoast(ac);
        SimScenarioState scenario = engine.Scenario!;

        // Past every deadline, so only the missing scenario keeps the step from expiring the entry.
        scenario.ElapsedSeconds += 100;
        engine.Scenario = null;

        engine.TickDisconnectCoastExpiry(spine);

        Assert.Empty(spine.DisconnectCoastExpiries);
        Assert.Single(scenario.DisconnectCoasts);
    }

    private const string LiveTrafficBundlePath = "TestData/66fd6538542e.zip";
    private const string Shadow = "UAL123";

    private static LiveTrafficSample ShadowSample(double observedAt) =>
        new(observedAt, 37.0, -122.0, 8_000, 240, 90, -500, LiveTrafficSource.Stars, 4521);

    private static AircraftSnapshotDto ShadowSpawnState(double observedAt) =>
        LiveTrafficKinematics
            .CreateShadow(Shadow, "B738", ShadowSample(observedAt), new AircraftFlightPlan { HasFlightPlan = true, Destination = "KOAK" })
            .ToSnapshot();

    private static SessionRecording ShadowRecording(SessionRecording baseline, List<RecordedAction> actions, double total) =>
        new()
        {
            Version = baseline.Version,
            ScenarioJson = baseline.ScenarioJson,
            RngSeed = baseline.RngSeed,
            WeatherJson = baseline.WeatherJson,
            Actions = actions,
            TotalElapsedSeconds = total,
            ScenarioName = baseline.ScenarioName,
            ScenarioId = baseline.ScenarioId,
            ArtccId = baseline.ArtccId,
        };

    /// <summary>One live sim-second on <paramref name="host"/>, with a feed sample landing in its pre-physics as the server's sync does.</summary>
    private static void LiveSecond(SimulationEngine engine, SpineCapturingHost host, Action? feed)
    {
        engine.BeginSecond();
        engine.OpenSecond(host);
        feed?.Invoke();
        engine.RunPrePhysics(host);
        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
        }

        engine.RunPostPhysics(host);
        engine.RunEndOfSecond(host);
    }

    [Fact]
    public void AShadowRemovedAndRespawned_CoastsTheSameLiveAndOnReplay()
    {
        if (RecordingLoader.Load(LiveTrafficBundlePath) is not { } baseline)
        {
            return;
        }

        // Live: the feed spawns the shadow, loses it (an ERAM coast at 8,000 ft), re-supplies it under the same
        // callsign (the coast clears), then loses it again.
        var live = new SimulationEngine(new TestAirportGroundData());
        live.Replay(ShadowRecording(baseline, [], 0), 0);
        live.RunProfile = RunProfile.Live;
        var liveHost = new SpineCapturingHost(live);
        LiveSecond(live, liveHost, () => live.ApplyLiveTrafficSample(Shadow, ShadowSample(1), ShadowSpawnState(1)));
        LiveSecond(live, liveHost, null);
        Assert.True(live.RemoveLiveTraffic(Shadow, LiveTrafficRemovalReason.Stale));
        EntryFor(live, Shadow);
        LiveSecond(live, liveHost, null);
        LiveSecond(live, liveHost, () => live.ApplyLiveTrafficSample(Shadow, ShadowSample(4), ShadowSpawnState(4)));
        LiveSecond(live, liveHost, null);
        Assert.True(live.RemoveLiveTraffic(Shadow, LiveTrafficRemovalReason.Stale));
        LiveSecond(live, liveHost, null);

        List<RecordedAction> actions = [.. live.Scenario!.ActionLog];
        SessionRecording recording = ShadowRecording(baseline, actions, 6);

        // Replay, the public way: the state it ends on.
        var replay = new SimulationEngine(new TestAirportGroundData());
        replay.Replay(recording, 6);

        // Replay again under a capturing host wrapped around the replay host, for what the drains hand over.
        var captured = new SimulationEngine(new TestAirportGroundData());
        captured.Replay(ShadowRecording(baseline, [], 0), 0);
        var replayHost = new SpineCapturingHost(new ReplayHost(captured, new RecordedActionPump(actions), applier: null));
        using (captured.EnterReplay())
        {
            for (int second = 1; second <= 6; second++)
            {
                captured.RunSecond(replayHost);
            }
        }

        Assert.Equal(
            [
                [Shadow],
            ],
            liveHost.DisconnectCoastClears
        );
        Assert.Equal(liveHost.DisconnectCoastClears, replayHost.DisconnectCoastClears);

        AircraftDisconnectCoast liveCoast = Assert.Single(live.Scenario!.DisconnectCoasts).Value;
        Assert.Equal(5, liveCoast.CoastStartSimSeconds);
        foreach (SimulationEngine replayed in new[] { replay, captured })
        {
            KeyValuePair<string, AircraftDisconnectCoast> entry = Assert.Single(replayed.Scenario!.DisconnectCoasts);
            Assert.Equal(Shadow, entry.Key);
            Assert.Equal<DisconnectCoastFacet>(liveCoast.Facets, entry.Value.Facets);
            Assert.Equal(liveCoast with { Facets = entry.Value.Facets }, entry.Value);
        }
    }
}
