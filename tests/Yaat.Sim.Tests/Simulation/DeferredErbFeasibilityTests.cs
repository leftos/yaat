using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for deferred pattern-entry feasibility. Reproduces a bug report
/// against an SR22 (N2BP) descending to OAK from the southeast, where two
/// equivalent ways of scheduling a base-entry clearance behind the upstream
/// VFR transition fix VPCBT were both rejected at typing time:
///
///   LA N2BP DCT VPCBT; ERB 28R   → Unable, too close for base
///   LA N2BP AT VPCBT ERB 28R     → Unable, too high for base
///
/// At the moment of typing the aircraft was at 2000 ft, roughly 10 nm from the
/// 28R threshold — too close/high to enter base RIGHT NOW. But by the time the
/// aircraft reached VPCBT (a few miles ahead), the geometry would have been
/// fine, which is exactly why the controller was deferring the clearance.
///
/// Root cause: CommandDispatcher.DryRunValidate ignored the deferred nature of
/// the second block (or the AT condition on a single block) and ran the ERB
/// handler against the live aircraft state.
///
/// Recording: s2-oak4-deferred-erb-recording.yaat-bug-report-bundle.zip
/// (S2-OAK-4 | VFR Transitions/Radar Concepts, ZOA, OAK_TWR).
/// </summary>
public class DeferredErbFeasibilityTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak4-deferred-erb-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N2BP";

    // The user typed both rejected commands between t=1290 (after RTIS) and
    // t=1362 (when DCT VPCBT alone was finally accepted). Replay to t=1290
    // gives us a stable state to dispatch synthetic commands against without
    // letting the recording's own DCT VPCOL/VPCBT actions race us.
    private const int ReplayStartSeconds = 1290;

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

    /// <summary>
    /// Diagnostic: log N2BP's state at the replay start point so the failing
    /// assertions below have a documented reason for the magic time.
    /// </summary>
    [Fact]
    public void Diagnostic_LogN2bpStateAtReplayStart()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayStartSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"t={ReplayStartSeconds} {Callsign}: alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F0} "
                + $"pos={aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4} tgtAlt={aircraft.Targets.TargetAltitude} "
                + $"route=[{string.Join(",", aircraft.Targets.NavigationRoute.Select(f => f.Name))}]"
        );
    }

    /// <summary>
    /// `DCT VPCBT; ERB 28R` must accept. The DCT applies immediately; ERB sits
    /// in the queue behind it and fires when the aircraft sequences VPCBT.
    /// Before fix: rejected with "Unable, too close for base" because the
    /// dry-run ran ERB against the aircraft's current position (~10 nm out).
    /// </summary>
    [Fact]
    public void DctThenErb_Compound_Accepts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayStartSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var result = engine.SendCommand(Callsign, "DCT VPCBT; ERB 28R");

        output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"Expected DCT VPCBT; ERB 28R to accept, got: {result.Message}");

        // The DCT block applies immediately (no trigger). The ERB block sits in
        // the queue behind it, unapplied, waiting for the navigation route to
        // empty when the aircraft reaches VPCBT.
        var queue = aircraft.Queue.Blocks;
        Assert.Contains(queue, b => !b.IsApplied && b.Description.Contains("ERB", StringComparison.OrdinalIgnoreCase));

        // The aircraft's navigation route is set to VPCBT by the immediately-applied DCT.
        Assert.Contains(aircraft.Targets.NavigationRoute, f => string.Equals(f.Name, "VPCBT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// `AT VPCBT ERB 28R` must accept. The block carries a ReachFix(VPCBT)
    /// trigger and fires when the aircraft arrives at VPCBT.
    /// Before fix: rejected with "Unable, too high for base" because the
    /// dry-run ignored the AT condition and ran ERB against current state.
    /// </summary>
    [Fact]
    public void AtVpcbtErb_Conditional_Accepts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayStartSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var result = engine.SendCommand(Callsign, "AT VPCBT ERB 28R");

        output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"Expected AT VPCBT ERB 28R to accept, got: {result.Message}");

        // Find the queued ERB block and confirm its trigger is ReachFix(VPCBT).
        var erbBlock = aircraft.Queue.Blocks.FirstOrDefault(b => b.Description.Contains("ERB", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(erbBlock);
        Assert.NotNull(erbBlock.Trigger);
        Assert.Equal(BlockTriggerType.ReachFix, erbBlock.Trigger.Type);
        Assert.Equal("VPCBT", erbBlock.Trigger.FixName, ignoreCase: true);
        Assert.False(erbBlock.IsApplied);
    }

    /// <summary>
    /// End-to-end: after `DCT VPCBT; ERB 28R` is accepted, ticking forward
    /// must fire ERB once the aircraft sequences VPCBT, installing pattern
    /// phases for 28R. Confirms the deferred command actually has the intended
    /// effect at the trigger moment, not just that dry-run no longer rejects.
    /// </summary>
    [Fact]
    public void DctThenErb_FiresAtVpcbt_InstallsBasePatternFor28R()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayStartSeconds);

        var sendResult = engine.SendCommand(Callsign, "DCT VPCBT; ERB 28R");
        Assert.True(sendResult.Success, $"Send failed: {sendResult.Message}");

        // Tick forward, watching for ERB to fire (pattern phase installed).
        // Generous budget: VPCBT is several miles from the aircraft at descent
        // speed (~120 kt), so this can take ~3-5 minutes of sim time.
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                break;
            }

            // ERB triggers when the aircraft sequences VPCBT — at that point
            // TryEnterPattern installs PatternEntryPhase + BasePhase + tail
            // for the 28R pattern.
            var currentPhase = aircraft.Phases?.CurrentPhase;
            bool patternInstalled = currentPhase is PatternEntryPhase or BasePhase;
            if (patternInstalled)
            {
                output.WriteLine(
                    $"ERB fired at t={ReplayStartSeconds + t}: phase={currentPhase?.GetType().Name} runway={aircraft.Phases?.AssignedRunway?.Designator}"
                );
                Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
                return;
            }
        }

        Assert.Fail("ERB 28R never fired — pattern phases were not installed within 600s after sending DCT VPCBT; ERB 28R");
    }

    /// <summary>
    /// When a deferred command's trigger fires but the command is genuinely
    /// infeasible at that moment, the rejection must surface to the RPO via
    /// <see cref="AircraftState.PendingWarnings"/>. Without this, removing
    /// dry-run for deferred blocks would silently swallow legitimate failures.
    ///
    /// Subscribes to <see cref="SimulationEngine.WarningEmitted"/> to observe
    /// per-aircraft warnings as the engine drains them each tick (the engine
    /// itself is otherwise non-observant — the server's <c>TickProcessor</c>
    /// is what fans warnings out to clients in production).
    ///
    /// Dispatches `LV &lt;currentAlt-100&gt; ERB 28R` against the bug recording.
    /// The aircraft is descending and abeam OAK 28R; the LV trigger fires
    /// within ~10s, and ERB rejects "too close for base" at the trigger
    /// fire moment.
    /// </summary>
    [Fact]
    public void DeferredErbInfeasibleAtTrigger_SurfacesPendingWarning()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayStartSeconds);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        output.WriteLine($"Aircraft alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F0} tgtAlt={aircraft.Targets.TargetAltitude}");

        var capturedWarnings = new List<(string Callsign, string Warning)>();
        engine.WarningEmitted += (cs, w) => capturedWarnings.Add((cs, w));

        // LV trigger just below current altitude — aircraft is descending toward 2000,
        // so it will pass through this within ~10s. Aircraft is abeam OAK 28R threshold,
        // so ERB will reject "too close for base" at fire time.
        int triggerAlt = (int)(Math.Floor(aircraft.Altitude / 100.0) * 100) - 100;
        var dispatchResult = engine.SendCommand(Callsign, $"LV {triggerAlt} ERB 28R");
        output.WriteLine($"Send LV {triggerAlt} ERB 28R: Success={dispatchResult.Success}, Message={dispatchResult.Message}");
        Assert.True(dispatchResult.Success, $"LV ... ERB compound should accept (deferred); got: {dispatchResult.Message}");

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();

            var match = capturedWarnings.FirstOrDefault(p =>
                p.Callsign == Callsign
                && (
                    p.Warning.Contains("too close for base", StringComparison.OrdinalIgnoreCase)
                    || p.Warning.Contains("too high for base", StringComparison.OrdinalIgnoreCase)
                )
            );
            if (match != default)
            {
                output.WriteLine($"Warning surfaced at +{t}s: {match.Callsign} '{match.Warning}'");
                return;
            }
        }

        Assert.Fail(
            $"Expected a 'too close/high for base' WarningEmitted within 60s after LV trigger fired. "
                + $"Captured: [{string.Join(" | ", capturedWarnings.Select(p => $"{p.Callsign}:{p.Warning}"))}]"
        );
    }
}
