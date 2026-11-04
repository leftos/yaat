using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #187 (ZHU S3-T1-L7): IFR departures with no commanded altitude
/// (the preset <c>CTO 320</c> is a takeoff heading, not an altitude) climbed straight through their
/// published initial altitude to cruise. They must instead fly the SID transition's TDLS default: KIAH's
/// is a climb via "EXC MAINT 4000FT", put in force at takeoff, and KHOU's an initial altitude of 5000 held
/// until climbed (per-transition <c>defaultInitialAlt</c>).
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
        return new SimulationEngine(
            RecordingGroundData.Evergreen("SID initial-altitude caps on IAH and HOU departures"),
            PushPlanStore.CreateShipped()
        );
    }

    private void AssertHoldsInitialAltitude(string callsign, int expectedCapFt)
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 92); // departures spawn at t=90; the CTO preset fires at spawn

        AircraftState? aircraft = engine.FindAircraft(callsign);
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

    /// <summary>
    /// KIAH's SID transitions carry no initial altitude, only the TDLS climb via "CLIMB VIA SID EXC MAINT 4000FT": the
    /// preset CTO puts that climb via in force from takeoff, 4,000 its except-maintain. The recording then vectors the
    /// departure off the SID with no altitude, which cancels the climb via: the controller is warned and the aircraft
    /// levels where it is, below 4,000.
    /// </summary>
    [Fact]
    public void Aal7122_KiahDeparture_ClimbsVia4000_ThenLevelsWhenVectoredWithoutAnAltitude()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        var warnings = new List<string>();
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == "AAL7122")
            {
                warnings.Add(warning);
            }
        };
        engine.Replay(recording, 92); // departures spawn at t=90; the CTO preset fires at spawn

        AircraftState? aircraft = engine.FindAircraft("AAL7122");
        Assert.NotNull(aircraft);
        Assert.True(aircraft.Procedure.SidViaMode);
        Assert.Equal(4000, aircraft.Procedure.ClimbViaTargetFt);
        Assert.Equal(4000, aircraft.Procedure.SidViaCeiling);
        Assert.Null(aircraft.Procedure.SidInitialAltitudeFt);

        double maxAlt = aircraft.Altitude;
        for (int t = 1; t <= 360; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("AAL7122");
            if (aircraft is null)
            {
                break;
            }

            maxAlt = Math.Max(maxAlt, aircraft.Altitude);
        }

        output.WriteLine($"maxAlt={maxAlt:F0} finalAlt={aircraft?.Altitude:F0} warnings: {string.Join(" | ", warnings)}");
        Assert.Contains("Vectored off procedure without an altitude assignment", warnings);
        Assert.NotNull(aircraft);
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        Assert.True(maxAlt < 4000, $"AAL7122 climbed to {maxAlt:F0} after the vector cancelled the climb via");
        Assert.InRange(aircraft.Altitude, maxAlt - 50, maxAlt + 50);
        Assert.InRange(aircraft.VerticalSpeed, -100, 100);
    }

    [Fact]
    public void Swa2871_KhouDeparture_HoldsInitialAltitude5000() => AssertHoldsInitialAltitude("SWA2871", 5000);
}
