using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #401: `ERB 28R` rejected with "Unable, too high for base" for a
/// light aircraft at 2500 ft a few miles from the field.
///
/// Recording: S2-OAK-P (S2 Rating Practical Exam, ZOA). N44046 is spawned as a C208 Caravan
/// (turboprop category) and flown at a controller-assigned 90 kt. At t≈2780 it is at 2500 ft MSL
/// over Lake Chabot, ≈3.4 nm along-track east of the 28R threshold and ≈1.75 nm right of the
/// extended centerline — about 5 nm of base + final. The ERB feasibility gate budgeted the
/// descent at the turboprop *category* base speed (130 kt) and the normal pattern descent rate
/// (800 fpm), which demands 6.75 nm of path, and refused. The trainee's recorded `EF 28R` eight
/// seconds later raised the sibling "unable to descend for straight-in — too high" warning.
///
/// The gate now mirrors what BasePhase actually flies: the aircraft's own (or assigned) base speed,
/// and losing only the altitude above the 3° glideslope at rollout over the base leg at the
/// category's maximum pattern descent rate.
/// </summary>
public class Issue401ErbTooHighTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue401-oak-erb-too-high-recording.zip";
    private const string Callsign = "N44046";

    // The rejected ERB was typed at server-log 02:45:00; the recorded EF 28R follows at t=2788.
    private const int ErbSeconds = 2780;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.InitializeForTest(loggerFactory);
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void Erb28R_AtLakeChabot_Accepts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ErbSeconds);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        output.WriteLine(
            $"t={ErbSeconds} {Callsign}: type={aircraft.AircraftType} alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F0} "
                + $"pos={aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}"
        );

        var result = engine.SendCommand(Callsign, "ERB 28R");
        output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"ERB 28R should accept at 2500 ft with ~5 nm of base + final, got: {result.Message}");
        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void Erb28R_AtLakeChabot_LandsWithoutGoAround()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ErbSeconds);
        var result = engine.SendCommand(Callsign, "ERB 28R");
        Assert.True(result.Success, result.Message);
        Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

        bool sawFinal = false;
        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            var phase = aircraft.Phases?.CurrentPhase;
            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"t=+{t} phase={phase?.Name ?? "(none)"} alt={aircraft.Altitude:F0} vs={aircraft.VerticalSpeed:F0} ias={aircraft.IndicatedAirspeed:F0}"
                );
            }

            Assert.False(phase is GoAroundPhase, $"Aircraft went around at t=+{t} — the accepted base entry was not flyable");
            sawFinal |= phase is FinalApproachPhase;

            if (aircraft.IsOnGround)
            {
                Assert.True(sawFinal, "Aircraft landed without flying a final approach");
                return;
            }
        }

        Assert.Fail("Aircraft did not land within 420 s of the base entry");
    }
}
