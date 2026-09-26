using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Default (uninstructed) runway-exit selection, which depends on the exit's class (aviation ruling 2026-09-25):
/// a high-speed exit (turn-off speed at or above <see cref="CategoryPerformance.HighSpeedExitSpeed"/>, angle ≤ 46°)
/// is reachable when it needs no more than <see cref="CategoryPerformance.ComfortableExitDecelRate"/>; a standard
/// exit only when it needs no more than the routine <see cref="CategoryPerformance.RolloutDecelRate"/>. And the
/// braking the rollout actually flies never exceeds the limit that selected the exit.
///
/// <para>Each case spawns the aircraft on a 1 nm final to a real runway (real navdata and ground layout), clears it
/// to land with no exit instruction, and ticks the production engine until the aircraft is on its exit taxiway.</para>
/// </summary>
public class UninstructedExitSelectionTests(ITestOutputHelper output)
{
    /// <summary>One-second samples of a constant commanded rate read that rate to within rounding.</summary>
    private const double SampleToleranceKtsPerSec = 0.05;

    /// <summary>What one uninstructed landing did.</summary>
    /// <param name="ExitTaxiway">The first taxiway the aircraft was on after the rollout.</param>
    /// <param name="Candidate">The exit <see cref="LandingPhase"/> last held as its candidate.</param>
    /// <param name="PeakRolloutDecel">Largest one-second ground-speed drop while <see cref="LandingPhase"/> owned the aircraft on the ground.</param>
    private sealed record Landing(string? ExitTaxiway, ResolvedExitInfo? Candidate, double PeakRolloutDecel);

    [Theory]
    [InlineData("A320")]
    [InlineData("A319")]
    [InlineData("B737")]
    [InlineData("E75L")]
    [InlineData("CRJ9")]
    [InlineData("B712")]
    public void Sfo19L_TakesHighSpeedH_NotStandardF1(string aircraftType)
    {
        Landing? landing = Land("SFO", "19L", aircraftType);
        if (landing is null)
        {
            return;
        }

        Assert.Equal("H", landing.ExitTaxiway);
    }

    [Fact]
    public void Oak30_E75L_TakesHighSpeedW4()
    {
        Landing? landing = Land("OAK", "30", "E75L");
        if (landing is null)
        {
            return;
        }

        Assert.Equal("W4", landing.ExitTaxiway);
    }

    /// <summary>
    /// The rollout never brakes harder than the limit that made its exit reachable: the comfortable-exit rate for a
    /// high-speed exit, the routine rollout rate for a standard one. The committed exit carries that limit as its
    /// selection rate, and the rollout's peak stays within it.
    /// </summary>
    [Theory]
    [InlineData("SFO", "19L", "A320")]
    [InlineData("SFO", "19L", "A319")]
    [InlineData("SFO", "19L", "B737")]
    [InlineData("SFO", "19L", "E75L")]
    [InlineData("OAK", "30", "E75L")]
    public void RolloutPeakDecel_DoesNotExceedTheChosenExitsSelectionLimit(string airport, string runway, string aircraftType)
    {
        Landing? landing = Land(airport, runway, aircraftType);
        if (landing is null)
        {
            return;
        }

        Assert.NotNull(landing.Candidate);
        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        bool highSpeedExit = landing.Candidate.TurnOffSpeed >= CategoryPerformance.HighSpeedExitSpeed(category);
        double limit = highSpeedExit ? CategoryPerformance.ComfortableExitDecelRate(category) : CategoryPerformance.RolloutDecelRate(category);

        Assert.Equal(limit, landing.Candidate.SelectionDecelRate);
        Assert.True(
            landing.PeakRolloutDecel <= limit + SampleToleranceKtsPerSec,
            $"{aircraftType} on {airport} {runway}: rollout peak decel {landing.PeakRolloutDecel:F2} kt/s exceeds the "
                + $"{(highSpeedExit ? "high-speed" : "standard")} exit selection rate {limit:F2} kt/s (exit {landing.ExitTaxiway})"
        );
    }

    /// <summary>
    /// A fast lander on a short runway: at a 140 kt touchdown on OAK 28R (5,458 ft) no exit passes the class-based
    /// filter, so the crew takes the earliest exit it can make braking firmly — C1 — rather than stopping on the
    /// runway. The exit carries the firm cap as its selection rate, and the rollout brakes harder than the routine
    /// rate that would have admitted C1 (a standard exit) under the class-based filter — the firm-braking fallback
    /// chose it — while the firm cap bounds the braking.
    /// </summary>
    [Fact]
    public void Oak28R_Crj9_Uninstructed_ExitsTheRunway()
    {
        Landing? landing = Land("OAK", "28R", "CRJ9");
        if (landing is null)
        {
            return;
        }

        const AircraftCategory Category = AircraftCategory.Jet;
        double standardExitLimit = CategoryPerformance.RolloutDecelRate(Category);
        double firmCap = Math.Min(RolloutBraking.FirmBrakingRateKtsPerSec, CategoryPerformance.ExpediteExitDecelRate(Category));

        Assert.Equal("C1", landing.ExitTaxiway);
        Assert.NotNull(landing.Candidate);
        Assert.True(landing.Candidate.TurnOffSpeed < CategoryPerformance.HighSpeedExitSpeed(Category), "C1 should be a standard exit");
        Assert.Equal(firmCap, landing.Candidate.SelectionDecelRate);
        Assert.True(
            landing.PeakRolloutDecel > standardExitLimit + SampleToleranceKtsPerSec,
            $"CRJ9 rollout peak decel {landing.PeakRolloutDecel:F2} kt/s is not above the standard-exit selection rate {standardExitLimit:F2} kt/s"
        );
        Assert.True(
            landing.PeakRolloutDecel <= firmCap + SampleToleranceKtsPerSec,
            $"CRJ9 rollout peak decel {landing.PeakRolloutDecel:F2} kt/s exceeds the firm cap {firmCap:F2} kt/s"
        );
    }

    private Landing? Land(string airport, string runwayDesignator, string aircraftType)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand(airport, runwayDesignator, aircraftType, "TST1");
        if (spawned is null)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;

        double peakRolloutDecel = 0;
        double? previousRolloutGs = null;
        ResolvedExitInfo? candidate = null;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            Phase? phase = aircraft.Phases?.CurrentPhase;

            if ((phase is LandingPhase landing) && aircraft.IsOnGround)
            {
                candidate = landing.CandidateExit ?? candidate;
                if (previousRolloutGs is { } previous)
                {
                    peakRolloutDecel = Math.Max(peakRolloutDecel, previous - aircraft.GroundSpeed);
                }

                previousRolloutGs = aircraft.GroundSpeed;
            }
            else
            {
                previousRolloutGs = null;
            }

            if (aircraft.IsOnGround && (aircraft.Ground.CurrentTaxiway is { } taxiway))
            {
                output.WriteLine(
                    $"{aircraftType} {airport} {runwayDesignator}: exit {taxiway} at t+{t}s, candidate turn-off "
                        + $"{candidate?.TurnOffSpeed:F0} kt selected at {candidate?.SelectionDecelRate:F2} kt/s, "
                        + $"rollout peak decel {peakRolloutDecel:F2} kt/s"
                );
                return new Landing(taxiway, candidate, peakRolloutDecel);
            }
        }

        Assert.Fail($"{aircraftType} on {airport} {runwayDesignator} never reached an exit taxiway within 300 s");
        return null;
    }
}
