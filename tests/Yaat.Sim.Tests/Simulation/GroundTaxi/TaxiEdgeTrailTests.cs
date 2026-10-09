using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <see cref="AircraftGroundOps.TaxiEdgeTrail"/>: the straight taxi edges a ground aircraft has driven, oldest first, written
/// once per sim-second whatever its phase except the runway rolls (line-up, takeoff, landing rollout).
///
/// <para>The geometry is <see cref="OakCrossThenContinueTests"/>': a C560 on KOAK taxiway B short of 28R, facing the
/// runway, cleared <c>TAXI B W 30</c>, which crosses 28R and then 28L on B.</para>
/// </summary>
public class TaxiEdgeTrailTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C560";
    private const string AirportId = "OAK";
    private const int HoldBudgetSeconds = 300;

    /// <summary>
    /// Taxiing to the 28R bar, crossing 28R on <c>CROSS</c> and running on to the 28L bar, the trail lists the B edges the
    /// aircraft drove, in the order it drove them: each is an edge of its route, no edge twice in a row, and the edge into the
    /// near 28R bar comes before the edge out of the far one, which comes before the edge into the 28L bar.
    /// </summary>
    [Fact]
    public void RecordsEdgesAcrossTaxiToCrossingToExit()
    {
        if (Start(autoCross: false, route: "TAXI B W 30", edgesBack: 1) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Aircraft.Ground.AssignedTaxiRoute);
        HashSet<IGroundEdge> routeEdges = [.. route.Segments.Select(segment => segment.Edge.Edge)];

        Assert.True(TickUntil(run.Engine, HoldBudgetSeconds, () => IsHoldingShortOf(run.Aircraft, "28R")), "never held short of 28R");
        Assert.True(run.Engine.SendCommand(Callsign, "CROSS").Success);
        Assert.True(TickUntil(run.Engine, HoldBudgetSeconds, () => IsHoldingShortOf(run.Aircraft, "28L")), "never held short of 28L");

        IReadOnlyList<GroundEdge> trail = ResolveTrail(run);
        Assert.All(trail, edge => Assert.Contains(edge, routeEdges));
        for (int i = 1; i < trail.Count; i++)
        {
            Assert.NotSame(trail[i - 1], trail[i]);
        }

        GroundNode farBar28R = run.Bar28R.Single(node => node.Id != run.NearBar28R.Id);
        int intoNear28R = IndexTouching(trail, run.NearBar28R.Id);
        int outOfFar28R = IndexTouching(trail, farBar28R.Id);
        int into28L = run.Bar28L.Select(node => IndexTouching(trail, node.Id)).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        Assert.True(intoNear28R >= 0, "no edge into the near 28R bar");
        Assert.True(outOfFar28R > intoNear28R, $"edge out of the far 28R bar at {outOfFar28R}, not after the near bar's at {intoNear28R}");
        Assert.True(into28L > outOfFar28R, $"edge into the 28L bar at {into28L}, not after the far 28R bar's at {outOfFar28R}");
    }

    /// <summary>A second <c>TAXI</c> part-way along the route replaces the route but keeps the edges already driven, first in the trail.</summary>
    [Fact]
    public void SurvivesTaxiReplacement()
    {
        if (Start(autoCross: true, route: "TAXI B W 30", edgesBack: 1) is not { } run)
        {
            return;
        }

        Assert.True(
            TickUntil(run.Engine, HoldBudgetSeconds, () => run.Aircraft.Ground.TaxiEdgeTrail.Edges.Count >= 2),
            "trail never reached two edges"
        );
        List<TaxiTrailEdge> before = [.. run.Aircraft.Ground.TaxiEdgeTrail.Edges];
        TaxiRoute firstRoute = Assert.IsType<TaxiRoute>(run.Aircraft.Ground.AssignedTaxiRoute);

        CommandResult retaxi = run.Engine.SendCommand(Callsign, "TAXI B W 30");
        Assert.True(retaxi.Success, retaxi.Message);
        Assert.NotSame(firstRoute, run.Aircraft.Ground.AssignedTaxiRoute);
        TickUntil(run.Engine, 20, () => false);

        IReadOnlyList<TaxiTrailEdge> after = run.Aircraft.Ground.TaxiEdgeTrail.Edges;
        output.WriteLine($"before={before.Count} edges, after={after.Count} edges");
        Assert.True(after.Count > before.Count, "the trail did not grow after the new TAXI");
        Assert.Equal(before, after.Take(before.Count));
    }

    /// <summary>
    /// A taxi longer than the cap keeps only the newest edges: the oldest edges it drove are dropped, what remains is the tail
    /// of what it drove, and the edges after the oldest kept one sum to no more than the cap.
    /// </summary>
    [Fact]
    public void CapsAtAbout3000Ft()
    {
        if (Start(autoCross: true, route: "TAXI B W 30", edgesBack: 1) is not { } run)
        {
            return;
        }

        List<TaxiTrailEdge> driven = [];
        TaxiEdgeTrail trail = run.Aircraft.Ground.TaxiEdgeTrail;
        bool dropped = TickUntil(
            run.Engine,
            HoldBudgetSeconds * 2,
            () =>
            {
                if ((trail.Newest is { } newest) && ((driven.Count == 0) || (driven[^1] != newest)))
                {
                    driven.Add(newest);
                }

                return (driven.Count > 0) && (trail.Edges[0] != driven[0]);
            }
        );
        double drivenFt = driven.Sum(edge => edge.LengthFt);
        output.WriteLine($"driven {driven.Count} edges / {drivenFt:F0} ft; trail {trail.Edges.Count} edges / {trail.TotalLengthFt:F0} ft");

        Assert.True(dropped, $"the trail never dropped its oldest edge over {drivenFt:F0} ft of taxi");
        Assert.Equal(driven.TakeLast(trail.Edges.Count), trail.Edges);
        Assert.True(trail.TotalLengthFt - trail.Edges[0].LengthFt <= TaxiEdgeTrail.CapFt, $"trail {trail.TotalLengthFt:F0} ft past the cap");
        Assert.True(trail.TotalLengthFt >= TaxiEdgeTrail.CapFt, $"trail {trail.TotalLengthFt:F0} ft kept less than the cap after a drop");
    }

    /// <summary>A departure taxied to the 28R bar has a trail; after <c>CTO</c> it is empty the second the aircraft lifts off.</summary>
    [Fact]
    public void ClearedWhenAirborne()
    {
        if (Start(autoCross: false, route: "TAXI B 28R", edgesBack: 3) is not { } run)
        {
            return;
        }

        Assert.True(
            TickUntil(run.Engine, HoldBudgetSeconds, () => run.Aircraft.Phases?.CurrentPhase is HoldingShortPhase),
            "never held short of 28R"
        );
        Assert.NotEmpty(run.Aircraft.Ground.TaxiEdgeTrail.Edges);

        CommandResult cto = run.Engine.SendCommand(Callsign, "CTO");
        Assert.True(cto.Success, cto.Message);
        Assert.True(TickUntil(run.Engine, HoldBudgetSeconds, () => !run.Aircraft.IsOnGround), "never lifted off");

        output.WriteLine($"airborne in {run.Aircraft.Phases?.CurrentPhase?.Name} at {run.Aircraft.Altitude:F0} ft");
        Assert.Empty(run.Aircraft.Ground.TaxiEdgeTrail.Edges);
    }

    /// <summary>
    /// A snapshot taken mid-taxi, sent through the recording JSON and restored into a second engine, carries the trail, and
    /// the restored aircraft goes on with the same positions and the same trail as the run that was never interrupted.
    /// </summary>
    [Fact]
    public void SnapshotRoundTrip_TrailMatchesUninterrupted()
    {
        if (Start(autoCross: true, route: "TAXI B W 30", edgesBack: 1) is not { } run)
        {
            return;
        }

        Assert.True(
            TickUntil(run.Engine, HoldBudgetSeconds, () => run.Aircraft.Ground.TaxiEdgeTrail.Edges.Count >= 2),
            "trail never reached two edges"
        );
        SimulationEngine restoredEngine = NewEngine(run.GroundData, autoCross: true);
        restoredEngine.RestoreFromSnapshot(RoundTrip(run.Engine.CaptureSnapshot()));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));
        Assert.Equal(run.Aircraft.Ground.TaxiEdgeTrail.Edges, restored.Ground.TaxiEdgeTrail.Edges);

        for (int second = 1; second <= 120; second++)
        {
            run.Engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(run.Aircraft.Position, restored.Position);
            Assert.Equal(run.Aircraft.Ground.TaxiEdgeTrail.Edges, restored.Ground.TaxiEdgeTrail.Edges);
        }

        output.WriteLine(
            $"after 120 s: {run.Aircraft.Ground.TaxiEdgeTrail.Edges.Count} edges, {run.Aircraft.Ground.TaxiEdgeTrail.TotalLengthFt:F0} ft"
        );
    }

    /// <summary>
    /// <c>WARPG</c> moves the aircraft across the field, so the edges it drove before are no path to where it is: the warp
    /// empties the trail, and a new <c>TAXI</c> from there records only the edges driven after the warp.
    /// </summary>
    [Fact]
    public void WarpG_ClearsTheTrail()
    {
        if (Start(autoCross: true, route: "TAXI B W 30", edgesBack: 1) is not { } run)
        {
            return;
        }

        Assert.True(
            TickUntil(run.Engine, HoldBudgetSeconds, () => run.Aircraft.Ground.TaxiEdgeTrail.Edges.Count >= 2),
            "trail never reached two edges"
        );
        List<TaxiTrailEdge> beforeWarp = [.. run.Aircraft.Ground.TaxiEdgeTrail.Edges];

        GroundNode warpTo = run.Bar28L.OrderBy(node => GeoMath.DistanceNm(node.Position, run.NearBar28R.Position)).First();
        CommandResult warp = run.Engine.SendCommand(Callsign, $"WARPG #{warpTo.Id}");
        Assert.True(warp.Success, warp.Message);
        Assert.Empty(run.Aircraft.Ground.TaxiEdgeTrail.Edges);

        CommandResult taxi = run.Engine.SendCommand(Callsign, "TAXI B W 30");
        Assert.True(taxi.Success, taxi.Message);
        Assert.True(
            TickUntil(run.Engine, HoldBudgetSeconds, () => run.Aircraft.Ground.TaxiEdgeTrail.Edges.Count >= 2),
            "trail never reached two edges after the warp"
        );

        IReadOnlyList<TaxiTrailEdge> afterWarp = run.Aircraft.Ground.TaxiEdgeTrail.Edges;
        output.WriteLine($"before the warp {beforeWarp.Count} edges; after it {afterWarp.Count} edges");
        Assert.DoesNotContain(afterWarp, edge => beforeWarp.Contains(edge));
    }

    /// <summary>
    /// An arrival rolling out on 28R passes over the taxiways that cross the runway without driving them, so nothing is
    /// recorded while it rolls out; the trail starts with the edges of the exit route it turns off on.
    /// </summary>
    [Fact]
    public void LandingRollout_RecordsNoEdgesUntilTheExit()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand(AirportId, "28R", "B738", "SWA123") is not { } arrival)
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return;
        }

        AircraftState aircraft = arrival.Aircraft;
        int rolloutSeconds = 0;
        bool recording = TickUntil(
            arrival.Engine,
            HoldBudgetSeconds,
            () =>
            {
                if (aircraft.IsOnGround && (aircraft.Phases?.CurrentPhase is LandingPhase) && (aircraft.GroundSpeed > 0.0))
                {
                    rolloutSeconds++;
                    Assert.Empty(aircraft.Ground.TaxiEdgeTrail.Edges);
                }

                return aircraft.Ground.TaxiEdgeTrail.Edges.Count > 0;
            }
        );

        output.WriteLine($"{rolloutSeconds} s of rollout; first edge in {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(rolloutSeconds > 0, "never rolled out on the runway");
        Assert.True(recording, "never recorded an edge after the rollout");
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(aircraft.Phases?.CurrentPhase);
        RunwayExitPhaseDto exitDto = Assert.IsType<RunwayExitPhaseDto>(exit.ToSnapshot());
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        GroundEdge first = Assert.IsType<GroundEdge>(aircraft.Ground.TaxiEdgeTrail.Edges[0].Resolve(layout));
        output.WriteLine(
            $"exit {exitDto.ExitTaxiway} at node #{exitDto.ExitNodeId}; first edge {first.TaxiwayName} #{first.Nodes[0].Id}-#{first.Nodes[1].Id}"
        );
        Assert.True(
            first.MatchesTaxiway(exitDto.ExitTaxiway ?? ""),
            $"first edge on {first.TaxiwayName}, not the exit taxiway {exitDto.ExitTaxiway}"
        );
    }

    /// <summary>A stopped aircraft adds nothing: held short of 28R for a minute, its trail stays exactly as it was.</summary>
    [Fact]
    public void StoppedAircraft_TrailUnchanged()
    {
        if (Start(autoCross: false, route: "TAXI B W 30", edgesBack: 3) is not { } run)
        {
            return;
        }

        Assert.True(
            TickUntil(
                run.Engine,
                HoldBudgetSeconds,
                () => (run.Aircraft.Phases?.CurrentPhase is HoldingShortPhase) && (run.Aircraft.GroundSpeed <= 0.0)
            ),
            "never came to rest short of 28R"
        );
        List<TaxiTrailEdge> atRest = [.. run.Aircraft.Ground.TaxiEdgeTrail.Edges];
        Assert.NotEmpty(atRest);

        for (int second = 1; second <= 60; second++)
        {
            run.Engine.TickOneSecond();
            Assert.Equal(0.0, run.Aircraft.GroundSpeed);
            Assert.Equal(atRest, run.Aircraft.Ground.TaxiEdgeTrail.Edges);
        }
    }

    /// <summary>
    /// The entry node recorded for an edge is the end behind the aircraft's movement: taxiing nose-first along a KOAK B edge it
    /// is the end behind the nose, either way along the edge; on a tug pushing it tail-first the nose points at the other end,
    /// and the entry is the end the push moves away from.
    /// </summary>
    [Fact]
    public void EntryNodeOf_ForwardTaxiAndPushback_IsTheEndBehindTheMovement()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        GroundNode near = chain[3];
        GroundNode far = chain[4];
        GroundEdge edge = FollowCornerGeometry.EdgeBetween(near, far);
        LatLon midway = FollowCornerGeometry.Between(near.Position, far.Position, 0.5);

        AircraftState towardFar = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            Callsign,
            AircraftType,
            midway,
            FollowCornerGeometry.Facing(near, far)
        );
        Assert.Equal(near.Id, TaxiEdgeTrail.EntryNodeOf(edge, towardFar).Id);

        AircraftState towardNear = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            Callsign,
            AircraftType,
            midway,
            FollowCornerGeometry.Facing(far, near)
        );
        Assert.Equal(far.Id, TaxiEdgeTrail.EntryNodeOf(edge, towardNear).Id);

        AircraftState pushedTowardNear = FollowCornerGeometry.Spawn(
            FollowCornerGeometry.AirportId,
            Callsign,
            AircraftType,
            midway,
            FollowCornerGeometry.Facing(near, far)
        );
        pushedTowardNear.Ground.PushbackTrueHeading = FollowCornerGeometry.Facing(far, near);
        Assert.Equal(far.Id, TaxiEdgeTrail.EntryNodeOf(edge, pushedTowardNear).Id);
    }

    /// <summary>
    /// Recording an edge with an entry node that is not one of its ends is refused, and leaves the trail as it was; recording
    /// it with one of its ends keeps that end as the edge's entry node.
    /// </summary>
    [Fact]
    public void Record_EntryNodeNotAnEndOfTheEdge_Throws()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        GroundEdge edge = FollowCornerGeometry.EdgeBetween(chain[3], chain[4]);
        var trail = new TaxiEdgeTrail();

        Assert.Throws<ArgumentException>(() => trail.Record(edge, chain[5]));
        Assert.Empty(trail.Edges);

        trail.Record(edge, chain[4]);
        Assert.Equal(chain[4].Id, Assert.Single(trail.Edges).EntryNodeId);
    }

    /// <summary>
    /// A snapshot trail edge whose entry node is not one of its ends — node 0, what a snapshot written without the field loads —
    /// is refused on load with the edge named, rather than restoring a trail whose turned-about test reads a node that is not there.
    /// </summary>
    [Fact]
    public void FromSnapshot_EntryNodeNotAnEndOfTheEdge_Throws()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = FollowCornerGeometry.BChain(layout);
        GroundEdge edge = FollowCornerGeometry.EdgeBetween(chain[3], chain[4]);
        List<TaxiTrailEdgeDto> dto =
        [
            new TaxiTrailEdgeDto
            {
                NodeA = edge.Nodes[0].Id,
                NodeB = edge.Nodes[1].Id,
                LengthFt = edge.DistanceNm * GeoMath.FeetPerNm,
                EntryNodeId = 0,
            },
        ];

        InvalidDataException thrown = Assert.Throws<InvalidDataException>(() => TaxiEdgeTrail.FromSnapshot(dto));
        output.WriteLine(thrown.Message);
        Assert.Contains($"#{edge.Nodes[0].Id}-#{edge.Nodes[1].Id}", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An edge recorded while the aircraft is still turning at a sharp unfilleted KOAK junction, its heading more than 90° off the
    /// edge it is turning onto, was entered from the node it shares with the trail's newest edge, the junction — not from the far
    /// end its heading alone reads as behind it. With nothing recorded before it, the heading is all there is to go on.
    /// </summary>
    [Fact]
    public void EntryNodeOf_RecordedMidTurnAtASharpJunction_IsTheSharedNode()
    {
        if (FollowCornerGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        (GroundNode farIn, GroundNode junction, GroundNode farOut, GroundEdge inEdge, GroundEdge outEdge, double turnDeg) =
            FollowCornerGeometry.SharpTurn(layout);
        TrueHeading stillInbound = FollowCornerGeometry.Facing(farIn, junction);
        double offOutDeg = GeoMath.AbsBearingDifference(stillInbound.Degrees, GeoMath.BearingTo(junction.Position, farOut.Position));
        output.WriteLine($"junction #{junction.Id}: turn {turnDeg:F0}°, heading {offOutDeg:F0}° off the edge out to #{farOut.Id}");
        Assert.True(offOutDeg > 90.0, "the heading is within 90° of the edge out, so the test proves nothing");

        double outFt = outEdge.DistanceNm * GeoMath.FeetPerNm;
        LatLon intoTheTurn = FollowCornerGeometry.Between(junction.Position, farOut.Position, 5.0 / outFt);
        AircraftState aircraft = FollowCornerGeometry.Spawn(FollowCornerGeometry.AirportId, Callsign, AircraftType, intoTheTurn, stillInbound);
        Assert.Equal(farOut.Id, TaxiEdgeTrail.EntryNodeOf(outEdge, aircraft).Id);

        aircraft.Ground.TaxiEdgeTrail.Record(inEdge, farIn);
        GroundNode entry = TaxiEdgeTrail.EntryNodeOf(outEdge, aircraft);
        aircraft.Ground.TaxiEdgeTrail.Record(outEdge, entry);

        Assert.Equal(junction.Id, entry.Id);
        Assert.Equal(junction.Id, aircraft.Ground.TaxiEdgeTrail.Newest?.EntryNodeId);
    }

    private sealed record TrailRun(
        TestAirportGroundData GroundData,
        SimulationEngine Engine,
        AircraftState Aircraft,
        AirportGroundLayout Layout,
        List<GroundNode> Bar28R,
        List<GroundNode> Bar28L,
        GroundNode NearBar28R
    );

    /// <summary>
    /// An engine with the C560 spawned <paramref name="edgesBack"/> B edges short of the near 28R bar, facing it, and cleared
    /// <paramref name="route"/>; null when navdata or the KOAK layout is unavailable.
    /// </summary>
    private TrailRun? Start(bool autoCross, string route, int edgesBack)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return null;
        }

        SimulationEngine engine = NewEngine(groundData, autoCross);
        List<GroundNode> bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        List<GroundNode> bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B");
        GroundNode nearBar28R = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position)).First();
        GroundNode start = nearBar28R;
        GroundNode ahead = nearBar28R;
        for (int step = 0; step < edgesBack; step++)
        {
            GroundNode current = start;
            GroundNode previous = ahead;
            start = current
                .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.OtherNode(current))
                .Where(node => node.Id != previous.Id)
                .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position))
                .First();
            ahead = current;
        }

        output.WriteLine(
            $"start #{start.Id}, {GeoMath.DistanceNm(start.Position, nearBar28R.Position) * GeoMath.FeetPerNm:F0} ft short of the 28R bar"
        );
        AircraftState aircraft = Spawn(start, new TrueHeading(GeoMath.BearingTo(start.Position, ahead.Position)), layout);
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Callsign, route);
        Assert.True(taxi.Success, taxi.Message);
        output.WriteLine($"route={aircraft.Ground.AssignedTaxiRoute?.ToSummary()}");
        return new TrailRun(groundData, engine, aircraft, layout, bar28R, bar28L, nearBar28R);
    }

    private IReadOnlyList<GroundEdge> ResolveTrail(TrailRun run)
    {
        List<GroundEdge> edges = [];
        foreach (TaxiTrailEdge entry in run.Aircraft.Ground.TaxiEdgeTrail.Edges)
        {
            GroundEdge edge = Assert.IsType<GroundEdge>(entry.Resolve(run.Layout));
            output.WriteLine($"  {edge.TaxiwayName} #{edge.Nodes[0].Id}-#{edge.Nodes[1].Id} {entry.LengthFt:F0} ft");
            edges.Add(edge);
        }

        return edges;
    }

    private static int IndexTouching(IReadOnlyList<GroundEdge> trail, int nodeId)
    {
        for (int i = 0; i < trail.Count; i++)
        {
            if ((trail[i].Nodes[0].Id == nodeId) || (trail[i].Nodes[1].Id == nodeId))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsHoldingShortOf(AircraftState aircraft, string runwayDesignator) =>
        (aircraft.Phases?.CurrentPhase is HoldingShortPhase hold)
        && (hold.HoldShort.TargetName is { } target)
        && RunwayIdentifier.Parse(target).Contains(runwayDesignator);

    private static bool TickUntil(SimulationEngine engine, int budgetSeconds, Func<bool> done)
    {
        for (int second = 1; second <= budgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return true;
            }
        }

        return false;
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    private SimulationEngine NewEngine(TestAirportGroundData groundData, bool autoCross)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-taxi-edge-trail",
                ScenarioName = "OAK taxi edge trail",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunway = autoCross,
            },
        };
    }

    private static AircraftState Spawn(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
