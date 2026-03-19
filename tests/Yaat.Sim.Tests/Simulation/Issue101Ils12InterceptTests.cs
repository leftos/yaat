using System.Text.Json;
using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Diagnostic tests for GitHub issue #101: Aircraft not joining ILS 12 approach at OAK.
///
/// Recording: S3-NCTC-4 — SWA1850 vectored heading 150 for ILS 12 (course ~130°).
/// CAPP issued at t=1328. The aircraft flies through the localizer without capturing.
///
/// Timeline from recording:
///   t=1288: FH 170
///   t=1312: FH 160
///   t=1326: FH 150
///   t=1328: CAPP (1st attempt)
///   t=1396: CMN 30, FHN 100, CAPP (2nd attempt)
///   t=1412: FHN 150
///   t=1415: CAPP (3rd attempt)
/// </summary>
public class Issue101Ils12InterceptTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue101-ils12-intercept-recording.json";

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
    /// Tick-by-tick diagnostic of SWA1850 after the first CAPP at t=1328.
    /// Heading 190° (mid-turn toward 150°), FAC ~130°, crossTrack 0.33nm.
    /// </summary>
    [Fact]
    public void Diagnostic_Capp1_t1328_TickByTick()
    {
        TraceCappAttempt(1328, "CAPP #1");
    }

    /// <summary>
    /// Tick-by-tick diagnostic of SWA1850 after the second CAPP at t=1396.
    /// User issued CMN 30 + FHN 100 + CAPP all at t=1396.
    /// </summary>
    [Fact]
    public void Diagnostic_Capp2_t1396_TickByTick()
    {
        TraceCappAttempt(1396, "CAPP #2");
    }

    /// <summary>
    /// Tick-by-tick diagnostic of SWA1850 after the third CAPP at t=1415.
    /// User issued FHN 150 at t=1412, then CAPP at t=1415.
    /// </summary>
    [Fact]
    public void Diagnostic_Capp3_t1415_TickByTick()
    {
        TraceCappAttempt(1415, "CAPP #3");
    }

    private void TraceCappAttempt(int replayTime, string label)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, replayTime);

        var aircraft = engine.FindAircraft("SWA1850");
        Assert.NotNull(aircraft);

        var approach = aircraft.Phases?.ActiveApproach;
        var interceptPhase = aircraft.Phases?.Phases.OfType<InterceptCoursePhase>().FirstOrDefault();

        output.WriteLine($"=== SWA1850 state at t={replayTime} (immediately after {label}) ===");
        output.WriteLine($"  Heading:          {aircraft.TrueHeading.Degrees:F2}");
        output.WriteLine($"  AssignedHeading:  {aircraft.Targets.AssignedMagneticHeading}");
        output.WriteLine($"  TargetHeading:    {aircraft.Targets.TargetTrueHeading}");
        output.WriteLine($"  Altitude:         {aircraft.Altitude:F0}");
        output.WriteLine($"  Position:         ({aircraft.Latitude:F6}, {aircraft.Longitude:F6})");
        output.WriteLine($"  ActiveApproach:   {approach?.ApproachId ?? "null"}");
        output.WriteLine($"  FAC:              {approach?.FinalApproachCourse.Degrees.ToString("F1") ?? "null"}");
        output.WriteLine($"  Phases:           {FormatPhases(aircraft)}");
        output.WriteLine($"  Notifications:    {FormatNotifications(aircraft)}");

        if (interceptPhase is null)
        {
            output.WriteLine("  >>> No InterceptCoursePhase found — CAPP may have failed or used a different phase <<<");
            return;
        }

        double signedXT = GeoMath.SignedCrossTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            interceptPhase.ThresholdLat,
            interceptPhase.ThresholdLon,
            interceptPhase.FinalApproachCourse
        );
        double hdgDiff = aircraft.TrueHeading.AbsAngleTo(interceptPhase.FinalApproachCourse);
        output.WriteLine($"  SignedCrossTrack: {signedXT:F4} nm");
        output.WriteLine($"  CrossTrack:       {Math.Abs(signedXT):F4} nm");
        output.WriteLine($"  HeadingDiff:      {hdgDiff:F1}°");
        output.WriteLine($"  Phase FAC:        {interceptPhase.FinalApproachCourse:F1}");
        output.WriteLine($"  Phase Threshold:  ({interceptPhase.ThresholdLat:F6}, {interceptPhase.ThresholdLon:F6})");

        output.WriteLine("");
        output.WriteLine("tick | heading  | tgtHdg   | signedXT   | crossTrack | hdgDiff | phases                          | notifications");
        output.WriteLine("---- | -------- | -------- | ---------- | ---------- | ------- | ------------------------------- | -------------");

        double prevSignedXT = signedXT;
        bool hadApproach = approach is not null;

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SWA1850");
            if (aircraft is null)
            {
                output.WriteLine($"{t, 4} | AIRCRAFT DELETED");
                break;
            }

            double currentSignedXT = GeoMath.SignedCrossTrackDistanceNm(
                aircraft.Latitude,
                aircraft.Longitude,
                interceptPhase.ThresholdLat,
                interceptPhase.ThresholdLon,
                interceptPhase.FinalApproachCourse
            );
            double currentXT = Math.Abs(currentSignedXT);
            double currentHdgDiff = aircraft.TrueHeading.AbsAngleTo(interceptPhase.FinalApproachCourse);

            bool signFlipped = (prevSignedXT > 0 && currentSignedXT < 0) || (prevSignedXT < 0 && currentSignedXT > 0);
            string marker = signFlipped ? " <<<SIGN FLIP>>>" : "";

            output.WriteLine(
                $"{t, 4} | {aircraft.TrueHeading.Degrees, 8:F2} | {aircraft.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "null", 8} | {currentSignedXT, 10:F4} | {currentXT, 10:F4} | {currentHdgDiff, 7:F1} | {FormatPhases(aircraft), -31} | {FormatNotifications(aircraft)}{marker}"
            );

            prevSignedXT = currentSignedXT;

            if (hadApproach && aircraft.Phases?.ActiveApproach is null)
            {
                output.WriteLine($"     | >>> Approach cleared at tick {t} <<<");
                break;
            }
        }
    }

    /// <summary>
    /// Broader view: replay from t=1280 (before heading assignments) through all three
    /// CAPP attempts. Shows the full sequence of commands and their effects.
    /// </summary>
    [Fact]
    public void Diagnostic_AllThreeAttempts_Overview()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=1280, just before the heading assignments leading to CAPP
        engine.Replay(recording, 1280);

        var aircraft = engine.FindAircraft("SWA1850");
        Assert.NotNull(aircraft);

        output.WriteLine("=== SWA1850 overview from t=1280 through all three CAPP attempts ===");
        output.WriteLine($"  Initial heading: {aircraft.TrueHeading.Degrees:F1}");
        output.WriteLine($"  Initial position: ({aircraft.Latitude:F6}, {aircraft.Longitude:F6})");
        output.WriteLine($"  Initial altitude: {aircraft.Altitude:F0}");
        output.WriteLine("");

        // Key timestamps from recording
        int[] keyTimes = [1288, 1312, 1326, 1328, 1396, 1412, 1415];
        string[] keyLabels = ["FH 170", "FH 160", "FH 150", "CAPP #1", "CMN30+FHN100+CAPP #2", "FHN 150", "CAPP #3"];

        output.WriteLine(" time | heading  | tgtHdg   | assignHdg | approach | phases                          | notifications        | event");
        output.WriteLine("----- | -------- | -------- | --------- | -------- | ------------------------------- | -------------------- | -----");

        int currentTime = 1280;
        int keyIdx = 0;

        for (int t = 1; t <= 200; t++)
        {
            engine.TickOneSecond();
            currentTime++;

            aircraft = engine.FindAircraft("SWA1850");
            if (aircraft is null)
            {
                output.WriteLine($"{currentTime, 5} | AIRCRAFT DELETED");
                break;
            }

            string event_ = "";
            if (keyIdx < keyTimes.Length && currentTime >= keyTimes[keyIdx])
            {
                event_ = keyLabels[keyIdx];
                keyIdx++;
            }

            // Log every 2 seconds, plus at key timestamps
            if (t % 2 == 0 || event_.Length > 0)
            {
                output.WriteLine(
                    $"{currentTime, 5} | {aircraft.TrueHeading.Degrees, 8:F2} | {aircraft.Targets.TargetTrueHeading?.Degrees.ToString("F2") ?? "null", 8} | {aircraft.Targets.AssignedMagneticHeading?.Degrees.ToString("F0") ?? "null", 9} | {aircraft.Phases?.ActiveApproach?.ApproachId ?? "null", 8} | {FormatPhases(aircraft), -31} | {FormatNotifications(aircraft), -20} | {event_}"
                );
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
