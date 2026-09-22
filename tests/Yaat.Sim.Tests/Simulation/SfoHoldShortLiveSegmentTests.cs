using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A standalone <c>HS &lt;taxiway&gt;</c> issued to an aircraft already rolling down the segment the bar sits
/// on must take effect on that live segment — the taxi phase re-aims at the painted bar and re-plans its
/// speed there and then. Field case (S1-SFO-2 Ground Control 28/01): SKW5416 westbound on B at 28 kt was
/// given <c>HS T</c> while already at the bar; the taxi kept the junction node as its target, braked to
/// 5 kt and crept there for 15 s before stopping with its nose in the T intersection.
///
/// <para>The second half of the contract is honesty: a bar closer than the aircraft's braking distance
/// cannot be made, so the pilot answers "unable … stopping" and the aircraft brakes at the full taxi rate
/// onto the best stop it has, instead of pretending the clearance was flown.</para>
/// </summary>
public class SfoHoldShortLiveSegmentTests(ITestOutputHelper output)
{
    /// <summary>S1-SFO-2 Ground Control 28/01 — the session the field case came from.</summary>
    private const string RecordingPath = "TestData/sfo-gc-28-01-spot-lanes-recording.zip";

    private const string Callsign = "SKW9001";
    private const string Type = "B738";
    private const string TaxiClearance = "TAXI B K A T7 @E2";

    /// <summary>Speed band (kts) a braking aircraft must pass straight through rather than settle in.</summary>
    private const double CreepLowKts = 3.0;
    private const double CreepHighKts = 7.0;

    /// <summary>Longest run of seconds inside the creep band that still counts as braking rather than creeping.</summary>
    private const int MaxCreepSeconds = 3;

    /// <summary>Ground speed (kts) the aircraft must have reached before a hold-short is issued to it.</summary>
    private const double IssueSpeedKts = 24.0;

    /// <summary>
    /// Ground speed (kts) SKW5416 must still be doing when the replayed case issues its hold-short: above a
    /// crawl, so the bar really is inside the braking distance and the case is the field one.
    /// </summary>
    private const double MinIssueSpeedKts = 15.0;

    /// <summary>Session second the replayed window starts at — SKW5416 rolling west on B toward the T bar.</summary>
    private const int WindowStartSeconds = 1265;

    /// <summary>How many seconds of the window are watched for the detector's cap to engage.</summary>
    private const int WindowSeconds = 25;

    /// <summary>Buffer (ft) the taxiway bar sits back from the junction node, on top of the fuselage length.</summary>
    private const double BarBufferFt = 30.0;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// Distance (ft) a jet needs to brake from <paramref name="speedKts"/> to a stop at the taxi
    /// deceleration rate — v² / 2a, computed here from the published rate so the test measures the
    /// kinematics rather than whatever the phase happens to compute.
    /// </summary>
    private static double BrakingDistanceFt(double speedKts)
    {
        double vFtPerSec = speedKts * GeoMath.FeetPerNm / 3600.0;
        double decelFtPerSec2 = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0;
        return (vFtPerSec * vFtPerSec) / (2.0 * decelFtPerSec2);
    }

    /// <summary>One scripted "roll down B, then hold short of T" run and everything asserted about it.</summary>
    private sealed record HsRun(AircraftState Aircraft, GroundNode Spawn, GroundNode Junction, CommandResult Result, RunTrace Trace);

    /// <summary>What the run measured: where and how fast the clearance was issued, and every speed after it.</summary>
    private sealed record RunTrace(double IssueDistFt, double IssueSpeedKts, LatLon IssuePosition, List<double> SpeedsAfterIssue);

    [Fact]
    public void HsT_IssuedFarOut_HoldsAtTheBar()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        HsRun? run = RunHoldShortOfT(built.Value, issueWithinFt: 600.0);
        if (run is null)
        {
            return;
        }

        Assert.True(run.Result.Success, $"HS T refused: {run.Result.Message}");
        Assert.StartsWith("Hold short of", run.Result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable", run.Result.Message, StringComparison.OrdinalIgnoreCase);

        AssertHoldingShortOfT(run);
        AssertNoCreep(run);

        double stopFromJunctionFt = DistanceFt(run.Aircraft.Position, run.Junction.Position);
        double barBackFt = (FaaAircraftDatabase.Get(Type)?.LengthFt ?? 0) + BarBufferFt;
        output.WriteLine($"stopped {stopFromJunctionFt:F0} ft from the B/T junction; bar sits {barBackFt:F0} ft back");
        Assert.True(
            stopFromJunctionFt >= barBackFt - 5.0,
            $"stopped {stopFromJunctionFt:F0} ft from the junction — the bar is {barBackFt:F0} ft back from it"
        );
    }

    [Fact]
    public void HsT_IssuedAtTheBar_BrakesAndReportsUnable()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        HsRun? run = RunHoldShortOfT(built.Value, issueWithinFt: 220.0);
        if (run is null)
        {
            return;
        }

        Assert.True(run.Result.Success, $"HS T refused: {run.Result.Message}");
        Assert.Contains("Unable", run.Result.Message, StringComparison.OrdinalIgnoreCase);

        AssertHoldingShortOfT(run);
        AssertNoCreep(run);

        double brakingFt = BrakingDistanceFt(run.Trace.IssueSpeedKts);
        double rolledFt = DistanceFt(run.Trace.IssuePosition, run.Aircraft.Position);
        double fromSpawnFt = DistanceFt(run.Spawn.Position, run.Aircraft.Position);
        double junctionFromSpawnFt = DistanceFt(run.Spawn.Position, run.Junction.Position);
        output.WriteLine(
            $"issued at {run.Trace.IssueDistFt:F0} ft / {run.Trace.IssueSpeedKts:F1} kt; rolled {rolledFt:F0} ft "
                + $"(braking distance {brakingFt:F0} ft); stopped {DistanceFt(run.Aircraft.Position, run.Junction.Position):F0} ft short of the junction"
        );

        Assert.True(rolledFt <= brakingFt * 1.1, $"rolled {rolledFt:F0} ft to stop; braking distance was {brakingFt:F0} ft");
        Assert.True(
            fromSpawnFt < junctionFromSpawnFt,
            $"stopped {fromSpawnFt:F0} ft from the spawn, past the junction at {junctionFromSpawnFt:F0} ft — unable must not mean carry on"
        );
    }

    /// <summary>
    /// The field case itself: SKW5416 at ~28 kt approaching the B/T bar is given <c>HS T</c>, is told the bar
    /// cannot be made, and stops.
    ///
    /// <para>The 5 kt crawl the session recorded is <em>not</em> the hold-short's doing: re-simulating this
    /// window with no <c>HS</c> issued at all, SKW5416 still drops from 28.4 kt to exactly 5.0 kt at ~230 ft
    /// from the junction and crawls in — <c>Ground.SpeedLimit</c>, the ground conflict detector's cap for
    /// SWA2644 on T, which no phase may override. So the creep check here only covers the seconds the
    /// detector left the aircraft alone; the uncapped crawl is pinned by the two scripted cases above.</para>
    /// </summary>
    [Fact]
    public void Replay_Skw5416_HsT_NearTheBar_ReportsUnableAndStops()
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

        // Pinned to the traffic, not to a distance: Replay(recording, t) re-simulates from t=0, so any sim
        // change before the window moves a "within 300 ft at 24 kt" trigger and the case quietly stops being
        // the one the field controller saw. So pass one measures the second SWA2644's cap engages on SKW5416,
        // and pass two re-simulates the same window — the replay is deterministic — to issue on the second
        // before it: the last second SKW5416 is still rolling at taxi speed, which is the moment reproduced.
        // (The cap lands inside the tick that reports it, at 5 kt, so that tick is already too late.)
        int secondsToCap = SecondsUntilDetectorCap(recording, groundData);
        Assert.True(
            secondsToCap > 1,
            $"the detector capped SKW5416 {secondsToCap}s after t={WindowStartSeconds} (0 = never in {WindowSeconds}s), leaving no rolling second to issue on"
        );

        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = ReplayToWindow(recording, groundData);
        for (int second = 1; second < secondsToCap; second++)
        {
            engine.TickOneSecond();
        }

        // Ticked, not replayed: the session's own HS T lands at t=1272 and this test issues its own.
        double issueDistFt = DistanceFt(aircraft.Position, junction.Position);
        double issueSpeedKts = aircraft.GroundSpeed;
        CommandResult issued = engine.SendCommand("SKW5416", "HS T");
        output.WriteLine(
            $"HS T at t={WindowStartSeconds + secondsToCap - 1}s, {issueDistFt:F0} ft / {issueSpeedKts:F1} kt: {issued.Success} — {issued.Message}"
        );

        Assert.True(issued.Success, $"HS T refused: {issued.Message}");
        Assert.True(issueSpeedKts > MinIssueSpeedKts, $"SKW5416 was only doing {issueSpeedKts:F1} kt the second before the cap engaged");

        Assert.Contains("Unable", issued.Message, StringComparison.OrdinalIgnoreCase);

        var speeds = new List<double>();
        var capped = new List<string>();
        for (int second = 1; second <= 40; second++)
        {
            engine.TickOneSecond();
            // A detector-capped second is recorded as "not the taxi's speed" so the creep check skips it.
            speeds.Add(aircraft.Ground.SpeedLimit is null ? aircraft.GroundSpeed : double.NaN);
            capped.Add($"{aircraft.GroundSpeed:F1}{(aircraft.Ground.SpeedLimit is { } limit ? $"[cap {limit:F0}]" : "")}");
        }

        output.WriteLine($"phase after 40s: {PhaseName(aircraft)}; speeds: {string.Join(" ", capped)}");
        Assert.True(aircraft.Phases?.CurrentPhase is HoldingShortPhase, $"SKW5416 never took the hold; phase {PhaseName(aircraft)}");
        AssertNoCreep(speeds, "SKW5416");
    }

    /// <summary>
    /// The recorded session replayed to the start of the window, with the aircraft and the B/T junction the
    /// case turns on. Two passes need the same starting point, and the replay gives a deterministic one.
    /// </summary>
    /// <param name="recording">The session recording.</param>
    /// <param name="groundData">Ground data the engine resolves SFO from.</param>
    /// <returns>The engine, SKW5416, and the B/T junction node.</returns>
    private static (SimulationEngine Engine, AircraftState Aircraft, GroundNode Junction) ReplayToWindow(
        SessionRecording recording,
        TestAirportGroundData groundData
    )
    {
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, WindowStartSeconds);

        AircraftState? aircraft = engine.FindAircraft("SKW5416");
        Assert.NotNull(aircraft);
        AirportGroundLayout? layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        GroundNode? junction = layout!.FindIntersectionNode("B", "T");
        Assert.True(junction is not null, "SFO layout has no B/T junction");
        return (engine, aircraft!, junction!);
    }

    /// <summary>
    /// Seconds after <see cref="WindowStartSeconds"/> that the ground conflict detector first caps SKW5416,
    /// or 0 if it never does inside the window.
    /// </summary>
    /// <param name="recording">The session recording.</param>
    /// <param name="groundData">Ground data the engine resolves SFO from.</param>
    /// <returns>The offset in seconds, or 0.</returns>
    private int SecondsUntilDetectorCap(SessionRecording recording, TestAirportGroundData groundData)
    {
        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = ReplayToWindow(recording, groundData);
        for (int second = 1; second <= WindowSeconds; second++)
        {
            engine.TickOneSecond();
            output.WriteLine(
                $"t={WindowStartSeconds + second}s dist={DistanceFt(aircraft.Position, junction.Position):F0} ft gs={aircraft.GroundSpeed:F1} kt "
                    + $"cap={(aircraft.Ground.SpeedLimit is { } cap ? cap.ToString("F0") : "none")} phase={PhaseName(aircraft)}"
            );
            if (aircraft.Ground.SpeedLimit is not null)
            {
                return second;
            }
        }

        return 0;
    }

    private static string PhaseName(AircraftState aircraft) => aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)";

    /// <summary>
    /// Spawns a jet far enough east on B to be at taxi speed by the bar, clears it west past T, and issues
    /// <c>HS T</c> the first second it is within <paramref name="issueWithinFt"/> of the B/T junction at taxi
    /// speed. Returns null when the layout cannot host the case (missing junction or run-up room).
    /// </summary>
    private HsRun? RunHoldShortOfT(SfoGround ground, double issueWithinFt)
    {
        GroundNode? junction = ground.Layout.FindIntersectionNode("B", "T");
        Assert.True(junction is not null, "SFO layout has no B/T junction");

        GroundNode spawn = WalkEastAlongB(ground.Layout, junction, wantFt: 1800.0);
        double runUpFt = DistanceFt(spawn.Position, junction.Position);
        output.WriteLine($"spawn node {spawn.Id} is {runUpFt:F0} ft east of the B/T junction {junction.Id}");
        Assert.True(runUpFt > issueWithinFt + 200.0, $"only {runUpFt:F0} ft of B east of the B/T junction to roll in");

        var heading = new TrueHeading(GeoMath.BearingTo(spawn.Position, junction.Position));
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, Callsign, Type, (spawn, heading), new HoldingInPositionPhase());

        CommandResult taxi = ground.Engine.SendCommand(Callsign, TaxiClearance);
        output.WriteLine($"{TaxiClearance}: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, $"{TaxiClearance} refused: {taxi.Message}");

        RunTrace? trace = null;
        CommandResult? issued = null;
        for (int second = 1; (second <= 240) && (issued is null); second++)
        {
            ground.Engine.TickOneSecond();
            double distFt = DistanceFt(aircraft.Position, junction.Position);
            if ((distFt > issueWithinFt) || (aircraft.GroundSpeed < IssueSpeedKts))
            {
                continue;
            }

            trace = new RunTrace(distFt, aircraft.GroundSpeed, aircraft.Position, []);
            issued = ground.Engine.SendCommand(Callsign, "HS T");
            output.WriteLine($"HS T at {distFt:F0} ft / {aircraft.GroundSpeed:F1} kt: {issued.Success} — {issued.Message}");
        }

        Assert.True((issued is not null) && (trace is not null), $"{Callsign} never reached the B/T bar at taxi speed");

        for (int second = 1; second <= 60; second++)
        {
            ground.Engine.TickOneSecond();
            trace.SpeedsAfterIssue.Add(aircraft.GroundSpeed);
            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase)
            {
                break;
            }
        }

        output.WriteLine($"phase {PhaseName(aircraft)}; speeds: {string.Join(" ", trace.SpeedsAfterIssue.Select(s => s.ToString("F1")))}");
        return new HsRun(aircraft, spawn, junction, issued, trace);
    }

    /// <summary>
    /// The node on taxiway B closest to <paramref name="wantFt"/> east of <paramref name="junction"/>,
    /// picked by along-track projection on B's east bearing rather than by walking edges — the fillet arcs
    /// at each junction carry joined names, so an edge walk stops at the first corner.
    /// </summary>
    private static GroundNode WalkEastAlongB(AirportGroundLayout layout, GroundNode junction, double wantFt)
    {
        const double MaxCrossTrackFt = 200.0;

        double eastBearing = EastBearingAlongB(junction);
        GroundNode best = junction;
        double bestScore = double.MaxValue;
        double bestAlongFt = 0;

        foreach (GroundNode node in layout.GetNodesOnTaxiway("B"))
        {
            double distFt = DistanceFt(junction.Position, node.Position);
            double deltaDeg = GeoMath.AbsBearingDifference(eastBearing, GeoMath.BearingTo(junction.Position, node.Position));
            double alongFt = distFt * Math.Cos(deltaDeg * Math.PI / 180.0);
            double crossFt = Math.Abs(distFt * Math.Sin(deltaDeg * Math.PI / 180.0));
            if ((alongFt <= 0) || (crossFt > MaxCrossTrackFt))
            {
                continue;
            }

            double score = Math.Abs(alongFt - wantFt);
            if (score < bestScore)
            {
                best = node;
                bestScore = score;
                bestAlongFt = alongFt;
            }
        }

        Assert.True(bestAlongFt > 0, "SFO layout has no taxiway-B node east of the B/T junction");
        return best;
    }

    /// <summary>Bearing from <paramref name="junction"/> along B toward the rising longitude (east).</summary>
    private static double EastBearingAlongB(GroundNode junction)
    {
        foreach (IGroundEdge edge in junction.Edges)
        {
            if (!edge.MatchesTaxiway("B"))
            {
                continue;
            }

            GroundNode other = edge.OtherNode(junction);
            if (other.Position.Lon > junction.Position.Lon)
            {
                return GeoMath.BearingTo(junction.Position, other.Position);
            }
        }

        throw new InvalidOperationException("SFO layout has no eastbound taxiway-B edge at the B/T junction");
    }

    private void AssertHoldingShortOfT(HsRun run)
    {
        Phase? phase = run.Aircraft.Phases?.CurrentPhase;
        Assert.True(phase is HoldingShortPhase, $"{Callsign} never took the hold; phase {PhaseName(run.Aircraft)}");
        var hold = (HoldingShortPhase)phase!;
        Assert.Equal("T", hold.HoldShort.TargetName, ignoreCase: true);
    }

    private void AssertNoCreep(HsRun run) => AssertNoCreep(run.Trace.SpeedsAfterIssue, Callsign);

    /// <summary>
    /// Fails when the aircraft sat in the creep band for longer than a brake-through takes — the 5 kt crawl
    /// the field case recorded, which is what a bar the navigator never aimed at looks like from outside.
    /// A <see cref="double.NaN"/> sample is a second the taxi did not own (the conflict detector capped it)
    /// and breaks the run rather than extending it.
    /// </summary>
    private static void AssertNoCreep(IReadOnlyList<double> speeds, string callsign)
    {
        int run = 0;
        int longest = 0;
        foreach (double speed in speeds)
        {
            run = (speed >= CreepLowKts) && (speed <= CreepHighKts) ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        Assert.True(longest <= MaxCreepSeconds, $"{callsign} crept between {CreepLowKts:F0} and {CreepHighKts:F0} kt for {longest}s");
    }
}
