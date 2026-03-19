using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #102: InterceptCoursePhase must actively turn
/// aircraft onto localizer.
///
/// Recording: S3-NCTC-4 — SWA1850 vectored heading 150 for ILS 12 at KOAK.
/// ILS 12 FAC ~130°, giving a ~20° intercept angle.
///
/// Timeline from recording:
///   t=1156: FH 210, CM 030
///   t=1232: PTAC 150 030 (heading 150°, 3000ft, cleared ILS 12)
/// </summary>
public class Ils12LocalizerCaptureTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/ils12-localizer-capture-recording.json";

    private static SessionRecording? LoadRecording()
    {
        if (!File.Exists(RecordingPath))
        {
            return null;
        }

        var json = File.ReadAllText(RecordingPath);
        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private SimulationEngine? BuildEngine()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Information));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After PTAC 150 030 at t=1232, SWA1850 should capture the ILS 12
    /// localizer (FAC ~130°) and transition to FinalApproachPhase within
    /// 120 seconds. The ~20° intercept angle is well within the 30° limit.
    /// </summary>
    [Fact]
    public void Swa1850_PtacIls12_CapturesLocalizer()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 1232);

        var aircraft = engine.FindAircraft("SWA1850");
        Assert.NotNull(aircraft);

        output.WriteLine($"State at t=1232 (after PTAC 150 030):");
        output.WriteLine($"  Heading:        {aircraft.TrueHeading.Degrees:F1}");
        output.WriteLine($"  ActiveApproach: {aircraft.Phases?.ActiveApproach?.ApproachId ?? "null"}");
        output.WriteLine($"  Phases:         {FormatPhases(aircraft)}");

        Assert.NotNull(aircraft.Phases);
        var interceptPhase = aircraft.Phases.Phases.OfType<InterceptCoursePhase>().FirstOrDefault();
        Assert.NotNull(interceptPhase);

        bool captured = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SWA1850");
            Assert.NotNull(aircraft);

            if (t % 30 == 0 || aircraft.Phases?.Phases.OfType<FinalApproachPhase>().Any(p => p.Status == PhaseStatus.Active) == true)
            {
                output.WriteLine(
                    $"  t+{t}: hdg={aircraft.TrueHeading.Degrees:F1}, phases={FormatPhases(aircraft)}, notifs={FormatNotifications(aircraft)}"
                );
            }

            var finalPhase = aircraft.Phases?.Phases.OfType<FinalApproachPhase>().FirstOrDefault();
            if (finalPhase is { Status: PhaseStatus.Active })
            {
                output.WriteLine($"  >>> Captured at t+{t}! FinalApproachPhase is active <<<");
                captured = true;
                break;
            }

            if (aircraft.Phases?.ActiveApproach is null)
            {
                Assert.Fail($"Approach cleared at t+{t} — bust-through instead of capture. Notifications: {FormatNotifications(aircraft)}");
            }
        }

        Assert.True(captured, "SWA1850 did not capture the ILS 12 localizer within 120 seconds of PTAC 150 030");
    }

    /// <summary>
    /// Full approach diagnostic: tick-by-tick trace from PTAC through landing/go-around.
    /// Tracks heading, altitude, distance, cross-track, glideslope deviation, speed,
    /// warnings, and phase transitions.
    /// </summary>
    [Fact]
    public void Diagnostic_FullApproach_TickByTick()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        engine.Replay(recording, 1232);

        var aircraft = engine.FindAircraft("SWA1850");
        Assert.NotNull(aircraft);

        var interceptPhase = aircraft.Phases?.Phases.OfType<InterceptCoursePhase>().FirstOrDefault();
        if (interceptPhase is null)
        {
            output.WriteLine("No InterceptCoursePhase found after replay");
            return;
        }

        double threshLat = interceptPhase.ThresholdLat;
        double threshLon = interceptPhase.ThresholdLon;
        TrueHeading fac = interceptPhase.FinalApproachCourse;

        output.WriteLine(
            $"Aircraft: ({aircraft.Latitude:F6}, {aircraft.Longitude:F6}) hdg={aircraft.TrueHeading.Degrees:F1} alt={aircraft.Altitude:F0}"
        );
        output.WriteLine($"FAC: {fac.Degrees:F1}  Threshold: ({threshLat:F6}, {threshLon:F6})");
        output.WriteLine(
            $"ApproachId: {interceptPhase.ApproachId}  ApproachScore: {aircraft.ActiveApproachScore?.InterceptDistanceNm.ToString("F1") ?? "null"}"
        );
        output.WriteLine("");

        output.WriteLine("tick | heading  | tgtHdg   | alt    | dist   | signedXT | hdgDiff | gs     | phase          | warnings/notifications");
        output.WriteLine("---- | -------- | -------- | ------ | ------ | -------- | ------- | ------ | -------------- | ----------------------");

        string prevPhase = "";
        int prevWarnings = 0;
        int prevNotifs = 0;

        for (int t = 1; t <= 200; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SWA1850");
            if (aircraft is null)
            {
                output.WriteLine($"{t, 4} | AIRCRAFT DELETED");
                break;
            }

            double signedXT = GeoMath.SignedCrossTrackDistanceNm(aircraft.Latitude, aircraft.Longitude, threshLat, threshLon, fac);
            double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, threshLat, threshLon);
            double hdgDiff = aircraft.TrueHeading.AbsAngleTo(fac);
            string currentPhase = aircraft.Phases?.CurrentPhase?.Name ?? "none";

            // Log on phase transitions, new warnings/notifications, every 5s, or near threshold
            bool phaseChanged = currentPhase != prevPhase;
            bool newWarning = aircraft.PendingWarnings.Count > prevWarnings;
            bool newNotif = aircraft.PendingNotifications.Count > prevNotifs;
            bool nearThreshold = dist < 1.0;

            if (phaseChanged || newWarning || newNotif || t % 5 == 0 || nearThreshold)
            {
                string alerts = "";
                if (aircraft.PendingWarnings.Count > 0)
                {
                    alerts += "W:" + string.Join("; ", aircraft.PendingWarnings);
                }

                if (aircraft.PendingNotifications.Count > 0)
                {
                    if (alerts.Length > 0)
                    {
                        alerts += " | ";
                    }

                    alerts += "N:" + string.Join("; ", aircraft.PendingNotifications);
                }

                if (alerts.Length == 0)
                {
                    alerts = "-";
                }

                string marker = phaseChanged ? " <<<" : "";

                output.WriteLine(
                    $"{t, 4} | {aircraft.TrueHeading.Degrees, 8:F2} | {aircraft.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "null", 8} | {aircraft.Altitude, 6:F0} | {dist, 6:F2} | {signedXT, 8:F4} | {hdgDiff, 7:F1} | {aircraft.GroundSpeed, 6:F0} | {currentPhase, -14} | {alerts}{marker}"
                );

                if (aircraft.ActiveApproachScore is { } score && phaseChanged && currentPhase == "FinalApproach")
                {
                    output.WriteLine(
                        $"     | >>> ApproachScore: interceptDist={score.InterceptDistanceNm:F1}nm, interceptAngle={score.InterceptAngleDeg:F1}°, gsDeviation={score.GlideSlopeDeviationFt:F0}ft <<<"
                    );
                }
            }

            prevPhase = currentPhase;
            prevWarnings = aircraft.PendingWarnings.Count;
            prevNotifs = aircraft.PendingNotifications.Count;

            // Stop after landing or deletion
            if (currentPhase == "Landing" && aircraft.IsOnGround)
            {
                output.WriteLine($"     | >>> Touchdown at tick {t} <<<");
                break;
            }

            if (aircraft.Phases is null)
            {
                output.WriteLine($"     | >>> Phases cleared at tick {t} <<<");
                break;
            }
        }
    }

    private static string FormatPhases(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return "null";
        }

        return string.Join(", ", aircraft.Phases.Phases.Select(p => $"{p.Name}({p.Status})"));
    }

    private static string FormatNotifications(AircraftState aircraft)
    {
        if (aircraft.PendingNotifications.Count == 0)
        {
            return "none";
        }

        return string.Join("; ", aircraft.PendingNotifications);
    }
}
