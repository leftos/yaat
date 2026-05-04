using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for CFIX speed acceleration — SKW3398 should accelerate to 210 IAS
/// to meet the CFIX CEPIN 3000 210 restriction in its scenario presets.
///
/// Bug: When CFIX, CAPP, and SPD are separate scenario presets (not one
/// compound command), each DispatchCompound call clears conflicting blocks
/// from the previous. CAPP's lateral dimension cleared CFIX's navigation,
/// destroying the speed target before the aircraft could accelerate.
///
/// Fix: Compose same-timestamp presets into a single compound command so
/// the command queue sequences them correctly (CFIX immediate, CAPP/SPD
/// queued with AT CEPIN triggers).
///
/// Recording: S1-SFO-2 | Ground Control 28/01
/// Presets: "CFIX CEPIN 3000 210", "CAPP 28R", "AT CEPIN SPD 180 AXMUL"
/// </summary>
public class CfixSpeedAccelerationTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/5d33df162626.zip";

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
    /// SKW3398 must accelerate to 210 IAS within 15 seconds of spawn.
    /// The CFIX CEPIN 3000 210 preset commands the aircraft to cross CEPIN
    /// at 210 knots; the aircraft spawns at ~182 and must accelerate.
    /// </summary>
    [Fact]
    public void SKW3398_AcceleratesTo210ForCfix()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        var aircraft = engine.FindAircraft("SKW3398");
        Assert.NotNull(aircraft);

        double initialIas = aircraft.IndicatedAirspeed;
        output.WriteLine($"Spawn IAS={initialIas:F1}, TargetSpeed={aircraft.Targets.TargetSpeed?.ToString("F1") ?? "null"}");

        // CFIX should set TargetSpeed=210 at dispatch
        Assert.Equal(210.0, aircraft.Targets.TargetSpeed);

        // Tick forward — aircraft should reach 210 within 15 seconds
        for (int t = 1; t <= 15; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("SKW3398");
            Assert.NotNull(aircraft);

            if (aircraft.IndicatedAirspeed >= 209)
            {
                output.WriteLine($"Reached 210 IAS at t={t}");
                return;
            }
        }

        Assert.Fail(
            $"SKW3398 should accelerate to 210 for CFIX restriction but was at {aircraft.IndicatedAirspeed:F1} after 15 seconds (started at {initialIas:F1})"
        );
    }

    /// <summary>
    /// Diagnostic: log SKW3398's full speed/state profile from spawn through first 120 seconds.
    /// </summary>
    [Fact]
    public void Diagnostic_SKW3398_CfixSpeedProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);

        var skw = engine.FindAircraft("SKW3398");
        Assert.NotNull(skw);

        output.WriteLine(
            $"Spawn: IAS={skw.IndicatedAirspeed:F1} GS={skw.GroundSpeed:F1} alt={skw.Altitude:F0} "
                + $"tgtSpd={skw.Targets.TargetSpeed?.ToString("F1") ?? "null"} asnSpd={skw.Targets.AssignedSpeed?.ToString("F0") ?? "null"} "
                + $"phase={skw.Phases?.CurrentPhase?.Name ?? "-"} "
                + $"route=[{string.Join(", ", skw.Targets.NavigationRoute.Select(f => f.Name + (f.SpeedRestriction is not null ? $"@{f.SpeedRestriction.SpeedKts}" : "")))}]"
        );

        output.WriteLine($"{"t", 5} {"IAS", 7} {"tgtSpd", 7} {"alt", 7} {"phase", 18} {"route", 30}");
        output.WriteLine(new string('-', 90));

        for (int t = 1; t <= 120; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3398");
            if (ac is null)
            {
                output.WriteLine($"{t, 5} -- aircraft deleted --");
                break;
            }

            string routeStr = string.Join(
                ", ",
                ac.Targets.NavigationRoute.Select(f => f.Name + (f.SpeedRestriction is not null ? $"@{f.SpeedRestriction.SpeedKts}" : ""))
            );
            if (routeStr.Length > 30)
            {
                routeStr = routeStr[..27] + "...";
            }

            if (t <= 15 || t % 10 == 0)
            {
                output.WriteLine(
                    $"{t, 5} {ac.IndicatedAirspeed, 7:F1} "
                        + $"{ac.Targets.TargetSpeed?.ToString("F1") ?? "null", 7} "
                        + $"{ac.Altitude, 7:F0} "
                        + $"{ac.Phases?.CurrentPhase?.Name ?? "-", 18} "
                        + $"{routeStr, 30}"
                );
            }
        }
    }
}
