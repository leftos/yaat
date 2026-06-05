using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #187 (ZHU S3-T1-L7): IFR departures with no commanded altitude
/// (the preset <c>CTO 320</c> is a takeoff heading, not an altitude) climbed straight through their
/// published initial altitude to cruise. They must instead hold the SID's TDLS initial altitude until
/// climbed: KIAH = 4000 (facility <c>initialAlts</c> fallback, per-transition value blank) and
/// KHOU = 5000 (per-transition <c>defaultInitialAlt</c>).
/// </summary>
public class Issue187DepartureCapTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue187-star-dvia-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private void AssertHoldsInitialAltitude(string callsign, int expectedCapFt)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 92); // departures spawn at t=90; the CTO preset fires at spawn

        var aircraft = engine.FindAircraft(callsign);
        Assert.NotNull(aircraft);
        output.WriteLine($"{callsign}: SidInitialAltitudeFt={aircraft.Procedure.SidInitialAltitudeFt}");

        // The published cap must be resolved from the bundled ARTCC TDLS config when CTO fires.
        Assert.Equal(expectedCapFt, aircraft.Procedure.SidInitialAltitudeFt);

        double maxAlt = aircraft.Altitude;
        for (int t = 1; t <= 360; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(callsign);
            if (aircraft is null)
            {
                break;
            }

            maxAlt = Math.Max(maxAlt, aircraft.Altitude);
            if (t % 60 == 0)
            {
                output.WriteLine(
                    $"  t=+{t, 3} alt={aircraft.Altitude, 6:F0} tgt={aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 6} VS={aircraft.VerticalSpeed, 6:F0}"
                );
            }
        }

        Assert.True(
            maxAlt < expectedCapFt + 300,
            $"{callsign} should hold the published {expectedCapFt} ft initial altitude, but climbed to {maxAlt:F0}"
        );
        Assert.True(
            aircraft!.Altitude > expectedCapFt - 300,
            $"{callsign} should be level near {expectedCapFt} ft, but was at {aircraft.Altitude:F0}"
        );
    }

    [Fact]
    public void Aal7122_KiahDeparture_HoldsInitialAltitude4000() => AssertHoldsInitialAltitude("AAL7122", 4000);

    [Fact]
    public void Swa2871_KhouDeparture_HoldsInitialAltitude5000() => AssertHoldsInitialAltitude("SWA2871", 5000);
}
