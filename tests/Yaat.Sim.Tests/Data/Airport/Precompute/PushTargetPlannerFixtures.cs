using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerTests;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// What the <see cref="PushTargetPlannerTests"/> classes share: the sample stands' entries, computed once per test
/// process, and the helpers that plan a stand's candidates straight through the tug planner.
/// </summary>
internal static class PushTargetPlannerFixtures
{
    /// <summary>The ARTCCs folder both the sidecar catalog and <see cref="AirportSidecarHash.For"/> read.</summary>
    internal static readonly string ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

    /// <summary>A whole airport takes minutes, so the tests compute gate 26 and the two stands nearest it.</summary>
    internal static readonly Lazy<IReadOnlySet<string>> SampleStands = new(() =>
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        return layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(gate.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(3)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);
    });

    internal static readonly Lazy<IReadOnlyList<PushTargetEntry>> OakEntries = new(() =>
        PushTargetPlanner.ComputeStands(Oak(), DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, SampleStands.Value, Sequential)
    );

    internal static readonly IReadOnlySet<string> Gate26 = new HashSet<string>(StringComparer.Ordinal) { "26" };

    /// <summary>The golden's options: the stored ones, indented with LF line ends so a review diff shows one field a line.</summary>
    internal static readonly JsonSerializerOptions IndentedOptions = new(GroundLayoutSerializer.Options) { WriteIndented = true, NewLine = "\n" };

    internal static PushTargetEntry Entry(string stand, string group) =>
        Assert.Single(OakEntries.Value, e => (e.StandName == stand) && (e.DesignGroup == group));

    internal static MovementAreaClassification Classification(AirportGroundLayout layout) => MovementAreaClassification.Build(layout, Sidecars.Value);

    internal static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    /// <summary>The <paramref name="count"/> named parking nodes nearest <paramref name="reference"/>, nearest first then by node id.</summary>
    internal static IReadOnlySet<string> NearestStandNames(AirportGroundLayout layout, GroundNode reference, int count) =>
        layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .OrderBy(n => GeoMath.DistanceNm(reference.Position, n.Position))
            .ThenBy(n => n.Id)
            .Take(count)
            .Select(n => n.Name!)
            .ToHashSet(StringComparer.Ordinal);

    internal static PushbackPose StartOf(GroundNode stand) => new(stand.Position, stand.TrueHeading!.Value.Degrees);

    internal static GroundNode SpotNode(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"No spot {name} at {layout.AirportId}");

    /// <summary>
    /// Where a push off <paramref name="standName"/> to <paramref name="goal"/> with <paramref name="group"/>'s envelope ends,
    /// as the planner flies it: the pose the aircraft is held at after the push.
    /// </summary>
    internal static PushbackPose HeldAfter(AirportGroundLayout layout, string standName, TugGoal goal, AirplaneDesignGroup group)
    {
        AircraftFootprint footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(group);
        TugRequest request = PushTargetPlanner.RequestFor(
            StartOf(Stand(layout, standName)),
            footprint,
            Classification(layout),
            goal,
            startsAtStand: true
        );
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, $"The push off {standName} for group {group} was refused: {refusal}");
        return plan.End;
    }

    internal static IReadOnlyList<PrecomputedPushTarget> HeldTargets(AirportGroundLayout layout, PushbackPose held, AirplaneDesignGroup group) =>
        PushTargetPlanner.ComputeHeld(layout, DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, held, group)
        ?? throw new InvalidOperationException($"No envelope for group {group}: nothing to plan the held pose with");

    internal static TugGoal StraightBack(AirportGroundLayout layout, GroundNode stand, string taxiway) =>
        TugGoal.StraightBackTo(
            layout.FindExitByTaxiway(stand.Position, taxiway) ?? throw new InvalidOperationException($"No exit onto {taxiway}"),
            taxiway
        );

    /// <summary>
    /// Every bare taxiway and spot candidate of <paramref name="stand"/>, planned straight through the tug planner with no
    /// length cap: the path length, rounded as stored, or null when the planner refuses it.
    /// </summary>
    internal static List<UncappedPlan> UncappedPlans(
        AirportGroundLayout layout,
        GroundNode stand,
        AircraftFootprint footprint,
        MovementAreaClassification classification
    ) =>
        [
            .. Candidates(layout, stand)
                .Select(g =>
                {
                    TugPlan? plan = TugMovePlanner.Plan(
                        layout,
                        PushTargetPlanner.RequestFor(StartOf(stand), footprint, classification, g.Goal, startsAtStand: true),
                        out _
                    );
                    double? lengthFt = plan is null ? null : Math.Round(plan.Moves.Sum(m => m.PathLengthFt), 1);
                    return new UncappedPlan(g.Kind, g.Name, CommandOf(g.Kind, g.Name), lengthFt);
                }),
        ];

    /// <summary>Every candidate of <paramref name="stand"/> the tug planner accepts, with its plan, uncapped.</summary>
    internal static List<(string Command, TugPlan Plan)> AcceptedPlans(
        AirportGroundLayout layout,
        GroundNode stand,
        AircraftFootprint footprint,
        MovementAreaClassification classification
    )
    {
        var accepted = new List<(string Command, TugPlan Plan)>();
        foreach ((PushTargetKind kind, string name, TugGoal goal) in Candidates(layout, stand))
        {
            if (
                TugMovePlanner.Plan(
                    layout,
                    PushTargetPlanner.RequestFor(StartOf(stand), footprint, classification, goal, startsAtStand: true),
                    out _
                ) is
                { } plan
            )
            {
                accepted.Add((CommandOf(kind, name), plan));
            }
        }

        return accepted;
    }

    internal static List<(PushTargetKind Kind, string Name, TugGoal Goal)> Candidates(AirportGroundLayout layout, GroundNode stand)
    {
        var goals = new List<(PushTargetKind Kind, string Name, TugGoal Goal)>();
        IEnumerable<string> taxiways = layout
            .Edges.OfType<GroundEdge>()
            .Where(e => !e.IsRamp && !e.IsRunwayCenterline && !e.IsRunwayCrossingLink)
            .Where(e => e.Nodes.Any(n => DistanceFt(stand.Position, n.Position) <= PushTargetPlanner.CandidateSearchFt))
            .Select(e => e.TaxiwayName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string taxiway in taxiways)
        {
            if (layout.FindExitByTaxiway(stand.Position, taxiway) is { } exit)
            {
                goals.Add((PushTargetKind.Taxiway, taxiway, TugGoal.StraightBackTo(exit, taxiway)));
            }
        }

        IEnumerable<string> spots = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && !string.IsNullOrEmpty(n.Name))
            .Where(n => DistanceFt(stand.Position, n.Position) <= PushTargetPlanner.CandidateSearchFt)
            .Select(n => n.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string spot in spots)
        {
            if (layout.FindSpotNodeByName(spot) is { } node)
            {
                goals.Add((PushTargetKind.Spot, spot, TugGoal.Spot(node)));
            }
        }

        return goals;
    }

    internal static string CommandOf(PushTargetKind kind, string name) => kind == PushTargetKind.Spot ? $"PUSH ${name}" : $"PUSH {name}";

    /// <summary>The four corners of the footprint (length × wingspan, centred on the sample) at one flown sample.</summary>
    internal static IEnumerable<LatLon> Corners(PushbackPose sample, AircraftFootprint footprint)
    {
        double noseRad = sample.NoseTrueDeg * Math.PI / 180.0;
        double feetPerDegree = 60.0 * GeoMath.FeetPerNm;
        foreach (double along in (double[])[footprint.LengthFt / 2.0, -footprint.LengthFt / 2.0])
        {
            foreach (double across in (double[])[footprint.WingspanFt / 2.0, -footprint.WingspanFt / 2.0])
            {
                double eastFt = (along * Math.Sin(noseRad)) + (across * Math.Cos(noseRad));
                double northFt = (along * Math.Cos(noseRad)) - (across * Math.Sin(noseRad));
                yield return new LatLon(
                    sample.Position.Lat + (northFt / feetPerDegree),
                    sample.Position.Lon + (eastFt / (feetPerDegree * Math.Cos(sample.Position.Lat * Math.PI / 180.0)))
                );
            }
        }
    }

    internal static List<RunwayLine> RunwayLines(AirportGroundLayout layout) =>
        [
            .. layout
                .Edges.Where(e => e.IsRunwayCenterline)
                .GroupBy(e => e.TaxiwayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RunwayLine(g.Key, [.. g.Select(EdgePoints)])),
        ];

    /// <summary>Each runway holding position, with the runway whose centreline it lies nearest and how far from it, feet.</summary>
    internal static List<RunwayHold> Holds(AirportGroundLayout layout, List<RunwayLine> runways) =>
        [
            .. layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
                .Select(n => (Node: n, Runway: runways.MinBy(r => r.DistanceFt(n.Position))!))
                .Select(h => new RunwayHold(h.Node, h.Runway, h.Runway.DistanceFt(h.Node.Position))),
        ];

    internal static LatLon FirstPointAlong(AirportGroundLayout layout, GroundEdge edge, GroundNode from)
    {
        if (edge.IntermediatePoints.Count == 0)
        {
            return layout.Nodes[edge.OtherNodeId(from.Id)].Position;
        }

        (double Lat, double Lon) first = edge.Nodes[0].Id == from.Id ? edge.IntermediatePoints[0] : edge.IntermediatePoints[^1];
        return new LatLon(first.Lat, first.Lon);
    }

    internal static List<LatLon> EdgePoints(GroundEdge edge) =>
        [edge.Nodes[0].Position, .. edge.IntermediatePoints.Select(q => new LatLon(q.Lat, q.Lon)), edge.Nodes[1].Position];

    internal static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    internal static string NewRoot() => Path.Combine(Path.GetTempPath(), "precompute-" + Guid.NewGuid());

    internal static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal sealed record UncappedPlan(PushTargetKind Kind, string Name, string Command, double? PathLengthFt);

    internal sealed record RunwayHold(GroundNode Hold, RunwayLine Runway, double HoldFt);

    /// <summary>A runway's centreline pieces, flattened around their first point to measure point distances in feet.</summary>
    internal sealed record RunwayLine(string Name, List<List<LatLon>> Pieces)
    {
        public double DistanceFt(LatLon point) =>
            Pieces.SelectMany(piece => piece.Zip(piece.Skip(1), (a, b) => SegmentFt(point, a, b))).DefaultIfEmpty(double.PositiveInfinity).Min();

        private static double SegmentFt(LatLon p, LatLon a, LatLon b)
        {
            double cos = Math.Cos(a.Lat * Math.PI / 180.0);
            double feetPerDegree = 60.0 * GeoMath.FeetPerNm;
            (double X, double Y) Local(LatLon q) => ((q.Lon - a.Lon) * cos * feetPerDegree, (q.Lat - a.Lat) * feetPerDegree);
            (double px, double py) = Local(p);
            (double bx, double by) = Local(b);
            double lengthSq = (bx * bx) + (by * by);
            double t = lengthSq <= 0.0 ? 0.0 : Math.Clamp(((px * bx) + (py * by)) / lengthSq, 0.0, 1.0);
            return Math.Sqrt(Math.Pow(px - (t * bx), 2) + Math.Pow(py - (t * by), 2));
        }
    }
}
