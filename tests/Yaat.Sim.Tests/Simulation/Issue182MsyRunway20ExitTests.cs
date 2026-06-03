using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #182: at KMSY, arrivals assigned to runway "2" (single-digit designator)
/// got placed on runway 20's approach and overran the far end, because the un-normalized designator "2"
/// failed to match the zero-padded runway ends ("02"/"20"). <see cref="RunwayInfo.IsEnd1"/> compared
/// "02".Equals("2") (false), flipping the active end, and the runway-exit search received "2" which
/// matches no "RWY02/20" centerline edge — so no exit was ever found and the aircraft rolled off the end.
/// Recording: S1-L5 (MSY_GND), ZHU. Arrivals on runway "11" (two digits) were unaffected.
/// </summary>
public class Issue182MsyRunway20ExitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue182-msy-rwy2-exit-recording.yaat-bug-report-bundle.zip";

    // SWA4141 is the first runway-"2" arrival: OnFinal spawn at t=240, lands and should exit before t=640.
    private const string Callsign = "SWA4141";
    private const int SpawnAtSeconds = 240;
    private const int ObserveSeconds = 400;

    private readonly ITestOutputHelper _output = output;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void GetRunway_SingleDigitDesignator_SelectsAssignedEnd()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy = navDb.GetRunway("MSY", "2");
        Assert.NotNull(rwy);

        // The designator must normalize to one of the runway's ends so end-selection works.
        Assert.True(rwy.Id.Contains(rwy.Designator), $"Designator '{rwy.Designator}' does not match either end ({rwy.Id.End1}/{rwy.Id.End2})");

        // Runway 2 lands roughly northbound (~15-25°). The bug resolved it to runway 20 (~195°).
        _output.WriteLine(
            $"Designator={rwy.Designator} heading={rwy.TrueHeading.Degrees:F1} threshold=({rwy.ThresholdLatitude:F5},{rwy.ThresholdLongitude:F5})"
        );
        Assert.InRange(rwy.TrueHeading.Degrees, 0.0, 90.0);
    }

    [Fact]
    public void Diagnostic_Runway2ArrivalPhaseTimeline()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, SpawnAtSeconds);
        string? lastPhase = null;
        for (int t = 1; t <= ObserveSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                continue;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            if (phase != lastPhase)
            {
                _output.WriteLine(
                    $"t={SpawnAtSeconds + t} phase={phase} pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F0}"
                );
                lastPhase = phase;
            }
        }
    }

    [Fact]
    public void Runway2Arrival_ExitsRunway_NotStuckOnRunway()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, SpawnAtSeconds);

        bool reachedExit = false;
        for (int t = 1; t <= ObserveSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                continue;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "";
            if (phase is "Holding After Exit" or "Taxiing" or "Crossing Runway")
            {
                reachedExit = true;
                _output.WriteLine(
                    $"{Callsign} cleared the runway at t={SpawnAtSeconds + t}: phase={phase} pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5})"
                );
                break;
            }
        }

        Assert.True(
            reachedExit,
            $"{Callsign} never cleared the runway within {ObserveSeconds}s of spawn — it stayed in Landing/RunwayExit "
                + "and overran the runway end (issue #182)."
        );
    }
}
