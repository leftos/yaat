using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests that aircraft queue behind a hold-short point instead of stacking
/// on top of each other at the same node.
/// </summary>
public class HoldShortQueueTests
{
    private const double FtPerNm = 6076.12;
    private const double BaseLat = 37.725;
    private const double BaseLon = -122.205;
    private const double OffsetLatPer100Ft = 100.0 / FtPerNm / 60.0;

    /// <summary>
    /// Builds a 5-node layout simulating taxiway B leading to a runway hold-short:
    ///   Node0 --[B]--> Node1 --[B]--> Node2 --[B]--> Node3 --[B]--> Node4 (RunwayHoldShort 28R)
    /// Nodes spaced ~150ft apart.
    /// </summary>
    private static (AirportGroundLayout Layout, int HoldShortNodeId) BuildHoldShortLayout()
    {
        var rwyId = RunwayIdentifier.Parse("28R/10L");
        var layout = new AirportGroundLayout { AirportId = "KOAK" };

        var nodes = new GroundNode[5];
        for (int i = 0; i < 5; i++)
        {
            nodes[i] = new GroundNode
            {
                Id = i,
                Latitude = BaseLat - i * 1.5 * OffsetLatPer100Ft, // heading south
                Longitude = BaseLon,
                Type = i == 4 ? GroundNodeType.RunwayHoldShort : GroundNodeType.TaxiwayIntersection,
                RunwayId = i == 4 ? rwyId : null,
            };
        }

        for (int i = 0; i < 4; i++)
        {
            var edge = new GroundEdge
            {
                FromNodeId = i,
                ToNodeId = i + 1,
                TaxiwayName = "B",
                DistanceNm = 150.0 / FtPerNm,
            };
            nodes[i].Edges.Add(edge);
            nodes[i + 1].Edges.Add(edge);
            layout.Edges.Add(edge);
        }

        foreach (var n in nodes)
        {
            layout.Nodes[n.Id] = n;
        }

        return (layout, 4);
    }

    private static TaxiRoute MakeRouteToHoldShort(AirportGroundLayout layout, int startNodeId, int holdShortNodeId)
    {
        var segments = new List<TaxiRouteSegment>();
        for (int i = startNodeId; i < holdShortNodeId; i++)
        {
            var edge = layout.Edges.First(e => (e.FromNodeId == i && e.ToNodeId == i + 1) || (e.FromNodeId == i + 1 && e.ToNodeId == i));
            segments.Add(
                new TaxiRouteSegment
                {
                    FromNodeId = i,
                    ToNodeId = i + 1,
                    TaxiwayName = "B",
                    Edge = edge,
                }
            );
        }

        var holdShortPoints = new List<HoldShortPoint>
        {
            new()
            {
                NodeId = holdShortNodeId,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = "28R",
            },
        };

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShortPoints,
            CurrentSegmentIndex = 0,
        };
    }

    private static AircraftState MakeAircraft(string callsign, string type, double lat, double lon)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(180), // heading south toward hold-short
            IsOnGround = true,
            Departure = "KOAK",
        };
    }

    private static PhaseContext MakeContext(AircraftState aircraft, AirportGroundLayout layout, Func<int, bool>? isHoldShortNodeOccupied = null)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
            IsHoldShortNodeOccupied = isHoldShortNodeOccupied,
        };
    }

    [Fact]
    public void StalledAtThreshold_DoesNotTrigger_WhenConflictDetectorStopsAircraft()
    {
        var (layout, hsNodeId) = BuildHoldShortLayout();
        var hsNode = layout.Nodes[hsNodeId];

        // Aircraft A: holding short at node 4
        var acA = MakeAircraft("HOLD01", "C172", hsNode.Latitude, hsNode.Longitude);
        acA.Phases = new PhaseList();
        var holdPhase = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = hsNodeId,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = "28R",
            }
        );
        acA.Phases.Add(holdPhase);
        acA.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        // Aircraft B: taxiing toward hold-short, starting at node 2 (2 segments away)
        var startNode = layout.Nodes[2];
        var acB = MakeAircraft("TAXI01", "C172", startNode.Latitude, startNode.Longitude);
        var route = MakeRouteToHoldShort(layout, 2, hsNodeId);
        acB.AssignedTaxiRoute = route;
        acB.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        acB.Phases.Add(taxiPhase);

        Func<int, bool> occupancyCheck = nodeId => nodeId == hsNodeId;
        var ctx = MakeContext(acB, layout, occupancyCheck);
        acB.Phases.Start(ctx);

        var allAircraft = new List<AircraftState> { acA, acB };

        // Run ticks: taxi B toward the hold-short node
        for (int tick = 0; tick < 200; tick++)
        {
            // Clear and reapply conflict limits
            GroundConflictDetector.ApplySpeedLimits(allAircraft, layout, 1.0);

            // Physics
            FlightPhysics.Update(acB, 1.0);

            // Phase tick
            ctx = MakeContext(acB, layout, occupancyCheck);
            bool done = taxiPhase.OnTick(ctx);

            // If the taxiing phase completed unexpectedly, the aircraft arrived at the hold-short
            // Check separation
            double distFt = GeoMath.DistanceNm(acA.Latitude, acA.Longitude, acB.Latitude, acB.Longitude) * FtPerNm;
            Assert.True(
                distFt > 25.0,
                $"Tick {tick}: Aircraft B is only {distFt:F0}ft from aircraft A — they are stacking! "
                    + $"B pos=({acB.Latitude:F6}, {acB.Longitude:F6}), A pos=({acA.Latitude:F6}, {acA.Longitude:F6})"
            );

            if (done)
            {
                break;
            }
        }
    }

    [Fact]
    public void ArriveAtNode_DoesNotSnap_WhenHoldShortNodeOccupied()
    {
        var (layout, hsNodeId) = BuildHoldShortLayout();
        var hsNode = layout.Nodes[hsNodeId];

        // Aircraft A: at the hold-short node
        var acA = MakeAircraft("HOLD01", "C172", hsNode.Latitude, hsNode.Longitude);
        acA.Phases = new PhaseList();
        acA.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = hsNodeId,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R",
                }
            )
        );
        acA.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        // Aircraft B: just barely before the hold-short node (within arrival threshold)
        // Place it ~80ft from the hold-short node (within NodeArrivalThresholdNm = 0.015nm ≈ 91ft)
        double offsetNm = 80.0 / FtPerNm;
        var acB = MakeAircraft("TAXI01", "C172", hsNode.Latitude + offsetNm / 60.0, hsNode.Longitude);

        // Route: single segment to the hold-short node, starting from "just before"
        var lastEdge = layout.Edges.Last(); // node 3 → node 4
        var route = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment
                {
                    FromNodeId = 3,
                    ToNodeId = hsNodeId,
                    TaxiwayName = "B",
                    Edge = lastEdge,
                },
            ],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = hsNodeId,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R",
                },
            ],
            CurrentSegmentIndex = 0,
        };
        acB.AssignedTaxiRoute = route;
        acB.Phases = new PhaseList();
        var taxiPhase = new TaxiingPhase();
        acB.Phases.Add(taxiPhase);
        acB.IndicatedAirspeed = 5; // creeping forward

        Func<int, bool> occupancyCheck = nodeId => nodeId == hsNodeId;
        var ctx = MakeContext(acB, layout, occupancyCheck);
        acB.Phases.Start(ctx);

        // Tick — the aircraft is within arrival threshold of the hold-short node
        ctx = MakeContext(acB, layout, occupancyCheck);
        taxiPhase.OnTick(ctx);

        // Aircraft B should NOT have snapped to the hold-short node
        double distFt = GeoMath.DistanceNm(acA.Latitude, acA.Longitude, acB.Latitude, acB.Longitude) * FtPerNm;
        Assert.True(distFt > 25.0, $"Aircraft B snapped to occupied hold-short node! Distance: {distFt:F0}ft");
    }

    [Fact]
    public void ConflictDetector_UsesAircraftLength_ForSeparation()
    {
        // Real ACD data has B738 LengthFt=129.5, C172 LengthFt=27.2
        TestVnasData.EnsureInitialized();

        // B738 holding at a point, C172 approaching from behind
        var leader = new AircraftState
        {
            Callsign = "LEAD",
            AircraftType = "B738",
            Latitude = BaseLat,
            Longitude = BaseLon,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            IndicatedAirspeed = 0,
        };
        leader.Phases = new PhaseList();
        leader.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 99,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R",
                }
            )
        );
        leader.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        // Trailer at 120ft behind (less than B738 length of 129.5ft + buffer)
        double trailOffsetNm = 120.0 / FtPerNm;
        var trailer = new AircraftState
        {
            Callsign = "TRAIL",
            AircraftType = "C172",
            Latitude = BaseLat - trailOffsetNm / 60.0,
            Longitude = BaseLon,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            IndicatedAirspeed = 15,
        };

        var allAircraft = new List<AircraftState> { leader, trailer };
        GroundConflictDetector.ApplySpeedLimits(allAircraft, null);

        // At 120ft behind a 129.5ft aircraft, trailer should be stopped
        // (stop distance = leader length + 25 = 154.5ft, and 120 < 154.5)
        Assert.NotNull(trailer.GroundSpeedLimit);
        Assert.Equal(0.0, trailer.GroundSpeedLimit.Value);
    }
}
