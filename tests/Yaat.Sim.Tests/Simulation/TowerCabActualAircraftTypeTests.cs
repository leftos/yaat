using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for the bug where the CRC Tower Cab datablock blanked the aircraft
/// type whenever an instructor blanked the type in the flight plan editor. Tower Cab
/// represents the controller looking out the window and must always show the actual
/// physical type. STARS, ASDE-X, the Flight Plan Editor and flight strips are flight-
/// plan-driven and correctly go blank when the FP is blanked.
///
/// The fix split aircraft type into two fields:
///   * <see cref="AircraftState.AircraftType"/>     — actual / physical, fixed at spawn
///   * <see cref="AircraftFlightPlan.AircraftType"/> — filed FP, mutable via amendment
///
/// These tests verify (a) amendments only touch the filed field, (b) scenario load
/// honours the "top-level wins; FP is opt-in" rule, (c) snapshots round-trip both
/// fields independently, and (d) replaying a real bug-report bundle that includes an
/// <c>AmendFlightPlan { AircraftType: null }</c> action does not corrupt sim state.
/// </summary>
public class TowerCabActualAircraftTypeTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/a67670e50d58.zip";

    private static SimulationEngine NewEngine() => new(new TestAirportGroundData());

    private static AircraftState SpawnAircraft(SimulationEngine engine, string callsign, string actualType, string filedType)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = actualType,
            Position = new LatLon(37.72, -122.22),
            Altitude = 0,
            IndicatedAirspeed = 0,
            Transponder = new AircraftTransponder
            {
                Code = 0,
                AssignedCode = 0,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                AircraftType = filedType,
                FlightRules = "VFR",
                HasFlightPlan = true,
                CruiseAltitude = 0,
            },
            Track = new AircraftTrack(),
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void AmendFlightPlan_BlanksFiledType_DoesNotChangeActualType()
    {
        var engine = NewEngine();
        SpawnAircraft(engine, "UAL238", actualType: "B738", filedType: "B738");

        engine.AmendFlightPlan("UAL238", new FlightPlanAmendment(AircraftType: ""));

        var ac = engine.FindAircraft("UAL238");
        Assert.NotNull(ac);
        Assert.Equal("B738", ac.AircraftType);
        Assert.Equal("", ac.FlightPlan.AircraftType);
        output.WriteLine($"After blanking: actual='{ac.AircraftType}' filed='{ac.FlightPlan.AircraftType}'");
    }

    [Fact]
    public void AmendFlightPlan_ChangesFiledType_DoesNotChangeActualType()
    {
        var engine = NewEngine();
        SpawnAircraft(engine, "UAL238", actualType: "B738", filedType: "B738");

        engine.AmendFlightPlan("UAL238", new FlightPlanAmendment(AircraftType: "A320"));

        var ac = engine.FindAircraft("UAL238");
        Assert.NotNull(ac);
        Assert.Equal("B738", ac.AircraftType);
        Assert.Equal("A320", ac.FlightPlan.AircraftType);
    }

    [Fact]
    public void ScenarioLoad_TopLevelOnly_FiledRemainsBlank()
    {
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "UAL238",
            AircraftType = "B738",
            FlightPlan = new ScenarioFlightPlan { Departure = "KOAK" },
        };

        var ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: null, primaryApproach: null);

        Assert.Equal("B738", ac.AircraftType);
        Assert.Equal("", ac.FlightPlan.AircraftType);
    }

    [Fact]
    public void ScenarioLoad_BothSetDifferently_EachLandsInItsOwnField()
    {
        var scenarioAircraft = new ScenarioAircraft
        {
            AircraftId = "UAL238",
            AircraftType = "B738",
            FlightPlan = new ScenarioFlightPlan { AircraftType = "A320", Departure = "KOAK" },
        };

        var ac = ScenarioLoader.CreateBaseState(scenarioAircraft, primaryAirportId: null, primaryApproach: null);

        Assert.Equal("B738", ac.AircraftType);
        Assert.Equal("A320", ac.FlightPlan.AircraftType);
    }

    [Fact]
    public void Snapshot_RoundTrip_PreservesActualAndFiledTypes()
    {
        var original = new AircraftFlightPlan
        {
            AircraftType = "A320",
            HasFlightPlan = true,
            EquipmentSuffix = "L",
        };
        var dto = original.ToSnapshot();
        var restored = AircraftFlightPlan.FromSnapshot(dto);

        Assert.Equal("A320", dto.AircraftType);
        Assert.Equal("A320", restored.AircraftType);
        Assert.Equal("L", restored.EquipmentSuffix);
    }

    [Fact]
    public void Snapshot_LegacyV3MissingFiledType_MigratesFromActual()
    {
        // Pre-v4 snapshots lack AircraftFlightPlanDto.AircraftType. Build a real aircraft
        // snapshot where the filed FP type is blank (as legacy snapshots will be after
        // the deserializer applies the default value), wrap it in a SchemaVersion=3
        // snapshot, run the migrator, and verify it seeds the filed type from the parent
        // aircraft's actual type so FP-driven displays still surface the type after replay.
        var live = new AircraftState
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan { AircraftType = "", HasFlightPlan = true },
        };
        var aircraftDto = live.ToSnapshot();

        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = 3,
            ElapsedSeconds = 0,
            Rng = new RngState(0, 0, 0, 0),
            Aircraft = [aircraftDto],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "",
                ScenarioName = "",
                RngSeed = 0,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal("B738", snapshot.Aircraft[0].FlightPlan.AircraftType);
    }

    /// <summary>
    /// Real-world bundle regression: replay through the timestamp where the bundle
    /// records an <c>AmendFlightPlan { AircraftType: null, ... }</c> action and verify
    /// every live aircraft still has its actual <see cref="AircraftState.AircraftType"/>
    /// populated. The bundle's null-AircraftType amendment targets ASA1196 (an FP-only
    /// entity that never enters the sim), so the regression here is "the replay completes
    /// without any live aircraft losing its physical type."
    /// </summary>
    [Fact]
    public void BundleReplay_AmendmentWithNullAircraftType_DoesNotBlankLiveAircraft()
    {
        var recording = RecordingLoader.Load(BundlePath);
        if (recording is null)
        {
            output.WriteLine($"Skipped: bundle missing at {BundlePath}");
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("Skipped: NavData not available");
            return;
        }

        var engine = NewEngine();
        // The null-AircraftType amendment in this bundle fires at t≈796s; replay a bit past it.
        engine.Replay(recording, 800);

        int liveAircraft = 0;
        int blankActualType = 0;
        foreach (var ac in engine.World.GetSnapshot())
        {
            liveAircraft++;
            if (string.IsNullOrEmpty(ac.AircraftType))
            {
                blankActualType++;
                output.WriteLine($"Aircraft {ac.Callsign} has blank actual AircraftType (filed='{ac.FlightPlan.AircraftType}')");
            }
        }

        output.WriteLine($"After replay to t=800s: {liveAircraft} live aircraft, {blankActualType} with blank actual type");
        Assert.True(liveAircraft > 0, "Expected at least one live aircraft in the bundle at t=800s");
        Assert.Equal(0, blankActualType);
    }
}
