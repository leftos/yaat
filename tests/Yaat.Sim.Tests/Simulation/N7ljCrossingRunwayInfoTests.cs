using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: while an aircraft is in CrossingRunwayPhase, the phase must carry the
/// runway being crossed (sourced from the preceding HoldingShortPhase's HoldShortPoint),
/// not the aircraft's departure / destination runway. The client's "Crossing Runway"
/// Info text consumes this field; pre-fix it fell back to AssignedRunway and rendered
/// "Crossing runway 30" for N7LJ — which is the takeoff runway, not the runway being
/// crossed.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts —
/// N7LJ (LJ45) preset <c>TAXI D C B W W1 30 HS 28R</c>. The aircraft taxis through
/// two parallel pairs (28R/10L and 28L/10R) on its way to runway 30. At t=1384 RES
/// cleared the first hold-short and N7LJ entered CrossingRunwayPhase for 28R/10L
/// (the first parallel pair encountered from SIG6 parking).
/// </summary>
public class N7ljCrossingRunwayInfoTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n7lj-crossing-runway-info-recording.yaat-bug-report-bundle.zip";

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
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void CrossingRunwayPhase_ReportsCrossingRunwayId_NotDepartureRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // t=1390s is ~5s into the CrossingRunwayPhase that begins at t=1385 after
        // RES at t=1384. The aircraft should be mid-crossing of 28R/10L (the first
        // parallel pair from SIG6 parking), still 20+ seconds before completing.
        engine.Replay(recording, 1390);

        var ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);

        var crossing = ac.Phases?.CurrentPhase as CrossingRunwayPhase;
        Assert.NotNull(crossing);

        output.WriteLine(
            $"phase={crossing.GetType().Name} runwayId={crossing.RunwayId} " + $"departureRunway={ac.Phases?.AssignedRunway?.Designator}"
        );

        // The phase must carry the runway being crossed, sourced from the preceding
        // HoldingShortPhase's HoldShortPoint.TargetName (matches OAK layout: "28R/10L"
        // — combined runway pair name as stored in the airport ground layout).
        Assert.Equal("28R/10L", crossing.RunwayId);

        // And it must NOT echo the departure runway (30) — that was the pre-fix bug.
        Assert.NotEqual(ac.Phases?.AssignedRunway?.Designator, crossing.RunwayId);
    }
}
