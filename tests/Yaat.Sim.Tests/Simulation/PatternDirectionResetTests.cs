using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Replay tests for the pattern-direction-reset bug. When a controller issues
/// MLT/MRT/CTO MLT/CTO MRT (persistent pattern direction intent) and later
/// vectors the aircraft (FH/TR/TL clears phases) or issues a single-approach
/// "Expect..." clearance like ERB 28L (right base for spacing), today the
/// persistent intent is lost because pattern direction lives only on
/// <see cref="PhaseList.TrafficDirection"/> which is wiped/overwritten.
///
/// User-visible symptom (S2-OAK-3 (2) VFR Sequencing recording): "Aircraft
/// seemed to be changing which direction of traffic they were doing between
/// turns for seemingly no reason. They'd be given MLT off the ground and
/// eventually be doing right traffic, and vice-versa."
///
/// Two test scenarios from that recording:
/// - N342T received CTO MLT at t=890, did several left circuits, was vectored
///   off via FH 360 at t=1910, given ERB 28L at t=2032 for a single right-base
///   entry, then COPT at t=2109. After the touch-and-go (~t=2195) the auto-
///   cycled next circuit should resume LEFT traffic per the original MLT
///   intent. Pre-fix: it cycles RIGHT (the controller manually re-issues MLT
///   at t=2288 to fix it).
/// - N172SP received CTO MRT at t=170, was vectored via FH 280 at t=597 then
///   FH 010 at t=655. The persistent MRT direction must survive the vectors.
///   Pre-fix: AircraftPattern.TrafficDirection is null because nothing ever
///   wrote to it; the only direction storage was PhaseList which got nulled.
/// </summary>
public class PatternDirectionResetTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// N342T: CTO MLT (left traffic, t=890) → many left circuits → FH 360 vector
    /// (t=1910) → ERB 28L for a single right-base entry (t=2032) → COPT (t=2109)
    /// → TouchAndGo (t≈2195) → auto-cycled next circuit (rebuilt at t=2215).
    ///
    /// By t=2300 the aircraft has completed the ERB right-base approach + touch-and-go (~t=2270) and is
    /// mid-Upwind/MidfieldCrossing on the auto-cycled new circuit. Its
    /// <see cref="PhaseList.TrafficDirection"/> should be Left (auto-cycle honors the persistent MLT
    /// intent), not Right (the transient ERB stamp from the previous approach). The sample time sits
    /// after the touch-and-go so it is robust to small replay-timeline shifts.
    /// </summary>
    [Fact]
    public void N342T_AfterErbAndCopt_NextCircuitResumesLeftTraffic()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        // Replay through the ERB approach + touch-and-go into the auto-cycled new circuit (t=2300).
        for (int t = 1; t <= 2300; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft("N342T");
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);

        output.WriteLine(
            $"t=2300 N342T: Pattern.TrafficDirection={aircraft.Pattern.TrafficDirection?.ToString() ?? "null"}, "
                + $"Phases.TrafficDirection={aircraft.Phases.TrafficDirection?.ToString() ?? "null"}, "
                + $"AssignedRunway={aircraft.Phases.AssignedRunway?.Designator ?? "null"}, "
                + $"phases={DescribePhases(aircraft)}"
        );

        // The persistent direction (CTO MLT at t=890) must survive the FH vector
        // and the ERB single-approach intent.
        Assert.Equal(PatternDirection.Left, aircraft.Pattern.TrafficDirection);

        // The current circuit's transient direction must reflect the auto-cycled
        // direction (i.e. the persistent Left, not the prior ERB Right).
        Assert.Equal(PatternDirection.Left, aircraft.Phases.TrafficDirection);
    }

    /// <summary>
    /// N172SP: CTO MRT (right traffic, t=170) → FH 280 (t=597) → FH 010 (t=655).
    /// FH commands clear <see cref="AircraftState.Phases"/>. The persistent
    /// AircraftPattern.TrafficDirection must survive the vectors so a later
    /// re-engagement (COPT, FOLLOW, EF, etc.) can honor the original MRT intent
    /// without the controller having to re-issue it.
    /// </summary>
    [Fact]
    public void N172SP_AfterFhVector_PreservesPersistentMrt()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        // Replay through CTO MRT (t=170) and both FH commands (t=597, t=655).
        for (int t = 1; t <= 700; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft("N172SP");
        Assert.NotNull(aircraft);

        output.WriteLine(
            $"t=700 N172SP: Pattern.TrafficDirection={aircraft.Pattern.TrafficDirection?.ToString() ?? "null"}, "
                + $"Phases is null = {aircraft.Phases is null}, "
                + $"Phases.TrafficDirection={aircraft.Phases?.TrafficDirection?.ToString() ?? "null"}"
        );

        // Persistent intent from CTO MRT must outlive FH vectors that null the PhaseList.
        Assert.Equal(PatternDirection.Right, aircraft.Pattern.TrafficDirection);
    }

    private static string DescribePhases(AircraftState ac)
    {
        if (ac.Phases is null)
        {
            return "(null)";
        }
        var plist = ac.Phases.Phases;
        if (plist.Count == 0)
        {
            return "(empty)";
        }
        return string.Join(" -> ", plist.Select((p, i) => i == ac.Phases.CurrentIndex ? $"[{p.GetType().Name}]" : p.GetType().Name));
    }
}
