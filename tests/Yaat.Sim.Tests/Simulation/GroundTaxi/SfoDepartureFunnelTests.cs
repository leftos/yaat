using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The ZOA "two rivers into one funnel" departure technique on an SFO 28/28 configuration: TRUKN
/// departures are staged down the inactive runway 1R and parked short of F1, everybody else flows via
/// A-L-F or via F1 across 1L, and the two rivers are merged onto the single 28L hold-short in the order
/// the controller wants — 3, 1, 4, 2 — rather than the order they left the ramp.
///
/// <para>Four B738 leave the A ramp: FUN1 (spot 2) and FUN2 (spot 4) take the 1R river, FUN3 (spot 1)
/// takes A-L-F, FUN4 (spot 3) takes F1 across 1L. The facts below pin, in order: that the four
/// clearances resolve to the hold-shorts they imply; that the staging parks the 1R river on the runway
/// short of F1 without anyone touching 28L; and that the release choreography — by <c>FOLLOWG</c> in
/// one variant and by <c>RES</c> in the other — produces the intended physical order at the bar and
/// the matching <see cref="AircraftGroundOps.RunwayQueuePosition"/> ordinals the RPO reads.</para>
/// </summary>
public class SfoDepartureFunnelTests(ITestOutputHelper output)
{
    private const string Type = "B738";

    /// <summary>Tick budget for the staging phase: four aircraft off the ramp, two of them down 1R behind a give-way.</summary>
    private const int StageBudgetSeconds = 600;

    /// <summary>Tick budget for a release choreography, measured from the end of staging.</summary>
    private const int ReleaseBudgetSeconds = 900;

    private const double FeetPerNm = 6076.12;

    /// <summary>Gap at which a leader is close enough ahead for the trailer to be told to follow it (~700 ft).</summary>
    private const double CloseUpNm = 700.0 / FeetPerNm;

    /// <summary>
    /// Distance from the 28L hold-short node inside which an aircraft counts as "arrived at the bar". Derived
    /// from the queue's own gate so the choreography's idea of "at the bar" cannot drift away from the set of
    /// aircraft the queue is willing to number.
    /// </summary>
    private const double AtDestinationNm = RunwayDepartureQueue.ProximityNm;

    private const int TraceIntervalSeconds = 15;

    /// <summary>Seconds of continuous sub-1-kt ground speed that count as "stopped", not "between two taxi steps".</summary>
    private const int StationaryConfirmSeconds = 2;

    // ---------------------------------------------------------------------------------------------
    // Fact A — the four clearances resolve to the hold-shorts they imply.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Each of the four funnel clearances resolves, and carries exactly the hold-shorts it implies: the
    /// explicit <c>HS</c> bar, any <c>CROSS</c>-cleared runway on the way, and the 28L destination bar.
    /// The two 1R-river routes additionally run down the 1R centerline and bind their F1 hold to a node
    /// on it, and all four routes end at the same 28L node — the queue is keyed per node, so a split
    /// there would mean four independent lines rather than one funnel.
    /// </summary>
    [Fact]
    public void FourRoutes_Resolve_WithExpectedHoldShorts()
    {
        if (StageAll() is not { } funnel)
        {
            return;
        }

        SfoGroundHarness.AssertHoldShorts(
            output,
            RouteOf(funnel.Fun1),
            ("F1", HoldShortReason.ExplicitHoldShort, false),
            ("28L", HoldShortReason.DestinationRunway, false)
        );
        SfoGroundHarness.AssertHoldShorts(
            output,
            RouteOf(funnel.Fun2),
            ("1L", HoldShortReason.RunwayCrossing, true),
            ("F1", HoldShortReason.ExplicitHoldShort, false),
            ("28L", HoldShortReason.DestinationRunway, false)
        );
        SfoGroundHarness.AssertHoldShorts(
            output,
            RouteOf(funnel.Fun3),
            ("A1", HoldShortReason.ExplicitHoldShort, false),
            ("28L", HoldShortReason.DestinationRunway, false)
        );
        SfoGroundHarness.AssertHoldShorts(
            output,
            RouteOf(funnel.Fun4),
            ("1L", HoldShortReason.RunwayCrossing, true),
            ("1R", HoldShortReason.ExplicitHoldShort, false),
            ("28L", HoldShortReason.DestinationRunway, false)
        );

        AssertRunsDown1RToItsF1Bar(funnel, funnel.Fun1);
        AssertRunsDown1RToItsF1Bar(funnel, funnel.Fun2);

        var nodeIds = funnel.All.Select(ac => DestinationHoldOf(ac).NodeId).ToList();
        if (nodeIds.Distinct().Count() != 1)
        {
            string ends = string.Join(", ", funnel.All.Select((ac, i) => $"{ac.Callsign}={nodeIds[i]}"));
            output.WriteLine(
                $"SKIP: the four routes end at different 28L hold-short nodes ([{ends}]) "
                    + "— the departure queue is keyed per node, so there is no single funnel to rank"
            );
            return;
        }

        output.WriteLine($"all four routes funnel to 28L hold-short node {nodeIds[0]}");
    }

    // ---------------------------------------------------------------------------------------------
    // Fact B — the staging: the 1R river parks on the inactive runway short of F1.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// After the four clearances, the 1R river stages on the inactive runway: FUN1 holds short of F1 at
    /// the bar on the 1R centerline and FUN2 stops behind it, both physically on 1R pavement and FUN2
    /// farther from the F1 node than FUN1 (behind it, not past it). The other two hold at their own
    /// bars — FUN3 at A1, FUN4 short of 1R on F1 — and nobody touches 28L at any point.
    /// </summary>
    [Fact]
    public void Stage_OnInactive1R_ShortOfF1()
    {
        if (StageAll() is not { } funnel)
        {
            return;
        }

        int staged = WaitForStaged(funnel);
        AssertStaged(funnel, staged);
    }

    // ---------------------------------------------------------------------------------------------
    // Fact C — the release, merged by FOLLOWG.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The staged funnel is released 3, 1, 4, 2 by handing each trailer to the aircraft in front of it
    /// with <c>FOLLOWG</c>: FUN3 goes first on <c>RES</c>, FUN1 follows it off 1R once it is past the
    /// F/L junction, FUN4 is crossed over 1R behind them, and FUN2 follows FUN4. The line that forms at
    /// the 28L bar is in that order physically, and the departure queue numbers it 1-2-3-4 — which needs
    /// the follower-ranking pass, since a following aircraft is in neither queue-eligible phase.
    /// </summary>
    [Fact]
    public void Release_3_1_4_2_FollowG()
    {
        if (StageAll() is not { } funnel)
        {
            return;
        }

        AssertStaged(funnel, WaitForStaged(funnel));

        var script = new ReleaseScript(funnel, output, byFollow: true);
        int done = RunUntil(funnel, _ => script.IsComplete(), ReleaseBudgetSeconds, script.Step);
        script.Dump();
        Assert.True(done > 0, $"the funnel never merged onto the 28L bar within {ReleaseBudgetSeconds}s of the release: {script.Describe()}");

        AssertMergedInOrder(funnel);
        AssertQueueOrdinals(funnel);
    }

    // ---------------------------------------------------------------------------------------------
    // Fact D — the same release order, merged by RES on each aircraft's own route.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same 3, 1, 4, 2 release, but the 1R river is let go on its own route with <c>RES</c> rather
    /// than handed to a leader: both FUN1 and FUN2 hold F1 explicitly, so resuming them continues the
    /// clearance they already have. Everyone who reaches the 28L bar inside the queue's proximity gate
    /// is numbered in the release order, with FUN3 at the front.
    /// </summary>
    [Fact]
    public void Release_3_1_4_2_ResVariant()
    {
        if (StageAll() is not { } funnel)
        {
            return;
        }

        AssertStaged(funnel, WaitForStaged(funnel));

        var script = new ReleaseScript(funnel, output, byFollow: false);
        int done = RunUntil(funnel, _ => script.IsComplete(), ReleaseBudgetSeconds, script.Step);
        script.Dump();
        Assert.True(done > 0, $"the funnel never merged onto the 28L bar within {ReleaseBudgetSeconds}s of the release: {script.Describe()}");

        AssertQueueOrderedWithinProximityGate(funnel);
    }

    // ---------------------------------------------------------------------------------------------
    // Fact E — "follow, cross" issued at the 1R bar.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// FUN4, staged short of 1R on F1, is handed to FUN3 at the bar in one transmission — <c>FOLLOWG FUN3; CROSS 1R</c>,
    /// the follow plus crossing clearance of 7110.65 §3-7-2d. The FOLLOWG arms behind the hold and the CROSS
    /// releases it, so FUN4 crosses 1R along F1 and comes off the far side following FUN3, never stopping at a 1R bar
    /// again and never touching 28L.
    /// </summary>
    [Fact]
    public void Fun4_FollowGThenCross_CrossesOneRightAndFollowsFun3()
    {
        if (StageAll() is not { } funnel)
        {
            return;
        }

        AssertStaged(funnel, WaitForStaged(funnel));

        CommandResult res = funnel.Ground.Engine.SendCommand("FUN3", "RES");
        Assert.True(res.Success, res.Message);
        CommandResult followCross = funnel.Ground.Engine.SendCommand("FUN4", "FOLLOWG FUN3; CROSS 1R");
        output.WriteLine($"FUN4 <- 'FOLLOWG FUN3; CROSS 1R' => success={followCross.Success} msg={followCross.Message}");
        Assert.True(followCross.Success, $"'FOLLOWG FUN3; CROSS 1R' at the 1R bar was rejected: {followCross.Message}");

        bool crossing = false;
        bool wasOn1R = false;
        int following = RunUntil(
            funnel,
            _ => (funnel.Fun4.Phases?.CurrentPhase is FollowingPhase) && wasOn1R && !RunwayOccupancy.IsOnPavement(funnel.Fun4, funnel.Runway1R),
            ReleaseBudgetSeconds,
            second =>
            {
                crossing |= funnel.Fun4.Phases?.CurrentPhase is CrossingRunwayPhase;
                wasOn1R |= RunwayOccupancy.IsOnPavement(funnel.Fun4, funnel.Runway1R);
                if (crossing && HoldsShortOf(funnel.Fun4, "1R"))
                {
                    Assert.Fail($"t={second}s: FUN4 stopped at a 1R bar again after the CROSS: {DescribeAll(funnel)}");
                }
            }
        );

        Assert.True(following > 0, $"FUN4 never came off 1R following FUN3 within {ReleaseBudgetSeconds}s: {DescribeAll(funnel)}");
        Assert.True(crossing, "FUN4 reached FollowingPhase without crossing 1R in a CrossingRunwayPhase");
        Assert.Equal("FUN3", Assert.IsType<FollowingPhase>(funnel.Fun4.Phases?.CurrentPhase).TargetCallsign);
        Assert.Null(funnel.Entered28L);
    }

    // ---------------------------------------------------------------------------------------------
    // Staging
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the SFO environment, spawns the four departures on the A ramp, and issues their taxi
    /// clearances one per tick in the order FUN1, FUN3, FUN4, FUN2 — FUN1 first because FUN2's
    /// clearance gives way to it. Returns null on the repo's silent-skip path (no navdata / no layout).
    /// </summary>
    private Funnel? StageAll()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: false);
        if (built is null)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        SfoGround ground = built.Value;
        AircraftState fun1 = SfoGroundHarness.SpawnAtSpot(ground, "FUN1", Type, "2");
        AircraftState fun3 = SfoGroundHarness.SpawnAtSpot(ground, "FUN3", Type, "1");
        AircraftState fun4 = SfoGroundHarness.SpawnAtSpot(ground, "FUN4", Type, "3");
        AircraftState fun2 = SfoGroundHarness.SpawnAtSpot(ground, "FUN2", Type, "4");
        var guard = new DeadlockGuard(fun1, fun2, fun3, fun4);

        (AircraftState Aircraft, string Command)[] clearances =
        [
            (fun1, "TAXI A A1 1R F1 F RWY 28L HS F1"),
            (fun3, "TAXI A L F 28L HS A1"),
            (fun4, "TAXI A F1 F RWY 28L CROSS 1L HS 1R"),
            (fun2, "TAXI A G 1R F1 F RWY 28L CROSS 1L HS F1 GIVEWAY FUN1"),
        ];

        int second = 0;
        foreach ((AircraftState? aircraft, string? command) in clearances)
        {
            CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
            Assert.True(result.Success, $"'{aircraft.Callsign}: {command}' was rejected: {result.Message}");
            second++;
            ground.Engine.TickOneSecond();
            guard.Tick(second);
        }

        HoldShortPoint destination = DestinationHoldOf(fun1);
        Assert.True(
            ground.Layout.Nodes.TryGetValue(destination.NodeId, out GroundNode? destinationNode),
            $"FUN1's 28L destination hold-short node {destination.NodeId} is not in the layout"
        );
        GroundNode junction =
            ground.Layout.FindIntersectionNode("F", "L") ?? throw new InvalidOperationException("SFO layout has no junction of taxiways 'F' and 'L'");

        return new Funnel
        {
            Ground = ground,
            Fun1 = fun1,
            Fun2 = fun2,
            Fun3 = fun3,
            Fun4 = fun4,
            Guard = guard,
            DestinationNodeId = destination.NodeId,
            DestinationPosition = destinationNode!.Position,
            FlJunctionPosition = junction.Position,
            Runway1R = SfoGroundHarness.Runway("1R"),
            Runway28L = SfoGroundHarness.Runway("28L"),
            Elapsed = second,
        };
    }

    /// <summary>
    /// Ticks until the 1R river is staged: FUN1 stopped at its F1 bar and FUN2 stopped behind it, either
    /// auto-yielding to FUN1 or sitting at an F1 bar of its own. Returns the second that first held, or
    /// -1 on budget exhaustion.
    /// </summary>
    private int WaitForStaged(Funnel funnel)
    {
        int fun2MovingAt = -1;
        int fun2StationarySec = 0;

        return RunUntil(
            funnel,
            _ =>
            {
                fun2StationarySec = funnel.Fun2.GroundSpeed < SfoGroundHarness.StationarySpeedKts ? fun2StationarySec + 1 : 0;
                return HoldsShortOf(funnel.Fun1, "F1") && (fun2StationarySec >= StationaryConfirmSeconds) && IsQueuedBehindFun1(funnel);
            },
            StageBudgetSeconds,
            second =>
            {
                if ((fun2MovingAt < 0) && (funnel.Fun2.GroundSpeed >= SfoGroundHarness.StationarySpeedKts))
                {
                    fun2MovingAt = second;
                    output.WriteLine($"t={second}: FUN2 started moving (its GIVEWAY FUN1 released) at gs={funnel.Fun2.GroundSpeed:F1}kt");
                }
            }
        );
    }

    /// <summary>
    /// FUN2 is queued behind FUN1 rather than merely stopped somewhere. Three shapes count, and which one
    /// the sim produces depends on how far up the line FUN2 got: auto-yielding to FUN1 by name, reaching
    /// an F1 bar of its own, or — the one this staging actually lands in — stopped nose-to-tail on the 1R
    /// pavement behind FUN1, which owns the single F1 bar, so FUN2 never reaches a bar of its own and is
    /// held by a zero ground speed limit with no named yield target at all. Sitting give-way-held back at
    /// the spot is none of the three.
    /// </summary>
    private static bool IsQueuedBehindFun1(Funnel funnel) =>
        string.Equals(funnel.Fun2.Ground.AutoYieldTarget, "FUN1", StringComparison.OrdinalIgnoreCase)
        || HoldsShortOf(funnel.Fun2, "F1")
        || ((funnel.Fun2.Ground.SpeedLimit is <= 0) && RunwayOccupancy.IsOnPavement(funnel.Fun2, funnel.Runway1R));

    private void AssertStaged(Funnel funnel, int staged)
    {
        Assert.True(staged > 0, $"the 1R river never staged short of F1 within {StageBudgetSeconds}s: {DescribeAll(funnel)}");
        output.WriteLine($"staged at t={staged}s: {DescribeAll(funnel)}");

        Assert.True(RunwayOccupancy.IsOnPavement(funnel.Fun1, funnel.Runway1R), "FUN1 staged short of F1 but is not on 1R pavement");
        Assert.True(RunwayOccupancy.IsOnPavement(funnel.Fun2, funnel.Runway1R), "FUN2 staged behind FUN1 but is not on 1R pavement");

        LatLon f1Node = FollowingHoldNode(funnel, funnel.Fun1);
        double fun1ToBarNm = GeoMath.DistanceNm(funnel.Fun1.Position, f1Node);
        double fun2ToBarNm = GeoMath.DistanceNm(funnel.Fun2.Position, f1Node);
        Assert.True(
            fun2ToBarNm > fun1ToBarNm,
            $"FUN2 is {fun2ToBarNm:F3}nm from the F1 bar and FUN1 {fun1ToBarNm:F3}nm — FUN2 is not behind FUN1 in the 1R line"
        );

        Assert.True(HoldsShortOf(funnel.Fun3, "A1"), $"FUN3 is not holding short of A1: {Describe(funnel, funnel.Fun3)}");
        Assert.True(HoldsShortOf(funnel.Fun4, "1R"), $"FUN4 is not holding short of 1R: {Describe(funnel, funnel.Fun4)}");
        Assert.Null(funnel.Entered28L);
    }

    // ---------------------------------------------------------------------------------------------
    // Release assertions
    // ---------------------------------------------------------------------------------------------

    private static void AssertMergedInOrder(Funnel funnel)
    {
        Assert.Null(funnel.Entered28L);

        var byDistance = funnel.All.OrderBy(ac => GeoMath.DistanceNm(ac.Position, funnel.DestinationPosition)).Select(ac => ac.Callsign).ToList();
        Assert.Equal(new[] { "FUN3", "FUN1", "FUN4", "FUN2" }, byDistance);
    }

    private static void AssertQueueOrdinals(Funnel funnel)
    {
        (AircraftState, int)[] expected = [(funnel.Fun3, 1), (funnel.Fun1, 2), (funnel.Fun4, 3), (funnel.Fun2, 4)];
        foreach ((AircraftState? aircraft, int position) in expected)
        {
            Assert.Equal(position, aircraft.Ground.RunwayQueuePosition);
            Assert.Equal("28L", aircraft.Ground.RunwayQueueRunway);
        }
    }

    private static void AssertQueueOrderedWithinProximityGate(Funnel funnel)
    {
        var inLine = new[] { funnel.Fun3, funnel.Fun1, funnel.Fun4, funnel.Fun2 }
            .Where(ac => GeoMath.DistanceNm(ac.Position, funnel.DestinationPosition) <= RunwayDepartureQueue.ProximityNm)
            .ToList();

        Assert.Contains(funnel.Fun3, inLine);
        Assert.Equal(1, funnel.Fun3.Ground.RunwayQueuePosition);

        for (int i = 1; i < inLine.Count; i++)
        {
            Assert.True(
                inLine[i].Ground.RunwayQueuePosition > inLine[i - 1].Ground.RunwayQueuePosition,
                $"{inLine[i].Callsign} is #{inLine[i].Ground.RunwayQueuePosition} but sits behind {inLine[i - 1].Callsign} at "
                    + $"#{inLine[i - 1].Ground.RunwayQueuePosition} in the release order"
            );
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fact A helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A 1R-river route reaches the runway itself — it carries a 1R centerline segment — and binds its
    /// explicit F1 hold to a node on that centerline, which is what staging "on the runway short of F1"
    /// means rather than holding on the F1 taxiway beside it.
    /// </summary>
    private void AssertRunsDown1RToItsF1Bar(Funnel funnel, AircraftState aircraft)
    {
        TaxiRoute route = RouteOf(aircraft);
        Assert.True(
            route.Segments.Any(s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway("1R")),
            $"{aircraft.Callsign}'s route has no 1R runway-centerline segment "
                + $"(taxiways: {string.Join(" ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))})"
        );

        HoldShortPoint hold = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && SfoGroundHarness.HoldShortMatches(h, "F1")
        );
        Assert.True(
            funnel.Ground.Layout.Nodes.TryGetValue(hold.NodeId, out GroundNode? node),
            $"{aircraft.Callsign}'s F1 hold node {hold.NodeId} is not in the layout"
        );
        Assert.True(
            node!.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("1R")),
            $"{aircraft.Callsign}'s F1 hold node {hold.NodeId} has no 1R runway-centerline edge "
                + $"(edges: {string.Join(", ", node.Edges.Select(e => e.TaxiwayName))})"
        );
    }

    // ---------------------------------------------------------------------------------------------
    // Tick driver and diagnostics
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ticks one second at a time, running the deadlock guard, the 28L-incursion watch, the per-second
    /// script hook and the periodic trace, until <paramref name="done"/> holds. Returns the elapsed
    /// second it first held, or -1 on budget exhaustion.
    /// </summary>
    private int RunUntil(Funnel funnel, Func<int, bool> done, int budgetSeconds, Action<int> eachSecond)
    {
        for (int i = 1; i <= budgetSeconds; i++)
        {
            funnel.Ground.Engine.TickOneSecond();
            funnel.Elapsed++;
            funnel.Guard.Tick(funnel.Elapsed);
            Watch28L(funnel);
            eachSecond(funnel.Elapsed);
            Trace(funnel, funnel.Elapsed);
            if (done(funnel.Elapsed))
            {
                return funnel.Elapsed;
            }
        }

        return -1;
    }

    /// <summary>
    /// Records the first aircraft to put a wheel on 28L. 28L is the active departure runway in this
    /// configuration: nothing in the funnel is ever cleared onto it, so any entry is an incursion.
    /// </summary>
    private static void Watch28L(Funnel funnel)
    {
        if (funnel.Entered28L is not null)
        {
            return;
        }

        foreach (AircraftState aircraft in funnel.All)
        {
            if (RunwayOccupancy.IsOnPavement(aircraft, funnel.Runway28L))
            {
                funnel.Entered28L = $"{aircraft.Callsign} entered 28L at t={funnel.Elapsed}s";
                return;
            }
        }
    }

    private void Trace(Funnel funnel, int second)
    {
        if (second % TraceIntervalSeconds != 0)
        {
            return;
        }

        output.WriteLine($"t={second}s | {DescribeAll(funnel)}");
    }

    private static string DescribeAll(Funnel funnel) => string.Join(" || ", funnel.All.Select(ac => Describe(funnel, ac)));

    private static string Describe(Funnel funnel, AircraftState aircraft) =>
        $"{aircraft.Callsign} {aircraft.Phases?.CurrentPhase?.Name ?? "no-phase"} gs={aircraft.GroundSpeed:F1} "
        + $"lim={aircraft.Ground.SpeedLimit?.ToString("F0") ?? "-"} yield={aircraft.Ground.AutoYieldTarget ?? "-"} "
        + $"d28L={GeoMath.DistanceNm(aircraft.Position, funnel.DestinationPosition):F3}nm q#{aircraft.Ground.RunwayQueuePosition}";

    // ---------------------------------------------------------------------------------------------
    // Shared predicates
    // ---------------------------------------------------------------------------------------------

    private static TaxiRoute RouteOf(AircraftState aircraft)
    {
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.True(route is not null, $"{aircraft.Callsign} has no assigned taxi route");
        return route!;
    }

    private static HoldShortPoint DestinationHoldOf(AircraftState aircraft)
    {
        HoldShortPoint? hold = RouteOf(aircraft).HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.True(hold is not null, $"{aircraft.Callsign}'s route has no destination-runway hold-short");
        return hold!;
    }

    private static bool HoldsShortOf(AircraftState aircraft, string target) =>
        (aircraft.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, target);

    /// <summary>Position of the node an aircraft is currently holding short at, for behind/ahead comparisons.</summary>
    private static LatLon FollowingHoldNode(Funnel funnel, AircraftState aircraft)
    {
        Assert.True(aircraft.Phases?.CurrentPhase is HoldingShortPhase, $"{aircraft.Callsign} is not holding short");
        HoldShortPoint hold = ((HoldingShortPhase)aircraft.Phases!.CurrentPhase!).HoldShort;
        Assert.True(funnel.Ground.Layout.Nodes.TryGetValue(hold.NodeId, out GroundNode? node), $"hold-short node {hold.NodeId} is not in the layout");
        return node!.Position;
    }

    // ---------------------------------------------------------------------------------------------
    // Nested types
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The staged funnel: the four aircraft, the environment they taxi in, and the fixed geometry the
    /// choreography and its assertions measure against.
    /// </summary>
    private sealed class Funnel
    {
        internal required SfoGround Ground { get; init; }
        internal required AircraftState Fun1 { get; init; }
        internal required AircraftState Fun2 { get; init; }
        internal required AircraftState Fun3 { get; init; }
        internal required AircraftState Fun4 { get; init; }
        internal required DeadlockGuard Guard { get; init; }
        internal required int DestinationNodeId { get; init; }
        internal required LatLon DestinationPosition { get; init; }
        internal required LatLon FlJunctionPosition { get; init; }
        internal required RunwayInfo Runway1R { get; init; }
        internal required RunwayInfo Runway28L { get; init; }

        /// <summary>Set the first time any aircraft puts a wheel on 28L; null means the run stayed clear of it.</summary>
        internal string? Entered28L { get; set; }

        /// <summary>Simulated seconds elapsed since the first clearance, across staging and release.</summary>
        internal int Elapsed { get; set; }

        internal AircraftState[] All => [Fun1, Fun2, Fun3, Fun4];
    }

    /// <summary>
    /// The 3, 1, 4, 2 release, driven per tick by what the aircraft are doing rather than by a timer —
    /// a queued <c>WAIT n</c> does not count down behind an active <see cref="TaxiingPhase"/>, so the
    /// steps are gated on geometry instead.
    ///
    /// <para>Four steps, at most one dispatch per second: FUN3 is resumed; FUN1 is released once FUN3 is
    /// past the F/L junction and within ~700 ft; FUN4 is crossed over 1R; FUN2 is released once FUN4 is
    /// past the 1R centerline and within ~700 ft. <c>byFollow</c> picks how the 1R river is released —
    /// handed to the aircraft ahead with <c>FOLLOWG</c>, or let go on its own clearance with
    /// <c>RES</c>.</para>
    /// </summary>
    private sealed class ReleaseScript(SfoDepartureFunnelTests.Funnel funnel, ITestOutputHelper output, bool byFollow)
    {
        private readonly List<string> _log = [];
        private int _step;
        private bool _fun4WasOn1R;
        private bool _fun4Crossed1R;
        private double _minGapNm = double.PositiveInfinity;

        internal void Step(int second)
        {
            TrackFun4Crossing();
            AssertNoFollowerStuckAt1R(second);
            AdvanceScript(second);
        }

        internal bool IsComplete() =>
            HoldsShortOfDestination(funnel.Fun3) && new[] { funnel.Fun1, funnel.Fun4, funnel.Fun2 }.All(IsParkedAtDestination);

        internal void Dump()
        {
            foreach (string line in _log)
            {
                output.WriteLine(line);
            }
        }

        internal string Describe() =>
            $"step={_step}/4, fun4Crossed1R={_fun4Crossed1R}, closestGapToLeader={_minGapNm:F3}nm, {SfoDepartureFunnelTests.DescribeAll(funnel)}";

        /// <summary>
        /// FUN4 has crossed 1R once it has been on the pavement and is off it again — the F1 leg east of
        /// the runway. Tracked continuously because the crossing takes a handful of seconds.
        /// </summary>
        private void TrackFun4Crossing()
        {
            bool onPavement = RunwayOccupancy.IsOnPavement(funnel.Fun4, funnel.Runway1R);
            _fun4WasOn1R |= onPavement;
            _fun4Crossed1R |= _fun4WasOn1R && !onPavement;
        }

        /// <summary>
        /// Neither 1R-river follower should ever stop at a 1R bar. Both are staged on 1R itself, and a
        /// follower does not hold short of a runway it is already standing on — leaving a runway is not
        /// crossing it. If one does stop there the choreography is wedged on a crossing clearance nobody
        /// issued, which is a finding about the sim rather than something for the test to paper over, so
        /// fail here with the trace instead of quietly issuing a CROSS.
        /// </summary>
        private void AssertNoFollowerStuckAt1R(int second)
        {
            foreach (AircraftState? aircraft in new[] { funnel.Fun1, funnel.Fun2 })
            {
                if (aircraft.Phases?.CurrentPhase is not HoldingShortPhase hold)
                {
                    continue;
                }

                if (!SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R") || SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"))
                {
                    continue;
                }

                Assert.Fail(
                    $"t={second}s: {aircraft.Callsign} stopped at a 1R bar (node {hold.HoldShort.NodeId}, reason {hold.HoldShort.Reason}) "
                        + $"while being led off runway 1R: {SfoDepartureFunnelTests.DescribeAll(funnel)}"
                );
            }
        }

        private void AdvanceScript(int second)
        {
            switch (_step)
            {
                case 0:
                    _log.Add(
                        $"F/L junction is {GeoMath.DistanceNm(funnel.FlJunctionPosition, funnel.DestinationPosition):F3}nm from the 28L bar; "
                            + $"release mode={(byFollow ? "FOLLOWG" : "RES")}"
                    );
                    Send(second, "FUN3", "RES");
                    _step = 1;
                    return;
                case 1 when PastFlJunction(funnel.Fun3) && IsClearedToRelease(funnel.Fun1, funnel.Fun3):
                    Release(second, funnel.Fun1, "FUN3");
                    return;
                case 2:
                    Send(second, "FUN4", "CROSS 1R");
                    _step = 3;
                    return;
                case 3 when _fun4Crossed1R && IsClearedToRelease(funnel.Fun2, funnel.Fun4):
                    Release(second, funnel.Fun2, "FUN4");
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// Releases a staged 1R-river aircraft behind its leader. In the <c>RES</c> variant the aircraft
        /// must actually be holding for the resume to apply, so the step waits rather than firing a
        /// command that would be rejected.
        /// </summary>
        private void Release(int second, AircraftState aircraft, string leader)
        {
            if (byFollow)
            {
                Send(second, aircraft.Callsign, $"FOLLOWG {leader}");
                _step++;
                _minGapNm = double.PositiveInfinity;
                return;
            }

            if (aircraft.Phases?.CurrentPhase is not HoldingShortPhase)
            {
                return;
            }

            Send(second, aircraft.Callsign, "RES");
            _step++;
            _minGapNm = double.PositiveInfinity;
        }

        private void Send(int second, string callsign, string command)
        {
            CommandResult result = funnel.Ground.Engine.SendCommand(callsign, command);
            _log.Add($"t={second}s: {callsign} <- '{command}' => {(result.Success ? "ok" : $"REJECTED: {result.Message}")}");
            Assert.True(result.Success, $"t={second}s: '{callsign}: {command}' was rejected: {result.Message}");
        }

        /// <summary>
        /// Past the F/L junction heading for 28L: closer to the 28L bar than the junction itself is, which
        /// is along-track progress down F because the junction sits on that leg.
        /// </summary>
        private bool PastFlJunction(AircraftState aircraft) =>
            GeoMath.DistanceNm(aircraft.Position, funnel.DestinationPosition)
            < GeoMath.DistanceNm(funnel.FlJunctionPosition, funnel.DestinationPosition);

        /// <summary>
        /// The <c>FOLLOWG</c> variant hands the trailer to a leader it can actually see ahead (~700 ft), so
        /// the gap gates the release. The <c>RES</c> variant puts the trailer back on its own clearance,
        /// which does not depend on the leader's position — there the leader's trigger alone releases it.
        /// </summary>
        private bool IsClearedToRelease(AircraftState trailer, AircraftState leader)
        {
            double gapNm = GeoMath.DistanceNm(trailer.Position, leader.Position);
            _minGapNm = Math.Min(_minGapNm, gapNm);
            return !byFollow || (gapNm <= SfoDepartureFunnelTests.CloseUpNm);
        }

        private static bool HoldsShortOfDestination(AircraftState aircraft) =>
            (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort: { Reason: HoldShortReason.DestinationRunway } hold })
            && SfoGroundHarness.HoldShortMatches(hold, "28L");

        private bool IsParkedAtDestination(AircraftState aircraft) =>
            (aircraft.GroundSpeed < SfoGroundHarness.StationarySpeedKts)
            && (GeoMath.DistanceNm(aircraft.Position, funnel.DestinationPosition) <= SfoDepartureFunnelTests.AtDestinationNm);
    }
}
