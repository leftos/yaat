using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-bug #7 (WJA1521 "M4 unreachable"): WJA1521 pushed back onto taxiway M4, then
/// <c>TAXI M4 M2 $2</c> was rejected — "Cannot taxi via M4 from the aircraft's position — it is
/// unreachable without crossing a runway or leaving the movement area." — even though the aircraft
/// is ON M4. Re-issuing <c>TAXI M2 $2</c> (omitting the current taxiway) worked (`M5 M2 $2`). Naming
/// the taxiway the aircraft is already on as the first cleared taxiway must not make it unreachable.
///
/// Reported from the S1-SFO-4 (ZOA) recording: PUSH M4 at t=1903, TAXI M4 M2 $2 issued shortly after.
/// </summary>
public class Issue172Wja1521CurrentTaxiwayTests(ITestOutputHelper output)
{
    /// <summary>How long the push onto M4 may take; a B737 covers several hundred feet at 5 kt in well under this.</summary>
    private const int PushBudgetSeconds = 180;

    /// <summary>
    /// Replay-free: WJA1521 (a B737) parked on gate B2, as the recording spawns it. A bare <c>PUSH M4</c> pushes
    /// the aircraft back onto taxiway M4, which runs alongside the push, and <c>TAXI M4 M2 $2</c> is then accepted.
    /// </summary>
    [Fact]
    public void PushM4FromGateB2_EndsOnM4_ThenTaxiM4M2Accepted()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var aircraft = SfoGroundHarness.SpawnParked(ground, "WJA1521", "B737", "B2");
        var push = ground.Engine.SendCommand(aircraft.Callsign, "PUSH M4");
        output.WriteLine($"PUSH M4: success={push.Success} msg={push.Message}");
        Assert.True(push.Success, $"PUSH M4 off B2 was refused: {push.Message}");

        bool everPushed = false;
        int doneSecond = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => everPushed && (aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase),
            PushBudgetSeconds,
            _ => everPushed |= aircraft.Phases?.CurrentPhase is PushbackPhase
        );
        Assert.True(doneSecond > 0, $"PUSH M4 never finished within {PushBudgetSeconds}s (phase={aircraft.Phases?.CurrentPhase?.Name ?? "null"})");

        var nearest = ground.Layout.FindNearestNode(aircraft.Position.Lat, aircraft.Position.Lon);
        Assert.NotNull(nearest);
        output.WriteLine(
            $"push finished t={doneSecond}s; nearest node {nearest.Id}: {string.Join(", ", nearest.Edges.SelectMany(RampLaneReposition.EdgeNames))}"
        );
        Assert.Contains(nearest.Edges, e => e.MatchesTaxiway("M4"));

        var taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI M4 M2 $2");
        output.WriteLine($"TAXI M4 M2 $2: success={taxi.Success} msg={taxi.Message}");
        Assert.True(taxi.Success, $"TAXI M4 M2 $2 should succeed from M4 but failed: {taxi.Message}");
    }
}
