using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.PathfinderGrid;

/// <summary>
/// Necessity proof for the deferred state-aware-pruning fix (#4). Sweeps a dense
/// origin-destination grid on real V2-fillet layouts (OAK/SFO/FLL) and diffs the
/// production AutoRouter (closed set keyed by node id alone) against
/// <see cref="OracleAutoRouter"/> (closed set keyed by node id + arrival-bearing bucket,
/// hence strictly more complete). Any pair where the oracle resolves a route production
/// misses (HARD), or finds a strictly better one (SOFT), is a real-world manifestation of
/// the node-id-only pruning bug. Zero diffs across the grid is evidence #4 is unneeded on
/// current airports.
///
/// Guard: writes a full report to <c>.tmp/state-aware-necessity.log</c> and asserts zero HARD
/// (production missing a route the oracle finds) and zero ANOMALY (oracle failing where production
/// succeeds — would indicate the oracle is not a correct superset). Because the oracle is an
/// independent (node, bearing-bucket) search, a future regression that reverts production's
/// closed-set key to node-id-only would resurface HARD diffs and fail this test. SOFT/DIFFER
/// counts are reported but not asserted (cost-tie route differences may remain).
/// </summary>
[Trait("Category", "PathfinderGrid")]
public class StateAwarePruningNecessityTests
{
    private const int BearingBucketDeg = 1;
    private const int MaxExpansions = 200_000;
    private const int ParkingPairSampleSize = 15;
    private const double DistanceEpsilonNm = 1e-6;
    private const double UTurnThresholdDeg = 135.0;

    private static readonly string[] Airports = ["OAK", "SFO", "FLL"];

    private readonly ITestOutputHelper output;

    public StateAwarePruningNecessityTests(ITestOutputHelper output)
    {
        this.output = output;
        TestVnasData.EnsureInitialized();
    }

    private enum Verdict
    {
        Agree,
        AgreeUnreachable,
        Hard,
        Soft,
        DifferNotWorse,
        Anomaly,
        Inconclusive,
    }

    [Fact]
    public void StateAwarePruning_DenseGrid_DiffProductionVsOracle()
    {
        var report = new StringBuilder();
        report.AppendLine("# State-aware A* pruning — necessity sweep (production node-id pruning vs (node,bearing) oracle)");
        report.AppendLine($"bucket={BearingBucketDeg}deg  maxExpansions={MaxExpansions}  parkingPairSample={ParkingPairSampleSize}");
        report.AppendLine();

        int grandHard = 0;
        int grandSoft = 0;
        int grandAnomaly = 0;
        int grandInconclusive = 0;
        int grandPairs = 0;

        foreach (var airport in Airports)
        {
            var layout = new TestAirportGroundData(FilletMode.V2).GetLayout(airport);
            if (layout is null)
            {
                report.AppendLine($"## {airport}: layout unavailable — SKIP");
                report.AppendLine();
                continue;
            }

            var grid = BuildGrid(layout, airport);
            var counts = new Dictionary<Verdict, int>();
            var diffs = new List<string>();
            long prodMs = 0;
            long orcMs = 0;

            foreach (var (label, from, to) in grid)
            {
                var ctx = BuildContext(layout, from, to);

                var sw1 = Stopwatch.StartNew();
                var (prodRoute, _) = AutoRouter.Run(ctx);
                sw1.Stop();
                prodMs += sw1.ElapsedMilliseconds;

                var sw2 = Stopwatch.StartNew();
                var orc = OracleAutoRouter.Run(ctx, BearingBucketDeg, MaxExpansions);
                sw2.Stop();
                orcMs += sw2.ElapsedMilliseconds;

                var verdict = Classify(prodRoute, orc, out string detail);
                counts[verdict] = counts.GetValueOrDefault(verdict) + 1;

                if (verdict is Verdict.Hard or Verdict.Soft or Verdict.Anomaly or Verdict.Inconclusive)
                {
                    diffs.Add($"  [{verdict}] {label}  {detail}");
                }
            }

            grandPairs += grid.Count;
            grandHard += counts.GetValueOrDefault(Verdict.Hard);
            grandSoft += counts.GetValueOrDefault(Verdict.Soft);
            grandAnomaly += counts.GetValueOrDefault(Verdict.Anomaly);
            grandInconclusive += counts.GetValueOrDefault(Verdict.Inconclusive);

            report.AppendLine($"## {airport}  ({grid.Count} pairs)");
            report.AppendLine(
                $"  AGREE={counts.GetValueOrDefault(Verdict.Agree)}  "
                    + $"AGREE_UNREACHABLE={counts.GetValueOrDefault(Verdict.AgreeUnreachable)}  "
                    + $"HARD={counts.GetValueOrDefault(Verdict.Hard)}  "
                    + $"SOFT={counts.GetValueOrDefault(Verdict.Soft)}  "
                    + $"DIFFER_NOT_WORSE={counts.GetValueOrDefault(Verdict.DifferNotWorse)}  "
                    + $"ANOMALY={counts.GetValueOrDefault(Verdict.Anomaly)}  "
                    + $"INCONCLUSIVE={counts.GetValueOrDefault(Verdict.Inconclusive)}"
            );
            report.AppendLine($"  latency: production={prodMs}ms  oracle={orcMs}ms  (ratio {(prodMs == 0 ? 0 : (double)orcMs / prodMs):F1}x)");
            if (diffs.Count > 0)
            {
                report.AppendLine("  diffs:");
                foreach (var d in diffs)
                {
                    report.AppendLine(d);
                }
            }

            report.AppendLine();
            output.WriteLine(
                $"{airport}: {grid.Count} pairs  HARD={counts.GetValueOrDefault(Verdict.Hard)}  "
                    + $"SOFT={counts.GetValueOrDefault(Verdict.Soft)}  ANOMALY={counts.GetValueOrDefault(Verdict.Anomaly)}  "
                    + $"INCONCLUSIVE={counts.GetValueOrDefault(Verdict.Inconclusive)}  (prod {prodMs}ms / oracle {orcMs}ms)"
            );
        }

        report.Insert(
            0,
            $"SUMMARY: pairs={grandPairs}  HARD={grandHard}  SOFT={grandSoft}  ANOMALY={grandAnomaly}  INCONCLUSIVE={grandInconclusive}\n\n"
        );

        Directory.CreateDirectory(".tmp");
        File.WriteAllText(Path.Combine(".tmp", "state-aware-necessity.log"), report.ToString());

        output.WriteLine($"SUMMARY: pairs={grandPairs} HARD={grandHard} SOFT={grandSoft} ANOMALY={grandAnomaly} INCONCLUSIVE={grandInconclusive}");
        output.WriteLine("Full report: .tmp/state-aware-necessity.log");

        Assert.Equal(0, grandAnomaly);
        Assert.Equal(0, grandHard);
    }

    private static Verdict Classify(TaxiRoute? prodRoute, OracleAutoRouter.OracleResult orc, out string detail)
    {
        bool prodOk = prodRoute is not null;
        bool orcOk = orc.Route is not null;

        if (orc.Exhausted)
        {
            detail = $"oracle exhausted ({orc.Expansions} expansions); prodOk={prodOk}";
            return Verdict.Inconclusive;
        }

        if (!prodOk && orcOk)
        {
            detail =
                $"prod=NULL  oracle={orc.Route!.Segments.Count} segs / {orc.Route.TotalDistanceNm * 6076.115:F0}ft / {CountUTurns(orc.Route)} U-turns";
            return Verdict.Hard;
        }

        if (prodOk && !orcOk)
        {
            detail = $"prod={prodRoute!.Segments.Count} segs  oracle=NULL ({orc.FailReason})";
            return Verdict.Anomaly;
        }

        if (!prodOk && !orcOk)
        {
            detail = "both unreachable";
            return Verdict.AgreeUnreachable;
        }

        if (SameNodeSequence(prodRoute!, orc.Route!))
        {
            detail = "identical";
            return Verdict.Agree;
        }

        int prodU = CountUTurns(prodRoute!);
        int orcU = CountUTurns(orc.Route!);
        double prodD = prodRoute!.TotalDistanceNm;
        double orcD = orc.Route!.TotalDistanceNm;

        if ((orcU < prodU) || (orcD < prodD - DistanceEpsilonNm))
        {
            detail =
                $"prod={prodRoute.Segments.Count}segs/{prodD * 6076.115:F0}ft/{prodU}U  "
                + $"oracle={orc.Route.Segments.Count}segs/{orcD * 6076.115:F0}ft/{orcU}U  (oracle better)";
            return Verdict.Soft;
        }

        detail = $"prod {prodU}U/{prodD * 6076.115:F0}ft  oracle {orcU}U/{orcD * 6076.115:F0}ft (not worse)";
        return Verdict.DifferNotWorse;
    }

    private static SearchContext BuildContext(AirportGroundLayout layout, int from, int to) =>
        SearchContext.Compile(
            layout,
            from,
            waypointSequence: [],
            destinationRunway: null,
            destinationParking: null,
            destinationSpot: null,
            destinationNodeId: to,
            explicitHoldShortRunways: null,
            category: AircraftCategory.Jet,
            preference: RoutePreference.FewestTurns,
            diagnosticLog: null
        );

    private static List<(string Label, int From, int To)> BuildGrid(AirportGroundLayout layout, string airport)
    {
        var parking = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).OrderBy(n => n.Id).ToList();

        var runwayEnds = new List<(string Desig, int NodeId)>();
        foreach (var rwy in layout.Runways)
        {
            foreach (var desig in rwy.EndDesignators)
            {
                var holdShorts = layout.GetRunwayHoldShortNodes(desig);
                if (holdShorts.Count == 0)
                {
                    continue;
                }

                var lineup = RouteMaterialiser.FindFullLengthLineupHoldShort(layout, holdShorts[0], desig, holdShorts);
                runwayEnds.Add((desig, lineup.Id));
            }
        }

        var pairs = new List<(string, int, int)>();

        // Departures: every parking → every runway-end lineup hold-short.
        foreach (var p in parking)
        {
            foreach (var (desig, nodeId) in runwayEnds)
            {
                pairs.Add(($"{airport} {Name(p)}->{desig}", p.Id, nodeId));
            }
        }

        // Arrivals/exits: every runway-end → every parking.
        foreach (var (desig, nodeId) in runwayEnds)
        {
            foreach (var p in parking)
            {
                pairs.Add(($"{airport} {desig}->{Name(p)}", nodeId, p.Id));
            }
        }

        // Parking ↔ parking (sampled): exercises cross-field junction traversal.
        var sample = parking.Take(ParkingPairSampleSize).ToList();
        foreach (var a in sample)
        {
            foreach (var b in sample)
            {
                if (a.Id != b.Id)
                {
                    pairs.Add(($"{airport} {Name(a)}->{Name(b)}", a.Id, b.Id));
                }
            }
        }

        return pairs;
    }

    private static string Name(GroundNode n) => string.IsNullOrEmpty(n.Name) ? $"#{n.Id}" : n.Name;

    private static bool SameNodeSequence(TaxiRoute a, TaxiRoute b)
    {
        if (a.Segments.Count != b.Segments.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Segments.Count; i++)
        {
            if ((a.Segments[i].FromNodeId != b.Segments[i].FromNodeId) || (a.Segments[i].ToNodeId != b.Segments[i].ToNodeId))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountUTurns(TaxiRoute route)
    {
        if (route.Segments.Count < 2)
        {
            return 0;
        }

        int count = 0;
        for (int i = 1; i < route.Segments.Count; i++)
        {
            double prev = route.Segments[i - 1].Edge.ArrivalBearing;
            double curr = route.Segments[i].Edge.DepartureBearing;
            double delta = Math.Abs(NormalizeAngle(curr - prev));
            if (delta > UTurnThresholdDeg)
            {
                count++;
            }
        }

        return count;
    }

    private static double NormalizeAngle(double deg)
    {
        while (deg > 180.0)
        {
            deg -= 360.0;
        }

        while (deg < -180.0)
        {
            deg += 360.0;
        }

        return deg;
    }
}
