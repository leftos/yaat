using System.Collections.Immutable;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The hold-bar pass on the bare engine at SFO: only the bars whose status changed since the last report reach the host, a
/// snapshot restore diffs against what was last reported rather than starting over, and a scenario load or unload reports
/// the bars it leaves Active as Suppressed. The whole set with its current statuses is what a subscribing display gets.
/// </summary>
public class AsdexHoldBarStepTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<ArtccConfigRoot> FullZoa = new(() =>
        JsonSerializer.Deserialize<ArtccConfigRoot>(
            File.ReadAllText(Path.Combine(TickRecorder.FindRepoRoot(), "docs", "vnas-artcc-config-examples", "zoa.json")),
            JsonOptions
        )!
    );

    private const string OneAircraftNearSfo = """
        {
          "name": "SFO hold bars", "artccId": "ZOA", "primaryAirportId": "SFO",
          "aircraft": [
            {
              "id": "a1", "aircraftId": "UAL1", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.9, "lon": -122.6 }, "altitude": 5000, "heading": 280, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KSFO", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" }
            }
          ]
        }
        """;

    public AsdexHoldBarStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Seed() =>
        AsdexSafetyLogicSeed.Build(FullZoa.Value, NavigationDatabase.Instance, ActiveRunways.Empty);

    private static SimulationEngine SfoEngine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(OneAircraftNearSfo, 7, MagneticDeclination.EvaluationDateUtc);
        engine.Scenario!.AsdexSafetyLogicConfigs = Seed();
        return engine;
    }

    /// <summary>The 28L landing end and its safety-logic runway id.</summary>
    private static (RunwayInfo End, string RunwayId) Sfo28L()
    {
        AsdexHoldBarRunway runway = AsdexHoldBarBuilderTests.RunwayWithEnd(AsdexHoldBarBuilderTests.SfoRunways(), "28L");
        return (runway.Runway.ForApproach("28L"), runway.Id);
    }

    /// <summary>Puts the one aircraft 3000 ft before the 28L threshold on final, inside one mile.</summary>
    private static void PutOnFinal28L(SimulationEngine engine)
    {
        (RunwayInfo end, _) = Sfo28L();
        AircraftState ac = engine.World.GetSnapshot()[0];
        var threshold = new LatLon(end.ThresholdLatitude, end.ThresholdLongitude);
        ac.Position = GeoMath.ProjectPoint(threshold, new TrueHeading(end.TrueHeading.Degrees + 180), 3000 / GeoMath.FeetPerNm);
        ac.TrueHeading = end.TrueHeading;
        ac.TrueTrack = end.TrueHeading;
        ac.Altitude = 170;
        ac.IsOnGround = false;
        ac.IndicatedAirspeed = 140;
    }

    private static HashSet<string> Ids(IEnumerable<AsdexHoldBarState> states) => [.. states.Select(state => state.Bar.Id)];

    [Fact]
    public void FirstTickWithAnArrivalInsideOneMile_ReportsExactlyItsRunwaysBars_AndASteadyTickReportsNothing()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);
        PutOnFinal28L(engine);

        engine.RunSecond(spine);

        (string facilityId, IReadOnlyList<AsdexHoldBarState> changed) = Assert.Single(spine.AsdexHoldBarChanges);
        Assert.Equal("SFO", facilityId);
        Assert.All(changed, state => Assert.True(state.IsActive));
        string runwayId = Sfo28L().RunwayId;
        Assert.Equal(Ids(engine.AsdexHoldBarStates("SFO").Where(state => state.Bar.ProtectedRunwayId == runwayId)), Ids(changed));

        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);
        Assert.Single(spine.AsdexHoldBarChanges);
    }

    [Fact]
    public void TheWholeSet_CarriesTheStatusLastReported()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);

        // Before any tick the set is still there for a subscribing display, every bar Suppressed.
        IReadOnlyList<AsdexHoldBarState> before = engine.AsdexHoldBarStates("SFO");
        Assert.Equal(25 + 27 + 21 + 19, before.Count);
        Assert.All(before, state => Assert.False(state.IsActive));

        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);

        IReadOnlyList<AsdexHoldBarState> after = engine.AsdexHoldBarStates("SFO");
        Assert.Equal(Ids(before), Ids(after));
        Assert.Equal(Ids(spine.AsdexHoldBarChanges[0].Changed), Ids(after.Where(state => state.IsActive)));
        Assert.Empty(engine.AsdexHoldBarStates("OAK"));
    }

    [Fact]
    public void ARestoreOntoAWorldWithNoArrival_ReportsTheLitBarsSuppressed()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);
        StateSnapshotDto quiet = engine.CaptureSnapshot();

        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);
        HashSet<string> lit = Ids(spine.AsdexHoldBarChanges[0].Changed);

        engine.RestoreFromSnapshot(quiet);
        engine.Scenario!.AsdexSafetyLogicConfigs = Seed();
        engine.TickAsdexHoldBars(spine);

        Assert.Equal(2, spine.AsdexHoldBarChanges.Count);
        IReadOnlyList<AsdexHoldBarState> cleared = spine.AsdexHoldBarChanges[1].Changed;
        Assert.Equal(lit, Ids(cleared));
        Assert.All(cleared, state => Assert.False(state.IsActive));
    }

    [Fact]
    public void ARestoreOntoTheSamePicture_ReportsNothing()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);
        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);
        StateSnapshotDto lit = engine.CaptureSnapshot();

        engine.RestoreFromSnapshot(lit);
        engine.TickAsdexHoldBars(spine);

        Assert.Single(spine.AsdexHoldBarChanges);
    }

    [Fact]
    public void AScenarioLoad_ReportsTheLitBarsSuppressed()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);
        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);
        HashSet<string> lit = Ids(spine.AsdexHoldBarChanges[0].Changed);

        engine.LoadScenario(OneAircraftNearSfo, 7, MagneticDeclination.EvaluationDateUtc);
        engine.TickAsdexHoldBars(spine);

        IReadOnlyList<AsdexHoldBarState> cleared = spine.AsdexHoldBarChanges[1].Changed;
        Assert.Equal(lit, Ids(cleared));
        Assert.All(cleared, state => Assert.False(state.IsActive));
        Assert.Empty(engine.ReportedActiveAsdexHoldBars);
    }

    [Fact]
    public void AnUnload_ReportsTheLitBarsSuppressed()
    {
        SimulationEngine engine = SfoEngine();
        var spine = new SpineCapturingHost(engine);
        PutOnFinal28L(engine);
        engine.TickAsdexHoldBars(spine);
        HashSet<string> lit = Ids(spine.AsdexHoldBarChanges[0].Changed);
        Assert.Equal(lit, [.. engine.ReportedActiveAsdexHoldBars["SFO"].Select(bar => bar.Id)]);

        engine.Scenario = null;
        engine.TickAsdexHoldBars(spine);

        IReadOnlyList<AsdexHoldBarState> cleared = spine.AsdexHoldBarChanges[1].Changed;
        Assert.Equal(lit, Ids(cleared));
        Assert.All(cleared, state => Assert.False(state.IsActive));
        Assert.Empty(engine.AsdexHoldBarFacilityIds);
    }
}
