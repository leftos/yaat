using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
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
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

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
