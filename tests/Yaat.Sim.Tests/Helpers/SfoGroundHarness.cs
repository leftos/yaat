using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// A built SFO ground environment: the engine that ticks and the layout its aircraft taxi on.
/// Passed as one value so the spawn helpers stay inside the positional-parameter limit.
/// </summary>
/// <param name="Engine">Engine carrying the world and the scenario.</param>
/// <param name="Layout">The committed SFO ground layout.</param>
internal readonly record struct SfoGround(SimulationEngine Engine, AirportGroundLayout Layout);

/// <summary>
/// Shared scaffolding for scripted SFO ground tests: engine construction, spawning an aircraft at a
/// named layout feature (parking, spot, runway hold-short, taxiway junction), ticking to a
/// condition, and asserting the hold-short list a resolved <see cref="TaxiRoute"/> carries.
///
/// <para>Node ids are geometry-coupled and renumber whenever the layout is regenerated, so every
/// spawn point is resolved by name through <see cref="AirportGroundLayout"/> /
/// <see cref="TestLayoutNodes"/> — never hard-coded. A name the layout does not carry throws with
/// the name in the message rather than silently spawning somewhere else.</para>
/// </summary>
internal static class SfoGroundHarness
{
    /// <summary>Ground speed (knots) below which an aircraft counts as stationary.</summary>
    internal const double StationarySpeedKts = 1.0;

    /// <summary>
    /// Builds an engine over the committed SFO layout with a minimal scenario attached.
    /// </summary>
    /// <param name="output">Test output the sim log is routed to.</param>
    /// <param name="autoCross">Value for <c>SimScenarioState.AutoCrossRunway</c>.</param>
    /// <returns>
    /// The engine and the SFO layout, or null when navdata or the SFO layout is unavailable —
    /// the repo's silent-skip convention for missing test data.
    /// </returns>
    internal static SfoGround? Build(ITestOutputHelper output, bool autoCross)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-sfo-ground-harness",
                ScenarioName = "SFO Ground Harness",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
                AutoCrossRunway = autoCross,
            },
        };
        return new SfoGround(engine, layout);
    }

    /// <summary>
    /// Spawns a stationary aircraft on the ground at the placement node and adds it to the world.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="Build"/>.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type (drives category and performance).</param>
    /// <param name="placement">Layout node to place the aircraft at and the true heading it faces.</param>
    /// <param name="startPhase">Phase the aircraft starts in.</param>
    /// <returns>The spawned aircraft.</returns>
    internal static AircraftState SpawnAt(
        SfoGround ground,
        string callsign,
        string type,
        (GroundNode Node, TrueHeading Heading) placement,
        Phase startPhase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = placement.Node.Position,
            TrueHeading = placement.Heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// Spawns at a named parking stand, nose on the stand heading, in <see cref="AtParkingPhase"/>.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="Build"/>.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="parkingName">Parking stand name, e.g. <c>"B12"</c>.</param>
    /// <returns>The spawned aircraft.</returns>
    /// <exception cref="InvalidOperationException">The layout has no parking by that name.</exception>
    internal static AircraftState SpawnParked(SfoGround ground, string callsign, string type, string parkingName)
    {
        var parking =
            ground.Layout.FindParkingByName(parkingName) ?? throw new InvalidOperationException($"SFO layout has no parking named '{parkingName}'");
        return SpawnAt(ground, callsign, type, (parking, parking.TrueHeading ?? new TrueHeading(0)), new AtParkingPhase());
    }

    /// <summary>
    /// Spawns at a named spot, facing the nearest taxiway-A node, in <see cref="HoldingInPositionPhase"/>.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="Build"/>.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="spotName">Spot name, e.g. <c>"2"</c>.</param>
    /// <returns>The spawned aircraft.</returns>
    /// <exception cref="InvalidOperationException">The layout has no spot by that name, or no taxiway A.</exception>
    internal static AircraftState SpawnAtSpot(SfoGround ground, string callsign, string type, string spotName)
    {
        var spot = ground.Layout.FindSpotNodeByName(spotName) ?? throw new InvalidOperationException($"SFO layout has no spot named '{spotName}'");
        var nearestOnA = NearestOnTaxiway(ground.Layout, "A", spot.Position);
        var heading = new TrueHeading(GeoMath.BearingTo(spot.Position, nearestOnA.Node.Position));
        return SpawnAt(ground, callsign, type, (spot, heading), new HoldingInPositionPhase());
    }

    /// <summary>
    /// Spawns at the hold-short bar protecting <c>bar.Runway</c> on <c>bar.Taxiway</c>, facing away
    /// from the runway along that taxiway, in <see cref="HoldingInPositionPhase"/>.
    ///
    /// <para>A runway normally has a bar on each side of the pavement, so the side is chosen
    /// explicitly: the bar taken is the one closest to <c>bar.Toward</c>. Picking by list order
    /// instead would put the aircraft on the far side and silently add a runway crossing to every
    /// route it is then cleared on.</para>
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="Build"/>.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="bar">
    /// The runway the bar protects (e.g. <c>"28L"</c>), the taxiway it sits on (e.g. <c>"Q"</c>), and
    /// the taxiway on the side of the runway the aircraft should be on (e.g. <c>"B"</c>).
    /// </param>
    /// <returns>The spawned aircraft.</returns>
    /// <exception cref="InvalidOperationException">The layout has no such hold-short node, or no nodes on <c>bar.Toward</c>.</exception>
    internal static AircraftState SpawnAtHoldShort(SfoGround ground, string callsign, string type, (string Runway, string Taxiway, string Toward) bar)
    {
        var node = NearestHoldShortBar(ground.Layout, bar);
        return SpawnAt(ground, callsign, type, (node, TaxiCoverageRunner.TaxiwayDepartureHeading(node)), new HoldingInPositionPhase());
    }

    /// <summary>
    /// Spawns at the junction of two taxiways, facing along <paramref name="taxiwayA"/>, in
    /// <see cref="HoldingInPositionPhase"/>.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="Build"/>.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="taxiwayA">First taxiway of the junction; also the spawn heading's direction.</param>
    /// <param name="taxiwayB">Second taxiway of the junction.</param>
    /// <returns>The spawned aircraft.</returns>
    /// <exception cref="InvalidOperationException">The layout has no such junction, or it carries no A-taxiway edge.</exception>
    internal static AircraftState SpawnAtJunction(SfoGround ground, string callsign, string type, string taxiwayA, string taxiwayB)
    {
        var junction =
            ground.Layout.FindIntersectionNode(taxiwayA, taxiwayB)
            ?? throw new InvalidOperationException($"SFO layout has no junction of taxiways '{taxiwayA}' and '{taxiwayB}'");
        return SpawnAt(ground, callsign, type, (junction, HeadingAlongTaxiway(junction, taxiwayA)), new HoldingInPositionPhase());
    }

    /// <summary>
    /// Ticks the engine one second at a time until <paramref name="done"/> returns true.
    /// </summary>
    /// <param name="engine">Engine to tick.</param>
    /// <param name="done">Condition evaluated after each tick.</param>
    /// <param name="maxSeconds">Tick budget.</param>
    /// <param name="eachSecond">Per-second observer (diagnostics, deadlock guards); null for none.</param>
    /// <returns>The second the condition first held, or -1 when the budget ran out.</returns>
    internal static int TickUntil(SimulationEngine engine, Func<bool> done, int maxSeconds, Action<int>? eachSecond)
    {
        for (int second = 1; second <= maxSeconds; second++)
        {
            engine.TickOneSecond();
            eachSecond?.Invoke(second);
            if (done())
            {
                return second;
            }
        }

        return -1;
    }

    /// <summary>
    /// Shortest distance in feet from <paramref name="position"/> to any straight edge of
    /// <paramref name="taxiway"/>. Arcs (fillet corners, junction arcs) are skipped — the straight
    /// centerlines are what "is the aircraft on taxiway X" means. Returns
    /// <see cref="double.PositiveInfinity"/> when the layout has no straight edge of that taxiway.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="taxiway">Taxiway name, e.g. <c>"B"</c>.</param>
    /// <param name="position">Position to measure from.</param>
    /// <returns>Distance in feet.</returns>
    internal static double DistanceToTaxiwayFt(AirportGroundLayout layout, string taxiway, LatLon position)
    {
        double best = double.PositiveInfinity;
        foreach (var edge in layout.Edges)
        {
            if (!edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            double distFt = GeoMath.DistanceToSegmentFt(
                position.Lat,
                position.Lon,
                edge.Nodes[0].Position.Lat,
                edge.Nodes[0].Position.Lon,
                edge.Nodes[1].Position.Lat,
                edge.Nodes[1].Position.Lon
            );
            best = Math.Min(best, distFt);
        }

        return best;
    }

    /// <summary>
    /// Whether a hold-short protects <paramref name="target"/>. Matches the target name outright, or
    /// — when the hold-short names a runway in pair form (<c>"01R/19L"</c>) — either end of it after
    /// designator normalisation, so <c>"1R"</c> matches <c>"01R/19L"</c>.
    /// </summary>
    /// <param name="hold">Hold-short point from a resolved route.</param>
    /// <param name="target">Expected target: a runway end, taxiway, or spot token.</param>
    /// <returns>True when the hold-short protects that target.</returns>
    internal static bool HoldShortMatches(HoldShortPoint hold, string target)
    {
        if (hold.TargetName is null)
        {
            return false;
        }

        if (string.Equals(hold.TargetName, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RunwayIdentifier.Parse(hold.TargetName).Contains(target);
    }

    /// <summary>
    /// Asserts a route's hold-short list matches <paramref name="expected"/>. Comparison is
    /// order-preserving, except that a run of consecutive <see cref="HoldShortReason.RunwayCrossing"/>
    /// points is compared as an unordered set within that run — the engine's ordering between two
    /// crossings of the same run is not a contract. Dumps the full route on mismatch.
    /// </summary>
    /// <param name="output">Test output for the mismatch dump.</param>
    /// <param name="route">Resolved taxi route under test.</param>
    /// <param name="expected">Expected (target, reason, cleared) tuples in route order.</param>
    internal static void AssertHoldShorts(
        ITestOutputHelper output,
        TaxiRoute route,
        params (string Target, HoldShortReason Reason, bool Cleared)[] expected
    )
    {
        if (HoldShortsMatch(route.HoldShortPoints, expected))
        {
            return;
        }

        DumpRoute(output, route);
        Assert.Fail(
            $"hold-short mismatch:{Environment.NewLine}  expected: {FormatExpected(expected)}{Environment.NewLine}  actual:   {FormatActual(route.HoldShortPoints)}"
        );
    }

    /// <summary>
    /// Writes a route's taxiway sequence, warnings, and every hold-short to the test output.
    /// </summary>
    /// <param name="output">Test output to write to.</param>
    /// <param name="route">Route to dump.</param>
    internal static void DumpRoute(ITestOutputHelper output, TaxiRoute route)
    {
        var taxiways = route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"route taxiways=[{string.Join(", ", taxiways)}] segments={route.Segments.Count}");
        output.WriteLine($"route warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (var hold in route.HoldShortPoints)
        {
            output.WriteLine($"  HS node={hold.NodeId} target={hold.TargetName} reason={hold.Reason} cleared={hold.IsCleared}");
        }
    }

    /// <summary>
    /// The compact one-line form of a route's hold-shorts, e.g. <c>"1L:RunwayCrossing:cleared; 28L:DestinationRunway"</c>.
    /// </summary>
    /// <param name="holds">Hold-short points in route order.</param>
    /// <returns>Formatted list, or <c>"(none)"</c>.</returns>
    internal static string FormatActual(IReadOnlyList<HoldShortPoint> holds)
    {
        if (holds.Count == 0)
        {
            return "(none)";
        }

        return string.Join("; ", holds.Select(h => $"{h.TargetName}:{h.Reason}{(h.IsCleared ? ":cleared" : "")}"));
    }

    /// <summary>
    /// The <see cref="RunwayInfo"/> for an SFO runway end.
    /// </summary>
    /// <param name="designator">Runway end designator, e.g. <c>"28L"</c> or <c>"1R"</c>.</param>
    /// <returns>The runway carrying that end.</returns>
    /// <exception cref="InvalidOperationException">SFO has no runway with that end.</exception>
    internal static RunwayInfo Runway(string designator)
    {
        foreach (var runway in RunwayOccupancy.AirportRunways("SFO"))
        {
            if (runway.Id.Contains(designator))
            {
                return runway;
            }
        }

        throw new InvalidOperationException($"SFO has no runway with end '{designator}'");
    }

    private static string FormatExpected(IReadOnlyList<(string Target, HoldShortReason Reason, bool Cleared)> expected)
    {
        if (expected.Count == 0)
        {
            return "(none)";
        }

        return string.Join("; ", expected.Select(e => $"{e.Target}:{e.Reason}{(e.Cleared ? ":cleared" : "")}"));
    }

    private static bool HoldShortsMatch(
        IReadOnlyList<HoldShortPoint> actual,
        IReadOnlyList<(string Target, HoldShortReason Reason, bool Cleared)> expected
    )
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        int i = 0;
        while (i < expected.Count)
        {
            if (expected[i].Reason != HoldShortReason.RunwayCrossing)
            {
                if (!HoldShortMatchesExpectation(actual[i], expected[i]))
                {
                    return false;
                }

                i++;
                continue;
            }

            int runEnd = ExpectedCrossingRunEnd(expected, i);
            if ((ActualCrossingRunEnd(actual, i) != runEnd) || !CrossingRunMatches(actual, expected, i, runEnd))
            {
                return false;
            }

            i = runEnd;
        }

        return true;
    }

    private static bool HoldShortMatchesExpectation(HoldShortPoint actual, (string Target, HoldShortReason Reason, bool Cleared) expected) =>
        (actual.Reason == expected.Reason) && (actual.IsCleared == expected.Cleared) && HoldShortMatches(actual, expected.Target);

    private static int ExpectedCrossingRunEnd(IReadOnlyList<(string Target, HoldShortReason Reason, bool Cleared)> expected, int start)
    {
        int end = start;
        while ((end < expected.Count) && (expected[end].Reason == HoldShortReason.RunwayCrossing))
        {
            end++;
        }

        return end;
    }

    private static int ActualCrossingRunEnd(IReadOnlyList<HoldShortPoint> actual, int start)
    {
        int end = start;
        while ((end < actual.Count) && (actual[end].Reason == HoldShortReason.RunwayCrossing))
        {
            end++;
        }

        return end;
    }

    private static bool CrossingRunMatches(
        IReadOnlyList<HoldShortPoint> actual,
        IReadOnlyList<(string Target, HoldShortReason Reason, bool Cleared)> expected,
        int start,
        int end
    )
    {
        var used = new bool[end - start];
        for (int e = start; e < end; e++)
        {
            int match = -1;
            for (int a = start; a < end; a++)
            {
                if (!used[a - start] && HoldShortMatchesExpectation(actual[a], expected[e]))
                {
                    match = a;
                    break;
                }
            }

            if (match < 0)
            {
                return false;
            }

            used[match - start] = true;
        }

        return true;
    }

    private static GroundNode NearestHoldShortBar(AirportGroundLayout layout, (string Runway, string Taxiway, string Toward) bar)
    {
        var candidates = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, bar.Runway, bar.Taxiway);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"SFO layout has no hold-short for runway '{bar.Runway}' on taxiway '{bar.Taxiway}'");
        }

        GroundNode best = candidates[0];
        double bestNm = double.MaxValue;
        foreach (var candidate in candidates)
        {
            double distNm = NearestOnTaxiway(layout, bar.Toward, candidate.Position).DistanceNm;
            if (distNm < bestNm)
            {
                bestNm = distNm;
                best = candidate;
            }
        }

        return best;
    }

    private static (GroundNode Node, double DistanceNm) NearestOnTaxiway(AirportGroundLayout layout, string taxiway, LatLon from)
    {
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (var node in layout.GetNodesOnTaxiway(taxiway))
        {
            double distNm = GeoMath.DistanceNm(from, node.Position);
            if (distNm < bestNm)
            {
                bestNm = distNm;
                best = node;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException($"SFO layout has no nodes on taxiway '{taxiway}'");
        }

        return (best, bestNm);
    }

    private static TrueHeading HeadingAlongTaxiway(GroundNode node, string taxiway)
    {
        foreach (var edge in node.Edges)
        {
            if (!edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            var other = edge.OtherNode(node);
            return new TrueHeading(GeoMath.BearingTo(node.Position, other.Position));
        }

        throw new InvalidOperationException($"node {node.Id} has no edge on taxiway '{taxiway}'");
    }
}

/// <summary>
/// Per-second watchdog over a set of taxiing aircraft. Fails the test on the two ground-deadlock
/// shapes that otherwise burn the whole tick budget silently: a mutual yield (two aircraft each
/// giving way to the other) and a global stall (nobody moving, nobody legitimately stopped).
///
/// <para>A stop is legitimate in six phases — <see cref="HoldingShortPhase"/>,
/// <see cref="AtParkingPhase"/>, <see cref="HoldingInPositionPhase"/>,
/// <see cref="HoldingAfterPushbackPhase"/>, <see cref="CrossingRunwayPhase"/> and
/// <see cref="HoldingAfterExitPhase"/>. That is deliberately wider than the three
/// <see cref="TaxiBudgetEvaluator"/> uses: this guard watches aircraft that are waiting on a
/// clearance or lined up, states a taxi-budget run never starts from.</para>
/// </summary>
internal sealed class DeadlockGuard
{
    private const int MutualYieldLimitSec = 30;
    private const int GlobalStallLimitSec = 60;

    private readonly AircraftState[] _tracked;
    private readonly int[,] _mutualYieldSec;
    private int _stallSec;

    /// <summary>
    /// Creates a guard over the aircraft to watch.
    /// </summary>
    /// <param name="tracked">Aircraft observed each second.</param>
    internal DeadlockGuard(params AircraftState[] tracked)
    {
        _tracked = tracked;
        _mutualYieldSec = new int[tracked.Length, tracked.Length];
    }

    /// <summary>
    /// Observes the tracked aircraft for one simulated second and fails the test on a deadlock.
    /// </summary>
    /// <param name="second">Elapsed second, for the failure message.</param>
    internal void Tick(int second)
    {
        CheckMutualYield(second);
        CheckGlobalStall(second);
    }

    private void CheckMutualYield(int second)
    {
        for (int i = 0; i < _tracked.Length; i++)
        {
            for (int j = i + 1; j < _tracked.Length; j++)
            {
                _mutualYieldSec[i, j] = IsMutuallyDeadlocked(_tracked[i], _tracked[j]) ? _mutualYieldSec[i, j] + 1 : 0;
                if (_mutualYieldSec[i, j] >= MutualYieldLimitSec)
                {
                    Assert.Fail(
                        $"mutual-yield deadlock at t={second}s: {_tracked[i].Callsign} and {_tracked[j].Callsign} "
                            + $"have each been yielding to the other, stopped, for {_mutualYieldSec[i, j]}s"
                    );
                }
            }
        }
    }

    private void CheckGlobalStall(int second)
    {
        bool stalled = (_tracked.Length > 0) && _tracked.All(IsStationary) && !_tracked.Any(IsLegitimateStop);
        _stallSec = stalled ? _stallSec + 1 : 0;
        if (_stallSec >= GlobalStallLimitSec)
        {
            Assert.Fail(
                $"global ground stall at t={second}s: [{string.Join(", ", _tracked.Select(a => a.Callsign))}] "
                    + $"all stationary for {_stallSec}s with none in a legitimate stop phase"
            );
        }
    }

    private static bool IsMutuallyDeadlocked(AircraftState a, AircraftState b) =>
        IsStationary(a) && IsStationary(b) && YieldsTo(a, b) && YieldsTo(b, a);

    private static bool YieldsTo(AircraftState a, AircraftState b) =>
        string.Equals(a.Ground.AutoYieldTarget, b.Callsign, StringComparison.OrdinalIgnoreCase) || (a.Ground.Hold?.IsGiveWayFor(b.Callsign) ?? false);

    private static bool IsStationary(AircraftState ac) => ac.GroundSpeed < SfoGroundHarness.StationarySpeedKts;

    private static bool IsLegitimateStop(AircraftState ac) =>
        ac.Phases?.CurrentPhase
            is HoldingShortPhase
                or AtParkingPhase
                or HoldingInPositionPhase
                or HoldingAfterPushbackPhase
                or CrossingRunwayPhase
                or HoldingAfterExitPhase;
}
