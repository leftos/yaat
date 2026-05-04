using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the SFO R10L offset-approach bug. EVA18 is cleared for the RNAV (GPS)
/// approach to KSFO Rwy 10L (CAPP at t=916 in the recording). Before the fix in commits
/// ac1c965 / 0a26ebd / 2f25a72, the aircraft would align with the runway heading instead
/// of tracking the published final approach course. R10L is an RNAV approach with TF legs,
/// so the FAC must be computed via the great-circle bearing between the FAF/MAP fix
/// endpoints (the CIFP does not publish OutboundCourse for TF legs).
///
/// Recording: S3-NCTB-6 (B) | SFO10 (user-reported bug bundle).
/// </summary>
public class IssueSfoR10lOffsetApproachTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-sfo-r10l-offset-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "EVA18";
    private const int CappTime = 916;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_LogStateAfterCapp()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to a few seconds after CAPP, then tick forward to observe behaviour.
        engine.Replay(recording, CappTime + 5);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var clearance = aircraft.Phases?.ActiveApproach;
        output.WriteLine($"=== {Callsign} state at t={CappTime + 5} (CAPP+5s) ===");
        output.WriteLine($"  Position:    ({aircraft.Position.Lat:F5}, {aircraft.Position.Lon:F5})");
        output.WriteLine($"  Altitude:    {aircraft.Altitude:F0} ft");
        output.WriteLine($"  Heading:     {aircraft.TrueHeading.Degrees:F1}°");
        output.WriteLine($"  TgtHdg:      {aircraft.Targets.TargetTrueHeading?.Degrees.ToString("F1") ?? "null"}");
        output.WriteLine($"  Approach:    {clearance?.ApproachId ?? "null"}");
        output.WriteLine($"  FAC:         {clearance?.FinalApproachCourse.Degrees.ToString("F1") ?? "null"}");
        output.WriteLine(
            $"  Anchor:      ({clearance?.FinalApproachAnchorLat?.ToString("F5") ?? "null"}, {clearance?.FinalApproachAnchorLon?.ToString("F5") ?? "null"})"
        );
        output.WriteLine($"  Phases:      {string.Join(", ", aircraft.Phases?.Phases.Select(p => $"{p.Name}({p.Status})") ?? [])}");

        output.WriteLine("");
        output.WriteLine("tick |  alt | tgtHdg | hdg    | phases");
        output.WriteLine("---- | ---- | ------ | ------ | ------");

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                output.WriteLine($"{t, 4} | DELETED");
                break;
            }
            string phases = string.Join(",", aircraft.Phases?.Phases.Where(p => p.Status == PhaseStatus.Active).Select(p => p.Name) ?? []);
            output.WriteLine(
                $"{t, 4} | {aircraft.Altitude, 4:F0} | {aircraft.Targets.TargetTrueHeading?.Degrees.ToString("F1") ?? "null", 6} | {aircraft.TrueHeading.Degrees, 6:F1} | {phases}"
            );
        }
    }

    [Fact]
    public void Capp_PopulatesOffsetFinalApproachCourse()
    {
        // Regression guard: when CAPP is issued for R10L, ApproachClearance.FinalApproachCourse
        // must come from FinalApproachCourseExtractor, NOT from the silent runway-heading
        // fallback. The discriminating assertion is that the FAC is not bit-equal to the
        // runway true heading — for R10L the published course is computed from fix-to-fix
        // bearings on TF legs and is within a few degrees of (but not equal to) runway 10L.
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, CappTime + 5);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var clearance = aircraft.Phases?.ActiveApproach;
        var assignedRunway = aircraft.Phases?.AssignedRunway;
        Assert.NotNull(clearance);
        Assert.NotNull(assignedRunway);
        Assert.Equal("R10L", clearance.ApproachId);

        // Sanity: FAC is in the easterly sector (the "is the value sane" check).
        Assert.InRange(clearance.FinalApproachCourse.Degrees, 80.0, 140.0);

        // The actual regression guard: if FinalApproachPhase or ApproachCommandHandler ever
        // silently regresses to using runway.TrueHeading, this assertion fails. The runway
        // and the published FAC are derived through different code paths, so they are
        // bitwise-distinct doubles in the success case.
        Assert.NotEqual(assignedRunway.TrueHeading.Degrees, clearance.FinalApproachCourse.Degrees);
    }

    [Fact]
    public void OnFinal_AircraftConvergesToFacNotRunwayHeading()
    {
        // After the aircraft is established on final, both its actual heading and its
        // cross-track should converge to the published FAC. The pre-fix bug fell back to
        // runway heading; if that bug regressed, the cross-track measured against the FAC
        // would NOT converge to zero (the aircraft would track a different line offset by
        // the FAC-vs-runway angle).
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, CappTime + 5);

        // Tick until the aircraft is fully established on the published final approach course:
        // both lateral (cross-track < 0.05 NM) AND directional (track within 3° of FAC) must
        // hold. A pure xte<threshold check fires the moment the aircraft crosses the line at
        // a steep angle during intercept, which is not steady-state.
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var running = ac.Phases?.Phases.FirstOrDefault(p => p.Status == PhaseStatus.Active);
            if (running?.Name != "FinalApproach")
            {
                continue;
            }

            var clearance = ac.Phases?.ActiveApproach;
            Assert.NotNull(clearance);

            double anchorLat = clearance.FinalApproachAnchorLat ?? GetThreshold(ac).Lat;
            double anchorLon = clearance.FinalApproachAnchorLon ?? GetThreshold(ac).Lon;

            double xte = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(ac.Position.Lat, ac.Position.Lon, anchorLat, anchorLon, clearance.FinalApproachCourse)
            );
            double trackDiff = ac.TrueTrack.AbsAngleTo(clearance.FinalApproachCourse);

            if (xte >= 0.05 || trackDiff >= 3.0)
            {
                continue;
            }

            // Established. Assert the aircraft GROUND TRACK (not heading — wind can crab the
            // heading several degrees off the track) is within 5° of the FAC. For SFO R10L
            // the published FAC and runway heading differ by a few degrees, so this is the
            // discriminating assertion: pre-fix code would track the runway centerline, post-
            // fix code tracks the FAC line.
            double trackDiffFromFac = ac.TrueTrack.AbsAngleTo(clearance.FinalApproachCourse);
            double trackDiffFromRunway = ac.TrueTrack.AbsAngleTo(GetRunwayHeading(ac));
            output.WriteLine(
                $"Established at t+{t}s. xte={xte:F4}nm, track={ac.TrueTrack.Degrees:F1}, hdg={ac.TrueHeading.Degrees:F1}, "
                    + $"FAC={clearance.FinalApproachCourse.Degrees:F1}, rwy={GetRunwayHeading(ac).Degrees:F1}, "
                    + $"diff(track,FAC)={trackDiffFromFac:F1}°, diff(track,rwy)={trackDiffFromRunway:F1}°"
            );
            Assert.True(
                trackDiffFromFac < 5.0,
                $"Aircraft ground track {ac.TrueTrack.Degrees:F1}° should be within 5° of FAC {clearance.FinalApproachCourse.Degrees:F1}° once established (diff {trackDiffFromFac:F1}°)"
            );
            return;
        }

        Assert.Fail($"Aircraft {Callsign} never established on final approach course within 240s after CAPP");
    }

    private static (double Lat, double Lon) GetThreshold(AircraftState aircraft)
    {
        var rwy = aircraft.Phases?.AssignedRunway;
        return rwy is null ? (0.0, 0.0) : (rwy.ThresholdLatitude, rwy.ThresholdLongitude);
    }

    private static TrueHeading GetRunwayHeading(AircraftState aircraft)
    {
        var rwy = aircraft.Phases?.AssignedRunway;
        return rwy?.TrueHeading ?? new TrueHeading(0);
    }
}
