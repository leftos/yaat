using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation.Coast;

/// <summary>
/// The ERAM coverage-loss coast on the bare engine (<see cref="SimulationEngine.TickEramCoverageCoast"/>): a visible track
/// stays visible down to the coverage floor less the hysteresis band, below it the target is lost and the track coasts
/// (CST) for <see cref="DisconnectCoastRules.EramCoverageCoastSeconds"/> from the aircraft's last ERAM sweep, and a climb
/// back to the full floor reacquires it. The fixture is the real ZOA configuration with an airborne aircraft over OAK.
/// </summary>
public class EramCoverageCoastStepTests
{
    private const string Callsign = "AAL1";

    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Zoa14 = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EramCoverageCoastStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        return engine;
    }

    /// <summary>An airborne aircraft over OAK, <paramref name="feetAboveField"/> above its field, added to the world.</summary>
    private static AircraftState Airborne(SimulationEngine engine, double feetAboveField)
    {
        AircraftState aircraft = AiTestFixture.Airborne(Callsign, 37.75, -122.25, 0);
        aircraft.IndicatedAirspeed = 250;
        SetHeightAboveField(aircraft, feetAboveField);
        aircraft.Track.Owner = Student;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static void SetHeightAboveField(AircraftState aircraft, double feetAboveField)
    {
        aircraft.Altitude = FieldElevationResolver.Resolve(aircraft, NavigationDatabase.Instance) + feetAboveField;
        aircraft.Targets.TargetAltitude = aircraft.Altitude;
    }

    /// <summary>A visible track that has just descended below the band and started its coast.</summary>
    private static AircraftState Coasting(SimulationEngine engine)
    {
        AircraftState aircraft = Airborne(engine, 3000);
        AiTestFixture.Tick(engine, 2);
        SetHeightAboveField(aircraft, 1000);
        AiTestFixture.Tick(engine, 1);
        Assert.True(aircraft.Eram.IsCoverageCoasting);
        return aircraft;
    }

    [Fact]
    public void BelowTheBand_StartsACoast_AnchoredAtTheLatestSweep()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        AiTestFixture.Tick(engine, 2);
        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);
        EramSweepSample sweep = aircraft.Eram.LastEramSweep!.Value;

        SetHeightAboveField(aircraft, 1000);
        AiTestFixture.Tick(engine, 1);

        Assert.True(aircraft.Eram.IsCoverageCoasting);
        Assert.False(aircraft.Eram.IsVisibleOnEramCoverage);
        Assert.Equal(engine.Scenario!.ElapsedSeconds, aircraft.Eram.CoverageCoastStartSeconds);
        Assert.Equal(sweep, aircraft.Eram.CoverageCoastAnchor);
    }

    [Fact]
    public void InsideTheBand_WhileVisible_DoesNotCoast()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        AiTestFixture.Tick(engine, 1);

        SetHeightAboveField(aircraft, 1400);
        AiTestFixture.Tick(engine, 30);

        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);
        Assert.False(aircraft.Eram.IsCoverageCoasting);
        Assert.Null(aircraft.Eram.CoverageCoastStartSeconds);
    }

    [Fact]
    public void TheCoast_ClearsAfter24Seconds()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Coasting(engine);

        AiTestFixture.Tick(engine, 23);
        Assert.True(aircraft.Eram.IsCoverageCoasting);

        AiTestFixture.Tick(engine, 1);
        Assert.False(aircraft.Eram.IsCoverageCoasting);
        Assert.False(aircraft.Eram.IsVisibleOnEramCoverage);
        Assert.Null(aircraft.Eram.CoverageCoastStartSeconds);
        Assert.Null(aircraft.Eram.CoverageCoastAnchor);
        Assert.Equal(24, DisconnectCoastRules.EramCoverageCoastSeconds);
    }

    [Fact]
    public void ClimbingToTheFullFloor_DuringACoast_Reacquires()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Coasting(engine);

        // Inside the band a coasting track stays coasting: reacquisition takes the full floor.
        SetHeightAboveField(aircraft, 1400);
        AiTestFixture.Tick(engine, 1);
        Assert.True(aircraft.Eram.IsCoverageCoasting);

        SetHeightAboveField(aircraft, DisconnectCoastRules.EramCoverageFloorAglFt);
        AiTestFixture.Tick(engine, 1);

        Assert.False(aircraft.Eram.IsCoverageCoasting);
        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);
        Assert.Null(aircraft.Eram.CoverageCoastAnchor);
        Assert.Equal(200, DisconnectCoastRules.EramCoverageHysteresisFt);
    }

    /// <summary>The kinds of track the coverage coast never applies to.</summary>
    public enum ExemptKind
    {
        Frozen,
        Qt,
        Vehicle,
        Ground,
    }

    [Theory]
    [InlineData(ExemptKind.Frozen)]
    [InlineData(ExemptKind.Qt)]
    [InlineData(ExemptKind.Vehicle)]
    [InlineData(ExemptKind.Ground)]
    public void ExemptTracks_NeverCoast(ExemptKind kind)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        engine.TickEramCoverageCoast();
        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);

        aircraft.Eram.IsFrozen = kind == ExemptKind.Frozen;
        aircraft.Eram.IsCoastTrack = kind == ExemptKind.Qt;
        aircraft.Ghost.IsVehicle = kind == ExemptKind.Vehicle;
        aircraft.IsOnGround = kind == ExemptKind.Ground;
        SetHeightAboveField(aircraft, 200);

        for (int second = 0; second < 30; second++)
        {
            engine.Scenario!.ElapsedSeconds++;
            engine.TickEramCoverageCoast();
            Assert.False(aircraft.Eram.IsCoverageCoasting);
        }

        // A frozen or QT-coasted track persists as a track; a vehicle or a surface target drops at once.
        bool persists = kind is ExemptKind.Frozen or ExemptKind.Qt;
        Assert.Equal(persists, aircraft.Eram.IsVisibleOnEramCoverage);
    }

    [Fact]
    public void ARewind_ClearsTheCoast()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Coasting(engine);

        // A coast stamped later than the clock: sim time went backward under it.
        aircraft.Eram.CoverageCoastStartSeconds = engine.Scenario!.ElapsedSeconds + 10;
        engine.TickEramCoverageCoast();

        Assert.False(aircraft.Eram.IsCoverageCoasting);
        Assert.Null(aircraft.Eram.CoverageCoastStartSeconds);
    }

    [Fact]
    public void TheAnchor_IsTheSweepSample_NotTheLivePosition()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        AiTestFixture.Tick(engine, 1);

        // The last sweep CRC drew sits 2 NM south of the live aircraft, on a northbound track.
        double now = engine.Scenario!.ElapsedSeconds;
        var swept = new EramSweepSample(GeoMath.ProjectPoint(aircraft.Position, new TrueHeading(180), 2), 0, 240, now);
        aircraft.Eram.LastEramSweep = swept;
        SetHeightAboveField(aircraft, 1000);
        engine.Scenario!.ElapsedSeconds++;
        engine.TickEramCoverageCoast();

        Assert.True(aircraft.Eram.IsCoverageCoasting);
        Assert.Equal(swept, aircraft.Eram.CoverageCoastAnchor);
        Assert.NotEqual(aircraft.Position, aircraft.Eram.CoverageCoastAnchor!.Value.Position);
    }

    /// <summary>The aircraft's first <see cref="EramSweepGrid"/> sweep second after the sweep period <paramref name="second"/> falls in.</summary>
    private static double NextGridSweep(double second)
    {
        int offset = EramSweepGrid.OffsetSeconds(Callsign);
        return offset + ((EramSweepGrid.Index(offset, second) + 1) * EramSweepGrid.SweepSeconds);
    }

    /// <summary>Runs the coverage step one second at a time up to <paramref name="second"/>, moving the aircraft each second.</summary>
    private static void StepTo(SimulationEngine engine, AircraftState aircraft, double second)
    {
        while (engine.Scenario!.ElapsedSeconds < second)
        {
            engine.Scenario.ElapsedSeconds++;
            aircraft.Position = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading(0), 0.1);
            engine.TickEramCoverageCoast();
        }
    }

    [Fact]
    public void TheSweep_HoldsWithinOneSweepPeriod()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        engine.TickEramCoverageCoast();
        double gridSweep = NextGridSweep(engine.Scenario!.ElapsedSeconds);
        StepTo(engine, aircraft, gridSweep);
        EramSweepSample sweep = aircraft.Eram.LastEramSweep!.Value;
        Assert.Equal(gridSweep, sweep.SimSeconds);

        for (int second = 1; second < EramSweepGrid.SweepSeconds; second++)
        {
            StepTo(engine, aircraft, gridSweep + second);
            Assert.Equal(sweep, aircraft.Eram.LastEramSweep);
        }

        Assert.NotEqual(sweep.Position, aircraft.Position);
    }

    [Fact]
    public void TheSweep_IsRetakenOnTheNextGridSweep_StampedThatSecond()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Airborne(engine, 3000);
        engine.TickEramCoverageCoast();
        double firstGridSweep = NextGridSweep(engine.Scenario!.ElapsedSeconds);
        StepTo(engine, aircraft, firstGridSweep);
        Assert.Equal(firstGridSweep, aircraft.Eram.LastEramSweep!.Value.SimSeconds);

        double nextGridSweep = firstGridSweep + EramSweepGrid.SweepSeconds;
        StepTo(engine, aircraft, nextGridSweep);

        EramSweepSample sweep = aircraft.Eram.LastEramSweep!.Value;
        Assert.Equal(nextGridSweep, sweep.SimSeconds);
        Assert.Equal(aircraft.Position, sweep.Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSweep_IsRetakenTheSecondATrackReacquires(bool coasting)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        // A visible track that then descends below the band coasts; one that starts below the floor is never shown.
        AircraftState aircraft = Airborne(engine, coasting ? 3000 : 1000);
        engine.TickEramCoverageCoast();
        SetHeightAboveField(aircraft, 1000);
        StepTo(engine, aircraft, engine.Scenario!.ElapsedSeconds + 1);
        Assert.Equal(coasting, aircraft.Eram.IsCoverageCoasting);
        Assert.False(aircraft.Eram.IsVisibleOnEramCoverage);

        // The grid keeps sweeping while the target is not shown.
        double gridSweep = NextGridSweep(engine.Scenario.ElapsedSeconds);
        StepTo(engine, aircraft, gridSweep);
        Assert.Equal(gridSweep, aircraft.Eram.LastEramSweep!.Value.SimSeconds);

        // Back at the full floor one second later, off the grid: the sweep is retaken at once.
        SetHeightAboveField(aircraft, DisconnectCoastRules.EramCoverageFloorAglFt);
        StepTo(engine, aircraft, gridSweep + 1);

        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);
        EramSweepSample sweep = aircraft.Eram.LastEramSweep!.Value;
        Assert.Equal(gridSweep + 1, sweep.SimSeconds);
        Assert.Equal(aircraft.Position, sweep.Position);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(3000)]
    public void ACoverageCoastingAircraft_Removed_CarriesOnItsRunningCoastAsTheEramFacet(double feetAboveFieldAtRemoval)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = Coasting(engine);
        AiTestFixture.Tick(engine, 5);
        double coastStart = aircraft.Eram.CoverageCoastStartSeconds!.Value;
        EramSweepSample anchor = aircraft.Eram.CoverageCoastAnchor!.Value;

        // Below the floor, or back above it before the step has reacquired it: either way the same coast is running.
        SetHeightAboveField(aircraft, feetAboveFieldAtRemoval);
        engine.DeleteAircraft(Callsign, "DEL");

        // The same coast, to its own expiry and from its own anchor: not a fresh 24 s window from the removal.
        DisconnectCoastFacet eram = Assert.Single(engine.Scenario!.DisconnectCoasts[Callsign].Facets, f => f.Scope == DisconnectCoastScope.Eram);
        Assert.Equal(coastStart + DisconnectCoastRules.EramCoverageCoastSeconds, eram.DeadlineSimSeconds);
        Assert.Equal(anchor, eram.EramAnchor);
    }

    [Fact]
    public void AHandoffInsideTheBand_WhileVisible_IsAutoAccepted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Airborne(engine, 3000);
        AiTestFixture.Tick(engine, 1);
        SetHeightAboveField(aircraft, 1450);
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 6);

        Assert.True(aircraft.Eram.IsVisibleOnEramCoverage);
        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Zoa14));
    }

    [Fact]
    public void AHandoffWhileCoverageCoasting_IsWithheld()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        scenario.AutoAcceptDelay = TimeSpan.FromSeconds(5);
        AircraftState aircraft = Coasting(engine);

        // Climbing back inside the band: the display still shows CST until the full floor.
        SetHeightAboveField(aircraft, 1450);
        aircraft.Track.HandoffPeer = Zoa14;
        aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

        AiTestFixture.Tick(engine, 6);

        Assert.True(aircraft.Eram.IsCoverageCoasting);
        Assert.NotNull(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void ANeverEvaluatedAircraft_FallsBackToTheStatelessGuard()
    {
        AircraftState below = AiTestFixture.Airborne(Callsign, 37.75, -122.25, 0);
        SetHeightAboveField(below, 1000);
        AircraftState above = AiTestFixture.Airborne(Callsign, 37.75, -122.25, 0);
        SetHeightAboveField(above, 3000);

        Assert.Null(below.Eram.IsVisibleOnEramCoverage);
        Assert.True(TrackEngine.IsAutoAcceptWithheld(below, NavigationDatabase.Instance));
        Assert.False(TrackEngine.IsAutoAcceptWithheld(above, NavigationDatabase.Instance));
    }

    [Fact]
    public void Snapshot_RoundTripsTheCoverageCoast()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState live = Coasting(engine);
        Assert.NotNull(live.Eram.LastEramSweep);

        // Through the serializer a recording writes the snapshot with, not just the in-memory DTO.
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(
            JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
            RecordingJsonOptions.Default
        )!;

        if (Engine() is not { } target)
        {
            return;
        }

        target.RestoreFromSnapshot(reread);

        AircraftEramState restored = target.FindAircraft(Callsign)!.Eram;
        Assert.Equal(live.Eram.IsVisibleOnEramCoverage, restored.IsVisibleOnEramCoverage);
        Assert.True(restored.IsCoverageCoasting);
        Assert.Equal(live.Eram.CoverageCoastStartSeconds, restored.CoverageCoastStartSeconds);
        Assert.Equal(live.Eram.CoverageCoastAnchor, restored.CoverageCoastAnchor);
        Assert.Equal(live.Eram.LastEramSweep, restored.LastEramSweep);
    }

    [Fact]
    public void Snapshot_OfANeverEvaluatedAircraft_WritesNoCoverageFields()
    {
        var eram = new AircraftEramState();

        AircraftEramStateDto dto = eram.ToSnapshot();
        string json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);

        Assert.DoesNotContain("Coverage", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EramSweep", json, StringComparison.Ordinal);
        var restored = AircraftEramState.FromSnapshot(JsonSerializer.Deserialize<AircraftEramStateDto>(json, RecordingJsonOptions.Default)!);
        Assert.Null(restored.IsVisibleOnEramCoverage);
        Assert.False(restored.IsCoverageCoasting);
        Assert.Null(restored.LastEramSweep);
    }
}
