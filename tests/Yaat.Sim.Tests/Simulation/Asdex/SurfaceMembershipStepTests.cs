using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The ASDE-X and SAAB SAID surface-display membership pass on the bare engine:
/// <see cref="SimulationEngine.TickSurfaceMembership"/> decides, once a second, which configured surface airports an
/// aircraft shows on, with a hysteresis band on the vertical limit so a track near the ceiling does not flicker on
/// and off. The airports come from the real ZOA configuration (SFO is its one ASDE-X airport, 15 nm / 1,500 ft; OAK,
/// RNO and six others carry SAAB SAID), and the membership is per-aircraft snapshotted state.
/// </summary>
public class SurfaceMembershipStepTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public SurfaceMembershipStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The ZOA configuration's SFO ASDE-X tower location.</summary>
    private static readonly LatLon OverSfo = new(37.6159, -122.3838);

    /// <summary>About 17 nm due north of <see cref="OverSfo"/>: outside the 15 nm ASDE-X range.</summary>
    private static readonly LatLon NorthOfSfoOutOfRange = new(37.9, -122.3838);

    /// <summary>The ZOA configuration's RNO SAAB SAID tower location — a field high enough that AGL and MSL differ.</summary>
    private static readonly LatLon OverReno = new(39.4991, -119.7681);

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static AircraftState PutAt(SimulationEngine engine, LatLon position, double altitudeFt)
    {
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Position = position;
        ac.Altitude = altitudeFt;
        return ac;
    }

    [Fact]
    public void ParkedAtOak_IsAsdexMemberOfSfo()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        Assert.Empty(ac.Stars.VisibleAsdexAirports);

        engine.TickSurfaceMembership();

        // OAK itself has no ASDE-X in the ZOA configuration; SFO's 15 nm range reaches across the bay.
        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);
        Assert.Contains("OAK", ac.Stars.VisibleSaidAirports);
    }

    [Fact]
    public void ClimbingOut_StaysMemberUntilCeilingPlusHysteresis()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = PutAt(engine, OverSfo, 1000);
        engine.TickSurfaceMembership();
        Assert.Contains("SFO", ac.Stars.VisibleAsdexAirports);

        ac.Altitude = 1600;
        engine.TickSurfaceMembership();
        Assert.Contains("SFO", ac.Stars.VisibleAsdexAirports);

        ac.Altitude = 2099;
        engine.TickSurfaceMembership();
        Assert.Contains("SFO", ac.Stars.VisibleAsdexAirports);

        ac.Altitude = 2100;
        engine.TickSurfaceMembership();
        Assert.DoesNotContain("SFO", ac.Stars.VisibleAsdexAirports);
    }

    [Fact]
    public void Descending_JoinsOnlyBelowCeiling()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = PutAt(engine, OverSfo, 1700);
        engine.TickSurfaceMembership();
        Assert.DoesNotContain("SFO", ac.Stars.VisibleAsdexAirports);

        ac.Altitude = 1501;
        engine.TickSurfaceMembership();
        Assert.DoesNotContain("SFO", ac.Stars.VisibleAsdexAirports);

        ac.Altitude = 1500;
        engine.TickSurfaceMembership();
        Assert.Contains("SFO", ac.Stars.VisibleAsdexAirports);
    }

    [Fact]
    public void LeavingRange_DropsMembership()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = PutAt(engine, OverSfo, 500);
        engine.TickSurfaceMembership();
        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);

        ac.Position = NorthOfSfoOutOfRange;
        engine.TickSurfaceMembership();
        Assert.Empty(ac.Stars.VisibleAsdexAirports);
    }

    [Fact]
    public void Said_UsesFieldElevationPlus2500Agl()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        double elevation = NavigationDatabase.Instance.GetAirportElevation("RNO") ?? 0;
        Assert.True(elevation > 4000, $"RNO field elevation {elevation} ft: the test needs a high field so AGL and MSL differ");

        // 2,400 ft AGL: inside the 2,500 ft AGL ceiling, though far above 2,500 ft MSL.
        AircraftState ac = PutAt(engine, OverReno, elevation + 2400);
        engine.TickSurfaceMembership();
        Assert.Equal(["RNO"], ac.Stars.VisibleSaidAirports);

        ac.Altitude = elevation + 3099;
        engine.TickSurfaceMembership();
        Assert.Equal(["RNO"], ac.Stars.VisibleSaidAirports);

        ac.Altitude = elevation + 3100;
        engine.TickSurfaceMembership();
        Assert.Empty(ac.Stars.VisibleSaidAirports);

        ac.Altitude = elevation + 2501;
        engine.TickSurfaceMembership();
        Assert.Empty(ac.Stars.VisibleSaidAirports);

        ac.Altitude = elevation + 2500;
        engine.TickSurfaceMembership();
        Assert.Equal(["RNO"], ac.Stars.VisibleSaidAirports);
    }

    [Fact]
    public void Ghost_IsMemberOfNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSurfaceMembership();
        Assert.NotEmpty(ac.Stars.VisibleAsdexAirports);
        Assert.NotEmpty(ac.Stars.VisibleSaidAirports);

        ac.Ghost.IsUnsupported = true;
        engine.TickSurfaceMembership();

        Assert.Empty(ac.Stars.VisibleAsdexAirports);
        Assert.Empty(ac.Stars.VisibleSaidAirports);
    }

    [Fact]
    public void OverlayGhost_KeepsMembership()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSurfaceMembership();
        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);
        Assert.Contains("OAK", ac.Stars.VisibleSaidAirports);

        // A ghost attached to a real track is not a pure phantom: the track stays on the surface displays.
        ac.Ghost.IsUnsupported = true;
        ac.Ghost.IsOverlay = true;
        engine.TickSurfaceMembership();

        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);
        Assert.Contains("OAK", ac.Stars.VisibleSaidAirports);
    }

    [Fact]
    public void ConfigWithoutTheAirport_DropsStaleMembership()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSurfaceMembership();
        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);

        // A new config object — the real ZOA config, read afresh, with SFO's ASDE-X removed — replaces the old one.
        ArtccConfigRoot withoutSfoAsdex = FreshZoa();
        withoutSfoAsdex.FindFacility("SFO")!.AsdexConfiguration = null;
        engine.Scenario!.ArtccConfig = withoutSfoAsdex;
        engine.TickSurfaceMembership();

        Assert.Empty(ac.Stars.VisibleAsdexAirports);
        Assert.Contains("OAK", ac.Stars.VisibleSaidAirports);
    }

    [Fact]
    public void NoConfig_ClearsMembership()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSurfaceMembership();
        Assert.NotEmpty(ac.Stars.VisibleAsdexAirports);
        Assert.NotEmpty(ac.Stars.VisibleSaidAirports);

        engine.Scenario!.ArtccConfig = null;
        engine.TickSurfaceMembership();

        Assert.Empty(ac.Stars.VisibleAsdexAirports);
        Assert.Empty(ac.Stars.VisibleSaidAirports);
    }

    /// <summary>A private copy of the ZOA config: the shared one from <see cref="TestArtccConfig"/> must not be edited.</summary>
    private static ArtccConfigRoot FreshZoa() =>
        JsonSerializer.Deserialize<ArtccConfigRoot>(File.ReadAllText("TestData/artcc-zoa-snapshot.json"), RecordingJsonOptions.Default)!;

    [Fact]
    public void Snapshot_RoundTripsMembership()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSurfaceMembership();
        Assert.NotEmpty(ac.Stars.VisibleSaidAirports);

        string json = JsonSerializer.Serialize(ac.Stars.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftStarsStateDto dto = JsonSerializer.Deserialize<AircraftStarsStateDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftStarsState.FromSnapshot(dto);

        Assert.Equal(ac.Stars.VisibleAsdexAirports, restored.VisibleAsdexAirports);
        Assert.Equal(ac.Stars.VisibleSaidAirports, restored.VisibleSaidAirports);
        Assert.Same(StringComparer.Ordinal, restored.VisibleAsdexAirports.KeyComparer);
        Assert.Same(StringComparer.Ordinal, restored.VisibleSaidAirports.KeyComparer);
    }

    [Fact]
    public void Snapshot_NullMembership_RestoresEmpty()
    {
        AircraftStarsStateDto dto = new AircraftStarsState().ToSnapshot();
        Assert.Null(dto.VisibleAsdexAirports);
        Assert.Null(dto.VisibleSaidAirports);

        var restored = AircraftStarsState.FromSnapshot(dto);

        Assert.Empty(restored.VisibleAsdexAirports);
        Assert.Empty(restored.VisibleSaidAirports);
        Assert.Same(StringComparer.Ordinal, restored.VisibleAsdexAirports.KeyComparer);
        Assert.Same(StringComparer.Ordinal, restored.VisibleSaidAirports.KeyComparer);
    }

    [Fact]
    public void SpineOrder_RunsSurfaceMembershipBeforeAsdexAlerts()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        Assert.Empty(ac.Stars.VisibleAsdexAirports);

        // Through the whole second, not the body alone: the spine entry is what has to deliver this on every run kind.
        engine.RunSecond(new SpineCapturingHost(engine));

        Assert.Equal(["SFO"], ac.Stars.VisibleAsdexAirports);

        var ids = engine.StepTrace.LastSequence.Select(step => step.Id).ToList();
        int membership = ids.IndexOf(StepId.SurfaceMembership);
        Assert.True(membership >= 0, "SurfaceMembership did not run in the second");
        Assert.Equal(ids.IndexOf(StepId.AsdexAlerts) - 1, membership);
    }
}
