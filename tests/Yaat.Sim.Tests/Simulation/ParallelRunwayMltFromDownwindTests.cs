using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Switching an aircraft that is already on a downwind to the parallel runway's opposite-side
/// pattern is a crossover at midfield: it flies its current downwind to midfield, crosses the field
/// perpendicular at pattern altitude (it is already in the pattern, so the AIM 4-3-3.a.2 entry rule's
/// +500 ft does not apply), and rejoins the new runway's downwind. Switching to the parallel's
/// same-side pattern is just a rebuild that re-intercepts the laterally offset downwind track.
///
/// Recording: S2-OAK-4 "VFR Transitions / Radar Concepts" (ZOA, OAK). N342T (DA42) flies the 28R
/// right downwind (north of the field, tracking east) from t≈975 to t≈1050. Tests restore a snapshot
/// on that leg, drive the engine with <see cref="SimulationEngine.TickOneSecond"/> and issue the
/// pattern change directly — the recording's own commands are never replayed.
/// </summary>
public class ParallelRunwayMltFromDownwindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N342T";
    private const string AirportId = "OAK";

    // Early on the second 28R downwind, well before midfield.
    private const int BeforeMidfieldSnapshotTime = 980;

    // Past midfield on the same leg, before the base turn.
    private const int PastMidfieldSnapshotTime = 1030;

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
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Mlt28L_OnDownwind28R_BeforeMidfield_CrossesOverAtMidfield()
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

            var aircraft = RestoreOnDownwind(engine, archive, BeforeMidfieldSnapshotTime, beforeMidfield: true);
            if (aircraft is null)
            {
                return;
            }

            var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;
            var oldWaypoints = ((DownwindPhase)aircraft.Phases!.CurrentPhase!).Waypoints!;
            double midfieldAlongTrack = PatternGeometry.MidfieldAlongTrackNm(oldWaypoints);

            var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, "28L", null, aircraft.Ground.Layout);
            Assert.True(result.Success, $"MLT 28L was refused: {result.Message}");
            // The RPO reads only this line; an unannounced field crossing is the AIM 4-3-5 surprise.
            Assert.Contains("crossing midfield", result.Message ?? "", StringComparison.Ordinal);

            var chain = aircraft.Phases?.Phases ?? [];
            output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");

            var exitLeg = Assert.IsType<DownwindPhase>(chain.ElementAtOrDefault(0));
            Assert.True(exitLeg.ExitAtMidfield, "the leading downwind must exit at midfield for the crossover");
            var crossing = Assert.IsType<MidfieldCrossingPhase>(chain.ElementAtOrDefault(1));
            Assert.True(crossing.CrossAtPatternAltitude, "an in-pattern crossover crosses at TPA, not the entry height");
            Assert.Equal(TurnDirection.Right, crossing.InitialTurn);
            var rejoin = Assert.IsType<DownwindPhase>(chain.ElementAtOrDefault(2));
            Assert.True(rejoin.RejoinTrack, "the new downwind must re-intercept its track after the crossing");
            Assert.IsType<BasePhase>(chain.ElementAtOrDefault(3));
            Assert.IsType<FinalApproachPhase>(chain.ElementAtOrDefault(4));
            Assert.IsType<TouchAndGoPhase>(chain.ElementAtOrDefault(5));
            Assert.Equal(6, chain.Count);

            FlyTheCrossover(engine, rwy28L, oldWaypoints, midfieldAlongTrack);
        }
    }

    [Fact]
    public void Mlt28L_OnDownwind28R_PastMidfield_CrossesNow()
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

            var aircraft = RestoreOnDownwind(engine, archive, PastMidfieldSnapshotTime, beforeMidfield: false);
            if (aircraft is null)
            {
                return;
            }

            var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, "28L", null, aircraft.Ground.Layout);
            Assert.True(result.Success, $"MLT 28L was refused: {result.Message}");
            Assert.Contains("crossing midfield", result.Message ?? "", StringComparison.Ordinal);

            var chain = aircraft.Phases?.Phases ?? [];
            output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");

            // Past midfield there is nothing left of the old downwind to fly — the crossover starts now.
            var crossing = Assert.IsType<MidfieldCrossingPhase>(chain.ElementAtOrDefault(0));
            Assert.True(crossing.CrossAtPatternAltitude, "an in-pattern crossover crosses at TPA, not the entry height");
            Assert.Equal(TurnDirection.Right, crossing.InitialTurn);
            Assert.DoesNotContain(chain, p => p is DownwindPhase { ExitAtMidfield: true });
            var rejoin = chain.OfType<DownwindPhase>().FirstOrDefault();
            Assert.NotNull(rejoin);
            Assert.True(rejoin.RejoinTrack, "the new downwind must re-intercept its track after the crossing");
        }
    }

    [Fact]
    public void Mrt28L_OnDownwind28R_SameSide_RejoinsTheParallelDownwind()
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

            var aircraft = RestoreOnDownwind(engine, archive, BeforeMidfieldSnapshotTime, beforeMidfield: true);
            if (aircraft is null)
            {
                return;
            }

            var rwy28L = NavigationDatabase.Instance.GetRunway(AirportId, "28L")!;

            var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, "28L", null, aircraft.Ground.Layout);
            Assert.True(result.Success, $"MRT 28L was refused: {result.Message}");
            // Same-side rebuild: no field crossing, so the readback must not announce one.
            Assert.DoesNotContain("crossing midfield", result.Message ?? "", StringComparison.Ordinal);

            var chain = aircraft.Phases?.Phases ?? [];
            output.WriteLine($"chain=[{string.Join(",", chain.Select(p => $"{p.GetType().Name}:{p.Status}"))}]");
            Assert.DoesNotContain(chain, p => p is MidfieldCrossingPhase);

            var downwind = Assert.IsType<DownwindPhase>(aircraft.Phases?.CurrentPhase);
            Assert.True(downwind.RejoinTrack, "the parallel's downwind is laterally offset, so the leg must re-intercept its track");
            var waypoints = downwind.Waypoints;
            Assert.NotNull(waypoints);
            Assert.Equal(PatternDirection.Right, waypoints.Direction);
            Assert.True(
                GeoMath.DistanceNm(waypoints.ThresholdLat, waypoints.ThresholdLon, rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude) < 0.05,
                "the rebuilt downwind must be built on 28L's threshold"
            );

            // Fly it: the aircraft slides onto 28L's right-downwind track (the line through the abeam
            // point on the downwind heading) and holds it.
            double crossTrack = double.NaN;
            for (int i = 0; i < 60; i++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft(Callsign);
                Assert.NotNull(ac);
                if (ac.Phases?.CurrentPhase is not DownwindPhase leg || leg.Waypoints is null)
                {
                    break;
                }
                crossTrack = GeoMath.SignedCrossTrackDistanceNm(
                    ac.Position,
                    new LatLon(leg.Waypoints.DownwindAbeamLat, leg.Waypoints.DownwindAbeamLon),
                    leg.Waypoints.DownwindHeading
                );
            }

            output.WriteLine($"final downwind cross-track to 28L's right-downwind track: {crossTrack:F3} nm");
            Assert.True(Math.Abs(crossTrack) < 0.15, $"aircraft is {crossTrack:F3} nm off 28L's right-downwind track — it never rejoined it");
        }
    }

    /// <summary>
    /// Restore a snapshot on the 28R right downwind and confirm the restore point sits on the
    /// expected side of midfield. Returns null when the fixture is unavailable (skip-return).
    /// </summary>
    private AircraftState? RestoreOnDownwind(SimulationEngine engine, RecordingArchive archive, int snapshotTime, bool beforeMidfield)
    {
        engine.Replay(archive.ToBaseSessionRecording(), 0);

        var snapshot = archive.ReadSnapshotAt(snapshotTime);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={snapshotTime} — skipping");
            return null;
        }
        engine.RestoreFromSnapshot(snapshot.State);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var downwind = Assert.IsType<DownwindPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("28R", aircraft.Phases!.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);

        var waypoints = downwind.Waypoints;
        Assert.NotNull(waypoints);
        double alongTrack = AlongTrackOnLeg(waypoints, aircraft.Position);
        double midfield = PatternGeometry.MidfieldAlongTrackNm(waypoints);
        output.WriteLine($"t={snapshot.ElapsedSeconds}: along-track {alongTrack:F3} nm, midfield {midfield:F3} nm, alt {aircraft.Altitude:F0} ft");

        if (beforeMidfield)
        {
            Assert.True(
                alongTrack < midfield - DownwindPhase.MidfieldLeadNm,
                $"restore point t={snapshotTime} is not before midfield (along-track {alongTrack:F3} vs midfield {midfield:F3})"
            );
        }
        else
        {
            Assert.True(
                alongTrack >= midfield - DownwindPhase.MidfieldLeadNm,
                $"restore point t={snapshotTime} is not past midfield (along-track {alongTrack:F3} vs midfield {midfield:F3})"
            );
        }

        return aircraft;
    }

    /// <summary>
    /// Fly the crossover: the old downwind is held until midfield, the crossing starts there at
    /// pattern altitude with a right (toward-the-field) turn, and the aircraft ends up established on
    /// 28L's left downwind south of the runway, tracking its reciprocal.
    /// </summary>
    private void FlyTheCrossover(SimulationEngine engine, RunwayInfo patternRunway, PatternWaypoints oldWaypoints, double midfieldAlongTrack)
    {
        double? crossingStartAlongTrack = null;
        double? crossingTargetAltitude = null;
        double crossingPatternAltitude = 0;
        double maxCrossingBank = 0;
        int crossingTicks = 0;
        int exitLegTicks = 0;
        bool establishedOnDownwind = false;
        AircraftState? onDownwind = null;

        for (int i = 0; i < MaxTicksAfterCommand; i++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            switch (ac.Phases?.CurrentPhase)
            {
                case DownwindPhase { ExitAtMidfield: true }:
                    // The restore point catches the aircraft rolling out of its crosswind turn, so
                    // give it the roll-out before holding it to the downwind heading.
                    exitLegTicks++;
                    Assert.True(
                        (exitLegTicks < 15) || (ac.TrueHeading.AbsAngleTo(oldWaypoints.DownwindHeading) < 20),
                        $"the exiting downwind left its heading ({ac.TrueHeading.Degrees:F0}° vs {oldWaypoints.DownwindHeading.Degrees:F0}°) before midfield"
                    );
                    break;

                case MidfieldCrossingPhase crossing:
                    crossingStartAlongTrack ??= AlongTrackOnLeg(oldWaypoints, ac.Position);
                    crossingTargetAltitude ??= ac.Targets.TargetAltitude;
                    crossingPatternAltitude = crossing.Waypoints?.PatternAltitude ?? 0;
                    crossingTicks++;
                    if (crossingTicks <= 8)
                    {
                        maxCrossingBank = Math.Max(maxCrossingBank, ac.BankAngle);
                    }
                    break;

                case DownwindPhase:
                    onDownwind = ac;
                    // Established: south of 28L, tracking its reciprocal. The rejoining leg has to
                    // re-intercept its track after the crossing, so this takes a few tens of seconds —
                    // but it must happen before the base turn ends the leg.
                    establishedOnDownwind =
                        (ac.TrueHeading.AbsAngleTo(patternRunway.TrueHeading.ToReciprocal()) < 20) && (CrossTrackToRunway(patternRunway, ac) < -0.2);
                    break;
            }

            if (establishedOnDownwind)
            {
                break;
            }
        }

        Assert.NotNull(crossingStartAlongTrack);
        output.WriteLine(
            $"crossing began at along-track {crossingStartAlongTrack:F3} nm (midfield {midfieldAlongTrack:F3}), "
                + $"target alt {crossingTargetAltitude:F0} ft (TPA {crossingPatternAltitude:F0}), max bank {maxCrossingBank:F0}°"
        );

        Assert.True(
            crossingStartAlongTrack >= midfieldAlongTrack - DownwindPhase.MidfieldLeadNm,
            $"the crossing started at along-track {crossingStartAlongTrack:F3} nm, before midfield ({midfieldAlongTrack:F3} nm)"
        );
        Assert.NotNull(crossingTargetAltitude);
        Assert.True(
            Math.Abs(crossingTargetAltitude.Value - crossingPatternAltitude) < 1.0,
            $"the crossing targeted {crossingTargetAltitude:F0} ft instead of pattern altitude {crossingPatternAltitude:F0} ft"
        );
        Assert.True(maxCrossingBank > 5.0, $"the crossover's initial turn was not to the right (max bank {maxCrossingBank:F0}°)");

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

    private static double AlongTrackOnLeg(PatternWaypoints waypoints, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon), waypoints.DownwindHeading);

    private static double CrossTrackToRunway(RunwayInfo runway, AircraftState aircraft) =>
        GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
}
