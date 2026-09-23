using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <c>$spot</c> destination is reached along the lane the spot sits on, so the aircraft comes to rest facing
/// along that lane (either way down it). The ramp cuts used to be allowed to land on the spot node itself from
/// the side, which left SFO arrivals stopped across their own lane — <c>TAXI $5A</c> from gate D2 finished on
/// spot 5A heading 45° on a lane that runs 118 / 298, and <c>TAXI $7B</c> from spot 7A finished heading 118°
/// on a lane that runs 27 / 207.
/// </summary>
public class SfoSpotLaneAlignmentTests(ITestOutputHelper output)
{
    /// <summary>Recorded session the live-dispatch case replays from: S1-SFO-2 Ground Control 28/01.</summary>
    private const string RecordingPath = "TestData/sfo-gc-28-01-spot-lanes-recording.zip";

    /// <summary>How far the resting heading may sit off the lane's bearing (or its reciprocal).</summary>
    private const double AlignmentToleranceDeg = 15.0;

    /// <summary>Ground speed below which the aircraft counts as stopped.</summary>
    private const double AtRestKts = 1.0;

    /// <summary>
    /// The flat run floor the one-fuselage rule replaced, and the lower edge of the window the two disagree
    /// over. Held here rather than read from <c>RampLaneReposition</c>, where it is now private: this is the
    /// old rule the test contrasts against, and it must not follow the production constant if that moves.
    /// </summary>
    private const double RunFloorFt = 100.0;

    /// <summary>How many spot-lane approaches the search below pathfinds before giving up.</summary>
    private const int MaxLaneCandidates = 50;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Every direction the spot's own lane runs in at the spot node, both ways down each edge.</summary>
    private static List<double> LaneBearings(GroundNode spot, string lane)
    {
        var bearings = new List<double>();
        foreach (IGroundEdge edge in spot.Edges)
        {
            if (!edge.MatchesTaxiway(lane))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(spot.Position, edge.OtherNode(spot).Position);
            bearings.Add(bearing);
            bearings.Add((bearing + 180.0) % 360.0);
        }

        return bearings;
    }

    /// <summary>
    /// Asserts the aircraft came to rest on the spot, facing along the spot's lane.
    /// </summary>
    /// <param name="aircraft">Aircraft under test.</param>
    /// <param name="spot">The spot it was cleared to.</param>
    /// <param name="lane">The lane the spot sits on.</param>
    private void AssertRestingOnSpotAlongLane(AircraftState aircraft, GroundNode spot, string lane)
    {
        List<double> bearings = LaneBearings(spot, lane);
        Assert.True(bearings.Count > 0, $"spot {spot.Name} has no edge of lane {lane}");

        double headingDeg = aircraft.TrueHeading.Degrees;
        double offBy = bearings.Min(b => GeoMath.AbsBearingDifference(headingDeg, b));
        double distFt = DistanceFt(aircraft.Position, spot.Position);
        double halfLengthFt = (FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? 100.0) / 2.0;
        output.WriteLine(
            $"{aircraft.Callsign}: heading {headingDeg:F1}, lane {lane} runs [{string.Join(", ", bearings.Select(b => $"{b:F0}"))}], "
                + $"off by {offBy:F1}°, {distFt:F0} ft from spot {spot.Name} (half fuselage {halfLengthFt:F0} ft), "
                + $"phase {aircraft.Phases?.CurrentPhase?.GetType().Name}, {aircraft.GroundSpeed:F1} kts"
        );

        Assert.True(aircraft.GroundSpeed < AtRestKts, $"{aircraft.Callsign} is still moving at {aircraft.GroundSpeed:F1} kts");
        Assert.True(distFt <= halfLengthFt + 5.0, $"{aircraft.Callsign} stopped {distFt:F0} ft from spot {spot.Name}");
        Assert.True(
            offBy <= AlignmentToleranceDeg,
            $"{aircraft.Callsign} rests heading {headingDeg:F1}, {offBy:F1}° off lane {lane} — expected within {AlignmentToleranceDeg:F0}°"
        );
    }

    /// <summary>
    /// Asserts the aircraft came to rest on the spot facing along the spot's lane in one direction — toward
    /// <paramref name="toward"/> — where <see cref="AssertRestingOnSpotAlongLane"/> accepts either way down it.
    /// </summary>
    /// <param name="aircraft">Aircraft under test.</param>
    /// <param name="spot">The spot it was cleared to.</param>
    /// <param name="lane">The lane the spot sits on.</param>
    /// <param name="toward">A node down the lane in the direction the aircraft must face.</param>
    /// <param name="toleranceDeg">How far the resting heading may sit off that direction.</param>
    private void AssertRestingOnSpotFacing(AircraftState aircraft, GroundNode spot, string lane, GroundNode toward, double toleranceDeg)
    {
        List<double> bearings = LaneBearings(spot, lane);
        Assert.True(bearings.Count > 0, $"spot {spot.Name} has no edge of lane {lane}");

        double towardDeg = GeoMath.BearingTo(spot.Position, toward.Position);
        double laneDeg = bearings.MinBy(b => GeoMath.AbsBearingDifference(b, towardDeg));
        double headingDeg = aircraft.TrueHeading.Degrees;
        double offBy = GeoMath.AbsBearingDifference(headingDeg, laneDeg);
        double distFt = DistanceFt(aircraft.Position, spot.Position);
        double halfLengthFt = (FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? 100.0) / 2.0;
        output.WriteLine(
            $"{aircraft.Callsign}: heading {headingDeg:F1}, lane {lane} toward node {toward.Id} runs {laneDeg:F0}, off by {offBy:F1}°, "
                + $"{distFt:F0} ft from spot {spot.Name} (half fuselage {halfLengthFt:F0} ft), "
                + $"phase {aircraft.Phases?.CurrentPhase?.GetType().Name}, {aircraft.GroundSpeed:F1} kts"
        );

        Assert.True(aircraft.GroundSpeed < AtRestKts, $"{aircraft.Callsign} is still moving at {aircraft.GroundSpeed:F1} kts");
        Assert.True(distFt <= halfLengthFt + 5.0, $"{aircraft.Callsign} stopped {distFt:F0} ft from spot {spot.Name}");
        Assert.True(
            offBy <= toleranceDeg,
            $"{aircraft.Callsign} rests heading {headingDeg:F1}, {offBy:F1}° off lane {lane} toward node {toward.Id} ({laneDeg:F0}) "
                + $"— expected within {toleranceDeg:F0}°"
        );
    }

    /// <summary>The free-space cut that lands on the spot itself from the side is the shape under test.</summary>
    private static void AssertNoFreeSpaceLegIntoSpot(TaxiRoute route, GroundNode spot) =>
        Assert.DoesNotContain(route.Segments, s => (s.ToNodeId == spot.Id) && s.TaxiwayName.Equals("RAMP", StringComparison.OrdinalIgnoreCase));

    private int TickToRest(SimulationEngine engine, AircraftState aircraft, int maxSeconds) =>
        SfoGroundHarness.TickUntil(
            engine,
            () => (aircraft.GroundSpeed < AtRestKts) && (aircraft.Phases?.CurrentPhase is HoldingInPositionPhase or AtParkingPhase),
            maxSeconds,
            null
        );

    [Fact]
    public void TaxiTo5A_FromD2LeadOut_EndsAlongT5A()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot5A = ground.Layout.FindSpotNodeByName("5A");
        Assert.True(spot5A is not null, "SFO layout has no spot named '5A'");

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW1", "E75L", "D2");
        CommandResult result = ground.Engine.SendCommand("SKW1", "TAXI $5A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot5A);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW1 never came to rest at spot 5A");
        AssertRestingOnSpotFacing(aircraft, spot5A, "T5A", LaneJunctionWithA(ground.Layout, "T5A"), AlignmentToleranceDeg);
    }

    [Fact]
    public void TaxiT7aToSpot7A_FromOffLane_EndsAlongT7A()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");

        (GroundNode abeam, double laneBearing) = MidLanePoint(ground.Layout, spot7A, "T7A");
        LatLon offLane = GeoMath.ProjectPoint(abeam.Position, new TrueHeading((laneBearing + 90.0) % 360.0), 115.0 / GeoMath.FeetPerNm);
        output.WriteLine($"abeam #{abeam.Id}, lane bearing {laneBearing:F0}, spawning 115 ft off it");

        AircraftState aircraft = SpawnOffGraph(
            ground,
            "SKW2",
            "CRJ2",
            (offLane, new TrueHeading((laneBearing + 47.0) % 360.0)),
            new HoldingInPositionPhase()
        );
        CommandResult result = ground.Engine.SendCommand("SKW2", "TAXI T7A $7A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot7A);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW2 never came to rest at spot 7A");
        AssertRestingOnSpotAlongLane(aircraft, spot7A, "T7A");
    }

    /// <summary>SKW5590 at bundle t=30: PUSH T7A off F8 ended on the apron west of T7A's north end, nosed 254°.</summary>
    private static readonly (LatLon Position, TrueHeading Heading) Skw5590Pose = (
        new LatLon(37.62091321146667, -122.38580122316866),
        new TrueHeading(254.0)
    );

    /// <summary>How far back along taxiway A from the T7A junction the movement-area cases start.</summary>
    private const double OnTaxiwayABackFt = 300.0;

    /// <summary>
    /// A <c>TAXI … $spot</c> given on the ramp is a line-up to leave it (#456). SKW5590, pushed off F8 onto the
    /// apron west of T7A's north end, is cleared <c>TAXI T7A $7A</c>: it swings round on the ramp, joins T7A on
    /// the ramp side of the spot and comes up the lane, stopping nose-on-mark facing taxiway A — never swinging
    /// out over A to turn back in.
    /// </summary>
    [Fact]
    public void TaxiT7aToSpot7A_FromTheRamp_LinesUpFacingTaxiwayA()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");

        AircraftState aircraft = SpawnOffGraph(ground, "SKW5590", "CRJ7", Skw5590Pose, new HoldingAfterPushbackPhase());
        CommandResult result = ground.Engine.SendCommand("SKW5590", "TAXI T7A $7A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        DumpSegments(route);
        output.WriteLine($"spot 7A is node {spot7A.Id}; T7A meets A at node {junction.Id}");

        double? spanFt = FaaAircraftDatabase.Get("CRJ7")?.WingspanFt;
        Assert.True(spanFt is > 0.0, "no FAA wingspan for CRJ7");
        double halfSpanFt = spanFt.Value / 2.0;
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            double closestFt = ClosestApproachToTaxiwayFt(ground.Layout, "A", segment);
            Assert.True(
                closestFt > halfSpanFt,
                $"segment [{segment.FromNodeId} -> {segment.ToNodeId}] {segment.TaxiwayName} comes within {closestFt:F0} ft of A's centreline "
                    + $"(half span {halfSpanFt:F0} ft)"
            );
        }

        var trace = new List<(LatLon Position, double HeadingDeg, double SpeedKts)> { (aircraft.Position, aircraft.TrueHeading.Degrees, 0.0) };
        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.GroundSpeed < AtRestKts) && (aircraft.Phases?.CurrentPhase is HoldingInPositionPhase),
            300,
            _ => trace.Add((aircraft.Position, aircraft.TrueHeading.Degrees, aircraft.GroundSpeed))
        );
        Assert.True(stopped > 0, "SKW5590 never came to rest at spot 7A");
        double peakDecelKtsPerSec = trace.Zip(trace.Skip(1), (a, b) => a.SpeedKts - b.SpeedKts).DefaultIfEmpty(0.0).Max();
        output.WriteLine($"speeds {string.Join(" ", trace.Select(t => $"{t.SpeedKts:F1}"))}; peak deceleration {peakDecelKtsPerSec:F2} kt/s");
        AssertRestingOnSpotFacing(aircraft, spot7A, "T7A", junction, LineUpHeadingToleranceDeg);
        AssertQuarterTurnLineUp(
            trace,
            spot7A,
            GeoMath.BearingTo(spot7A.Position, junction.Position),
            FaaAircraftDatabase.Get("CRJ7")?.LengthFt ?? throw new InvalidOperationException("no FAA length for CRJ7")
        );
    }

    /// <summary>
    /// A snapshot taken while SKW5590 pulls up T7A onto spot 7A restores the line-up's slow pull: the restored route
    /// still marks where the pull starts, and the aircraft finishes the straight no faster than the pull speed.
    /// </summary>
    [Fact]
    public void SpotLineUp_RestoredMidPull_KeepsThePullSpeed()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState aircraft = SpawnOffGraph(ground, "SKW5590", "CRJ7", Skw5590Pose, new HoldingAfterPushbackPhase());
        CommandResult result = ground.Engine.SendCommand("SKW5590", "TAXI T7A $7A");
        Assert.True(result.Success, result.Message);
        int? pullFrom = aircraft.Ground.AssignedTaxiRoute?.SpotLineUpPullFromSegment;
        Assert.NotNull(pullFrom);

        // Snapshot at the very start of the pull — the segment change out of the quarter turn — where the cap first bites.
        int onPull = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.Ground.AssignedTaxiRoute is { } taxiing) && (taxiing.CurrentSegmentIndex == pullFrom.Value),
            200,
            null
        );
        Assert.True(onPull > 0, "SKW5590 never reached the lined-up pull");

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        ground.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = ground.Engine.FindAircraft("SKW5590") ?? throw new InvalidOperationException("SKW5590 missing after restore");
        Assert.Equal(pullFrom, restored.Ground.AssignedTaxiRoute?.SpotLineUpPullFromSegment);

        double maxKts = 0.0;
        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (restored.GroundSpeed < AtRestKts) && (restored.Phases?.CurrentPhase is HoldingInPositionPhase),
            120,
            _ => maxKts = Math.Max(maxKts, restored.GroundSpeed)
        );
        output.WriteLine($"restored at t={onPull} on segment {pullFrom.Value}; at rest {stopped} s later, peaking at {maxKts:F1} kts");
        Assert.True(stopped > 0, "the restored SKW5590 never came to rest at spot 7A");
        Assert.True(maxKts <= TaxiingPhase.SpotLineUpPullSpeedKts, $"the restored pull ran at up to {maxKts:F1} kts");
    }

    /// <summary>The type parked on the stand in <see cref="SpotLineUp_NeverCrossesAnOccupiedStand"/>.</summary>
    private const string ParkedType = "B77W";

    /// <summary>
    /// A line-up keeps its free-space legs a wingtip buffer clear of every parked aircraft. The stand nearest the legs
    /// SKW5590 plans across the empty ramp is one the line-up passes within that clearance of — an empty stand is no
    /// obstacle. With an aircraft parked on it, the re-issued clearance either picks a join whose legs pass clear or
    /// keeps the route as resolved.
    /// </summary>
    [Fact]
    public void SpotLineUp_NeverCrossesAnOccupiedStand()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        AircraftState aircraft = SpawnOffGraph(ground, "SKW5590", "CRJ7", Skw5590Pose, new HoldingAfterPushbackPhase());
        TaxiRoute clear = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.True(clear.SpotLineUpPullFromSegment is not null, "the line-up across the empty ramp was not planned");

        // A widebody on the stand: its wing reaches far enough toward the legs that the ramp beside SKW5590's pushback
        // offers a stand the empty-ramp line-up passes within the clearance of.
        double ownHalfSpanFt = (FaaAircraftDatabase.Get("CRJ7")?.WingspanFt ?? throw new InvalidOperationException("no FAA wingspan for CRJ7")) / 2.0;
        double parkedHalfSpanFt =
            (FaaAircraftDatabase.Get(ParkedType)?.WingspanFt ?? throw new InvalidOperationException($"no FAA wingspan for {ParkedType}")) / 2.0;
        double clearanceFt = ownHalfSpanFt + parkedHalfSpanFt + GroundOutlineSweep.WingtipBufferFt;
        List<TaxiRouteSegment> legs = [.. clear.Segments.Take(clear.SpotLineUpPullFromSegment.Value)];
        GroundNode stand = ground
            .Layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (DistanceFt(n.Position, Skw5590Pose.Position) >= clearanceFt))
            .MinBy(n => ClosestLegApproachFt(legs, n.Position))!;
        double emptyFt = ClosestLegApproachFt(legs, stand.Position);
        output.WriteLine($"stand {stand.Name} lies {emptyFt:F0} ft from the empty-ramp legs (clearance {clearanceFt:F0} ft)");
        Assert.True(emptyFt < clearanceFt, $"no stand lies under the line-up's legs — the nearest, {stand.Name}, is {emptyFt:F0} ft off");

        SfoGroundHarness.SpawnParked(ground, "UAL1", ParkedType, stand.Name!);
        TaxiRoute blocked = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.Equal(spot7A.Id, blocked.Segments[^1].ToNodeId);
        if (blocked.SpotLineUpPullFromSegment is not { } pullFrom)
        {
            output.WriteLine("no join passes clear of the parked aircraft: the route is kept as resolved");
            return;
        }

        foreach (TaxiRouteSegment leg in blocked.Segments.Take(pullFrom))
        {
            double legFt = ClosestLegApproachFt([leg], stand.Position);
            Assert.True(legFt >= clearanceFt, $"leg [{leg.FromNodeId} -> {leg.ToNodeId}] passes {legFt:F0} ft from occupied stand {stand.Name}");
        }
    }

    /// <summary>
    /// An aircraft pushed back onto a movement-area taxiway is on that taxiway, not on the ramp: holding after a
    /// pushback on taxiway A, <c>TAXI T7A $7A</c> is no line-up.
    /// </summary>
    [Fact]
    public void TaxiT7aToSpot7A_PushedBackOntoTaxiwayA_DoesNotLineUp()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");
        GroundNode spot7A = ground.Layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO layout has no spot named '7A'");

        // Pushed back to 60 ft short of the junction and 20 ft off A's centreline on the ramp side: still on A — its
        // centreline is the nearest edge and its wing overhangs it — but close enough to the ramp that a line-up's legs
        // would be drivable from here if the pushback alone counted as being on the ramp.
        (LatLon onA, TrueHeading alongA) = PoseOnTaxiwayA(ground.Layout, junction);
        double towardJunctionDeg = GeoMath.BearingTo(onA, junction.Position);
        LatLon position = GeoMath.ProjectPoint(junction.Position, new TrueHeading((towardJunctionDeg + 180.0) % 360.0), 60.0 / GeoMath.FeetPerNm);
        position = GeoMath.ProjectPoint(position, new TrueHeading(GeoMath.BearingTo(junction.Position, spot7A.Position)), 20.0 / GeoMath.FeetPerNm);
        AircraftState aircraft = SpawnOffGraph(ground, "SKW9", "CRJ7", (position, alongA), new HoldingAfterPushbackPhase());
        TaxiRoute route = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.Null(route.SpotLineUpPullFromSegment);
    }

    /// <summary>
    /// Movement area wins: an aircraft on taxiway A beside the T7A junction, or just onto T7A with its wing still over
    /// A, is on the movement area whichever lane edge lies nearest, and gets no line-up.
    /// </summary>
    [Theory]
    [InlineData(-15.0, 0.0)]
    [InlineData(15.0, 0.0)]
    [InlineData(0.0, 20.0)]
    public void TaxiT7aToSpot7A_WithTheWingOverTaxiwayA_DoesNotLineUp(double alongAFt, double downT7aFt)
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");
        (LatLon onA, TrueHeading alongA) = PoseOnTaxiwayA(ground.Layout, junction);
        double towardJunctionDeg = GeoMath.BearingTo(onA, junction.Position);
        double laneDeg = GeoMath.BearingTo(junction.Position, spot7A.Position);
        LatLon position = GeoMath.ProjectPoint(junction.Position, new TrueHeading(towardJunctionDeg), alongAFt / GeoMath.FeetPerNm);
        position = GeoMath.ProjectPoint(position, new TrueHeading(laneDeg), downT7aFt / GeoMath.FeetPerNm);
        TrueHeading heading = downT7aFt > 0.0 ? new TrueHeading(laneDeg) : alongA;
        output.WriteLine(
            $"{alongAFt:F0} ft along A past the junction, {downT7aFt:F0} ft down T7A, "
                + $"nearest edge {ground.Layout.FindNearestTaxiEdge(position)?.Edge.TaxiwayName}"
        );

        Assert.False(RampLaneReposition.StartsOffMovementArea(ground.Layout, position, atParking: false, "CRJ7"), "counted as off the movement area");
        AircraftState aircraft = SpawnOffGraph(ground, "SKW10", "CRJ7", (position, heading), new HoldingInPositionPhase());
        TaxiRoute route = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.Null(route.SpotLineUpPullFromSegment);
    }

    /// <summary>
    /// An aircraft that turned off A onto T7A and stopped between the junction and spot 7A is past its line-up point:
    /// <c>TAXI T7A $7A</c> takes it on to the spot, never round past it to come back up the lane.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(180.0)]
    public void TaxiT7aToSpot7A_OnT7aBetweenAAndTheSpot_DoesNotLineUp(double turnedFromIntoRampDeg)
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");
        double intoRampDeg = GeoMath.BearingTo(junction.Position, spot7A.Position);
        double betweenFt = DistanceFt(junction.Position, spot7A.Position) * 0.6;
        LatLon position = GeoMath.ProjectPoint(junction.Position, new TrueHeading(intoRampDeg), betweenFt / GeoMath.FeetPerNm);
        output.WriteLine(
            $"{betweenFt:F0} ft down T7A from the junction, nearest edge {ground.Layout.FindNearestTaxiEdge(position)?.Edge.TaxiwayName}"
        );

        AircraftState aircraft = SpawnOffGraph(
            ground,
            "SKW11",
            "CRJ7",
            (position, new TrueHeading((intoRampDeg + turnedFromIntoRampDeg) % 360.0)),
            new HoldingInPositionPhase()
        );
        TaxiRoute route = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.Null(route.SpotLineUpPullFromSegment);
    }

    /// <summary>
    /// A clearance that names taxiways beyond the spot's own lane is taxied as issued (the #454 rule): from gate D2,
    /// <c>TAXI T5 A T5A $5A</c> goes down T5 and along A, and is not re-planned into a line-up.
    /// </summary>
    [Fact]
    public void TaxiT5AT5aTo5A_FromD2_KeepsTheNamedTaxiways()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW12", "E75L", "D2");
        TaxiRoute route = AssignTaxi(ground, aircraft, "TAXI T5 A T5A $5A");
        List<string> lanes = LaneSequence(route.Segments);
        output.WriteLine($"lanes: {string.Join(" ", lanes)}");
        Assert.Null(route.SpotLineUpPullFromSegment);
        Assert.Contains("T5", lanes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("A", lanes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The resolved route's explicit hold-shorts and warnings are carried into the line-up route rather than replaced
    /// by the lane tail's: the clearance's own instructions survive the re-plan.
    /// </summary>
    [Fact]
    public void SpotLineUp_ExplicitHoldShortAndWarnings_Survive()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        AirportGroundLayout layout = built.Value.Layout;
        GroundNode spot7A = layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO layout has no spot named '7A'");
        GroundNode junction = LaneJunctionWithA(layout, "T7A");
        TaxiRoute resolved = JunctionToSpot(layout, spot7A);
        resolved.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = junction.Id,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = "A",
            }
        );
        resolved.Warnings.Add("taxiing via T7A — test warning");
        double lengthFt = FaaAircraftDatabase.Get("CRJ7")?.LengthFt ?? throw new InvalidOperationException("no FAA length for CRJ7");

        TaxiRoute? lineUp = RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(Skw5590Pose.Position, resolved, spot7A, lengthFt));

        Assert.NotNull(lineUp);
        Assert.Contains(lineUp.HoldShortPoints, h => (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.NodeId == junction.Id));
        Assert.Contains("taxiing via T7A — test warning", lineUp.Warnings);
    }

    /// <summary>
    /// The pull index is the first lane segment however many legs lead to it: a prepended approach leg moves it on by
    /// one, and a truncation keeps it only while the lane segment it points at is kept.
    /// </summary>
    [Fact]
    public void SpotLineUp_PullIndex_FollowsPrependAndTruncate()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        AircraftState aircraft = SpawnOffGraph(ground, "SKW5590", "CRJ7", Skw5590Pose, new HoldingAfterPushbackPhase());
        TaxiRoute route = AssignTaxi(ground, aircraft, "TAXI T7A $7A");
        Assert.True(route.SpotLineUpPullFromSegment is not null, "no line-up planned");
        int pullFrom = route.SpotLineUpPullFromSegment.Value;
        Assert.Equal(route.Segments.Count - pullFrom, route.Segments.Skip(pullFrom).Count(s => s.FromNodeId >= 0));

        double firstDeg = route.Segments[0].Edge.DepartureBearing;
        LatLon behind = GeoMath.ProjectPoint(Skw5590Pose.Position, new TrueHeading((firstDeg + 180.0) % 360.0), 80.0 / GeoMath.FeetPerNm);
        TaxiRoute prepended = TaxiApproachLeg.Prepend(ground.Layout, behind, new TrueHeading(firstDeg), route);
        Assert.Equal(route.Segments.Count + 1, prepended.Segments.Count);
        Assert.Equal(pullFrom + 1, prepended.SpotLineUpPullFromSegment);

        Assert.Equal(pullFrom, route.TruncateAt(spot7A.Id).SpotLineUpPullFromSegment);
        Assert.Null(route.TruncateAt(route.Segments[pullFrom - 1].ToNodeId).SpotLineUpPullFromSegment);
    }

    /// <summary>The resolved route SKW5590's clearance gives before any line-up: from T7A's junction with A down to spot 7A.</summary>
    private static TaxiRoute JunctionToSpot(AirportGroundLayout layout, GroundNode spot) =>
        TaxiPathfinder.FindRoute(layout, LaneJunctionWithA(layout, "T7A").Id, spot.Id, AircraftCategory.Jet)
        ?? throw new InvalidOperationException("no route from the T7A/A junction to spot 7A");

    private static SpotLineUpRequest LineUpRequest(LatLon position, TaxiRoute route, GroundNode spot, double lengthFt) =>
        new()
        {
            Callsign = "SKW5590",
            AircraftType = "CRJ7",
            Position = position,
            Route = route,
            Spot = spot,
            Category = AircraftCategorization.Categorize("CRJ7"),
            AircraftLengthFt = lengthFt,
            ClearedTaxiways = ["T7A"],
            OtherGroundAircraft = [],
        };

    /// <summary>
    /// Each way <see cref="RampLaneReposition.TryPlanSpotLineUp"/> declines hands the route back as resolved (null): a
    /// route already arriving toward the movement area, a spot not mid-lane, no join with the run a quarter turn needs,
    /// and a drive to the approach point the ramp cuts refuse. The same request from the SKW5590 pose plans one.
    /// </summary>
    [Fact]
    public void TryPlanSpotLineUp_EachRefusal_KeepsTheResolvedRoute()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        AirportGroundLayout layout = built.Value.Layout;
        GroundNode spot7A = layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO layout has no spot named '7A'");
        double lengthFt = FaaAircraftDatabase.Get("CRJ7")?.LengthFt ?? throw new InvalidOperationException("no FAA length for CRJ7");
        TaxiRoute resolved = JunctionToSpot(layout, spot7A);
        Assert.NotNull(RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(Skw5590Pose.Position, resolved, spot7A, lengthFt)));

        GroundNode rampSide = LaneNeighbourAwayFrom(spot7A, "T7A", LaneJunctionWithA(layout, "T7A"));
        TaxiRoute arriving = TaxiPathfinder.FindRoute(layout, rampSide.Id, spot7A.Id, AircraftCategory.Jet)!;
        Assert.Null(RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(Skw5590Pose.Position, arriving, spot7A, lengthFt)));

        Assert.Null(RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(Skw5590Pose.Position, resolved, spot7A, 2000.0)));

        LatLon farOut = GeoMath.ProjectPoint(Skw5590Pose.Position, Skw5590Pose.Heading, 900.0 / GeoMath.FeetPerNm);
        Assert.Null(RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(farOut, resolved, spot7A, lengthFt)));

        (GroundNode offLane, TaxiRoute toOffLane) = layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Spot) && !SitsMidLane(n) && (n.Edges.Count > 0))
            .Select(n => (Spot: n, Route: TaxiPathfinder.FindRoute(layout, n.Edges[0].OtherNode(n).Id, n.Id, AircraftCategory.Jet)))
            .Where(c => c.Route is { Segments.Count: > 0 })
            .Select(c => (c.Spot, c.Route!))
            .First();
        string lanes = string.Join(", ", offLane.Edges.Select(e => $"{e.TaxiwayName}{(e is GroundEdge ? "" : " (arc)")}"));
        output.WriteLine($"spot {offLane.Name} (node {offLane.Id}) does not sit mid-lane: [{lanes}]");
        Assert.Null(RampLaneReposition.TryPlanSpotLineUp(layout, LineUpRequest(Skw5590Pose.Position, toOffLane, offLane, lengthFt)));
    }

    /// <summary>The spot has exactly two straight edges, both of one lane — the shape a line-up needs.</summary>
    private static bool SitsMidLane(GroundNode spot)
    {
        var straight = spot.Edges.Where(e => e is GroundEdge).ToList();
        return (straight.Count == 2) && straight[0].TaxiwayName.Equals(straight[1].TaxiwayName, StringComparison.OrdinalIgnoreCase);
    }

    private static TaxiRoute AssignTaxi(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, result.Message);
        return aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{command} left no route");
    }

    private static double ClosestLegApproachFt(IEnumerable<TaxiRouteSegment> legs, LatLon point) =>
        legs.Min(l => GeoMath.DistanceToSegmentFt(point, l.Edge.FromNode.Position, l.Edge.ToNode.Position));

    /// <summary>How far a line-up's resting heading, and its lined-up pull, may sit off the lane toward the exit.</summary>
    private const double LineUpHeadingToleranceDeg = 10.0;

    /// <summary>The most a line-up may turn in one continuous turn: no loop round the compass.</summary>
    private const double MaxContinuousTurnDeg = 200.0;

    /// <summary>The most the last turn before a line-up stops may be: a quarter turn onto the lane, not a reversal.</summary>
    private const double MaxLastTurnDeg = 110.0;

    /// <summary>A heading change slower than this in a second is a straight, and ends a continuous turn.</summary>
    private const double TurningDegPerSecond = 1.0;

    /// <summary>How far either side of the lane's inbound bearing a heading counts as having turned back into the ramp.</summary>
    private const double LoopBandDeg = 20.0;

    /// <summary>
    /// Asserts the per-second trace of a line-up is one smooth quarter turn onto the lane and a slow straight pull:
    /// no continuous turn over <see cref="MaxContinuousTurnDeg"/>, a last turn no sharper than
    /// <see cref="MaxLastTurnDeg"/>, lined up within <see cref="LineUpHeadingToleranceDeg"/> for at least half a
    /// fuselage before the stop at no more than <see cref="TaxiingPhase.SpotLineUpPullSpeedKts"/>, and no heading back into the ramp
    /// once the aircraft has crossed the lane's line.
    /// </summary>
    private void AssertQuarterTurnLineUp(
        List<(LatLon Position, double HeadingDeg, double SpeedKts)> trace,
        GroundNode spot,
        double exitDeg,
        double lengthFt
    )
    {
        var turns = new List<double>();
        double current = 0.0;
        for (int i = 1; i < trace.Count; i++)
        {
            double delta = GeoMath.SignedBearingDifference(trace[i - 1].HeadingDeg, trace[i].HeadingDeg);
            bool turning = Math.Abs(delta) >= TurningDegPerSecond;
            if (turning && ((current == 0.0) || (Math.Sign(current) == Math.Sign(delta))))
            {
                current += delta;
                continue;
            }

            if (current != 0.0)
            {
                turns.Add(current);
            }

            current = turning ? delta : 0.0;
        }

        if (current != 0.0)
        {
            turns.Add(current);
        }

        double linedUpFt = 0.0;
        double linedUpMaxKts = 0.0;
        for (int i = trace.Count - 1; (i > 0) && (GeoMath.AbsBearingDifference(trace[i].HeadingDeg, exitDeg) <= LineUpHeadingToleranceDeg); i--)
        {
            linedUpFt += DistanceFt(trace[i - 1].Position, trace[i].Position);
            linedUpMaxKts = Math.Max(linedUpMaxKts, trace[i].SpeedKts);
        }

        var exitHeading = new TrueHeading(exitDeg);
        int startSide = Math.Sign(GeoMath.SignedCrossTrackDistanceNm(trace[0].Position, spot.Position, exitHeading));
        int crossed = trace.FindIndex(t => Math.Sign(GeoMath.SignedCrossTrackDistanceNm(t.Position, spot.Position, exitHeading)) != startSide);
        double inboundDeg = (exitDeg + 180.0) % 360.0;
        bool loops = (crossed >= 0) && trace.Skip(crossed).Any(t => GeoMath.AbsBearingDifference(t.HeadingDeg, inboundDeg) <= LoopBandDeg);

        output.WriteLine(
            $"turns [{string.Join(", ", turns.Select(t => $"{t:F0}"))}], lined up {linedUpFt:F0} ft (half fuselage {lengthFt / 2.0:F0}) "
                + $"at up to {linedUpMaxKts:F1} kts, crossed the lane line at t={crossed}, loops={loops}"
        );
        Assert.True(turns.Count > 0, "the trace never turned");
        Assert.True(turns.Max(Math.Abs) <= MaxContinuousTurnDeg, $"a continuous turn of {turns.Max(Math.Abs):F0}° — a loop");
        Assert.True(Math.Abs(turns[^1]) <= MaxLastTurnDeg, $"the last turn is {Math.Abs(turns[^1]):F0}°, sharper than a quarter turn");
        Assert.True(linedUpFt >= lengthFt / 2.0, $"lined up for only {linedUpFt:F0} ft before stopping");
        Assert.True(linedUpMaxKts <= TaxiingPhase.SpotLineUpPullSpeedKts, $"the lined-up pull runs at up to {linedUpMaxKts:F1} kts");
        Assert.False(loops, "the aircraft turned back into the ramp after crossing the lane's line");
    }

    /// <summary>
    /// The same clearance from the movement area keeps the arrival it has always had: an aircraft on taxiway A
    /// cleared <c>TAXI T7A $7A</c> turns off A onto T7A and comes to rest on the spot facing into the ramp.
    /// The ramp line-up rule decides nothing here.
    /// </summary>
    [Fact]
    public void TaxiT7aToSpot7A_FromTaxiwayA_EntersFacingIntoTheRamp()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");

        AircraftState aircraft = SpawnOffGraph(ground, "SKW7", "CRJ7", PoseOnTaxiwayA(ground.Layout, junction), new HoldingInPositionPhase());
        CommandResult result = ground.Engine.SendCommand("SKW7", "TAXI T7A $7A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        DumpSegments(route);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 300);
        Assert.True(stopped > 0, "SKW7 never came to rest at spot 7A");
        AssertRestingOnSpotFacing(aircraft, spot7A, "T7A", LaneNeighbourAwayFrom(spot7A, "T7A", junction), AlignmentToleranceDeg);
    }

    /// <summary>
    /// The mirror of the ramp line-up: an aircraft on taxiway A cleared <c>TAXI A $7A @F8</c> enters the ramp
    /// through the spot — off A onto T7A at the junction, through spot 7A heading into the ramp, on along T7A's
    /// taxilane and into gate F8.
    /// </summary>
    [Fact]
    public void TaxiAT7aSpotToGate_FromTaxiwayA_EntersTheRampThroughSpot7A()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");
        GroundNode? gateF8 = ground.Layout.FindParkingByName("F8");
        Assert.True(gateF8 is not null, "SFO layout has no parking named 'F8'");
        GroundNode junction = LaneJunctionWithA(ground.Layout, "T7A");

        AircraftState aircraft = SpawnOffGraph(ground, "SKW8", "CRJ7", PoseOnTaxiwayA(ground.Layout, junction), new HoldingInPositionPhase());
        CommandResult result = ground.Engine.SendCommand("SKW8", "TAXI A $7A @F8");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        DumpSegments(route);

        int intoSpot = route.Segments.FindIndex(s => s.ToNodeId == spot7A.Id);
        Assert.True(intoSpot >= 0, $"the route never reaches spot 7A (node {spot7A.Id})");
        List<string> lanesToSpot = LaneSequence(route.Segments.Take(intoSpot + 1));
        Assert.Equal(["A", "T7A"], lanesToSpot);

        double intoRampDeg = GeoMath.BearingTo(spot7A.Position, LaneNeighbourAwayFrom(spot7A, "T7A", junction).Position);
        double arrivalDeg = route.Segments[intoSpot].Edge.ArrivalBearing;
        output.WriteLine($"route passes spot 7A travelling {arrivalDeg:F0}; into the ramp is {intoRampDeg:F0}");
        Assert.True(
            GeoMath.AbsBearingDifference(arrivalDeg, intoRampDeg) <= AlignmentToleranceDeg,
            $"the route passes spot 7A travelling {arrivalDeg:F0}, not into the ramp ({intoRampDeg:F0})"
        );

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW8 never came to rest");
        double fromGateFt = DistanceFt(aircraft.Position, gateF8.Position);
        output.WriteLine($"SKW8 rests {fromGateFt:F0} ft from F8 in {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.IsType<AtParkingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(fromGateFt <= AirportGroundLayout.AtNodeToleranceFt, $"SKW8 parked {fromGateFt:F0} ft from gate F8");
    }

    /// <summary>The node where a spot lane meets taxiway A — the lane's movement-area end.</summary>
    private static GroundNode LaneJunctionWithA(AirportGroundLayout layout, string lane) =>
        layout.FindIntersectionNode("A", lane) ?? throw new InvalidOperationException($"SFO layout has no junction of A and {lane}");

    /// <summary>The spot's lane neighbour on the side away from <paramref name="away"/> — the next node down the lane.</summary>
    private static GroundNode LaneNeighbourAwayFrom(GroundNode spot, string lane, GroundNode away)
    {
        double awayDeg = GeoMath.BearingTo(spot.Position, away.Position);
        return spot.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway(lane))
                .Select(e => e.OtherNode(spot))
                .MaxBy(n => GeoMath.AbsBearingDifference(awayDeg, GeoMath.BearingTo(spot.Position, n.Position)))
            ?? throw new InvalidOperationException($"spot {spot.Name} has no edge of lane {lane}");
    }

    /// <summary>
    /// A pose on taxiway A's centreline <see cref="OnTaxiwayABackFt"/> west of <paramref name="junction"/> along A's
    /// own straight edges, nosed along A toward the junction.
    /// </summary>
    private static (LatLon Position, TrueHeading Heading) PoseOnTaxiwayA(AirportGroundLayout layout, GroundNode junction)
    {
        GroundNode node = junction;
        double bearingDeg = 270.0;
        double remainingFt = OnTaxiwayABackFt;
        var visited = new HashSet<int> { junction.Id };
        while (true)
        {
            GroundNode from = node;
            double headingDeg = bearingDeg;
            IGroundEdge edge =
                from.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("A") && !visited.Contains(e.OtherNode(from).Id))
                    .MinBy(e => GeoMath.AbsBearingDifference(headingDeg, GeoMath.BearingTo(from.Position, e.OtherNode(from).Position)))
                ?? throw new InvalidOperationException($"taxiway A ends at node {from.Id} within {OnTaxiwayABackFt:F0} ft of the T7A junction");
            GroundNode next = edge.OtherNode(from);
            double legDeg = GeoMath.BearingTo(from.Position, next.Position);
            double legFt = DistanceFt(from.Position, next.Position);
            if (legFt >= remainingFt)
            {
                LatLon position = GeoMath.ProjectPoint(from.Position, new TrueHeading(legDeg), remainingFt / GeoMath.FeetPerNm);
                Assert.True(SfoGroundHarness.DistanceToTaxiwayFt(layout, "A", position) < 1.0, "the pose is off A's centreline");
                return (position, new TrueHeading((legDeg + 180.0) % 360.0));
            }

            remainingFt -= legFt;
            visited.Add(next.Id);
            node = next;
            bearingDeg = legDeg;
        }
    }

    /// <summary>The taxiways a run of segments follows, in order, with consecutive repeats and arc members folded.</summary>
    private static List<string> LaneSequence(IEnumerable<TaxiRouteSegment> segments)
    {
        var lanes = new List<string>();
        foreach (TaxiRouteSegment segment in segments)
        {
            string[] names = segment.Edge.Edge is GroundArc arc ? arc.TaxiwayNames : [segment.TaxiwayName];
            foreach (string name in names)
            {
                if (!lanes.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    lanes.Add(name);
                }
            }
        }

        return lanes;
    }

    /// <summary>The closest any point of the segment's straight line comes to a straight edge of <paramref name="taxiway"/>, in feet.</summary>
    private static double ClosestApproachToTaxiwayFt(AirportGroundLayout layout, string taxiway, TaxiRouteSegment segment)
    {
        LatLon from = segment.Edge.FromNode.Position;
        LatLon to = segment.Edge.ToNode.Position;
        double lengthFt = DistanceFt(from, to);
        double bearingDeg = GeoMath.BearingTo(from, to);
        int steps = Math.Max(1, (int)Math.Ceiling(lengthFt / 5.0));
        double closestFt = double.PositiveInfinity;
        for (int i = 0; i <= steps; i++)
        {
            LatLon point = GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), lengthFt * i / steps / GeoMath.FeetPerNm);
            closestFt = Math.Min(closestFt, SfoGroundHarness.DistanceToTaxiwayFt(layout, taxiway, point));
        }

        return closestFt;
    }

    private void DumpSegments(TaxiRoute route)
    {
        SfoGroundHarness.DumpRoute(output, route);
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            output.WriteLine(
                $"  segment [{segment.FromNodeId} -> {segment.ToNodeId}] {segment.TaxiwayName}: "
                    + $"{segment.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft, arriving {segment.Edge.ArrivalBearing:F0}"
            );
        }
    }

    [Fact]
    public void TaxiSpot7A_To7B_EndsAlongT7B()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7B = ground.Layout.FindSpotNodeByName("7B");
        Assert.True(spot7B is not null, "SFO layout has no spot named '7B'");

        AircraftState aircraft = SfoGroundHarness.SpawnAtSpot(ground, "SKW3", "CRJ2", "7A");
        CommandResult result = ground.Engine.SendCommand("SKW3", "TAXI $7B");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot7B);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW3 never came to rest at spot 7B");
        AssertRestingOnSpotFacing(aircraft, spot7B, "T7B", LaneJunctionWithA(ground.Layout, "T7B"), AlignmentToleranceDeg);
    }

    /// <summary>The live case: SKW3396 parked at gate D2 in the recorded session, cleared to spot 5A.</summary>
    [Fact]
    public void Replay_Skw3396_TaxiTo5A_EndsAlongT5A()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 990);

        AircraftState? aircraft = engine.FindAircraft("SKW3396");
        Assert.NotNull(aircraft);

        // Isolate the subject: the recorded session packs traffic around the D2 alley, and a ground conflict
        // there stalls the taxi and masks the arrival geometry under test.
        foreach (AircraftState other in engine.World.GetSnapshot())
        {
            if (!string.Equals(other.Callsign, "SKW3396", StringComparison.Ordinal))
            {
                engine.World.RemoveAircraft(other.Callsign);
            }
        }

        AirportGroundLayout? layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        GroundNode? spot5A = layout.FindSpotNodeByName("5A");
        Assert.True(spot5A is not null, "SFO layout has no spot named '5A'");

        CommandResult result = engine.SendCommand("SKW3396", "TAXI $5A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot5A);

        // The graph route up T5A is longer than the free-space cut it replaces, so the budget is the taxi's,
        // not a fixed 90 s: SKW3396 is still rolling the last 80 ft at t=90.
        int stopped = TickToRest(engine, aircraft, maxSeconds: 300);
        Assert.True(stopped > 0, "SKW3396 never came to rest at spot 5A");
        AssertRestingOnSpotAlongLane(aircraft, spot5A, "T5A");
    }

    /// <summary>
    /// The run a ramp cut must leave into a spot is one fuselage, not a flat 100 ft: a landing point that
    /// gives a 106 ft regional jet room to straighten out leaves a 242 ft B77W still crossing its own lane
    /// when its nose reaches the marking. Driven from a landing node the SFO graph really offers between the
    /// two lengths, resolved from the layout rather than named.
    /// </summary>
    [Fact]
    public void SpotAlignmentRun_IsOneFuselage_NotAFlatHundredFeet()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        AirportGroundLayout layout = built.Value.Layout;
        double widebodyFt = FaaAircraftDatabase.Get("B77W")?.LengthFt ?? 0;
        Assert.True(widebodyFt > 200.0, $"B77W length came back as {widebodyFt:F0} ft");

        (GroundNode Spot, string Lane, TaxiRoute Tail)? found = FindShortLaneApproach(layout, widebodyFt);
        if (found is null)
        {
            output.WriteLine($"no SFO spot lane offers a landing node between {RunFloorFt:F0} and {widebodyFt:F0} ft of run — nothing to compare");
            return;
        }

        (GroundNode spot, string lane, TaxiRoute tail) = found.Value;
        output.WriteLine($"spot {spot.Name} on {lane}: landing node gives {tail.TotalDistanceFt:F0} ft of run");

        Assert.True(
            RampLaneReposition.EntersSpotAlongItsLane(spot, lane, tail, RunFloorFt),
            $"{tail.TotalDistanceFt:F0} ft is enough run for a {RunFloorFt:F0} ft aircraft"
        );
        Assert.False(
            RampLaneReposition.EntersSpotAlongItsLane(spot, lane, tail, widebodyFt),
            $"{tail.TotalDistanceFt:F0} ft of run was accepted for a {widebodyFt:F0} ft B77W"
        );
    }

    /// <summary>
    /// The first spot in the layout whose own lane offers a graph approach of between <see cref="RunFloorFt"/>
    /// and <paramref name="widebodyFt"/> feet of run — the window where the flat 100 ft rule and the
    /// one-fuselage rule disagree. Bounded at <see cref="MaxLaneCandidates"/> A* searches: SFO has enough
    /// spots, lanes and nodes that an exhaustive sweep is minutes of pathfinding on the no-match path, and a
    /// window this wide is either hit early or not there.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="widebodyFt">Upper bound on the run, in feet.</param>
    /// <returns>The spot, its lane, and the graph tail into it; null when the layout offers none.</returns>
    private static (GroundNode Spot, string Lane, TaxiRoute Tail)? FindShortLaneApproach(AirportGroundLayout layout, double widebodyFt)
    {
        int tried = 0;
        foreach (GroundNode spot in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Spot))
        {
            foreach (string lane in spot.Edges.Where(e => e is GroundEdge).Select(e => e.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (GroundNode from in layout.GetNodesOnTaxiway(lane).Where(n => n.Id != spot.Id))
                {
                    if (++tried > MaxLaneCandidates)
                    {
                        return null;
                    }

                    TaxiRoute? tail = TaxiPathfinder.FindRoute(layout, from.Id, spot.Id, AircraftCategory.Jet);
                    if (
                        (tail is null)
                        || (tail.Segments.Count == 0)
                        || (tail.TotalDistanceFt < RunFloorFt)
                        || (tail.TotalDistanceFt >= widebodyFt)
                        || !tail.Segments[^1].Edge.Edge.MatchesTaxiway(lane)
                        || (tail.Segments[^1].ToNodeId != spot.Id)
                    )
                    {
                        continue;
                    }

                    return (spot, lane, tail);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A point part-way down the spot's lane and the direction the lane runs there: the aircraft in the
    /// off-lane case sits abeam it, on open apron, with the lane's painted line beside it.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="spot">The spot whose lane is wanted.</param>
    /// <param name="lane">The lane name.</param>
    /// <returns>The node abeam which to place the aircraft, and the lane's true bearing there.</returns>
    private static (GroundNode Abeam, double LaneBearing) MidLanePoint(AirportGroundLayout layout, GroundNode spot, string lane)
    {
        var onLane = layout
            .GetNodesOnTaxiway(lane)
            .Where(n => (n.Id != spot.Id) && n.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway(lane)))
            .OrderBy(n => GeoMath.DistanceNm(spot.Position, n.Position))
            .ToList();
        if (onLane.Count == 0)
        {
            throw new InvalidOperationException($"SFO layout has no node on lane '{lane}' besides spot '{spot.Name}'");
        }

        GroundNode abeam = onLane[onLane.Count / 2];
        IGroundEdge edge = abeam.Edges.First(e => (e is GroundEdge) && e.MatchesTaxiway(lane));
        return (abeam, GeoMath.BearingTo(abeam.Position, edge.OtherNode(abeam).Position));
    }

    /// <summary>
    /// Spawns a stationary aircraft at a position that is not a graph node — the harness places aircraft on
    /// nodes, and this case needs one sitting out on the apron beside its lane.
    /// </summary>
    /// <param name="ground">Engine + layout.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="pose">Position and true heading to place it at.</param>
    /// <param name="startPhase">The holding phase it waits in — pushed back from a gate, or simply holding.</param>
    /// <returns>The spawned aircraft.</returns>
    private static AircraftState SpawnOffGraph(
        SfoGround ground,
        string callsign,
        string type,
        (LatLon Position, TrueHeading Heading) pose,
        Phase startPhase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pose.Position,
            TrueHeading = pose.Heading,
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
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
