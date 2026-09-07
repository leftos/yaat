using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>MLT 28L</c> issued to an aircraft climbing out on OAK's 28R upwind switches it to 28L's left
/// pattern leg to leg: the upwind continues on runway heading until beyond both runways' departure
/// ends (AIM 4-3-2), then the left crosswind carries it to 28L's downwind south of the field. The
/// two centerlines are 0.165 nm apart, so nothing is crossed and the phase chain holds no
/// <see cref="MidfieldCrossingPhase"/>; the target heading never reverts to the old 28R right-traffic
/// crosswind (~22°), which the leg rebuild is what prevents.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA). At t=1275 N342T (DA42) is on a fresh
/// upwind for 28R right traffic, about to pass 28R's departure end; the user's recorded
/// <c>MLT 28L</c> at t=1278 is the trigger and is replayed. Replay stops before the recorded
/// <c>FH 270</c> at t=1283 (which would clear the chain); the rest of the circuit is flown on
/// physics alone.
/// </summary>
public class Issue7MltCrossRunwayWrongSideTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue7-mlt-cross-runway-wrong-side-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const string AirportId = "OAK";

    private const int SnapshotTime = 1275;
    private const int MaxReplaySeconds = 4;
    private const int MaxTicksAfterCommand = 200;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("CrosswindPhase", LogLevel.Debug)
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Mlt28L_OnUpwindFrom28R_TransitionsToTheParallelPatternWithoutCrossingMidfield()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            // Hybrid replay: pin pre-MLT state via the recorded snapshot so RNG drift across 1275 s
            // of pattern work doesn't move the chain to a different leg by the time MLT fires.
            engine.Replay(archive.ToBaseSessionRecording(), 0);

            var snapshot = archive.ReadSnapshotAt(SnapshotTime);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={SnapshotTime} — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: at t=1275 N342T is on Upwind for 28R right traffic.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<UpwindPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28R", pre.Phases!.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Right, pre.Phases.TrafficDirection);

            var rwy28R = NavigationDatabase.Instance.GetRunway(AirportId, "28R")!;
            var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;

            var post = ReplayThroughTheRecordedMlt(engine);
            Assert.NotNull(post);

            output.WriteLine(
                $"post-MLT: phase={post.Phases?.CurrentPhase?.GetType().Name} hdg={post.TrueHeading.Degrees:F0} "
                    + $"tgtHdg={post.Targets.TargetTrueHeading?.Degrees:F0} bank={post.BankAngle:F0} "
                    + $"chain=[{string.Join(",", post.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? [])}]"
            );

            // The MLT stamped the new pattern intent.
            Assert.Equal("28L", post.Phases?.AssignedRunway?.Designator);
            Assert.Equal(PatternDirection.Left, post.Phases?.TrafficDirection);

            // (a) Leg-to-leg transition, not a field crossing.
            Assert.DoesNotContain(post.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);

            // The transition upwind's crosswind turn point clears BOTH departure ends: at OAK, 28L's
            // end projects 1.02 nm along 28L from its threshold and 28R's 0.90 nm, so the turn is
            // anchored on 28L's — the aircraft may not turn at 28R's end.
            var upwind = post.Phases?.Phases.OfType<UpwindPhase>().LastOrDefault();
            Assert.NotNull(upwind?.Waypoints);
            double turnAlongTrack = AlongTrack(rwy28L, upwind.Waypoints.CrosswindTurnLat, upwind.Waypoints.CrosswindTurnLon);
            double der28L = AlongTrack(rwy28L, rwy28L.EndLatitude, rwy28L.EndLongitude);
            double der28R = AlongTrack(rwy28L, rwy28R.EndLatitude, rwy28R.EndLongitude);
            output.WriteLine($"crosswind turn along-track={turnAlongTrack:F3} nm (28L DER {der28L:F3}, 28R DER {der28R:F3})");
            Assert.True(turnAlongTrack >= der28L - 0.001, $"turn point {turnAlongTrack:F3} nm is short of 28L's departure end ({der28L:F3} nm)");
            Assert.True(turnAlongTrack >= der28R - 0.001, $"turn point {turnAlongTrack:F3} nm is short of 28R's departure end ({der28R:F3} nm)");

            // (b) The target heading must not revert to the old 28R/right crosswind (~22°).
            Assert.NotNull(post.Targets.TargetTrueHeading);
            double tgt = post.Targets.TargetTrueHeading.Value.Degrees;
            Assert.False(tgt is >= 340 or <= 60, $"TargetTrueHeading={tgt:F1}° is the OLD 28R/right crosswind heading (~22°)");

            // (c) Not banked right: the aircraft either holds the upwind or rolls left onto 28L's
            // crosswind, never right toward the abandoned 28R pattern.
            Assert.True(post.BankAngle <= 5.0, $"aircraft is banked right ({post.BankAngle:F1}°) after MLT 28L");

            FlyToTheParallelDownwind(engine, rwy28L, rwy28R);
        }
    }

    /// <summary>
    /// Step the recording forward one second at a time until the recorded <c>MLT 28L</c> (t=1278) has
    /// been applied, stopping before the recorded <c>FH 270</c> at t=1283.
    /// </summary>
    private AircraftState? ReplayThroughTheRecordedMlt(SimulationEngine engine)
    {
        for (int dt = 1; dt <= MaxReplaySeconds; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (string.Equals(ac?.Phases?.AssignedRunway?.Designator, "28L", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"recorded MLT 28L applied {dt}s after t={SnapshotTime}");
                return ac;
            }
        }

        Assert.Fail($"the recorded MLT 28L never applied within {MaxReplaySeconds}s of t={SnapshotTime}");
        return null;
    }

    /// <summary>
    /// Physics-only tail: the crosswind fires beyond the farther departure end and the aircraft
    /// settles on 28L's left downwind, south of the runway, tracking its reciprocal.
    /// </summary>
    private void FlyToTheParallelDownwind(SimulationEngine engine, RunwayInfo patternRunway, RunwayInfo otherRunway)
    {
        double fartherDer = Math.Max(
            AlongTrack(patternRunway, patternRunway.EndLatitude, patternRunway.EndLongitude),
            AlongTrack(patternRunway, otherRunway.EndLatitude, otherRunway.EndLongitude)
        );

        double? crosswindStartAlongTrack = null;
        bool establishedOnDownwind = false;
        AircraftState? onDownwind = null;

        for (int i = 0; i < MaxTicksAfterCommand; i++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.DoesNotContain(ac.Phases?.Phases ?? [], p => p is MidfieldCrossingPhase);

            switch (ac.Phases?.CurrentPhase)
            {
                case UpwindPhase:
                    Assert.True(
                        ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading) < 15,
                        $"upwind heading {ac.TrueHeading.Degrees:F0}° drifted off runway heading before the departure ends"
                    );
                    break;
                case CrosswindPhase:
                    crosswindStartAlongTrack ??= AlongTrack(patternRunway, ac.Position.Lat, ac.Position.Lon);
                    break;
                case DownwindPhase:
                    onDownwind = ac;
                    // Established: south of 28L, tracking its reciprocal. The leg re-intercepts its
                    // track after the transition crosswind, so this takes a few tens of seconds — but
                    // it must happen before the base turn ends the leg.
                    establishedOnDownwind =
                        (ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading.ToReciprocal()) < 20) && (CrossTrackToRunway(patternRunway, ac) < -0.2);
                    break;
            }

            if (establishedOnDownwind)
            {
                break;
            }
        }

        Assert.NotNull(crosswindStartAlongTrack);
        Assert.True(
            crosswindStartAlongTrack >= fartherDer - 0.1,
            $"crosswind turn fired at along-track {crosswindStartAlongTrack:F3} nm, short of the farther departure end at {fartherDer:F3} nm"
        );

        Assert.NotNull(onDownwind);
        double crossTrack = CrossTrackToRunway(patternRunway, onDownwind);
        output.WriteLine(
            $"downwind: cross-track {crossTrack:F3} nm (negative = south of 28L), hdg {onDownwind.TrueHeading.Degrees:F0}°, "
                + $"established={establishedOnDownwind}"
        );
        Assert.True(
            establishedOnDownwind,
            $"never established on 28L's left downwind (south of the runway, tracking its reciprocal) — last sample was "
                + $"{crossTrack:F3} nm from 28L on heading {onDownwind.TrueHeading.Degrees:F0}°"
        );
    }

    private static double AlongTrack(RunwayInfo runway, double lat, double lon) =>
        GeoMath.AlongTrackDistanceNm(new LatLon(lat, lon), new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);

    private static double CrossTrackToRunway(RunwayInfo runway, AircraftState aircraft) =>
        GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
}
