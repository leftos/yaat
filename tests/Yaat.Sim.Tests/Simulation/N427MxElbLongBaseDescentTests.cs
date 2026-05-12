using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the ELB-long-base descent bug. N427MX (PA31) was inbound to
/// OAK at 1400 ft, ~9 nm SE of the field, when the controller issued
/// <c>ELB 28L</c> (Enter Left Base, no distance argument). BasePhase set
/// <c>TargetAltitude = 509 ft</c> — halfway between TPA (1009) and threshold
/// (9) — and the aircraft descended at 700 fpm to 509 ft, holding that
/// altitude for 6+ minutes through base and final, well below the 3° glide
/// path the entire way.
///
/// Root cause: <c>BasePhase.OnStart</c> only lowered <c>midAlt</c> to the
/// GS-at-rollout altitude when <c>gsAlt &lt; midAlt</c> (short-final SA case).
/// For a long base leg, <c>gsAlt</c> at rollout is much higher than
/// <c>midAlt</c>, but the existing clamp never raised it. Fix: target
/// <c>min(currentAltitude, gsAlt)</c> so the aircraft holds altitude on a
/// long base and intercepts the glide path naturally on final.
///
/// Recording: S2-OAK-3 (2) | VFR Sequencing — N427MX spawns at t=800,
/// receives <c>ELB 28R</c> then <c>ELB 28L</c> at t=853.
/// </summary>
public class N427MxElbLongBaseDescentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("BasePhase", Microsoft.Extensions.Logging.LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After ELB 28L issued at t=853 to N427MX (~9 nm SE at 1400 ft),
    /// the aircraft must not descend to half-TPA (~509 ft). It should hold
    /// near current altitude on the long base leg and only descend along the
    /// glide path on final.
    /// </summary>
    [Fact]
    public void Elb_OnLongBase_DoesNotDescendToHalfTpa()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay to one tick after the ELB 28L command (issued at t=853).
        engine.Replay(recording, 854);

        var aircraft = engine.FindAircraft("N427MX");
        Assert.NotNull(aircraft);
        output.WriteLine(
            $"N427MX at t=854: alt={aircraft.Altitude:F0} ias={aircraft.IndicatedAirspeed:F1} "
                + $"hdg={aircraft.TrueHeading.Degrees:F0} tgtAlt={aircraft.Targets.TargetAltitude} "
                + $"phase={aircraft.Phases?.CurrentPhase?.Name}"
        );

        double startAlt = aircraft.Altitude;

        // Step 80 seconds — enough for the bug to manifest (today: aircraft
        // hits 509 ft by t=925, ~70s after ELB).
        for (int t = 1; t <= 80; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("N427MX");
            Assert.NotNull(aircraft);

            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"  t=854+{t, 3} alt={aircraft.Altitude, 6:F0} VS={aircraft.VerticalSpeed, 6:F0} "
                        + $"tgtAlt={aircraft.Targets.TargetAltitude} phase={aircraft.Phases?.CurrentPhase?.Name}"
                );
            }
        }

        // After the fix, with finalDist ~7-9 nm and gsAlt at rollout > current,
        // BasePhase targets min(currentAlt, gsAlt) = currentAlt, so the
        // aircraft holds altitude. Allow ~100 ft sag for the inevitable few
        // ticks before TargetAltitude is set on the very first tick after the
        // command. Today's behavior: aircraft is at 509 ft.
        Assert.True(
            aircraft!.Altitude >= 1300,
            $"Aircraft on long-base ELB descended to {aircraft.Altitude:F0} ft — should hold near "
                + $"start altitude ({startAlt:F0}) and intercept GS naturally on final"
        );
    }
}
