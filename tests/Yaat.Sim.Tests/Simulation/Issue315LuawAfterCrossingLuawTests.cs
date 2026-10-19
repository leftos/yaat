using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #315, replayed: LUAW from the departure hold short lines the aircraft up, and the control case departs.
/// </summary>
public class Issue315LuawAfterCrossingLuawTests(ITestOutputHelper output) : Issue315LuawAfterCrossingTestBase(output)
{
    [Fact]
    public void Skw5237_LuawFromDestinationHoldShort_LinesUpOnRunway()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past the recorded LUAW, then tick physics only. The recording's later
        // actions (TAXI 28R C at t=666, DEL at t=686) are the controller working around
        // this very bug and would destroy the line-up under test.
        engine.Replay(recording, Skw5237PostLuawTime);

        AircraftState? ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);
        Output.WriteLine($"t={Skw5237PostLuawTime}: SKW5237 phase={ac.Phases?.CurrentPhase?.Name}");

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SKW5237");
            Assert.NotNull(ac);

            Phase? phase = ac.Phases?.CurrentPhase;
            if (phase is LineUpPhase lineUp)
            {
                Assert.False(
                    lineUp.CurrentState == LineUpPhase.State.Faulted,
                    $"LineUpPhase faulted {t}s after LUAW — the aircraft accepted the clearance but cannot line up"
                );
            }

            if (phase is LinedUpAndWaitingPhase)
            {
                Output.WriteLine($"t={Skw5237PostLuawTime + t}: SKW5237 lined up and waiting");
                return;
            }
        }

        Assert.Fail($"SKW5237 never reached LinedUpAndWaiting; phase={ac.Phases?.CurrentPhase?.Name} ias={ac.IndicatedAirspeed:F1}");
    }

    [Fact]
    public void Skw3473_LuawFromHoldShort_StillDeparts()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Control case: TAXI F C 28R (t=768), LUAW (t=885), CTO (t=948). Airborne by t=990.
        engine.Replay(recording, 990);

        AircraftState? ac = engine.FindAircraft("SKW3473");
        Assert.NotNull(ac);
        Output.WriteLine($"t=990: SKW3473 phase={ac.Phases?.CurrentPhase?.Name} alt={ac.Altitude:F0} onGround={ac.IsOnGround}");

        Assert.False(ac.IsOnGround, "SKW3473 should be airborne after CTO at t=948");
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
    }
}
