using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// SFO ATCT SOP 3-5.c.i: spots 5A and 5B (like 6A and 6B) are usable simultaneously by aircraft
/// smaller than a B757 — the two alley lanes they sit on run parallel about 139 ft apart, so two
/// gates push tail-to-tail into them at the same time as routine ramp practice.
///
/// <para>The pushback yield in <see cref="GroundConflictDetector"/> pinned a pusher to a full stop
/// for any non-parked aircraft within the pushback buffer that lay ahead of the push direction, with
/// no lateral-clearance test: two simultaneous pushers froze each other and neither ever released.
/// A B738 (117 ft span) beside an E75L (94 ft span) needs 58.5 + 47 + 25 = 130.5 ft of lateral room,
/// which the 139 ft lane separation gives.</para>
///
/// <para>Two B738s need 142 ft and so still block each other here, and that is sim conservatism rather
/// than a rule: the pairing is SOP-legal (both are smaller than a B757) and meets the design standard —
/// AC 150/5300-13 puts ADG III taxilane separation at 1.1 x span + 10 ft = 140 ft, which leaves a
/// 117.5 ft span about 22 ft of wingtip room. The block comes from the shared 25 ft
/// <c>WingtipBufferFt</c> asking for more room than the airport design standard does.</para>
/// </summary>
public class SfoSimultaneousAlleyPushTests
{
    private const string NorthCallsign = "PSH5A";
    private const string SouthCallsign = "PSH5B";
    private const string NorthType = "B738";
    private const string SouthType = "E75L";
    private const string NorthGate = "D3";
    private const string SouthGate = "C9";
    private const int PushBudgetSeconds = 240;

    /// <summary>A speed cap at or below this is a full stop.</summary>
    private const double PinnedLimitKts = 0.001;

    /// <summary>
    /// The largest total time either push may spend capped to a stop before it counts as waiting out
    /// the other rather than yielding to it. The passing run spends 1s capped, so the budget is kept
    /// close to that: at 30s a partial regression would still slip through green.
    /// </summary>
    private const int CappedBudgetSeconds = 10;

    /// <summary>Tug speed for the direct-detector aircraft; the yield geometry is speed-independent.</summary>
    private const double TugSpeedKts = 2.0;

    /// <summary>Range ahead of the push direction used by the direct-detector test (inside the 200 ft buffer).</summary>
    private const double AheadFt = 100.0;

    /// <summary>Lateral offset of the parallel alley lane — above the 130.5 ft a B738/E75L pair needs.</summary>
    private const double LaneOffsetFt = 139.0;

    /// <summary>Along-axis offset between two tugs pushing side by side in the two lanes.</summary>
    private const double StaggerFt = 50.0;

    private readonly ITestOutputHelper _output;

    public SfoSimultaneousAlleyPushTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Both pushes into the 5A/5B alley run to completion, nose-out on their spot, with neither
    /// aircraft frozen: no controller hold at any point, the pair never closer than the sum of their
    /// half-wingspans, and neither capped to a full stop for a long stretch.
    ///
    /// <para>A short stop is legitimate and expected: the two paths run in line down the alley before
    /// they split into the parallel lanes, so a pusher can be genuinely aimed at the other's fuselage
    /// for a while and must yield. What the SOP rules out is one aircraft waiting out the other's
    /// whole push — hence a budget on the total time capped rather than "never capped".</para>
    /// </summary>
    [Fact]
    public void FiveAlley_TailToTail_BothPushesComplete_NeitherHeldLong()
    {
        var built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return;
        }

        var ground = built.Value;
        var spotNorth = ground.Layout.FindSpotNodeByName("5A");
        var spotSouth = ground.Layout.FindSpotNodeByName("5B");
        if (spotNorth is null || spotSouth is null)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, NorthCallsign, NorthType, NorthGate);
        SfoGroundHarness.SpawnParked(ground, SouthCallsign, SouthType, SouthGate);
        // Sum of half-wingspans, compared against straight-line centroid distance: a proximity sanity
        // floor, not a wingspan claim. Two pushers running in line down the alley are bounded by
        // fuselage length rather than span, so this cannot stand in for a wingtip-separation check.
        double minCentroidSeparationFt = (Wingspan(NorthType) + Wingspan(SouthType)) / 2.0;
        _output.WriteLine(
            $"5A<->5B = {DistFt(spotNorth.Position, spotSouth.Position):F0}ft; {NorthGate}->5A = "
                + $"{DistFt(Parking(ground, NorthGate), spotNorth.Position):F0}ft; {SouthGate}->5B = "
                + $"{DistFt(Parking(ground, SouthGate), spotSouth.Position):F0}ft; floor = {minCentroidSeparationFt:F0}ft"
        );

        Push(ground, NorthCallsign, "PUSH $5A");
        Push(ground, SouthCallsign, "PUSH $5B");

        var watch = new Watch([], new PinTracker(NorthCallsign), new PinTracker(SouthCallsign));
        int completedSecond = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => HasFinishedPush(ground, NorthCallsign) && HasFinishedPush(ground, SouthCallsign),
            PushBudgetSeconds,
            second => Observe(ground, second, new Alley(spotNorth, spotSouth, minCentroidSeparationFt), watch)
        );

        foreach (var tracker in new[] { watch.North, watch.South })
        {
            _output.WriteLine(tracker.Describe());
            if (tracker.ExceedsBudget)
            {
                watch.Violations.Add(tracker.Describe());
            }
        }

        if (watch.Violations.Count > 0)
        {
            Assert.Fail(
                "SFO ATCT SOP 3-5.c.i: 5A and 5B are used simultaneously below a B757, so two tail-to-tail pushes "
                    + $"into the parallel alley lanes must both run (a short yield is fine, waiting out the other's whole "
                    + $"push is not). Violations:{Environment.NewLine}  "
                    + string.Join(Environment.NewLine + "  ", watch.Violations)
            );
        }

        Assert.True(
            completedSecond > 0,
            $"SFO ATCT SOP 3-5.c.i: both pushes must complete, but after {PushBudgetSeconds}s "
                + $"{NorthCallsign} is {PhaseName(ground, NorthCallsign)} and {SouthCallsign} is {PhaseName(ground, SouthCallsign)}"
        );

        AssertRestingAtSpot(ground, NorthCallsign, spotNorth);
        AssertRestingAtSpot(ground, SouthCallsign, spotSouth);
    }

    /// <summary>
    /// The lateral-clearance test must not repeal the pushback yield: an aircraft dead ahead of the
    /// push direction still stops the pusher; the same aircraft a lane over does not. Both are pushing
    /// nose-away from each other, so a clearance test that read the nose heading instead of the push
    /// direction would get both halves wrong.
    /// </summary>
    [Fact]
    public void PushbackYield_DeadAheadStops_AbeamClears()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var spot = layout.FindSpotNodeByName("5A");
        if (spot is null)
        {
            return;
        }

        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outboundDeg), "SFO layout gives spot 5A no outbound heading");
        double pushDirDeg = new TrueHeading(outboundDeg).ToReciprocal().Degrees;

        var pusher = MakePushing("PSHA", NorthType, spot.Position, pushDirDeg);
        var deadAhead = MakePushing("PSHB", SouthType, Project(spot.Position, pushDirDeg, AheadFt), pushDirDeg + 180.0);
        GroundConflictDetector.ApplySpeedLimits([pusher, deadAhead], layout);
        _output.WriteLine($"dead-ahead ({AheadFt:F0}ft, 0ft lateral): limit={Format(pusher.Ground.SpeedLimit)}");
        Assert.True(
            pusher.Ground.SpeedLimit is { } aheadLimit && aheadLimit <= PinnedLimitKts,
            $"a pusher aimed at another aircraft's fuselage {AheadFt:F0}ft away must still stop, got limit={Format(pusher.Ground.SpeedLimit)}"
        );

        var abeam = MakePushing(
            "PSHC",
            SouthType,
            Project(Project(spot.Position, pushDirDeg, AheadFt), pushDirDeg + 90.0, LaneOffsetFt),
            pushDirDeg + 180.0
        );
        GroundConflictDetector.ApplySpeedLimits([pusher, abeam], layout);
        _output.WriteLine($"abeam ({AheadFt:F0}ft, {LaneOffsetFt:F0}ft lateral): limit={Format(pusher.Ground.SpeedLimit)}");
        Assert.True(
            pusher.Ground.SpeedLimit is null,
            $"a pusher with {LaneOffsetFt:F0}ft of lateral room (needs 130.5ft) must not be held, got limit={Format(pusher.Ground.SpeedLimit)}"
        );
    }

    /// <summary>
    /// The freeze the SOP rules out, pinned as geometry: two tugs side by side in the parallel alley
    /// lanes, pushing the same way, staggered half a fuselage along the axis. Neither may be capped —
    /// before the lateral test was added the trailing pusher stopped for the one ahead-and-abeam, and
    /// with both of them in that state at a real ramp neither ever released.
    /// </summary>
    [Fact]
    public void PushbackYield_AbeamPushers_NeitherCapped()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var spot = layout.FindSpotNodeByName("5A");
        if (spot is null)
        {
            return;
        }

        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outboundDeg), "SFO layout gives spot 5A no outbound heading");
        double pushDirDeg = new TrueHeading(outboundDeg).ToReciprocal().Degrees;

        // Same push direction, one lane over, the far one StaggerFt further down the alley: the lead is
        // ahead of the trail's push axis (the yield's trigger) but LaneOffsetFt clear of it.
        var trail = MakePushing("PSHD", NorthType, spot.Position, pushDirDeg);
        var leadPosition = Project(Project(spot.Position, pushDirDeg, StaggerFt), pushDirDeg + 90.0, LaneOffsetFt);
        var lead = MakePushing("PSHE", SouthType, leadPosition, pushDirDeg);
        GroundConflictDetector.ApplySpeedLimits([trail, lead], layout);

        _output.WriteLine(
            $"abeam pair ({StaggerFt:F0}ft stagger, {LaneOffsetFt:F0}ft lateral): {trail.Callsign} limit="
                + $"{Format(trail.Ground.SpeedLimit)}, {lead.Callsign} limit={Format(lead.Ground.SpeedLimit)}"
        );
        Assert.True(
            trail.Ground.SpeedLimit is null,
            $"the trailing pusher has {LaneOffsetFt:F0}ft of lateral room (needs 130.5ft) and must not be capped, "
                + $"got limit={Format(trail.Ground.SpeedLimit)}"
        );
        Assert.True(lead.Ground.SpeedLimit is null, $"the leading pusher must not be capped either, got limit={Format(lead.Ground.SpeedLimit)}");
    }

    /// <summary>The two alley spots under test and the centroid-distance floor their pair must keep.</summary>
    private readonly record struct Alley(GroundNode SpotNorth, GroundNode SpotSouth, double MinCentroidSeparationFt);

    /// <summary>What the per-second observer accumulates: outright violations plus each aircraft's stopped-time tally.</summary>
    private sealed record Watch(List<string> Violations, PinTracker North, PinTracker South);

    private void Observe(SfoGround ground, int second, Alley alley, Watch watch)
    {
        var north = ground.Engine.FindAircraft(NorthCallsign);
        var south = ground.Engine.FindAircraft(SouthCallsign);
        if (north is null || south is null)
        {
            return;
        }

        double sepFt = DistFt(north.Position, south.Position);
        int before = watch.Violations.Count;
        RecordHold(watch.Violations, second, north, south, sepFt);
        RecordHold(watch.Violations, second, south, north, sepFt);
        bool capped = watch.North.Observe(second, north, south, sepFt) || watch.South.Observe(second, south, north, sepFt);
        if (sepFt < alley.MinCentroidSeparationFt)
        {
            watch.Violations.Add($"t={second}s separation {sepFt:F0}ft < centroid floor {alley.MinCentroidSeparationFt:F0}ft");
        }

        if ((second % 10 == 0) || capped || (watch.Violations.Count > before))
        {
            _output.WriteLine(
                $"t={second, 3}s {north.Callsign} gs={north.GroundSpeed:F1} lim={Format(north.Ground.SpeedLimit)} "
                    + $"d2spot={DistFt(north.Position, alley.SpotNorth.Position):F0}ft {north.Phases?.CurrentPhase?.Name ?? "null"} | "
                    + $"{south.Callsign} gs={south.GroundSpeed:F1} lim={Format(south.Ground.SpeedLimit)} "
                    + $"d2spot={DistFt(south.Position, alley.SpotSouth.Position):F0}ft {south.Phases?.CurrentPhase?.Name ?? "null"} "
                    + $"| sep={sepFt:F0}ft"
            );
        }
    }

    /// <summary>
    /// A controller hold on either pusher is an outright violation — nothing in this scenario issues
    /// one, so it can only come from the resolver deciding the pair needs an operator to break it.
    /// </summary>
    private static void RecordHold(List<string> violations, int second, AircraftState ac, AircraftState other, double sepFt)
    {
        if (ac.Ground.Hold is { } hold)
        {
            violations.Add(
                $"t={second}s {ac.Callsign} held ({hold.Kind}) limit={Format(ac.Ground.SpeedLimit)} sep={sepFt:F0}ft {Geometry(ac, other, sepFt)}"
            );
        }
    }

    /// <summary>
    /// Counts how long one aircraft spends capped to a full stop: the total over the run, which is what
    /// <see cref="CappedBudgetSeconds"/> bounds, plus the longest single stop and the geometry of each
    /// stop's first second for the failure text.
    ///
    /// <para>It samples <c>Ground.SpeedLimit</c> once per sim-second, while the detector rewrites the cap
    /// on every 0.25s sub-tick, so a count here is a lower bound on the time actually spent capped.</para>
    /// </summary>
    private sealed class PinTracker(string callsign)
    {
        private readonly List<string> _stops = [];
        private int _currentStreakSeconds;

        internal int TotalSeconds { get; private set; }

        internal int LongestStreakSeconds { get; private set; }

        internal bool ExceedsBudget => TotalSeconds > CappedBudgetSeconds;

        /// <summary>Records this second and returns whether the aircraft was capped to a stop in it.</summary>
        internal bool Observe(int second, AircraftState ac, AircraftState other, double sepFt)
        {
            if (ac.Ground.SpeedLimit is not { } limit || limit > PinnedLimitKts)
            {
                _currentStreakSeconds = 0;
                return false;
            }

            TotalSeconds++;
            _currentStreakSeconds++;
            LongestStreakSeconds = Math.Max(LongestStreakSeconds, _currentStreakSeconds);
            if (_currentStreakSeconds == 1)
            {
                _stops.Add($"t={second}s sep={sepFt:F0}ft {Geometry(ac, other, sepFt)}");
            }

            return true;
        }

        internal string Describe() =>
            $"{callsign} capped to a stop for {TotalSeconds}s total (budget {CappedBudgetSeconds}s), longest run "
            + $"{LongestStreakSeconds}s; stops began at: {(_stops.Count == 0 ? "(none)" : string.Join("; ", _stops))}";
    }

    /// <summary>
    /// The lateral room the pusher has along its push direction — the quantity the yield rule tests —
    /// so a violation says why it fired rather than only that it did.
    /// </summary>
    private static string Geometry(AircraftState ac, AircraftState other, double sepFt)
    {
        if (ac.Ground.PushbackTrueHeading is not { } pushHdg)
        {
            return $"(not pushing, nose={ac.TrueHeading.Degrees:F0}°)";
        }

        double bearingDeg = GeoMath.BearingTo(ac.Position, other.Position);
        double offAxisDeg = Math.Abs(new TrueHeading(pushHdg.Degrees).AbsAngleTo(new TrueHeading(bearingDeg)));
        double lateralFt = sepFt * Math.Sin(offAxisDeg * Math.PI / 180.0);
        return $"(push={pushHdg.Degrees:F0}° brg={bearingDeg:F0}° off-axis={offAxisDeg:F0}° lateral={lateralFt:F0}ft)";
    }

    private void AssertRestingAtSpot(SfoGround ground, string callsign, GroundNode spot)
    {
        var ac = ground.Engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        double halfLengthFt = (FaaAircraftDatabase.Get(ac.AircraftType)?.LengthFt ?? 110.0) / 2.0;
        double distFt = DistFt(ac.Position, spot.Position);
        Assert.True(
            distFt <= halfLengthFt + 25.0,
            $"{callsign} should rest nose-at the spot (centroid ~{halfLengthFt:F0}ft back), but is {distFt:F0}ft from it"
        );

        Assert.True(
            ground.Layout.TryGetSpotOutboundHeading(spot, out double outboundDeg),
            $"SFO layout gives the {callsign} spot no outbound heading"
        );
        double headingErrDeg = new TrueHeading(outboundDeg).AbsAngleTo(ac.TrueHeading);
        _output.WriteLine($"{callsign} final: {distFt:F0}ft from spot, nose {ac.TrueHeading.Degrees:F0}° vs out {outboundDeg:F0}°");
        Assert.True(
            headingErrDeg <= 15.0,
            $"{callsign} ended {ac.TrueHeading.Degrees:F0}° — expected ~{outboundDeg:F0}° nose-out, off by {headingErrDeg:F0}°"
        );
    }

    private static void Push(SfoGround ground, string callsign, string command)
    {
        var result = ground.Engine.SendCommand(callsign, command);
        Assert.True(result.Success, $"'{command}' for {callsign} failed: {result.Message}");
    }

    private static AircraftState MakePushing(string callsign, string type, LatLon position, double pushDirectionDeg)
    {
        var pushDir = new TrueHeading(pushDirectionDeg);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = pushDir.ToReciprocal(),
            IsOnGround = true,
            IndicatedAirspeed = TugSpeedKts,
            Ground = new AircraftGroundOps { PushbackTrueHeading = pushDir },
        };
    }

    /// <summary>
    /// The push has finished: a <c>PUSH $spot</c> hands over to <see cref="HoldingAfterPushbackPhase"/>, since
    /// a ramp spot is a marking the aircraft waits on rather than a stand it parks on.
    /// </summary>
    private static bool HasFinishedPush(SfoGround ground, string callsign) =>
        ground.Engine.FindAircraft(callsign)?.Phases?.CurrentPhase is HoldingAfterPushbackPhase;

    private static string PhaseName(SfoGround ground, string callsign) => ground.Engine.FindAircraft(callsign)?.Phases?.CurrentPhase?.Name ?? "null";

    private static LatLon Parking(SfoGround ground, string parkingName)
    {
        var parking = ground.Layout.FindParkingByName(parkingName);
        Assert.NotNull(parking);
        return parking.Position;
    }

    private static double Wingspan(string type)
    {
        double? span = FaaAircraftDatabase.Get(type)?.WingspanFt;
        Assert.NotNull(span);
        return span.Value;
    }

    private static LatLon Project(LatLon from, double bearingDeg, double distanceFt) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), distanceFt / GeoMath.FeetPerNm);

    private static double DistFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string Format(double? limit) => limit?.ToString("F1") ?? "none";
}
