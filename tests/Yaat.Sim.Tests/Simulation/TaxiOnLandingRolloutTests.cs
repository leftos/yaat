using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A TAXI given while an arrival is still on its landing rollout (YAAT-334). However the sim answers it, the aircraft
/// must not be put on a taxi route whose fillets it reaches at rollout speed: closed-form arc playback then writes it
/// round a 91° exit at well over 100 kt, and the navigator's teleport guard throws.
/// </summary>
public sealed class TaxiOnLandingRolloutTests
{
    private const string Arrival = "ARR1";
    private const int AfterDispatchSeconds = 120;

    /// <summary>Slack over the jet taxi speed for a fillet sampled once a second; today's faults are 50 kt and more over it.</summary>
    private const double FilletMarginKts = 5.0;

    private readonly ITestOutputHelper _output;

    public TaxiOnLandingRolloutTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// An A320 landing SFO 19L is sent <c>TAXIAUTO $1</c> some seconds after touchdown, still in <see cref="LandingPhase"/>,
    /// and ticked on for two minutes. No fillet is rounded faster than the jet taxi speed, which every fillet's own arc-speed
    /// cap is at or below, by more than <see cref="FilletMarginKts"/>. At touchdown (0 s) today's route starts at exit V,
    /// 95 ft ahead at 135 kt, and the first tick throws "[Nav] teleport"; at 4 s (123 kt) a fillet is rounded at about 85 kt.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(30)]
    public void TaxiAuto_OnTheSfo19lRollout_NeverRoundsAFilletAtRolloutSpeed(int secondsAfterTouchdown)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("SFO", "19L", "A320", Arrival, 1.0) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => aircraft.IsOnGround && (aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {aircraft.Phases?.CurrentPhase?.Name}");
        for (int second = 0; second < secondsAfterTouchdown; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        double dispatchKts = aircraft.GroundSpeed;
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXIAUTO $1");
        _output.WriteLine($"TAXIAUTO $1 at {dispatchKts:F1} kt -> success={taxi.Success} msg={taxi.Message}");

        double fastestOnFilletKts = 0;
        for (int second = 1; second <= AfterDispatchSeconds; second++)
        {
            spawned.Engine.TickOneSecond();
            if (aircraft.Ground.LastNavDiag is { OnArc: true })
            {
                fastestOnFilletKts = Math.Max(fastestOnFilletKts, aircraft.GroundSpeed);
            }
        }

        double ceilingKts = CategoryPerformance.TaxiSpeed(AircraftCategory.Jet) + FilletMarginKts;
        Assert.True(
            fastestOnFilletKts <= ceilingKts,
            $"after TAXIAUTO $1 at {dispatchKts:F1} kt on the rollout the aircraft rounded a fillet at {fastestOnFilletKts:F1} kt, "
                + $"over the jet taxi speed plus {FilletMarginKts:F0} kt ({ceilingKts:F0} kt)"
        );
    }
}
