using System.Collections.Concurrent;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// How far the planner swings the nose on a <em>clear</em> stand-to-spot push. A push whose candidates all foul a
/// taxiway's object-free area goes through the overswing filter; a clear one is held only to the swing band (the target
/// turn ± 100°, <see cref="TugPlanBuilder.InSwingBand(TugNoseSwing, double, bool)"/>), and this sweep pins that no clear
/// plan falls outside it. For every SFO push-back stand and every spot within <see cref="TugMovePlanner.MaxGoalDistanceFt"/>
/// of it, a <c>PUSH $spot</c> is planned as the sim plans it, on an empty ramp (no parked neighbours), for each
/// <see cref="Types"/>; the swing of each kept plan is measured the way the planner measures it
/// (<see cref="TugNoseSwing.From"/>: from the end of the stand push-off, sample to sample).
/// </summary>
public class SfoAlleyPushSwingSweepTests(ITestOutputHelper output)
{
    private static readonly string[] Types = ["CRJ7", "B738", "B77W"];

    /// <summary>
    /// One planned push: whether it was refused or fouls a taxiway, its swing, the lane's rotation and whether it keeps
    /// to the band.
    /// </summary>
    private sealed record Row(
        string Stand,
        string Spot,
        string Type,
        double DistanceFt,
        bool Refused,
        bool Clear,
        double SwingDeg,
        double LaneTurnDeg,
        bool InSwingBand,
        string Moves
    );

    /// <summary>Every clear plan keeps to the swing band: the band the planner already applies to every lane push.</summary>
    [Fact]
    [Trait("Category", "Nightly")]
    public void ClearPlans_KeepToTheSwingBand()
    {
        if (Sweep(Environment.ProcessorCount) is not { } rows)
        {
            return;
        }

        List<Row> outside = [.. rows.Where(r => !r.Refused && r.Clear && !r.InSwingBand)];
        int clear = rows.Count(r => !r.Refused && r.Clear);
        output.WriteLine($"{rows.Count} plans, {clear} clear, {outside.Count} clear plan(s) outside the swing band");
        Assert.True(outside.Count == 0, $"clear plans outside the swing band: {string.Join("; ", outside.Select(Describe))}");
    }

    private static string Describe(Row r) =>
        $"{r.Stand}→{r.Spot} {r.Type} ({r.DistanceFt:F0} ft): swing {r.SwingDeg:F1}°, lane turn {r.LaneTurnDeg:F1}°, {r.Moves}";

    /// <summary>Plans every SFO push-back stand to every spot within reach, for every type, on <paramref name="threads"/> threads.</summary>
    private static List<Row>? Sweep(int threads)
    {
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (new TestAirportGroundData().GetLayout("SFO") is not { } layout))
        {
            return null;
        }

        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;
        var movementArea = MovementAreaClassification.For(layout);
        List<GroundNode> stands =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is not null))
                .Where(n => StandDepartures.StandDepartureOf(layout, n, sidecars) != StandDeparture.TaxiOut)
                .OrderBy(n => n.Name, StringComparer.Ordinal),
        ];
        List<GroundNode> spots =
        [
            .. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && (n.Name is not null)).OrderBy(n => n.Name, StringComparer.Ordinal),
        ];
        List<(GroundNode Stand, GroundNode Spot, double DistanceFt, string Type)> work =
        [
            .. from stand in stands
            from spot in spots
            let distanceFt = GeoMath.DistanceNm(stand.Position, spot.Position) * GeoMath.FeetPerNm
            where distanceFt <= TugMovePlanner.MaxGoalDistanceFt
            from type in Types
            select (stand, spot, distanceFt, type),
        ];

        var rows = new ConcurrentBag<Row>();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = threads }, w => rows.Add(PlanOne(layout, movementArea, w)));
        return
        [
            .. rows.OrderBy(r => r.Stand, StringComparer.Ordinal)
                .ThenBy(r => r.Spot, StringComparer.Ordinal)
                .ThenBy(r => r.Type, StringComparer.Ordinal),
        ];
    }

    private static Row PlanOne(
        AirportGroundLayout layout,
        MovementAreaClassification movementArea,
        (GroundNode Stand, GroundNode Spot, double DistanceFt, string Type) w
    )
    {
        var start = new PushbackPose(w.Stand.Position, w.Stand.TrueHeading!.Value.Degrees);
        var request = new TugRequest
        {
            Start = start,
            StartsAtStand = true,
            StandName = w.Stand.Name,
            Footprint = AircraftFootprint.FromType(w.Type),
            MovementArea = movementArea,
            Goals = [TugGoal.Spot(w.Spot)],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out _);
        if (plan is null)
        {
            return new Row(w.Stand.Name!, w.Spot.Name!, w.Type, w.DistanceFt, true, false, 0, 0, true, "");
        }

        double facingDeg = layout.TryGetSpotOutboundHeading(w.Spot, out double lane) ? lane : plan.End.NoseTrueDeg;
        double pushOffNoseDeg = plan.Moves[0].End.NoseTrueDeg;
        double laneTurnDeg = new TrueHeading(pushOffNoseDeg).SignedAngleTo(new TrueHeading(facingDeg));
        var swing = TugNoseSwing.From(pushOffNoseDeg, plan.Moves.Skip(1).SelectMany(m => m.Samples).Select(s => s.NoseTrueDeg));
        var bandSwing = TugNoseSwing.From(start.NoseTrueDeg, plan.Moves.SelectMany(m => m.Samples).Select(s => s.NoseTrueDeg));
        double targetDeg = new TrueHeading(start.NoseTrueDeg).SignedAngleTo(new TrueHeading(facingDeg));
        bool clear = !plan.Warnings.Any(x => x.Kind == TugPlanWarningKind.FoulsTaxiway);
        string moves = string.Join(", ", plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape} {m.PathLengthFt:F0}"));
        return new Row(
            w.Stand.Name!,
            w.Spot.Name!,
            w.Type,
            w.DistanceFt,
            false,
            clear,
            swing.MaxDeg,
            laneTurnDeg,
            TugPlanBuilder.InSwingBand(bandSwing, targetDeg, explicitFacing: false),
            moves
        );
    }
}
