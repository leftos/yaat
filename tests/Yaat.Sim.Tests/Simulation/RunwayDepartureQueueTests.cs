using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Unit coverage for <see cref="RunwayDepartureQueue.UpdatePositions"/>: the per-hold-short-line
/// departure-queue ranking. Uses a real OAK layout and real 28R hold-short nodes; aircraft are placed
/// at controlled distances (via <see cref="GeoMath.ProjectPoint"/>) and given genuine phase/route
/// objects so the ranking is exercised on real geometry without a full taxi rollout.
/// </summary>
public class RunwayDepartureQueueTests
{
    private const string Runway = "28R";
    private const double FeetPerNm = 6076.12;

    private readonly AirportGroundLayout? _layout;

    public RunwayDepartureQueueTests()
    {
        TestVnasData.EnsureInitialized();
        _layout = new TestAirportGroundData().GetLayout("OAK");
    }

    private List<GroundNode> HoldShortNodes() => _layout is null ? [] : _layout.GetRunwayHoldShortNodes(Runway);

    /// <summary>
    /// Hold shorts on one named taxiway, picked by their edges rather than by node id (fillet node ids are
    /// geometry-coupled). KOAK 28R departs west: taxiway B at the east end is full length, E is an
    /// intersection ~1625 ft down.
    /// </summary>
    private List<GroundNode> HoldShortsOn(string taxiway) =>
        [
            .. HoldShortNodes()
                .Where(n => n.Edges.OfType<GroundEdge>().Select(e => e.TaxiwayName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([taxiway])),
        ];

    private AircraftState HoldingShort(string callsign, GroundNode node) => HoldingShortNamed(callsign, node, Runway);

    /// <summary>
    /// Holding short of the destination runway at <paramref name="node"/>, with the bar named
    /// <paramref name="targetName"/> — the name follows the clearance that reached it, so it is a single end
    /// on one route and the combined pavement id on another.
    /// </summary>
    private AircraftState HoldingShortNamed(string callsign, GroundNode node, string targetName)
    {
        AircraftState ac = MakeGroundAircraft(callsign, node.Position);
        ac.Phases!.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = targetName,
                }
            )
        );
        return ac;
    }

    private AircraftState TaxiingToward(string callsign, GroundNode node, double distanceNm)
    {
        AircraftState ac = MakeGroundAircraft(callsign, GeoMath.ProjectPoint(node.Position, new TrueHeading(90), distanceNm));
        ac.Phases!.Add(new TaxiingPhase());
        BindDestination(ac, node);
        return ac;
    }

    /// <summary>
    /// An aircraft in <see cref="FollowingPhase"/> behind <paramref name="leaderCallsign"/>, placed
    /// <paramref name="distanceFt"/> east of <paramref name="near"/> along the same line the taxiing
    /// factory uses. Position is given against a node rather than against the leader so a test can put a
    /// follower at its own bar, which is what distinguishes inheriting the leader's line from being
    /// ranked on its own route.
    ///
    /// <para>No route is attached — call <see cref="BindDestination"/> for a departure follower. A
    /// follower left without one stands for the arrival case, which must never join a departure line.</para>
    /// </summary>
    private AircraftState FollowingAt(string callsign, string leaderCallsign, GroundNode near, double distanceFt)
    {
        AircraftState ac = MakeGroundAircraft(callsign, GeoMath.ProjectPoint(near.Position, new TrueHeading(90), distanceFt / FeetPerNm));
        ac.Phases!.Add(new FollowingPhase(leaderCallsign));
        return ac;
    }

    /// <summary>Gives an aircraft a taxi route whose destination-runway bar is <paramref name="node"/>.</summary>
    private static void BindDestination(AircraftState ac, GroundNode node)
    {
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = Runway,
                },
            ],
        };
    }

    private AircraftState LinedUp(string callsign, GroundNode node)
    {
        AircraftState ac = MakeGroundAircraft(callsign, node.Position);
        ac.Phases!.Add(new LinedUpAndWaitingPhase());
        return ac;
    }

    private AircraftState MakeGroundAircraft(string callsign, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        ac.Ground.Layout = _layout;
        return ac;
    }

    [Fact]
    public void HoldingShortLead_AndTaxiingTrailer_AreNumberedOneAndTwo()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState trailer = TaxiingToward("TRAIL", nodes[0], 0.05);

        RunwayDepartureQueue.UpdatePositions([lead, trailer]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailer.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, lead.Ground.RunwayQueueRunway);
        Assert.Equal(Runway, trailer.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void TaxiingBeyondProximityGate_GetsNoNumber()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState near = TaxiingToward("NEAR", nodes[0], 0.05);
        AircraftState far = TaxiingToward("FAR", nodes[0], 0.3);

        RunwayDepartureQueue.UpdatePositions([lead, near, far]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, near.Ground.RunwayQueuePosition);
        Assert.Equal(0, far.Ground.RunwayQueuePosition);
        Assert.Equal("", far.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void LoneAircraftInLine_GetsNumberOneWithRunway()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState solo = HoldingShort("SOLO", nodes[0]);

        RunwayDepartureQueue.UpdatePositions([solo]);

        Assert.Equal(1, solo.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, solo.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// A bar whose name is the combined pavement id ("10L/28R") rather than the departure end is still
    /// labelled with the end the aircraft departs from, taken from its own clearance — and the aircraft
    /// behind it, which reached the same bar under the end's name, reads the same label. Two names for one
    /// line is what an RPO sees when the pavement id leaks through.
    /// </summary>
    [Fact]
    public void HoldShortNamedByPavementId_IsLabelledWithTheDepartureEnd()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShortNamed("LEAD", nodes[0], $"10L/{Runway}");
        BindDestination(lead, nodes[0]);
        AircraftState trailer = TaxiingToward("TRAIL", nodes[0], 0.05);

        RunwayDepartureQueue.UpdatePositions([lead, trailer]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailer.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, lead.Ground.RunwayQueueRunway);
        Assert.Equal(Runway, trailer.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void TwoHoldShortNodes_AreRankedIndependently()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count < 2)
        {
            return;
        }

        AircraftState leadA = HoldingShort("LEADA", nodes[0]);
        AircraftState trailA = TaxiingToward("TRAILA", nodes[0], 0.05);
        AircraftState leadB = HoldingShort("LEADB", nodes[1]);
        AircraftState trailB = TaxiingToward("TRAILB", nodes[1], 0.05);

        RunwayDepartureQueue.UpdatePositions([leadA, trailA, leadB, trailB]);

        Assert.Equal(1, leadA.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailA.Ground.RunwayQueuePosition);
        Assert.Equal(1, leadB.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailB.Ground.RunwayQueuePosition);
    }

    [Fact]
    public void IntersectionDepartureLine_NamesItsEntryTaxiway()
    {
        List<GroundNode> echo = HoldShortsOn("E");
        if (echo.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", echo[0]);
        AircraftState trailer = TaxiingToward("TRAIL", echo[0], 0.05);

        RunwayDepartureQueue.UpdatePositions([lead, trailer]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailer.Ground.RunwayQueuePosition);
        Assert.Equal("E", lead.Ground.RunwayQueueIntersection);
        Assert.Equal("E", trailer.Ground.RunwayQueueIntersection);
    }

    [Fact]
    public void FullLengthDepartureLine_HasNoEntryTaxiway()
    {
        List<GroundNode> bravo = HoldShortsOn("B");
        if (bravo.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", bravo[0]);

        RunwayDepartureQueue.UpdatePositions([lead]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, lead.Ground.RunwayQueueRunway);
        Assert.Equal("", lead.Ground.RunwayQueueIntersection);
    }

    [Fact]
    public void LinedUpAircraft_LeavesTheLine_TaxiingTrailersRankFromOne()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState linedUp = LinedUp("LUAW", nodes[0]);
        AircraftState near = TaxiingToward("NEAR", nodes[0], 0.05);
        AircraftState far = TaxiingToward("FAR", nodes[0], 0.08);

        RunwayDepartureQueue.UpdatePositions([linedUp, near, far]);

        Assert.Equal(0, linedUp.Ground.RunwayQueuePosition);
        Assert.Equal(1, near.Ground.RunwayQueuePosition);
        Assert.Equal(2, far.Ground.RunwayQueuePosition);
    }

    /// <summary>
    /// An aircraft told to follow the one holding short takes the place directly behind it — same line, same
    /// runway, same entry label. FollowingPhase is neither queue-eligible phase, so without the follower pass
    /// the trailer would be unnumbered while sitting 300 ft off the bar.
    /// </summary>
    [Fact]
    public void Follower_RanksDirectlyBehindItsLeader()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LEAD", nodes[0], 300);
        BindDestination(follower, nodes[0]);

        RunwayDepartureQueue.UpdatePositions([lead, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, follower.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, follower.Ground.RunwayQueueRunway);
        Assert.Equal(lead.Ground.RunwayQueueIntersection, follower.Ground.RunwayQueueIntersection);
    }

    /// <summary>
    /// A follower is admitted behind its leader only from inside the same proximity gate a taxiing
    /// aircraft faces — measured on its own distance to the bar, not on the gap to the leader. Inheriting
    /// the leader's line without that gate would number an aircraft still a quarter-mile down the taxiway.
    /// </summary>
    [Fact]
    public void Follower_BeyondProximityGate_Stays0()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LEAD", nodes[0], 0.15 * FeetPerNm);
        BindDestination(follower, nodes[0]);

        RunwayDepartureQueue.UpdatePositions([lead, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(0, follower.Ground.RunwayQueuePosition);
        Assert.Equal("", follower.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// Following someone is not by itself a departure. An arrival taxiing in behind a departure — no taxi
    /// route, so no destination-runway bar — must never be numbered in that departure's line, however
    /// close it is sitting.
    /// </summary>
    [Fact]
    public void ArrivalFollower_WithNoDepartureRoute_Stays0()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LEAD", nodes[0], 200);

        RunwayDepartureQueue.UpdatePositions([lead, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(0, follower.Ground.RunwayQueuePosition);
        Assert.Equal("", follower.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// A follower inherits its leader's line only when its own clearance ends at that same bar. Following
    /// an aircraft bound for a different intersection means the two are not in one line at all — the
    /// follower is ranked on its own route instead, at the front of its own bar's line.
    /// </summary>
    [Fact]
    public void Follower_BoundForADifferentBar_DoesNotInheritTheLeadersLine()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count < 2)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LEAD", nodes[1], 150);
        BindDestination(follower, nodes[1]);

        RunwayDepartureQueue.UpdatePositions([lead, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(1, follower.Ground.RunwayQueuePosition);
    }

    /// <summary>
    /// A follow chain is ranked all the way down: the second follower is placed on the pass after its own
    /// leader joins the line, so a three-deep merge reads 1-2-3 rather than stopping at the first follower.
    /// </summary>
    [Fact]
    public void FollowerOfFollower_RanksThird()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState first = FollowingAt("FOLLOW1", "LEAD", nodes[0], 200);
        AircraftState second = FollowingAt("FOLLOW2", "FOLLOW1", nodes[0], 400);
        BindDestination(first, nodes[0]);
        BindDestination(second, nodes[0]);

        // Deliberately out of chain order: the pass must not depend on the world list being sorted front-to-back.
        RunwayDepartureQueue.UpdatePositions([second, first, lead]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, first.Ground.RunwayQueuePosition);
        Assert.Equal(3, second.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, second.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// When the leader has left the line — here by lining up — the follower does not vanish with it: it is
    /// ranked on its own route, exactly as the taxiing aircraft it physically is, and moves up to #1. Falling
    /// through to its own geometry matters because the follower is still a departure sitting at the bar; only
    /// the aircraft it was told to trail has gone.
    /// </summary>
    [Fact]
    public void Follower_WithLeaderNotInLine_RanksByItsOwnRoute()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState linedUp = LinedUp("LUAW", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LUAW", nodes[0], 150);
        BindDestination(follower, nodes[0]);

        RunwayDepartureQueue.UpdatePositions([linedUp, follower]);

        Assert.Equal(0, linedUp.Ground.RunwayQueuePosition);
        Assert.Equal(1, follower.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, follower.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// A follower that has arrived at a bar is stopped in a <see cref="HoldingShortPhase"/> with its
    /// <see cref="FollowingPhase"/> queued behind it — <see cref="FollowingPhase.CheckRunwayHoldShort"/>
    /// inserts the pair. It is still the aircraft following its leader, so it keeps its place in the line
    /// rather than dropping to unnumbered the moment it stops.
    /// </summary>
    [Fact]
    public void ArrivedFollower_HoldingShortWithQueuedFollow_RanksBehindItsLeader()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = MakeGroundAircraft("FOLLOW", GeoMath.ProjectPoint(nodes[0].Position, new TrueHeading(90), 100 / FeetPerNm));
        follower.Phases!.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = nodes[0].Id,
                    Reason = HoldShortReason.RunwayCrossing,
                    TargetName = Runway,
                }
            )
        );
        follower.Phases!.Add(new FollowingPhase("LEAD"));
        BindDestination(follower, nodes[0]);

        RunwayDepartureQueue.UpdatePositions([lead, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, follower.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, follower.Ground.RunwayQueueRunway);
    }

    /// <summary>
    /// A follower tucked in behind the holding lead is ahead of an aircraft still taxiing up to the same bar,
    /// even though both are inside the proximity gate: the follower inherits the lead's tier, and tier beats
    /// raw distance. Without that, the taxier would show as next-up over an aircraft already in the line.
    /// </summary>
    [Fact]
    public void Follower_RanksAheadOfFartherTaxier()
    {
        List<GroundNode> nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        AircraftState lead = HoldingShort("LEAD", nodes[0]);
        AircraftState follower = FollowingAt("FOLLOW", "LEAD", nodes[0], 200);
        BindDestination(follower, nodes[0]);
        AircraftState taxier = TaxiingToward("TAXI", nodes[0], 0.09);

        RunwayDepartureQueue.UpdatePositions([lead, taxier, follower]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, follower.Ground.RunwayQueuePosition);
        Assert.Equal(3, taxier.Ground.RunwayQueuePosition);
    }
}
