using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test exercising the ground spawn snap + multi-turn taxi pipeline on
/// SFO. The aircraft is spawned ~20 ft north of M2 (node 1529 area) with a
/// heading near the M2 westbound bearing, then given a preset
/// <c>TAXI M2 A A1 1R</c> + <c>CTO 1R</c>. The route exercises two
/// consecutive ~90° turns (M2→A at node 92, A→A1 at node 97) before
/// reaching the 1R hold-short — verifying that pure-pursuit straight
/// steering, SlowTurn fillet playback, and LineUpPhase compose cleanly
/// when the aircraft doesn't spawn directly on the destination taxiway.
///
/// <para>
/// Regression protection for the fix landed in commit dc12009 (spawn
/// snap). Before that fix, an off-graph spawn near M2 would have the
/// pathfinder's start-node approach cut diagonally across terrain to
/// acquire the first M2 segment — breaking downstream alignment for both
/// turns and LineUp.
/// </para>
/// </summary>
[Collection("NavDbMutator")]
public class SfoM2MultiTurnTaxiTests(ITestOutputHelper output)
{
    private static SimulationEngine? BuildEngine(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("GroundNavigator", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("TaxiingPhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("LineUpPhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", Microsoft.Extensions.Logging.LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Build a minimal single-aircraft SFO scenario JSON with the aircraft
    /// placed at <paramref name="lat"/>/<paramref name="lon"/> and preset
    /// TAXI + CTO commands. Keeping the JSON inline (not a fixture file)
    /// lets the test stay co-located with its geometry assertions.
    /// </summary>
    private static string BuildScenarioJson(double lat, double lon, int headingMag)
    {
        return $$"""
            {
              "id": "test-m2-multiturn",
              "name": "M2 Multi-Turn Test",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "test-ac-1",
                  "aircraftId": "TEST1",
                  "aircraftType": "B738",
                  "transponderMode": "Standby",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{lat}}, "lon": {{lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "KSFO",
                    "destination": "KLAX",
                    "cruiseAltitude": 35000,
                    "cruiseSpeed": 450,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "B738/L"
                  },
                  "presetCommands": [
                    {"id": "p1", "command": "TAXI M2 A A1 1R", "timeOffset": 0},
                    {"id": "p2", "command": "WAIT 3 CTO 1R", "timeOffset": 0}
                  ],
                  "spawnDelay": 0,
                  "airportId": "SFO",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "Parked",
              "flightStripConfigurations": []
            }
            """;
    }

    /// <summary>
    /// Spawn TEST1 ~20 ft north of M2 node 1529 with heading 280° magnetic
    /// (snap should rotate to the M2 westbound true bearing ~297°), issue
    /// TAXI M2 A A1 1R + CTO, and verify the aircraft:
    /// <list type="bullet">
    /// <item>Gets snapped onto the M2 edge (not 20 ft north on the grass).</item>
    /// <item>Taxis west on M2 to node 92 (M2/A intersection, ~35 ft).</item>
    /// <item>Turns ~90° south onto A, taxis to node 97 (A/A1 intersection, ~745 ft).</item>
    /// <item>Turns ~90° east onto A1, taxis to the 1R hold-short.</item>
    /// <item>Holds short, then LineUpPhase + CTO roll the aircraft onto 1R.</item>
    /// <item>Reaches TakeoffPhase and becomes airborne within 5 minutes.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Test1_SpawnOffM2_TaxisThroughTwoNinetyTurns_AndTakesOff()
    {
        var engine = BuildEngine(output);
        if (engine is null)
        {
            output.WriteLine("SKIP: navdata or SFO layout not available");
            return;
        }

        // M2 node 1529 is at (37.607704, -122.384926). The edge from 1529 to 92
        // runs along M2 bearing ~297° (west). We spawn 20 ft north of 1529,
        // with heading 280° magnetic (~293° true at SFO's ~13°E declination) —
        // the snap should pick the westbound edge direction (~297°) and place
        // the aircraft on the M2 edge.
        const double spawnLat = 37.607759; // ~20 ft north of 1529
        const double spawnLon = -122.384926;
        const int spawnHdgMag = 280;

        string json = BuildScenarioJson(spawnLat, spawnLon, spawnHdgMag);
        var warnings = engine.LoadScenario(json, rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[WARN] {w}");
        }

        var ac = engine.FindAircraft("TEST1");
        Assert.NotNull(ac);

        // Verify the snap fired — aircraft should be within 1 ft of the M2
        // edge (foot-of-perpendicular on the 92↔1529 edge) rather than 20 ft
        // away. The snap also rotates heading to the nearest edge direction
        // (westbound ~297° true).
        var layout = ac.Ground.Layout;
        Assert.NotNull(layout);
        var nearest = layout.FindNearestTaxiEdge(ac.Position.Lat, ac.Position.Lon);
        Assert.NotNull(nearest);
        double distToEdgeFt = nearest.Value.DistNm * GeoMath.FeetPerNm;
        output.WriteLine(
            $"[t=0] spawn pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F1}° dist-to-edge={distToEdgeFt:F2}ft twy={nearest.Value.Edge.TaxiwayName}"
        );
        Assert.True(distToEdgeFt < 1.0, $"spawn snap should place aircraft on a taxi edge (got {distToEdgeFt:F2}ft off)");
        Assert.Equal("M2", nearest.Value.Edge.TaxiwayName);
        // Westbound M2 bearing at 1529 is ~297° true — allow ±3° for declination/projection noise.
        double hdgT = ac.TrueHeading.Degrees;
        Assert.InRange(hdgT, 294.0, 300.0);

        // Tick the sim. At t=1 the preset TAXI fires, at t=3 CTO fires,
        // then the aircraft rolls through the full taxi + LineUp + takeoff.
        int airborneAt = -1;
        int taxiStartedAt = -1;
        int reachedHoldShortAt = -1;
        int lineUpAt = -1;
        int takeoffAt = -1;
        string prevPhase = "";

        const int maxSeconds = 5 * 60; // 5 min budget — generous for a multi-segment taxi + LineUp
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("TEST1");
            if (ac is null)
            {
                break;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "(null)";
            if (phase != prevPhase)
            {
                output.WriteLine(
                    $"[t={t}] phase {prevPhase} -> {phase} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F1}° ias={ac.IndicatedAirspeed:F1}kt"
                );
                prevPhase = phase;

                if (phase == "Taxiing" && taxiStartedAt < 0)
                {
                    taxiStartedAt = t;
                }
                if (phase.StartsWith("Holding Short") && reachedHoldShortAt < 0)
                {
                    reachedHoldShortAt = t;
                }
                if (phase == "LiningUp" && lineUpAt < 0)
                {
                    lineUpAt = t;
                }
                if (phase == "Takeoff" && takeoffAt < 0)
                {
                    takeoffAt = t;
                }
            }

            if (!ac.IsOnGround)
            {
                airborneAt = t;
                output.WriteLine(
                    $"[t={t}] airborne at pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F1}° ias={ac.IndicatedAirspeed:F1}kt alt={ac.Altitude:F0}ft"
                );
                break;
            }
        }

        output.WriteLine("");
        output.WriteLine(
            $"summary: taxiStarted={taxiStartedAt}s holdShort={reachedHoldShortAt}s lineUp={lineUpAt}s takeoff={takeoffAt}s airborne={airborneAt}s"
        );

        Assert.True(taxiStartedAt > 0, "aircraft should enter Taxiing phase after preset TAXI fires");
        Assert.True(
            lineUpAt > 0,
            "aircraft should enter LiningUp phase after traversing M2 → A → A1 (CTO was pre-cleared, so HoldingShort is skipped)"
        );
        Assert.True(takeoffAt > 0, "aircraft should enter Takeoff phase after LineUp");
        Assert.True(airborneAt > 0, $"aircraft should be airborne within {maxSeconds}s (taxi+LineUp+roll) — last phase was '{prevPhase}'");

        // Sanity-check the timeline with BOTH bounds so the test catches
        // two classes of regression:
        //
        //   * Lower bound (>= 30 s): the route is ~1200 ft with two corner-
        //     speed slowdowns plus a synthesised slow-turn through the SFO
        //     A1 apex (node 507). A sub-30 s Taxi→LineUp transition would
        //     mean the pathfinder picked a shortcut, the aircraft cut a
        //     corner, or the synthesis was skipped and physics drove through
        //     the corner at cruise speed.
        //
        //   * Upper bound (<= 75 s): before the slow-turn-synthesis fix
        //     (PlanSynthesisLookahead), the aircraft would orbit node 877
        //     for 30+ s after overshooting the 90° bend at node 507 — total
        //     taxi time was 80+ s. Capping at 75 s catches any regression
        //     that reintroduces the spiral or removes the synthesis.
        int taxiDuration = lineUpAt - taxiStartedAt;
        Assert.True(
            taxiDuration >= 30,
            $"taxi should take at least 30 s for the M2 → A → A1 route (catch shortcut/corner-cutting); got {taxiDuration} s"
        );
        Assert.True(
            taxiDuration <= 75,
            $"taxi should complete within 75 s for the M2 → A → A1 route (catch spiral regression at node 507); got {taxiDuration} s"
        );
    }

    /// <summary>
    /// Diagnostic: same setup as <see cref="Test1_SpawnOffM2_TaxisThroughTwoNinetyTurns_AndTakesOff"/>
    /// but writes a per-tick CSV of the trajectory so the taxi can be
    /// rendered with <c>Yaat.LayoutInspector --ticks</c> for visual
    /// inspection. Writes to <c>.tmp/sfo-m2-multiturn.csv</c>. Render with:
    /// <code>
    /// dotnet run --project tools/Yaat.LayoutInspector -- \
    ///     tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    ///     --ticks .tmp/sfo-m2-multiturn.csv \
    ///     --html .tmp/sfo-m2-multiturn.html \
    ///     --html-runway 01R \
    ///     --tick-aircraft-length-ft 110 --tick-aircraft-wingspan-ft 117
    /// </code>
    /// </summary>
    [Fact]
    public void Diagnostic_RecordTicksForM2MultiTurnTaxi()
    {
        var engine = BuildEngine(output);
        if (engine is null)
        {
            output.WriteLine("SKIP: navdata or SFO layout not available");
            return;
        }

        const double spawnLat = 37.607759;
        const double spawnLon = -122.384926;
        const int spawnHdgMag = 280;

        string json = BuildScenarioJson(spawnLat, spawnLon, spawnHdgMag);
        engine.LoadScenario(json, rngSeed: 42);

        var ac = engine.FindAircraft("TEST1");
        Assert.NotNull(ac);

        var recorder = new TickRecorder(ac);
        recorder.Record(0);

        const int maxSeconds = 5 * 60;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("TEST1");
            if (ac is null)
            {
                break;
            }
            recorder.Record(t);
            if (!ac.IsOnGround)
            {
                output.WriteLine($"[diag] airborne at t={t}s, stopping trace");
                break;
            }
        }

        string repoRoot = TickRecorder.FindRepoRoot();
        string outPath = Path.Combine(repoRoot, ".tmp", "sfo-m2-multiturn.csv");
        recorder.WriteCsv(outPath);
        output.WriteLine($"[diag] wrote {recorder.Count} ticks to {outPath}");
    }
}
