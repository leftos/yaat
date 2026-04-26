using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// AIM 5-4-1 NOTE 2: "Once at the published speed, ATC expects pilots will
/// maintain the published speed until additional adjustment is required to
/// comply with further published or ATC assigned speed restrictions."
///
/// On the SFO ALWYS3 STAR, the terminating fix BERKS carries a 210 KIAS
/// maximum. After sequencing past BERKS, an aircraft with no further ATC
/// speed instruction must not accelerate beyond 210 KIAS. Before the fix
/// the aircraft accelerates to its default cruise/descent speed because
/// the auto speed schedule kicks in once the route empties.
/// </summary>
public class StarSpeedAfterTerminatingFixTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue77-alwys-descent-recording.json";
    private const string Callsign = "SKW5456";
    private const double TerminatingFixSpeedKts = 210.0;
    private const double Tolerance = 5.0;

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
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Ticks SKW5456 along the entire ALWYS3 STAR until BERKS (the terminating
    /// fix with a 210 KIAS max) is sequenced, then continues ticking and asserts
    /// IAS stays at or below 210 KIAS for the post-terminating-fix window.
    /// </summary>
    [Fact]
    public void Alwys3_DoesNotAccelerateAboveLastConstraintAfterBerks()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // SKW5456 spawns at t=660 on ALWYS3 with onAltitudeProfile=true.
        engine.Replay(recording, 662);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Sanity: BERKS is in the route with a 210 kt max.
        var berksFix = aircraft.Targets.NavigationRoute.FirstOrDefault(f => f.Name.Equals("BERKS", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(berksFix);
        Assert.NotNull(berksFix.SpeedRestriction);
        Assert.Equal(TerminatingFixSpeedKts, berksFix.SpeedRestriction.SpeedKts);
        Assert.True(berksFix.SpeedRestriction.IsMaximum);

        // Tick until BERKS is sequenced. Cap at 2400s (40 min) — same window the
        // sibling Issue77 test uses for ARRTU/BERKS.
        bool berksSequenced = false;
        int berksSequencedAt = -1;
        for (int t = 1; t <= 2400; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                output.WriteLine($"Aircraft deleted at t={t}");
                break;
            }

            bool hasBerks = aircraft.Targets.NavigationRoute.Any(f => f.Name.Equals("BERKS", StringComparison.OrdinalIgnoreCase));
            if (!hasBerks)
            {
                berksSequenced = true;
                berksSequencedAt = t;
                output.WriteLine(
                    $"BERKS sequenced at t={t}: alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F1} "
                        + $"tgtSpd={aircraft.Targets.TargetSpeed?.ToString("F1") ?? "null"} "
                        + $"ceil={aircraft.Targets.SpeedCeiling?.ToString("F1") ?? "null"}"
                );
                break;
            }
        }

        Assert.True(berksSequenced, "BERKS was never sequenced within 2400s");
        Assert.NotNull(aircraft);

        // Aircraft entered "limbo": STAR done, no approach pending. Track max IAS
        // over the next 120 s. Per AIM 5-4-1 it must hold at or below 210 KIAS.
        const int postWindowS = 120;
        double maxIas = aircraft.IndicatedAirspeed;
        int maxIasT = berksSequencedAt;
        for (int t = 1; t <= postWindowS; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                output.WriteLine($"Aircraft deleted in post-window at t+{t}");
                break;
            }

            if (aircraft.IndicatedAirspeed > maxIas)
            {
                maxIas = aircraft.IndicatedAirspeed;
                maxIasT = berksSequencedAt + t;
            }

            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"  t+{t, 3}s ias={aircraft.IndicatedAirspeed:F1} "
                        + $"tgtSpd={aircraft.Targets.TargetSpeed?.ToString("F1") ?? "null"} "
                        + $"ceil={aircraft.Targets.SpeedCeiling?.ToString("F1") ?? "null"} "
                        + $"route={aircraft.Targets.NavigationRoute.Count}"
                );
            }
        }

        output.WriteLine($"Post-BERKS max IAS: {maxIas:F1} kt at t={maxIasT}s (terminating constraint = {TerminatingFixSpeedKts} kt)");

        Assert.True(
            maxIas <= TerminatingFixSpeedKts + Tolerance,
            $"After sequencing past BERKS (terminating fix, max {TerminatingFixSpeedKts} kt), "
                + $"aircraft must not accelerate. Observed max IAS {maxIas:F1} kt at t={maxIasT}s "
                + $"(tolerance +{Tolerance} kt)."
        );
    }

    /// <summary>
    /// Regression: an explicit ATC speed command must clear the procedural
    /// last-published-speed memory. After SPD is issued, the aircraft is free
    /// to accelerate to the new commanded speed; SpeedCeiling and
    /// LastProcedureSpeedKts must both be null.
    /// </summary>
    [Fact]
    public void SpdCommand_ClearsProceduralSpeedMemory()
    {
        TestVnasData.EnsureInitialized();

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            FlightPlan = new AircraftFlightPlan { Destination = "KSFO" },
        };

        // Simulate post-terminating-fix state: ceiling published from procedural memory.
        ac.Procedure.LastProcedureSpeedKts = 210;
        ac.Targets.SpeedCeiling = 210;

        var cmd = new SpeedCommand(280);
        var result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(42)));

        Assert.True(result.Success);
        Assert.Null(ac.Procedure.LastProcedureSpeedKts);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Equal(280, ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }
}
