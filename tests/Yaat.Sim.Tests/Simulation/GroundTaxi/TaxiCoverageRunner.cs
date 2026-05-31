using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Shared runner for taxi-coverage tests. Both <see cref="TaxiCoverageOakTests"/>
/// and <see cref="TaxiCoverageSfoTests"/> hand a resolved <see cref="TaxiPair"/>
/// + origin/destination nodes here. Lives outside the test classes so per-
/// airport classes stay short and the harness logic stays single-sourced.
/// </summary>
internal static class TaxiCoverageRunner
{
    /// <summary>
    /// How long the aircraft is allowed to sit unmoving outside a legitimate
    /// stop (hold-short / runway-crossing / parked) before the test calls it
    /// a stall. Set well above the longest legitimate transient — the spawn-
    /// to-rolling acceleration alone takes a few seconds.
    /// </summary>
    public const int MaxAcceptableZeroProgressSec = 30;

    /// <summary>
    /// Tolerance (feet) when checking whether an aircraft has "arrived" at a
    /// parking destination. AtParkingPhase snaps the aircraft to the parking
    /// node position once rollout finishes; anything tighter is float drift.
    /// </summary>
    public const double ParkingArrivalToleranceFt = 50.0;

    /// <summary>
    /// Resolves the node for a taxi-coverage pair endpoint. For
    /// <see cref="TaxiNodeKind.RunwayExit"/> origins, <paramref name="name"/>
    /// is parsed as <c>"RWY/TWY"</c> (e.g. <c>"28R/J"</c>) — the hold-short
    /// on TWY that protects RWY. When multiple candidates exist (most exits
    /// have a Left and a Right hold-short flanking the runway),
    /// <paramref name="tieBreakerToNode"/> is used to pick the one whose A*
    /// route to that node is shortest, which usually picks the correct side
    /// (the side facing the destination).
    /// </summary>
    public static GroundNode? ResolveNode(
        AirportGroundLayout layout,
        string name,
        TaxiNodeKind kind,
        string? runwayHint,
        bool requireForwardLineup,
        GroundNode? tieBreakerToNode = null
    )
    {
        switch (kind)
        {
            case TaxiNodeKind.Parking:
                return layout.FindParkingByName(name);
            case TaxiNodeKind.RunwayExit:
            {
                if (requireForwardLineup)
                {
                    return ResolveRunwayDeparture(layout, runwayHint ?? name);
                }
                return ResolveRunwayExit(layout, name, tieBreakerToNode);
            }
            default:
                return null;
        }
    }

    private static GroundNode? ResolveRunwayDeparture(AirportGroundLayout layout, string designator)
    {
        var holdShorts = layout.GetRunwayHoldShortNodes(designator);
        if (holdShorts.Count == 0)
        {
            return null;
        }
        var runway = NavigationDatabase.Instance.GetRunway(layout.AirportId, designator);
        if (runway is null)
        {
            return holdShorts[0];
        }
        return TaxiPathfinder.FindFullLengthLineupHoldShort(layout, holdShorts[0], runway.Designator, holdShorts);
    }

    private static GroundNode? ResolveRunwayExit(AirportGroundLayout layout, string name, GroundNode? tieBreakerToNode)
    {
        var parts = name.Split('/', 2);
        if (parts.Length != 2)
        {
            return null;
        }
        string runwayDesignator = parts[0];
        string exitTaxiway = parts[1];

        var candidates = layout
            .GetRunwayHoldShortNodes(runwayDesignator)
            .Where(n => n.Edges.Any(e => !e.IsRunwayCenterline && e.MatchesTaxiway(exitTaxiway)))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1 || tieBreakerToNode is null)
        {
            return candidates[0];
        }

        GroundNode? best = null;
        double bestDistNm = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var route = TaxiPathfinder.FindRoute(layout, candidate.Id, tieBreakerToNode.Id, AircraftCategory.Jet);
            if (route is null)
            {
                continue;
            }
            if (route.TotalDistanceNm < bestDistNm)
            {
                bestDistNm = route.TotalDistanceNm;
                best = candidate;
            }
        }
        return best ?? candidates[0];
    }

    /// <summary>
    /// Bearing along the hold-short's first non-runway taxiway edge, pointed
    /// away from the runway. Used as the spawn heading for runway-exit-origin
    /// pairs — the aircraft just rolled off the runway and is facing along
    /// the exit taxiway. Falls back to 0° if the node has no taxiway edge.
    /// </summary>
    public static TrueHeading TaxiwayDepartureHeading(GroundNode holdShort)
    {
        foreach (var edge in holdShort.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }
            var other = edge.OtherNode(holdShort);
            return new TrueHeading(GeoMath.BearingTo(holdShort.Position, other.Position));
        }
        return new TrueHeading(0);
    }

    /// <summary>
    /// Builds the budget, spawns the aircraft, issues the TAXI command, and
    /// asserts arrival within time, cumulative turn within budget, and no
    /// stall window outside a legitimate stop. Skips with diagnostic output
    /// when the pathfinder produces no route.
    ///
    /// <para>When the <c>YAAT_TAXI_TICK_RECORD</c> environment variable matches
    /// the pair ID (or is set to <c>"*"</c> for all pairs), per-tick aircraft
    /// state is captured via <see cref="TickRecorder"/> and written to
    /// <c>&lt;repo&gt;/.tmp/taxi-coverage-&lt;pairId&gt;.json</c>. Visualize with
    /// <c>LayoutInspector --ticks &lt;json&gt; --html &lt;out.html&gt;</c>.
    /// </para>
    /// </summary>
    public static void Run(
        Func<SimulationEngine?> engineFactory,
        TaxiPair pair,
        GroundNode origin,
        GroundNode destination,
        AirportGroundLayout layout,
        ITestOutputHelper output
    )
    {
        if (TaxiPathfinder.FindRoute(layout, origin.Id, destination.Id, AircraftCategory.Jet) is null)
        {
            output.WriteLine($"SKIP {pair.PairId}: no A* route from {origin.Id} to {destination.Id}");
            return;
        }

        var budget = TaxiBudgetDeriver.Derive(layout, origin.Id, destination.Id, pair.Category);
        output.WriteLine(
            $"{pair.PairId}: optimal {budget.OptimalDistFt:F0}ft / {budget.OptimalTurnDeg:F0}deg / {budget.CornerCount} corners "
                + $"/ {budget.OptimalTimeSec:F0}s → budget {budget.TimeBudgetSec:F0}s, {budget.TurnBudgetDeg:F0}deg"
        );

        var engine = engineFactory();
        Assert.NotNull(engine);

        var aircraft = SpawnAircraft(pair, origin, layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = $"test-taxi-coverage-{pair.PairId}",
            ScenarioName = $"Taxi Coverage {pair.PairId}",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = pair.AirportId,
            AutoCrossRunway = true,
        };

        using var recorder = MaybeAttachRecorder(engine, pair, aircraft.Callsign, output);

        string command = BuildTaxiCommand(pair);
        var result = engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"{pair.PairId}: command '{command}' failed: {result.Message}");

        var evaluator = new TaxiBudgetEvaluator();
        int observedTicks = 0;
        bool arrived = false;
        int maxSeconds = (int)Math.Ceiling(budget.TimeBudgetSec);

        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            evaluator.Observe(aircraft);
            observedTicks = t;
            if (IsArrived(aircraft, pair, destination))
            {
                arrived = true;
                break;
            }
        }

        if (!arrived)
        {
            Assert.Fail(
                $"{pair.PairId}: did NOT arrive at destination in {maxSeconds}s budget. "
                    + $"Cmd='{command}'. Optimal {budget.OptimalDistFt:F0}ft / {budget.OptimalTurnDeg:F0}deg. "
                    + $"{evaluator.DiagnosticSummary()}"
            );
        }

        Assert.True(
            evaluator.CumulativeAbsTurnDeg <= budget.TurnBudgetDeg,
            $"{pair.PairId}: cumulative turn {evaluator.CumulativeAbsTurnDeg:F0}deg > budget {budget.TurnBudgetDeg:F0}deg "
                + $"(optimal {budget.OptimalTurnDeg:F0}deg). {evaluator.DiagnosticSummary()}"
        );

        Assert.True(
            evaluator.MaxConsecutiveZeroProgressSec <= MaxAcceptableZeroProgressSec,
            $"{pair.PairId}: sat unmoving for {evaluator.MaxConsecutiveZeroProgressSec}s outside a legitimate stop "
                + $"(max acceptable {MaxAcceptableZeroProgressSec}s). {evaluator.DiagnosticSummary()}"
        );

        output.WriteLine($"OK {pair.PairId}: arrived in {observedTicks}s. {evaluator.DiagnosticSummary()}");
    }

    private static IDisposable? MaybeAttachRecorder(SimulationEngine engine, TaxiPair pair, string callsign, ITestOutputHelper output)
    {
        string? envFilter = Environment.GetEnvironmentVariable("YAAT_TAXI_TICK_RECORD");
        if (string.IsNullOrEmpty(envFilter))
        {
            return null;
        }
        if (envFilter != "*" && !string.Equals(envFilter, pair.PairId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string repoRoot = TickRecorder.FindRepoRoot();
        string outPath = Path.Combine(repoRoot, ".tmp", $"taxi-coverage-{pair.PairId}.json");
        output.WriteLine($"TICK-RECORDING enabled for {pair.PairId} → {outPath}");
        return TickRecorder.Attach(engine, outPath, callsign);
    }

    private static AircraftState SpawnAircraft(TaxiPair pair, GroundNode origin, AirportGroundLayout layout)
    {
        var callsign = $"N{origin.Id:D4}";
        var spawnHeading = pair.OriginKind switch
        {
            TaxiNodeKind.Parking => origin.TrueHeading ?? new TrueHeading(0),
            TaxiNodeKind.RunwayExit => TaxiwayDepartureHeading(origin),
            _ => new TrueHeading(0),
        };

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = pair.AircraftType,
            Position = origin.Position,
            TrueHeading = spawnHeading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = pair.AirportId,
                Destination = pair.AirportId,
                FlightRules = "VFR",
                CruiseAltitude = 1500,
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(BuildInitialPhase(pair, origin));
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(startCtx);
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private static Phase BuildInitialPhase(TaxiPair pair, GroundNode origin)
    {
        return pair.OriginKind switch
        {
            TaxiNodeKind.Parking => new AtParkingPhase(),
            // Runway-exit: aircraft has just exited the runway at the hold-short
            // and is waiting for taxi instructions. HoldingAfterExitPhase
            // accepts TAXI/TAXIAUTO via ClearsPhase, sets speed=0, keeps the
            // heading we set, and (cosmetically) broadcasts "clear of runway".
            TaxiNodeKind.RunwayExit => new HoldingAfterExitPhase(
                runwayId: ExtractRunwayFromName(pair.OriginName),
                exitTaxiway: ExtractTaxiwayFromName(pair.OriginName),
                holdShortNodeId: origin.Id
            ),
            _ => throw new InvalidOperationException($"Unsupported origin kind: {pair.OriginKind}"),
        };
    }

    private static string? ExtractRunwayFromName(string originName)
    {
        var parts = originName.Split('/', 2);
        return parts.Length == 2 ? parts[0] : null;
    }

    private static string? ExtractTaxiwayFromName(string originName)
    {
        var parts = originName.Split('/', 2);
        return parts.Length == 2 ? parts[1] : null;
    }

    private static string BuildTaxiCommand(TaxiPair pair)
    {
        return pair.DestinationKind switch
        {
            TaxiNodeKind.Parking => $"TAXIAUTO @{pair.DestinationName.ToUpperInvariant()}",
            TaxiNodeKind.RunwayExit => $"TAXIAUTO {pair.DestinationRunway ?? pair.DestinationName}",
            _ => throw new InvalidOperationException($"Unsupported destination kind: {pair.DestinationKind}"),
        };
    }

    private static bool IsArrived(AircraftState aircraft, TaxiPair pair, GroundNode destination)
    {
        var phase = aircraft.Phases?.CurrentPhase;
        return pair.DestinationKind switch
        {
            TaxiNodeKind.RunwayExit => phase is HoldingShortPhase or HoldingInPositionPhase,
            TaxiNodeKind.Parking => phase is AtParkingPhase && AtNode(aircraft.Position, destination.Position),
            _ => false,
        };
    }

    private static bool AtNode(LatLon a, LatLon b)
    {
        return GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm <= ParkingArrivalToleranceFt;
    }
}
