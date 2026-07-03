using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E integration test for GitHub issue #236: SFO "A F1 B" taxi weirdness.
///
/// <para>
/// At SFO, taxiways A and B are parallel (~236 ft apart), joined by the ~perpendicular short
/// connector F1. The reporter's clearance <c>TAXI A F1 B M1 1L</c> is a lane change. The complaint:
/// the aircraft slows, makes a full turn to align with F1, accelerates hard on the straight, then
/// makes another huge turn onto B. The fix has two halves that this test exercises together on a
/// single aircraft driven through the real SFO ground graph:
/// </para>
/// <list type="number">
/// <item><b>Pathfinder</b> (transition-arc exemption): the A→F1 turn now flies the smooth
/// <c>[A,F1]</c> fillet corner arc instead of pivoting square through the junction node. See
/// <c>Issue236LaneChangeArcTests</c> for the route-shape assertion.</item>
/// <item><b>Navigator</b> (corner-arc speed cap): a corner arc is never flown faster than its safe
/// cornering speed, so the aircraft flows the whole A→F1→B lane change at a steady low speed instead
/// of surging mid-arc.</item>
/// </list>
///
/// <para>
/// This replaces the original dense-recording replay: capping every corner arc to its safe speed
/// materially slows the High-Intensity scenario's whole ground-traffic timeline, so the recorded
/// aircraft no longer reaches F1 within a bounded window. A single aircraft on the same layout with
/// the same clearance reproduces the lane change faithfully and deterministically.
/// </para>
/// </summary>
public class Issue236SfoAF1BConnectorTests(ITestOutputHelper output)
{
    private const string Callsign = "TEST1";

    /// <summary>
    /// Peak IAS ceiling across the F1 connector transit. The pre-fix behavior surges to ~19–21 kt on
    /// the connector/arc; a steady flow-through holds the bracketing corner arcs' ~10 kt safe cornering
    /// speed. 12 kt leaves margin over the ~10.3 kt arc <see cref="GroundArc.MaxSafeSpeedKts"/> while
    /// still failing hard on the surge.
    /// </summary>
    private const double ConnectorPeakIasCeilingKts = 12.0;

    private static string BuildScenarioJson(double lat, double lon, int headingMag)
    {
        return $$"""
            {
              "id": "test-issue236-af1b",
              "name": "Issue 236 A F1 B Lane Change",
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
                    {"id": "p1", "command": "TAXI A F1 B M1 1L", "timeOffset": 0}
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

    [Fact]
    public void TaxiAF1B_FlowsAcrossF1Connector_WithoutSurging()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            output.WriteLine("SKIP: SFO layout not available");
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();

        // Spawn on taxiway A a few hundred feet NORTH of the A/F1 junction (node 67 area), heading
        // south, so the aircraft approaches and turns left (east) onto F1 — the reporter's direction.
        (double Lat, double Lon) spawn = FindSpawnNorthOnA(layout);
        output.WriteLine($"Spawn: ({spawn.Lat:F6}, {spawn.Lon:F6})");

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(BuildScenarioJson(spawn.Lat, spawn.Lon, headingMag: 178), rngSeed: 42);

        double peakIasOnF1 = 0.0;
        int f1Ticks = 0;
        bool wasOnF1 = false;
        bool reachedBravoAfterF1 = false;

        for (int t = 1; t <= 4 * 60; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            string? twy = ac.Ground?.AssignedTaxiRoute?.CurrentSegment?.TaxiwayName;
            double ias = ac.IndicatedAirspeed;

            bool onF1 = twy is not null && twy.Contains("F1", StringComparison.OrdinalIgnoreCase);
            if (onF1)
            {
                wasOnF1 = true;
                f1Ticks++;
                peakIasOnF1 = Math.Max(peakIasOnF1, ias);
            }

            bool onBravo =
                twy is not null
                && (twy.Equals("B", StringComparison.OrdinalIgnoreCase) || twy.Contains("B", StringComparison.OrdinalIgnoreCase))
                && !onF1;
            if (onBravo && wasOnF1)
            {
                reachedBravoAfterF1 = true;
            }

            if (onF1 || (wasOnF1 && !reachedBravoAfterF1))
            {
                output.WriteLine(
                    $"t={t} twy={twy ?? "-"} ias={ias:F1} hdg={ac.TrueHeading.Degrees:F0} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
                );
            }

            if (reachedBravoAfterF1)
            {
                break; // captured the whole F1 transit
            }
        }

        output.WriteLine("");
        output.WriteLine($"summary: f1Ticks={f1Ticks} peakIasOnF1={peakIasOnF1:F1}kt reachedBravoAfterF1={reachedBravoAfterF1}");

        Assert.True(f1Ticks > 0, "TEST1 should transit the F1 connector");
        Assert.True(reachedBravoAfterF1, "TEST1 should complete A->F1->B and reach taxiway B (not stall on F1)");
        Assert.True(
            peakIasOnF1 <= ConnectorPeakIasCeilingKts,
            $"TEST1 should flow through the short F1 lane change at a steady low speed, not surge. "
                + $"Peak IAS on F1 was {peakIasOnF1:F1} kt (ceiling {ConnectorPeakIasCeilingKts:F1} kt). "
                + "Pre-fix, the aircraft accelerates mid-arc / on the connector to ~19-21 kt and brakes back down."
        );
    }

    /// <summary>
    /// A point on taxiway A roughly 250-550 ft north of the A/F1 junction, so a southbound spawn there
    /// establishes on A and then turns east onto F1. Computed from the graph so it survives layout
    /// re-pins rather than hard-coding coordinates.
    /// </summary>
    private static (double Lat, double Lon) FindSpawnNorthOnA(AirportGroundLayout layout)
    {
        // The A/F1 junction: the A node that also carries an F1 edge and the most edges (the real
        // multi-way intersection, not a tangent-cut). Fall back to any A∩F1 node.
        var junction = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("A")) && n.Edges.Any(e => e.MatchesTaxiway("F1")))
            .OrderByDescending(n => n.Edges.Count)
            .ThenBy(n => n.Id)
            .First();

        // The A node ~250-550 ft NORTH (higher latitude) of that junction, nearest to 400 ft.
        var spawnNode = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("A")) && (n.Position.Lat > junction.Position.Lat))
            .Select(n => (Node: n, Ft: GeoMath.DistanceNm(n.Position, junction.Position) * GeoMath.FeetPerNm))
            .Where(x => (x.Ft >= 250.0) && (x.Ft <= 550.0))
            .OrderBy(x => Math.Abs(x.Ft - 400.0))
            .First()
            .Node;

        return (spawnNode.Position.Lat, spawnNode.Position.Lon);
    }
}
